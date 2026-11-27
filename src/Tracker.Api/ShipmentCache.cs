using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace Tracker.Api;

/// <summary>
/// Step 3: cache-aside in front of <see cref="ShipmentStore"/> for the hot read. Look in Redis
/// first; on a miss, read Postgres and store the result with a TTL. Writes delete the entry after
/// they commit, so the next read rebuilds it.
///
/// Step 4 adds stampede protection. Coalescing: concurrent misses on one key share a single
/// Postgres read instead of each running their own. Jitter: each entry's TTL is spread by a random
/// fraction, so entries cached together (after a deploy, or a burst) don't all expire together.
/// Coalescing is per instance; with three instances a key can be rebuilt at most three times at
/// once. A Redis lock would make that one, at the cost of a round trip on every miss.
///
/// Redis is an optimisation, never a dependency: if it's down or slow, reads go to Postgres and a
/// failed delete is logged, leaving that entry stale for at most the TTL.
///
/// The known race, accepted and bounded by the TTL: a read misses and fetches the old row, a write
/// commits and deletes the key, then the read stores what it fetched, which is now stale.
/// </summary>
public sealed class ShipmentCache(ShipmentStore store, IConnectionMultiplexer redis, TrackerSettings settings, ILogger<ShipmentCache> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);
    private readonly ConcurrentDictionary<string, Lazy<Task<Shipment?>>> loading = new();
    private long hits, misses, loads, joined, unavailable;

    public static RedisKey Key(string trackingNumber) => $"shipment:{trackingNumber}";

    /// <summary>Since the process started: what the cache did. Served at /stats for the ladder and the tests.</summary>
    public CacheStats Stats => new(Interlocked.Read(ref hits), Interlocked.Read(ref misses), Interlocked.Read(ref loads),
        Interlocked.Read(ref joined), Interlocked.Read(ref unavailable));

    public async Task<(Shipment? Shipment, CacheResult Result)> GetAsync(string trackingNumber, CancellationToken ct)
    {
        if (!settings.Cache) return (await store.GetAsync(trackingNumber, ct), CacheResult.Off);

        var cached = await TryReadAsync(trackingNumber);
        if (cached.Unavailable)
        {
            Interlocked.Increment(ref unavailable);
            return (await store.GetAsync(trackingNumber, ct), CacheResult.Unavailable);
        }
        if (cached.Shipment is not null)
        {
            Interlocked.Increment(ref hits);
            return (cached.Shipment, CacheResult.Hit);
        }

        Interlocked.Increment(ref misses);
        if (!settings.Coalescing)
        {
            Interlocked.Increment(ref loads);
            return (await LoadAsync(trackingNumber, ct), CacheResult.Miss);
        }
        return (await LoadCoalescedAsync(trackingNumber, ct), CacheResult.Miss);
    }

    /// <summary>The first miss on a key starts the load; misses that arrive while it runs wait for it.</summary>
    private async Task<Shipment?> LoadCoalescedAsync(string trackingNumber, CancellationToken ct)
    {
        Lazy<Task<Shipment?>>? mine = null;
        mine = new Lazy<Task<Shipment?>>(() => LeadAsync(trackingNumber, mine!));
        var current = loading.GetOrAdd(trackingNumber, mine);
        if (!ReferenceEquals(current, mine)) Interlocked.Increment(ref joined);
        // The shared load ignores any one caller's cancellation, so a client that hangs up can't
        // fail the others; each caller stops waiting on its own token instead.
        return await current.Value.WaitAsync(ct);
    }

    private async Task<Shipment?> LeadAsync(string trackingNumber, Lazy<Task<Shipment?>> self)
    {
        try
        {
            // A miss that came just after the last load finished (and left this dictionary) would
            // otherwise run a second one; the entry that load stored answers it instead.
            var cached = await TryReadAsync(trackingNumber);
            if (cached.Shipment is not null) return cached.Shipment;
            Interlocked.Increment(ref loads);
            return await LoadAsync(trackingNumber, CancellationToken.None);
        }
        finally
        {
            // Only its own entry: a write may already have replaced it (see InvalidateAsync).
            loading.TryRemove(new KeyValuePair<string, Lazy<Task<Shipment?>>>(trackingNumber, self));
        }
    }

    private async Task<(Shipment? Shipment, bool Unavailable)> TryReadAsync(string trackingNumber)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(trackingNumber));
            return (value.HasValue ? JsonSerializer.Deserialize<Shipment>((byte[])value!, Json) : null, false);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("Cache read failed for {TrackingNumber}, reading Postgres: {Error}", trackingNumber, e.Message);
            return (null, true);
        }
    }

    private async Task<Shipment?> LoadAsync(string trackingNumber, CancellationToken ct)
    {
        var shipment = await store.GetAsync(trackingNumber, ct);
        // Unknown tracking numbers aren't cached. The workload only asks for ones that exist; a
        // public API would cache the 404 briefly too, so guessing numbers can't bypass the cache.
        if (shipment is null) return null;
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(trackingNumber), JsonSerializer.SerializeToUtf8Bytes(shipment, Json), Ttl());
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("Cache write failed for {TrackingNumber}: {Error}", trackingNumber, e.Message);
        }
        return shipment;
    }

    /// <summary>The TTL, spread evenly by up to ±CacheTtlJitter of itself.</summary>
    private TimeSpan Ttl()
    {
        var spread = settings.CacheTtlJitter * (2 * Random.Shared.NextDouble() - 1);
        return TimeSpan.FromSeconds(settings.CacheTtlSeconds * (1 + spread));
    }

    /// <summary>Called after a write commits, never before: deleting first would let a read refill the old row.</summary>
    public async Task InvalidateAsync(string trackingNumber)
    {
        if (!settings.Cache || !settings.CacheInvalidation) return;
        // A load that began before this write may return the old row. Reads that start from now on
        // must not join it, so they start a fresh one. (Another instance's load can still store the
        // old row: the same race as above, bounded by the TTL.)
        loading.TryRemove(trackingNumber, out _);
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Key(trackingNumber));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("Cache delete failed for {TrackingNumber}; stale for up to {Ttl} s: {Error}",
                trackingNumber, settings.CacheTtlSeconds, e.Message);
        }
    }
}

public enum CacheResult { Off, Hit, Miss, Unavailable }

/// <summary>
/// Loads are Postgres reads the cache started; joined are misses that waited for another's load
/// instead; unavailable are reads that skipped the cache because Redis didn't answer.
/// </summary>
public sealed record CacheStats(long Hits, long Misses, long Loads, long Joined, long Unavailable);
