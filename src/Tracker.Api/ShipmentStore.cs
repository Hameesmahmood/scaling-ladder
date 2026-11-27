using Dapper;
using Npgsql;

namespace Tracker.Api;

/// <summary>
/// All reads and writes of shipments, in hand-written SQL. Every method opens its own connection
/// from the data source, so whether that's a fresh physical connection or a pooled one is decided
/// in one place (<see cref="Database"/>), which is what step 2 of the ladder switches.
/// </summary>
public sealed class ShipmentStore(NpgsqlDataSource db)
{
    public const int EventsShown = 20;
    private long reads;

    /// <summary>Shipment lookups since the process started, whoever asked. Served at /stats.</summary>
    public long Reads => Interlocked.Read(ref reads);

    public async Task<Shipment?> GetAsync(string trackingNumber, CancellationToken ct)
    {
        Interlocked.Increment(ref reads);
        await using var connection = await db.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ShipmentRow>(new CommandDefinition("""
            SELECT id, tracking_number, customer_id, service, origin, destination, status, created_at, updated_at
            FROM shipments
            WHERE tracking_number = @trackingNumber
            """, new { trackingNumber }, cancellationToken: ct));
        if (row is null) return null;

        var events = await connection.QueryAsync<EventRow>(new CommandDefinition("""
            SELECT occurred_at, status, location
            FROM tracking_events
            WHERE shipment_id = @id
            ORDER BY occurred_at DESC
            LIMIT @limit
            """, new { id = row.Id, limit = EventsShown }, cancellationToken: ct));

        return new Shipment(row.TrackingNumber, row.CustomerId, row.Service, row.Origin, row.Destination,
            row.Status, Utc(row.CreatedAt), Utc(row.UpdatedAt),
            events.Select(e => new TrackingEvent(Utc(e.OccurredAt), e.Status, e.Location)).ToList());
    }

    /// <summary>A customer's shipments, newest first, after the cursor. Null if the customer doesn't exist.</summary>
    public async Task<ShipmentPage?> ListForCustomerAsync(long customerId, int limit, PageCursor? after, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM customers WHERE id = @customerId)", new { customerId }, cancellationToken: ct));
        if (!exists) return null;

        // Keyset pagination: (created_at, id) is unique and matches the step-1 index's order, so a page
        // costs the same however deep it is, unlike OFFSET.
        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition("""
            SELECT id, tracking_number, service, destination, status, created_at
            FROM shipments
            WHERE customer_id = @customerId
              AND (@afterCreated::timestamptz IS NULL OR (created_at, id) < (@afterCreated, @afterId))
            ORDER BY created_at DESC, id DESC
            LIMIT @take
            """, new { customerId, afterCreated = after?.CreatedAt, afterId = after?.Id ?? 0, take = limit + 1 }, cancellationToken: ct))).AsList();

        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? new PageCursor(Utc(page[^1].CreatedAt), page[^1].Id).Encode() : null;
        return new ShipmentPage(
            page.Select(r => new ShipmentSummary(r.TrackingNumber, r.Service, r.Destination, r.Status, Utc(r.CreatedAt))).ToList(),
            next);
    }

    /// <summary>Records an event and moves the shipment to its status, together. Null if the shipment doesn't exist.</summary>
    public async Task<TrackingEvent?> AddEventAsync(string trackingNumber, string status, string location, DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM shipments WHERE tracking_number = @trackingNumber FOR UPDATE",
            new { trackingNumber }, tx, cancellationToken: ct));
        if (id is null) return null;

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tracking_events (shipment_id, occurred_at, status, location) VALUES (@id, @occurredAt, @status, @location);
            UPDATE shipments SET status = @status, updated_at = @occurredAt WHERE id = @id;
            """, new { id, occurredAt, status, location }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new TrackingEvent(occurredAt, status, location);
    }

    // Npgsql reads timestamptz as a UTC DateTime, and Dapper maps constructor parameters by exact type,
    // so rows use DateTime and the public models get DateTimeOffset.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record ShipmentRow(long Id, string TrackingNumber, long CustomerId, string Service, string Origin,
        string Destination, string Status, DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record SummaryRow(long Id, string TrackingNumber, string Service, string Destination, string Status, DateTime CreatedAt);

    private sealed record EventRow(DateTime OccurredAt, string Status, string Location);
}
