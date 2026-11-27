using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Tracker.Tests;

/// <summary>
/// Step 3, each part switched on and off: cache-aside with a TTL, and invalidation on writes.
/// To prove a read didn't touch Postgres, the tests change a row behind the API's back: a read
/// that still returns the old value can only have come from Redis.
/// </summary>
[Collection(nameof(InfrastructureCollection))]
public sealed class CacheTests(Infrastructure infra)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // Every 100th shipment is in the seed's hot set, in transit.
    private const string Number = "SL0000000100";

    private async Task<(HttpClient Client, Api Api, string ConnectionString)> StartAsync(bool cache, int ttlSeconds = 60,
        bool invalidation = true, string? redis = null)
    {
        await infra.FlushRedisAsync();
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "10"), ("shipments", "200"));
        var connectionString = infra.ConnectionStringFor(database);
        var api = new Api(new Dictionary<string, string?>
        {
            ["Tracker:Database"] = connectionString,
            ["Tracker:Redis"] = redis ?? infra.RedisUrl,
            ["Tracker:Pooling"] = "true",
            ["Tracker:Cache"] = cache.ToString(),
            ["Tracker:CacheTtlSeconds"] = ttlSeconds.ToString(),
            ["Tracker:CacheInvalidation"] = invalidation.ToString(),
        });
        // The unreachable-Redis test can't wait for ready: it never will be.
        return (redis is null ? await api.CreateReadyClientAsync() : api.CreateClient(), api, connectionString);
    }

    private static async Task<(string Status, string Cache)> ReadAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/v1/shipments/{Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (body.GetProperty("status").GetString()!, response.Headers.GetValues("X-Cache").Single());
    }

    private static async Task ChangeStatusBehindTheApisBackAsync(string connectionString, string status)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.ExecuteAsync("UPDATE shipments SET status = @status WHERE tracking_number = @Number", new { status, Number });
    }

    private static async Task RecordEventAsync(HttpClient client, string status)
    {
        var response = await client.PostAsJsonAsync($"/v1/shipments/{Number}/events", new { status, location = "Depot 4" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_second_read_comes_from_Redis_and_after_the_TTL_from_Postgres_again()
    {
        var (client, api, db) = await StartAsync(cache: true, ttlSeconds: 1);
        await using var _ = api;

        Assert.Equal(("in_transit", "miss"), await ReadAsync(client));
        await ChangeStatusBehindTheApisBackAsync(db, "exception");

        // Postgres says "exception" now, so the old status proves this read never reached it.
        Assert.Equal(("in_transit", "hit"), await ReadAsync(client));

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(("exception", "miss"), await ReadAsync(client));
    }

    [Fact]
    public async Task Switched_off_every_read_goes_to_Postgres()
    {
        var (client, api, db) = await StartAsync(cache: false);
        await using var _ = api;

        Assert.Equal(("in_transit", "off"), await ReadAsync(client));
        await ChangeStatusBehindTheApisBackAsync(db, "exception");
        Assert.Equal(("exception", "off"), await ReadAsync(client));
    }

    [Fact]
    public async Task A_read_straight_after_a_write_sees_the_write()
    {
        var (client, api, _) = await StartAsync(cache: true);
        await using var __ = api;

        Assert.Equal(("in_transit", "miss"), await ReadAsync(client));
        await RecordEventAsync(client, "out_for_delivery");

        // The write deleted the entry, so this read rebuilt it from Postgres.
        Assert.Equal(("out_for_delivery", "miss"), await ReadAsync(client));
        Assert.Equal(("out_for_delivery", "hit"), await ReadAsync(client));
    }

    [Fact]
    public async Task Without_invalidation_a_read_after_a_write_returns_the_old_status()
    {
        var (client, api, _) = await StartAsync(cache: true, invalidation: false);
        await using var __ = api;

        Assert.Equal(("in_transit", "miss"), await ReadAsync(client));
        await RecordEventAsync(client, "out_for_delivery");

        // The courier has scanned the parcel, but the customer keeps seeing "in transit" for up to
        // the TTL, a minute here.
        Assert.Equal(("in_transit", "hit"), await ReadAsync(client));
    }

    [Fact]
    public async Task With_Redis_unreachable_reads_and_writes_still_work_through_Postgres()
    {
        // Nothing listens on port 1.
        var (client, api, _) = await StartAsync(cache: true, redis: "localhost:1");
        await using var __ = api;

        var timer = Stopwatch.StartNew();
        Assert.Equal(("in_transit", "unavailable"), await ReadAsync(client));
        await RecordEventAsync(client, "delivered");
        Assert.Equal(("delivered", "unavailable"), await ReadAsync(client));

        // Failing fast matters: queueing for a Redis that isn't there would make every request wait.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
    }
}
