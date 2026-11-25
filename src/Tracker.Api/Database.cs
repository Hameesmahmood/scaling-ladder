using Npgsql;

namespace Tracker.Api;

public static class Database
{
    /// <summary>
    /// The one place connections come from. With pooling off, Npgsql opens a fresh physical
    /// connection (TCP, authentication, a new server process) for every OpenConnectionAsync and
    /// closes it on dispose, which is what the ladder's baseline measures.
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(TrackerSettings settings)
    {
        var connection = new NpgsqlConnectionStringBuilder(settings.Database)
        {
            Pooling = settings.Pooling,
            MaxPoolSize = settings.MaxPoolSize,
            // Queue for a pooled connection for at most this long before failing the request.
            Timeout = 5,
            CommandTimeout = 30,
        };
        // Dapper maps snake_case columns (tracking_number) onto PascalCase properties (TrackingNumber).
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        return new NpgsqlDataSourceBuilder(connection.ConnectionString).Build();
    }
}
