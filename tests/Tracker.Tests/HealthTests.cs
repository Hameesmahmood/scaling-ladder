using System.Net;

namespace Tracker.Tests;

[Collection(nameof(InfrastructureCollection))]
public sealed class HealthTests(Infrastructure infra)
{
    private Api ApiWith(string redis) => new(new Dictionary<string, string?>
    {
        ["Tracker:Database"] = infra.PostgresUrl,
        ["Tracker:Redis"] = redis,
    });

    [Fact]
    public async Task Is_ready_when_Postgres_and_Redis_answer()
    {
        await using var api = ApiWith(infra.RedisUrl);
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);
    }

    [Fact]
    public async Task Is_alive_but_not_ready_when_Redis_is_unreachable_and_says_which()
    {
        await using var api = ApiWith("localhost:1,connectTimeout=200");
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        var ready = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("redis", await ready.Content.ReadAsStringAsync());
    }
}
