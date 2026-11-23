using Dapper;
using Npgsql;

namespace Tracker.Tests;

[Collection(nameof(InfrastructureCollection))]
public sealed class SeedTests(Infrastructure infra)
{
    private async Task<NpgsqlConnection> SeededAsync()
    {
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "100"), ("shipments", "2000"));
        var connection = new NpgsqlConnection(infra.ConnectionStringFor(database));
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task Creates_the_requested_sizes_with_a_one_percent_hot_set()
    {
        await using var db = await SeededAsync();

        Assert.Equal(100, await db.ExecuteScalarAsync<long>("SELECT count(*) FROM customers"));
        Assert.Equal(2000, await db.ExecuteScalarAsync<long>("SELECT count(*) FROM shipments"));
        Assert.Equal(20, await db.ExecuteScalarAsync<long>("SELECT count(*) FROM shipments WHERE status = 'in_transit'"));
        var perShipment = await db.ExecuteScalarAsync<double>("SELECT count(*)::float / 2000 FROM tracking_events");
        Assert.InRange(perShipment, 9.0, 11.0);
        Assert.Equal("SL0000000100", await db.ExecuteScalarAsync<string>("SELECT tracking_number FROM shipments WHERE id = 100"));
    }

    [Fact]
    public async Task Gives_the_same_rows_every_time()
    {
        const string fingerprint = """
            SELECT md5(string_agg(concat_ws(',', s.tracking_number, s.customer_id, s.status, s.created_at,
                       (SELECT string_agg(concat_ws(',', e.occurred_at, e.status, e.location), ';' ORDER BY e.id)
                        FROM tracking_events e WHERE e.shipment_id = s.id)), '|' ORDER BY s.id))
            FROM shipments s
            """;
        await using var first = await SeededAsync();
        await using var second = await SeededAsync();

        Assert.Equal(await first.ExecuteScalarAsync<string>(fingerprint), await second.ExecuteScalarAsync<string>(fingerprint));
    }
}
