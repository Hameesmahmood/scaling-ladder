using System.Text.Json;
using StackExchange.Redis;

namespace Tracker.Api;

/// <summary>
/// Step 3: cache-aside in front of <see cref="ShipmentStore"/> for the hot read. Look in Redis
/// first; on a miss, read Postgres and store the result with a TTL. Writes delete the entry after
/// they commit, so the next read rebuilds it.
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

    public static RedisKey Key(string trackingNumber) => $"shipment:{trackingNumber}";

    public async Task<(Shipment? Shipment, CacheResult Result)> GetAsync(string trackingNumber, CancellationToken ct)
    {
        if (!settings.Cache) return (await store.GetAsync(trackingNumber, ct), CacheResult.Off);

        var key = Key(trackingNumber);
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);
            if (cached.HasValue) return (JsonSerializer.Deserialize<Shipment>((byte[])cached!, Json), CacheResult.Hit);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("Cache read failed for {Key}, reading Postgres: {Error}", (string?)key, e.Message);
            return (await store.GetAsync(trackingNumber, ct), CacheResult.Unavailable);
        }

        var shipment = await store.GetAsync(trackingNumber, ct);
        // Unknown tracking numbers aren't cached. The workload only asks for ones that exist; a
        // public API would cache the 404 briefly too, so guessing numbers can't bypass the cache.
        if (shipment is not null)
        {
            try
            {
                await redis.GetDatabase().StringSetAsync(key, JsonSerializer.SerializeToUtf8Bytes(shipment, Json),
                    TimeSpan.FromSeconds(settings.CacheTtlSeconds));
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogWarning("Cache write failed for {Key}: {Error}", (string?)key, e.Message);
            }
        }
        return (shipment, CacheResult.Miss);
    }

    /// <summary>Called after a write commits, never before: deleting first would let a read refill the old row.</summary>
    public async Task InvalidateAsync(string trackingNumber)
    {
        if (!settings.Cache || !settings.CacheInvalidation) return;
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
