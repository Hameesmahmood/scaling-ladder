namespace Tracker.Api;

public sealed record TrackingEvent(DateTimeOffset OccurredAt, string Status, string Location);

public sealed record Shipment(
    string TrackingNumber,
    long CustomerId,
    string Service,
    string Origin,
    string Destination,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TrackingEvent> Events);

public sealed record ShipmentSummary(
    string TrackingNumber,
    string Service,
    string Destination,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record ShipmentPage(IReadOnlyList<ShipmentSummary> Shipments, string? NextCursor);

public sealed record NewTrackingEvent(string? Status, string? Location, DateTimeOffset? OccurredAt);

public static class Statuses
{
    public static readonly string[] All = ["label_created", "in_transit", "out_for_delivery", "delivered", "exception"];
}
