using Dapper;
using Npgsql;

namespace Tracker.Tests;

/// <summary>Step 1, switched on and off: the same queries, planned with and without the indexes.</summary>
[Collection(nameof(InfrastructureCollection))]
public sealed class IndexTests(Infrastructure infra)
{
    private static readonly string[] Queries =
    [
        "SELECT id FROM shipments WHERE tracking_number = 'SL0000012300'",
        "SELECT occurred_at, status, location FROM tracking_events WHERE shipment_id = 12300 ORDER BY occurred_at DESC LIMIT 20",
        "SELECT id FROM shipments WHERE customer_id = 7 ORDER BY created_at DESC, id DESC LIMIT 21",
    ];

    private async Task<NpgsqlConnection> SeededAsync(bool indexes)
    {
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "1000"), ("shipments", "20000"));
        if (indexes) await infra.RunSqlFileAsync(database, "indexes.sql");
        var connection = new NpgsqlConnection(infra.ConnectionStringFor(database));
        await connection.OpenAsync();
        // Not a bare "ANALYZE": Dapper treats a one-word command as a stored procedure name.
        await connection.ExecuteAsync("ANALYZE customers, shipments, tracking_events");
        return connection;
    }

    private static async Task<string> PlanAsync(NpgsqlConnection db, string sql) =>
        string.Join("\n", await db.QueryAsync<string>("EXPLAIN (COSTS OFF) " + sql));

    [Fact]
    public async Task With_the_indexes_every_hot_query_is_an_index_scan()
    {
        await using var db = await SeededAsync(indexes: true);
        foreach (var sql in Queries)
        {
            var plan = await PlanAsync(db, sql);
            Assert.DoesNotContain("Seq Scan", plan);
            Assert.Contains("Index", plan);
        }
    }

    [Fact]
    public async Task Switched_off_every_hot_query_reads_the_whole_table()
    {
        await using var db = await SeededAsync(indexes: false);
        foreach (var sql in Queries)
            Assert.Contains("Seq Scan", await PlanAsync(db, sql));
    }
}
