using Npgsql;
using StackExchange.Redis;
using Tracker.Api;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection("Tracker").Get<TrackerSettings>() ?? new TrackerSettings();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => Database.CreateDataSource(settings));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(settings.Redis);
    // Start even if Redis is down; /readyz reports it, and the cache steps fall back to Postgres.
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (NpgsqlDataSource db, IConnectionMultiplexer redis, CancellationToken ct) =>
{
    var failures = new List<string>();
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var command = db.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(timeout.Token);
    }
    catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException)
    {
        failures.Add("postgres");
    }

    try
    {
        await redis.GetDatabase().PingAsync();
    }
    catch (Exception e) when (e is RedisException or TimeoutException)
    {
        failures.Add("redis");
    }

    return failures.Count == 0
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(statusCode: 503, title: "Not ready", detail: $"Unreachable: {string.Join(", ", failures)}.");
});

app.Run();

/// <summary>Visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
