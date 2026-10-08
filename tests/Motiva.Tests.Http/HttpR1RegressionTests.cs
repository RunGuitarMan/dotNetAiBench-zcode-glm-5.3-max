using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace Motiva.Tests.Http;

/// <summary>R1 HTTP regression tests: JWT identity contract (D02), atomic If-Match (D07),
/// strict UTC-offset dates and binding errors (D09), catalog pagination and Actor DTO casing.</summary>
[Collection("http")]
public sealed class HttpR1RegressionTests(MotivaHttpFixture fixture) : IAsyncLifetime
{
    private Guid _company = Guid.Empty;
    private string _admin = string.Empty;

    public async Task InitializeAsync()
    {
        await fixture.InitializeDatabaseAsync();
        _company = await fixture.NewCompanyAsync();
        _admin = TestTokens.Issue(_company, masterId: 1, subject: "admin", admin: true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task jwt_without_subject_is_401()
    {
        var token = TestTokens.Issue(_company, masterId: 1, subject: "");
        var response = await Client(token).GetAsync("/api/v1/resources");
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task jwt_user_without_role_is_401()
    {
        // A user token without any role claim is not a valid identity (03_AUTH).
        var token = TestTokens.Issue(_company, masterId: 1, subject: "no-role", roles: false);
        var response = await Client(token).GetAsync("/api/v1/me/wallet");
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task jwt_service_with_admin_role_is_401()
    {
        // A service actor never carries roles; the combination must be rejected outright.
        var token = TestTokens.Issue(_company, actorType: "service", masterId: null, subject: "svc", admin: true, forceRoleForService: true);
        var response = await Client(token).GetAsync("/api/v1/resources");
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task concurrent_same_if_match_patch_yields_single_200()
    {
        var client = Client(_admin);
        await CreateAsync(client, "/api/v1/employees", new { masterId = 777 });
        for (var round = 0; round < 4; round++)
        {
            var current = await client.GetAsync("/api/v1/employees/777");
            var etag = current.Headers.ETag!.Tag!;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                await barrier.Task;
                using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/employees/777")
                {
                    Content = JsonContent.Create(new { isActive = true }),
                };
                request.Headers.Add("If-Match", etag);
                return await client.SendAsync(request);
            })).ToArray();
            barrier.SetResult();
            var responses = await Task.WhenAll(tasks);
            var ok = responses.Count(r => (int)r.StatusCode == 200);
            var preconditionFailed = responses.Count(r => (int)r.StatusCode == 412);
            // Exactly one transition succeeds per version; the rest get 412 (T04, T06).
            Assert.Equal(1, ok);
            Assert.Equal(responses.Length - 1, preconditionFailed);
        }
    }

    [Fact]
    public async Task campaign_dates_without_utc_offset_are_400()
    {
        var client = Client(_admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/campaigns")
        {
            Content = JsonContent.Create(new
            {
                code = "NOOFFSET",
                name = "n",
                ownerMasterId = 1,
                startsAt = "2026-10-01T00:00:00", // local wall-clock: rejected (T05)
                endsAt = "2026-12-31T20:00:00Z",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(400, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation.failed", body);
        Assert.Contains("traceId", body);
    }

    [Fact]
    public async Task campaign_date_with_nonzero_offset_is_accepted_and_normalized_to_utc()
    {
        var client = Client(_admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/campaigns")
        {
            Content = JsonContent.Create(new
            {
                code = "WITHOFFSET",
                name = "n",
                ownerMasterId = 1,
                startsAt = "2026-10-01T00:00:00+03:00",
                endsAt = "2026-12-31T20:00:00Z",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        var campaign = await response.Content.ReadFromJsonAsync<CampaignDto>();
        // 00:00+03:00 == 21:00Z of the previous day: normalized UTC on the way out.
        Assert.Equal("2026-09-30T21:00:00", campaign!.StartsAt[..19]);
    }

    [Fact]
    public async Task invalid_enum_and_non_integer_amount_are_contract_400s()
    {
        var client = Client(_admin);
        using var resourceRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resources")
        {
            Content = JsonContent.Create(new { code = "ENUM1", name = "x" }),
        };
        resourceRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var resource = await (await client.SendAsync(resourceRequest)).Content.ReadFromJsonAsync<ResourceDto>();

        // Invalid enum in a query: routed validation failure, not 500.
        var badFilter = await client.GetAsync("/api/v1/resources?status=NotAStatus");
        Assert.Equal(400, (int)badFilter.StatusCode);
        Assert.Contains("validation.failed", await badFilter.Content.ReadAsStringAsync());

        // Non-integer amount: malformed payload -> 400 ProblemDetails with code/traceId.
        using var spend = new HttpRequestMessage(HttpMethod.Post, "/api/v1/spends")
        {
            Content = new StringContent(
                """{"purchaseSystemId":"00000000-0000-0000-0000-000000000000","resourceId":"00000000-0000-0000-0000-000000000000","amount":1.5,"operationNumber":"X"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        var spendResponse = await client.SendAsync(spend);
        Assert.Equal(400, (int)spendResponse.StatusCode);
        Assert.Contains("validation.failed", await spendResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task catalog_pagination_reaches_all_items()
    {
        var client = Client(_admin);
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(client, "/api/v1/resources", new { code = "PAGE" + i, name = "x" });
        }

        var seen = new List<Guid>();
        string? cursor = null;
        for (var pages = 0; pages < 5; pages++)
        {
            var page = await client.GetFromJsonAsync<PageDto<ResourceDto>>("/api/v1/resources?limit=1" + (cursor is null ? "" : "&cursor=" + cursor));
            seen.AddRange(page!.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        // Sequential paging must reach every item exactly once (T04).
        Assert.Equal(3, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task actor_dto_uses_lowercase_enums()
    {
        var client = Client(_admin);
        var page = await client.GetFromJsonAsync<PageDto<AuditRecordDto>>("/api/v1/audit-records?limit=5");
        Assert.NotNull(page);
        if (page.Items.Count > 0)
        {
            // OpenAPI Actor.actorType: [user, service] — lowercase wire values.
            Assert.Contains(page.Items, item => item.Actor.ActorType is "user" or "service");
            Assert.DoesNotContain(page.Items, item => item.Actor.ActorType is "User" or "Service");
        }
    }

    [Fact]
    public async Task replay_of_create_keeps_location_header()
    {
        var client = Client(_admin);
        var key = Guid.NewGuid().ToString("N");
        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resources")
        {
            Content = JsonContent.Create(new { code = "REPLAYLOC", name = "x" }),
        };
        first.Headers.Add("Idempotency-Key", key);
        var firstResponse = await client.SendAsync(first);
        Assert.Equal(201, (int)firstResponse.StatusCode);
        Assert.NotNull(firstResponse.Headers.Location);

        using var repeat = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resources")
        {
            Content = JsonContent.Create(new { code = "REPLAYLOC", name = "x" }),
        };
        repeat.Headers.Add("Idempotency-Key", key);
        var repeatResponse = await client.SendAsync(repeat);
        Assert.Equal(201, (int)repeatResponse.StatusCode);
        Assert.Equal(firstResponse.Headers.Location, repeatResponse.Headers.Location);
        Assert.Equal(await firstResponse.Content.ReadAsStringAsync(), await repeatResponse.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string url, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private sealed record ResourceDto(Guid Id, string Code);

    private sealed record CampaignDto(Guid Id, string Code, string Name, string Status, string StartsAt);

    private sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);

    private sealed record ActorDto(string ActorType, int? MasterId, string? Subject);

    private sealed record AuditRecordDto(Guid Id, ActorDto Actor, string Action);
}
