using Dapper;
using Npgsql;

namespace Tracker.Tests;

/// <summary>Step 2, switched on and off: how many database sessions 50 requests open.</summary>
[Collection(nameof(InfrastructureCollection))]
public sealed class PoolingTests(Infrastructure infra)
{
    private async Task<long> SessionsOpenedBy50RequestsAsync(bool pooling)
    {
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "10"), ("shipments", "200"));
        await using var api = new Api(new Dictionary<string, string?>
        {
            ["Tracker:Database"] = infra.ConnectionStringFor(database),
            ["Tracker:Redis"] = infra.RedisUrl,
            ["Tracker:Pooling"] = pooling.ToString(),
            ["Tracker:MaxPoolSize"] = "5",
        });
        var client = api.CreateClient();

        var before = await SessionsAsync(database);
        await Task.WhenAll(Enumerable.Range(1, 50).Select(i => client.GetAsync($"/v1/shipments/SL{i:D10}")));
        // Postgres counts a session when it starts, but publishes its statistics only every so often.
        await Task.Delay(TimeSpan.FromSeconds(2));
        return await SessionsAsync(database) - before;
    }

    private async Task<long> SessionsAsync(string database)
    {
        await using var admin = new NpgsqlConnection(infra.PostgresUrl);
        await admin.OpenAsync();
        return await admin.ExecuteScalarAsync<long>("SELECT sessions FROM pg_stat_database WHERE datname = @database", new { database });
    }

    [Fact]
    public async Task With_a_pool_50_requests_open_at_most_the_pools_5_connections()
    {
        Assert.InRange(await SessionsOpenedBy50RequestsAsync(pooling: true), 1, 5);
    }

    [Fact]
    public async Task Switched_off_every_request_opens_its_own_connection()
    {
        // Each request opens one connection; a GET for a shipment that exists runs both queries on it.
        Assert.True(await SessionsOpenedBy50RequestsAsync(pooling: false) >= 50);
    }
}
