using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;
using Tracker.Api;

namespace Tracker.Tests;

/// <summary>
/// Step 4, switched on and off. A stampede needs a rebuild slow enough that misses pile up behind
/// it, so the test makes one: it locks the shipments table, sends 200 requests for one cold key,
/// waits until they're all queued, then lets go.
/// </summary>
[Collection(nameof(InfrastructureCollection))]
public sealed class StampedeTests(Infrastructure infra)
{
    private const string Number = "SL0000000100";

    private async Task<(Api Api, string ConnectionString)> StartAsync(bool coalescing, double jitter = 0)
    {
        await infra.FlushRedisAsync();
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "10"), ("shipments", "200"));
        var connectionString = infra.ConnectionStringFor(database);
        var api = new Api(new Dictionary<string, string?>
        {
            ["Tracker:Database"] = connectionString,
            ["Tracker:Redis"] = infra.RedisUrl,
            ["Tracker:Pooling"] = "true",
            ["Tracker:Cache"] = "true",
            ["Tracker:Coalescing"] = coalescing.ToString(),
            ["Tracker:CacheTtlJitter"] = jitter.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return (api, connectionString);
    }

    /// <summary>Sends 200 requests for one cold key while its rebuild is held up, and returns how many reads reached the store.</summary>
    private static async Task<long> StampedeAsync(Api api, string connectionString, Func<CacheStats, bool> allQueued)
    {
        var client = await api.CreateReadyClientAsync();
        var store = api.Services.GetRequiredService<ShipmentStore>();
        var cache = api.Services.GetRequiredService<ShipmentCache>();

        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("LOCK TABLE shipments IN ACCESS EXCLUSIVE MODE", transaction: tx);

        var requests = Enumerable.Range(0, 200).Select(_ => client.GetAsync($"/v1/shipments/{Number}")).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!allQueued(cache.Stats) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(allQueued(cache.Stats), $"not all 200 queued: {cache.Stats}, {store.Reads} reads");
        await tx.CommitAsync();

        var responses = await Task.WhenAll(requests);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        return store.Reads;
    }

    [Fact]
    public async Task With_coalescing_200_concurrent_misses_on_one_key_run_one_query()
    {
        var (api, db) = await StartAsync(coalescing: true);
        await using var _ = api;

        // Queued: all 200 missed, and 199 are waiting on the first one's load.
        var reads = await StampedeAsync(api, db, s => s.Misses == 200 && s.Joined == 199);

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Switched_off_they_run_200_queries()
    {
        var (api, db) = await StartAsync(coalescing: false);
        await using var _ = api;

        var reads = await StampedeAsync(api, db, s => s.Misses == 200);

        Assert.Equal(200, reads);
    }

    private async Task<List<double>> TtlsOfHundredEntriesCachedTogetherAsync(double jitter)
    {
        var (api, _) = await StartAsync(coalescing: true, jitter);
        await using var __ = api;
        var client = await api.CreateReadyClientAsync();
        var responses = await Task.WhenAll(Enumerable.Range(1, 100).Select(i => client.GetAsync($"/v1/shipments/SL{i:D10}")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisUrl);
        var ttls = new List<double>();
        var missing = new List<int>();
        for (var i = 1; i <= 100; i++)
        {
            var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(ShipmentCache.Key($"SL{i:D10}"));
            if (ttl is null) missing.Add(i); else ttls.Add(ttl.Value.TotalSeconds);
        }
        Assert.True(missing.Count == 0, $"not cached: {string.Join(",", missing)}; stats {api.Services.GetRequiredService<ShipmentCache>().Stats}");
        return ttls;
    }

    [Fact]
    public async Task With_jitter_entries_cached_together_expire_spread_over_48_to_72_seconds()
    {
        var ttls = await TtlsOfHundredEntriesCachedTogetherAsync(jitter: 0.2);

        Assert.All(ttls, t => Assert.InRange(t, 47, 72));
        Assert.True(ttls.Max() - ttls.Min() > 15, $"spread only {ttls.Max() - ttls.Min():F1} s");
    }

    [Fact]
    public async Task Without_jitter_they_all_expire_within_a_second_or_two_of_each_other()
    {
        var ttls = await TtlsOfHundredEntriesCachedTogetherAsync(jitter: 0);

        Assert.True(ttls.Max() - ttls.Min() < 2, $"spread {ttls.Max() - ttls.Min():F1} s");
    }
}
