using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Tracker.Tests;

[Collection(nameof(InfrastructureCollection))]
public sealed class ShipmentApiTests(Infrastructure infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private string connectionString = "";
    private Api api = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        var database = await infra.CreateDatabaseAsync();
        await infra.RunSqlFileAsync(database, "seed.sql", ("customers", "50"), ("shipments", "2000"));
        connectionString = infra.ConnectionStringFor(database);
        api = new Api(new Dictionary<string, string?> { ["Tracker:Database"] = connectionString, ["Tracker:Redis"] = infra.RedisUrl });
        client = api.CreateClient();
    }

    public async Task DisposeAsync() => await api.DisposeAsync();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    [Fact]
    public async Task Returns_a_shipment_with_its_20_most_recent_events_newest_first()
    {
        // The shipment with the most events, plus 5 new ones, so the 20-event cap is exercised.
        await using var db = new NpgsqlConnection(connectionString);
        var (number, total) = await db.QuerySingleAsync<(string, long)>("""
            SELECT s.tracking_number, count(*) FROM shipments s JOIN tracking_events e ON e.shipment_id = s.id
            GROUP BY s.tracking_number ORDER BY count(*) DESC, s.tracking_number LIMIT 1
            """);
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync($"/v1/shipments/{number}/events", new { status = "in_transit", location = $"Hub {i}", occurredAt = DateTimeOffset.UtcNow.AddMinutes(i) });

        var shipment = await GetJson($"/v1/shipments/{number}");

        Assert.Equal(number, shipment.GetProperty("tracking_number").GetString());
        var events = shipment.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("occurred_at").GetDateTimeOffset()).ToList();
        Assert.Equal(Math.Min(20, (int)total + 5), events.Count);
        Assert.Equal(events.OrderByDescending(e => e), events);
        Assert.Equal("Hub 4", shipment.GetProperty("events")[0].GetProperty("location").GetString());
    }

    [Fact]
    public async Task Answers_404_with_problem_details_for_an_unknown_tracking_number()
    {
        var response = await client.GetAsync("/v1/shipments/SL9999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Pages_through_a_customers_shipments_newest_first_with_none_repeated_or_missed()
    {
        await using var db = new NpgsqlConnection(connectionString);
        var expected = (await db.QueryAsync<string>(
            "SELECT tracking_number FROM shipments WHERE customer_id = 7 ORDER BY created_at DESC, id DESC")).ToList();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await GetJson($"/v1/customers/7/shipments?limit=7{(cursor is null ? "" : $"&cursor={cursor}")}");
            seen.AddRange(page.GetProperty("shipments").EnumerateArray().Select(s => s.GetProperty("tracking_number").GetString()!));
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next_cursor").GetString();
        } while (cursor is not null);

        Assert.True(expected.Count > 14);
        Assert.Equal(expected, seen);
    }

    [Theory]
    [InlineData("/v1/customers/7/shipments?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("/v1/customers/7/shipments?limit=101", HttpStatusCode.BadRequest)]
    [InlineData("/v1/customers/7/shipments?cursor=not-a-cursor", HttpStatusCode.BadRequest)]
    [InlineData("/v1/customers/999999/shipments", HttpStatusCode.NotFound)]
    public async Task Rejects_bad_list_requests(string url, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Records_an_event_and_moves_the_shipment_to_its_status()
    {
        var response = await client.PostAsJsonAsync("/v1/shipments/SL0000000100/events", new { status = "out_for_delivery", location = "Dubai depot" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var shipment = await GetJson("/v1/shipments/SL0000000100");
        Assert.Equal("out_for_delivery", shipment.GetProperty("status").GetString());
        Assert.Equal("Dubai depot", shipment.GetProperty("events")[0].GetProperty("location").GetString());
    }

    [Theory]
    [InlineData("SL0000000100", """{"status":"lost_in_space","location":"Mars"}""", HttpStatusCode.BadRequest)]
    [InlineData("SL0000000100", """{"status":"delivered"}""", HttpStatusCode.BadRequest)]
    [InlineData("SL9999999999", """{"status":"delivered","location":"Nowhere"}""", HttpStatusCode.NotFound)]
    public async Task Rejects_bad_events(string number, string body, HttpStatusCode expected)
    {
        var response = await client.PostAsync($"/v1/shipments/{number}/events", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(expected, response.StatusCode);
    }
}
