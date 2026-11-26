namespace Tracker.Api;

public static class ShipmentEndpoints
{
    public static void MapShipmentEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/shipments/{trackingNumber}", async (string trackingNumber, ShipmentCache cache, HttpContext http, CancellationToken ct) =>
        {
            var (shipment, result) = await cache.GetAsync(trackingNumber, ct);
            // Says where the answer came from: hit, miss, off or unavailable. The tests read it.
            http.Response.Headers["X-Cache"] = result.ToString().ToLowerInvariant();
            return shipment is not null ? Results.Ok(shipment) : NotFound($"No shipment has tracking number {trackingNumber}.");
        });

        v1.MapGet("/customers/{customerId:long}/shipments", async (long customerId, int? limit, string? cursor, ShipmentStore store, CancellationToken ct) =>
        {
            var take = limit ?? 20;
            if (take is < 1 or > 100) return BadRequest("limit must be between 1 and 100.");
            if (!PageCursor.TryDecode(cursor, out var after)) return BadRequest("cursor isn't one this API issued.");

            return await store.ListForCustomerAsync(customerId, take, after, ct) is { } page
                ? Results.Ok(page)
                : NotFound($"No customer has id {customerId}.");
        });

        v1.MapPost("/shipments/{trackingNumber}/events", async (string trackingNumber, NewTrackingEvent body, ShipmentStore store, ShipmentCache cache, CancellationToken ct) =>
        {
            if (body.Status is null || !Statuses.All.Contains(body.Status))
                return BadRequest($"status must be one of: {string.Join(", ", Statuses.All)}.");
            if (string.IsNullOrWhiteSpace(body.Location) || body.Location.Length > 100)
                return BadRequest("location is required, up to 100 characters.");

            var occurredAt = body.OccurredAt ?? DateTimeOffset.UtcNow;
            var created = await store.AddEventAsync(trackingNumber, body.Status, body.Location.Trim(), occurredAt, ct);
            if (created is null) return NotFound($"No shipment has tracking number {trackingNumber}.");
            await cache.InvalidateAsync(trackingNumber);

            return Results.Created($"/v1/shipments/{trackingNumber}", created);
        });
    }

    private static IResult NotFound(string detail) => Results.Problem(statusCode: 404, title: "Not found", detail: detail);

    private static IResult BadRequest(string detail) => Results.Problem(statusCode: 400, title: "Bad request", detail: detail);
}
