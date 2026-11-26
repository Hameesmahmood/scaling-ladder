using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Tracker.Tests;

/// <summary>Real Postgres and Redis, started once and shared by every test in the collection.</summary>
public sealed class Infrastructure : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithUsername("tracker").WithPassword("tracker").WithDatabase("tracker")
        .Build();

    public RedisContainer Redis { get; } = new RedisBuilder("redis:8-alpine").Build();

    public string PostgresUrl => Postgres.GetConnectionString();

    public string RedisUrl => Redis.GetConnectionString();

    public async Task InitializeAsync() => await Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync());

    public async Task DisposeAsync() => await Task.WhenAll(Postgres.DisposeAsync().AsTask(), Redis.DisposeAsync().AsTask());

    /// <summary>Runs a SQL file from db/ through psql inside the container, as scripts/db.sh does.</summary>
    public async Task RunSqlFileAsync(string database, string file, params (string Name, string Value)[] variables)
    {
        var sql = await File.ReadAllBytesAsync(Path.Combine(RepoRoot, "db", file));
        var target = $"/tmp/{Guid.NewGuid():N}.sql";
        await Postgres.CopyAsync(sql, target);
        var args = new List<string> { "psql", "-v", "ON_ERROR_STOP=1", "-q", "-U", "tracker", "-d", database, "-f", target };
        foreach (var (name, value) in variables) args.InsertRange(1, ["-v", $"{name}={value}"]);
        var result = await Postgres.ExecAsync(args);
        if (result.ExitCode != 0) throw new InvalidOperationException($"psql failed on {file}: {result.Stderr}");
    }

    /// <summary>A fresh database with the baseline schema, so tests never share rows.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        var result = await Postgres.ExecAsync(["psql", "-U", "tracker", "-d", "tracker", "-c", $"CREATE DATABASE {name}"]);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Stderr);
        await RunSqlFileAsync(name, "schema.sql");
        return name;
    }

    /// <summary>Empties Redis. Tests that use the cache call it first: every test database has the same tracking numbers.</summary>
    public async Task FlushRedisAsync()
    {
        var result = await Redis.ExecAsync(["redis-cli", "FLUSHALL"]);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Stderr);
    }

    public string ConnectionStringFor(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(PostgresUrl) { Database = database }.ConnectionString;

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScalingLadder.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Can't find the repository root.");
    }
}

[CollectionDefinition(nameof(InfrastructureCollection))]
public sealed class InfrastructureCollection : ICollectionFixture<Infrastructure>;

/// <summary>The API, pointed at the test containers, with any setting overridden per test.</summary>
public sealed class Api(IDictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
    }
}
