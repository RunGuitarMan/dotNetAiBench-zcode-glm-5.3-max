using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace Motiva.Tests.Http;

[Collection("http")]
public sealed class AuthNegativeTests(MotivaHttpFixture fixture) : IAsyncLifetime
{
    private Guid _company = Guid.Empty;

    public async Task InitializeAsync()
    {
        await fixture.InitializeDatabaseAsync();
        _company = await fixture.NewCompanyAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int Status, string Body)> CallAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/resources");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await fixture.CreateClient().SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task valid_employee_token_passes()
    {
        // The identity must map to an ACTIVE profile (§3.3): create employee 123 first.
        var admin = TestTokens.Issue(_company, masterId: 1, subject: "admin", admin: true);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employees")
        {
            Content = JsonContent.Create(new { masterId = 123, tags = Array.Empty<string>() }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        create.Headers.Add("Idempotency-Key", "auth-emp-123");
        var created = await fixture.CreateClient().SendAsync(create);
        Assert.True(created.IsSuccessStatusCode, (int)created.StatusCode + "");

        var token = TestTokens.Issue(_company, masterId: 123, subject: "employee-123");
        var (status, _) = await CallAsync(token);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task user_token_without_profile_gets_403()
    {
        // A verified identity without an active business profile is not an active initiator:
        // catalog reads are closed (B05.1, §3.3) — the same rule that closes blocked profiles.
        var token = TestTokens.Issue(_company, masterId: 7777, subject: "ghost");
        var (status, body) = await CallAsync(token);
        Assert.Equal(403, status);
        Assert.Contains("authz.employee-not-active", body);
    }

    [Fact]
    public async Task wrong_signature_is_401_with_problem_details()
    {
        var (status, body) = await CallAsync(TestTokens.SignedWithOtherKey(_company));
        Assert.Equal(401, status);
        Assert.Contains("auth.invalid-token", body);
        Assert.Contains("traceId", body);
    }

    [Fact]
    public async Task wrong_issuer_and_audience_are_401()
    {
        var badIssuer = await CallAsync(TestTokens.Issue(_company, masterId: 1, issuer: "someone-else"));
        Assert.Equal(401, badIssuer.Status);
        var badAudience = await CallAsync(TestTokens.Issue(_company, masterId: 1, audience: "other-api"));
        Assert.Equal(401, badAudience.Status);
    }

    [Fact]
    public async Task expired_token_is_401()
    {
        var expired = TestTokens.Issue(_company, masterId: 1, expires: DateTime.UtcNow.AddMinutes(-5));
        var (status, _) = await CallAsync(expired);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task user_without_masterid_is_401()
    {
        var token = TestTokens.Issue(_company, masterId: null);
        var (status, _) = await CallAsync(token);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task service_with_masterid_is_401()
    {
        var token = TestTokens.Issue(_company, actorType: "service", masterId: 5, subject: "svc");
        var (status, _) = await CallAsync(token);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task duplicated_identity_claims_are_401()
    {
        var duplicateMaster = await CallAsync(TestTokens.Issue(_company, masterId: 5, duplicateMasterId: true));
        Assert.Equal(401, duplicateMaster.Status);
        var duplicateCompany = await CallAsync(TestTokens.Issue(_company, masterId: 5, duplicateCompanyId: true));
        Assert.Equal(401, duplicateCompany.Status);
    }

    [Fact]
    public async Task missing_token_is_401()
    {
        var (status, body) = await CallAsync(string.Empty);
        Assert.Equal(401, status);
        Assert.Contains("auth.invalid-token", body);
    }
}

[Collection("http")]
public sealed class ContractTests(MotivaHttpFixture fixture) : IAsyncLifetime
{
    private Guid _company = Guid.Empty;
    private string _admin = string.Empty;
    private string _employee = string.Empty;
    private string _otherCompanyAdmin = string.Empty;
    private Guid _otherCompany;

    public async Task InitializeAsync()
    {
        await fixture.InitializeDatabaseAsync();
        _company = await fixture.NewCompanyAsync();
        _otherCompany = await fixture.NewCompanyAsync();
        _admin = TestTokens.Issue(_company, masterId: 1, subject: "admin", admin: true);
        _employee = TestTokens.Issue(_company, masterId: 123, subject: "employee-123");
        _otherCompanyAdmin = TestTokens.Issue(_otherCompany, masterId: 1, subject: "admin-other", admin: true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }


    [Fact]
    public async Task employee_create_returns_201_with_location_and_wallet_is_created()
    {
        var client = Client(_admin);
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employees")
        {
            Content = JsonContent.Create(new { masterId = 321, tags = Array.Empty<string>() }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith("/api/v1/employees/321", response.Headers.Location!.ToString());

        var employee321 = TestTokens.Issue(_company, masterId: 321, subject: "employee-321");
        var wallet = await Client(employee321).GetFromJsonAsync<WalletDto>("/api/v1/me/wallet");
        Assert.NotNull(wallet);
        Assert.DoesNotContain(wallet.Balances, b => b.Balance != 0);

        // Same Idempotency-Key: the stored response, same body.
        using var repeatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employees")
        {
            Content = JsonContent.Create(new { masterId = 321 }),
        };
        repeatRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        var repeat = await client.SendAsync(repeatRequest);
        Assert.Equal(201, (int)repeat.StatusCode);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task etag_cycle_428_and_412_on_employee_patch()
    {
        var client = Client(_admin);
        await CreateAsync(client, "/api/v1/employees", new { masterId = 500 });

        var missing = await client.PatchAsJsonAsync("/api/v1/employees/500", new { isActive = false });
        Assert.Equal(428, (int)missing.StatusCode);

        var stale = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/employees/500")
        {
            Content = JsonContent.Create(new { isActive = false }),
            Headers = { { "If-Match", "\"v999\"" } },
        });
        Assert.Equal(412, (int)stale.StatusCode);

        var current = await client.GetAsync("/api/v1/employees/500");
        var etag = current.Headers.ETag!.Tag;
        var ok = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/employees/500")
        {
            Content = JsonContent.Create(new { isActive = false }),
            Headers = { { "If-Match", etag } },
        });
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.NotNull(ok.Headers.ETag);
    }

    [Fact]
    public async Task cross_company_objects_are_404_e12()
    {
        var admin = Client(_admin);
        var created = await CreateAsync(admin, "/api/v1/resources", new { code = "CROSS", name = "x" });
        var resourceId = (await created.Content.ReadFromJsonAsync<ResourceDto>())!.Id;

        // The other company's admin sees nothing: 404, never the object itself.
        var foreign = await Client(_otherCompanyAdmin).GetAsync("/api/v1/resources/" + resourceId);
        Assert.Equal(404, (int)foreign.StatusCode);

        // A foreign employee wallet is 404 for a foreign admin; own masterId works in own company.
        await CreateAsync(admin, "/api/v1/employees", new { masterId = 123 });
        var otherCompanyEmployee = TestTokens.Issue(_otherCompany, masterId: 123, subject: "employee-123-o");
        await CreateAsync(Client(_otherCompanyAdmin), "/api/v1/employees", new { masterId = 123 });
        var own = await Client(otherCompanyEmployee).GetAsync("/api/v1/employees/123");
        Assert.Equal(200, (int)own.StatusCode);
    }

    [Fact]
    public async Task declined_economic_operation_is_201_with_problem_free_body()
    {
        var admin = Client(_admin);
        await CreateAsync(admin, "/api/v1/employees", new { masterId = 700 });
        var resource = (await (await CreateAsync(admin, "/api/v1/resources", new { code = "DL", name = "x" })).Content.ReadFromJsonAsync<ResourceDto>())!;
        var system = (await (await CreateAsync(
            admin,
            "/api/v1/purchase-systems",
            new { code = "SHOP", name = "shop", acceptedResourceIds = new[] { resource.Id } })).Content.ReadFromJsonAsync<PurchaseSystemDto>())!;
        var employee = Client(TestTokens.Issue(_company, masterId: 700, subject: "e700"));

        var declined = await employee.PostAsJsonAsync(
            "/api/v1/spends",
            new { purchaseSystemId = system.Id, resourceId = resource.Id, amount = 7, operationNumber = "S-1" });
        Assert.Equal(201, (int)declined.StatusCode);
        var body = await declined.Content.ReadFromJsonAsync<OperationDto>();
        Assert.Equal("Declined", body!.Result);
        Assert.Equal("InsufficientFunds", body.RefusalCode);

        // Replay of the same number: byte-identical stored body.
        var replay = await employee.PostAsJsonAsync(
            "/api/v1/spends",
            new { purchaseSystemId = system.Id, resourceId = resource.Id, amount = 7, operationNumber = "S-1" });
        Assert.Equal(await declined.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());

        // The number with different essential data: 409 conflict.business-number.
        var conflict = await employee.PostAsJsonAsync(
            "/api/v1/spends",
            new { purchaseSystemId = system.Id, resourceId = resource.Id, amount = 8, operationNumber = "S-1" });
        Assert.Equal(409, (int)conflict.StatusCode);
        Assert.Contains("conflict.business-number", await conflict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task list_envelope_and_append_only_cursor_on_progress_events()
    {
        var admin = Client(_admin);
        var progress = TestTokens.Issue(_company, actorType: "service", masterId: null, subject: "progress-http");
        await CreateAsync(admin, "/api/v1/employees", new { masterId = 900 });
        var resource = (await (await CreateAsync(admin, "/api/v1/resources", new { code = "PG", name = "x" })).Content.ReadFromJsonAsync<ResourceDto>())!;
        var campaign = (await (await CreateAsync(
            admin,
            "/api/v1/campaigns",
            new
            {
                code = "PAGE",
                name = "n",
                ownerMasterId = 1,
                startsAt = "2026-10-01T00:00:00Z",
                endsAt = "2026-12-31T20:00:00Z",
            })).Content.ReadFromJsonAsync<CampaignDto>())!;
        var put = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/v1/campaigns/" + campaign.Id + "/resources")
        {
            Content = JsonContent.Create(new { resourceIds = new[] { resource.Id } }),
            Headers = { { "If-Match", "\"v1\"" } },
        });
        Assert.Equal(200, (int)put.StatusCode);
        var stream = (await (await CreateAsync(
            admin,
            "/api/v1/campaigns/" + campaign.Id + "/streams",
            new { code = "S", name = "s" })).Content.ReadFromJsonAsync<StreamDto>())!;
        var task = (await (await CreateAsync(
            admin,
            "/api/v1/streams/" + stream.Id + "/tasks",
            new { code = "T", name = "t", goal = 50, period = "Day", streamPoints = 1, rewardItems = Array.Empty<object>() }))
            .Content.ReadFromJsonAsync<TaskDto>())!;
        var publish = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/campaigns/" + campaign.Id)
        {
            Content = JsonContent.Create(new { status = "Published" }),
            Headers = { { "If-Match", "\"v4\"" } },
        });
        Assert.Equal(200, (int)publish.StatusCode);
        await CreateAsync(admin, "/api/v1/integration-grants", new { subject = "progress-http", kind = "Progress", campaignId = campaign.Id });

        var progressClient = Client(progress);
        for (var i = 0; i < 3; i++)
        {
            var accepted = await progressClient.PostAsJsonAsync(
                "/api/v1/progress-events",
                new { eventNumber = "P" + i, masterId = 900, taskId = task.Id, delta = 1 });
            Assert.Equal(201, (int)accepted.StatusCode);
        }

        var employee = Client(TestTokens.Issue(_company, masterId: 900, subject: "e900"));
        var page1 = await employee.GetFromJsonAsync<PageDto<EventDto>>("/api/v1/me/progress-events?limit=2");
        Assert.Equal(2, page1!.Items.Count);
        Assert.NotNull(page1.NextCursor);
        // A concurrent insert between pages must not affect the keyset page (T04).
        await progressClient.PostAsJsonAsync(
            "/api/v1/progress-events",
            new { eventNumber = "PX", masterId = 900, taskId = task.Id, delta = 1 });
        var page2 = await employee.GetFromJsonAsync<PageDto<EventDto>>("/api/v1/me/progress-events?limit=2&cursor=" + page1.NextCursor);
        // The keyset page returns the two later events: nothing skipped, nothing duplicated.
        var ids = page1!.Items.Select(e => e.Id).Concat(page2!.Items.Select(e => e.Id)).ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Null(page2!.NextCursor);
    }

    [Fact]
    public async Task draft_campaigns_are_invisible_to_employees()
    {
        var admin = Client(_admin);
        var draft = await (await CreateAsync(
            admin,
            "/api/v1/campaigns",
            new { code = "SECRET", name = "n", ownerMasterId = 1, startsAt = "2026-10-01T00:00:00Z", endsAt = "2026-12-31T20:00:00Z" }))
            .Content.ReadFromJsonAsync<CampaignDto>();
        // An ACTIVE employee profile backs the reading identity (§3.3).
        await CreateAsync(admin, "/api/v1/employees", new { masterId = 123, tags = Array.Empty<string>() });
        var employee = Client(TestTokens.Issue(_company, masterId: 123, subject: "employee-123"));
        var visible = await employee.GetFromJsonAsync<PageDto<CampaignDto>>("/api/v1/campaigns");
        Assert.DoesNotContain(visible!.Items, c => c.Id == draft!.Id);
        var direct = await employee.GetAsync("/api/v1/campaigns/" + draft!.Id);
        Assert.Equal(404, (int)direct.StatusCode);
        var owner = await admin.GetAsync("/api/v1/campaigns/" + draft.Id);
        Assert.Equal(200, (int)owner.StatusCode);
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

    private sealed record PurchaseSystemDto(Guid Id, string Code, string Name, string Status, Guid[] AcceptedResourceIds);

    private sealed record WalletDto(int MasterId, WalletBalanceDto[] Balances, DateTimeOffset GeneratedAtUtc);

    private sealed record WalletBalanceDto(Guid ResourceId, string ResourceCode, long Balance, string ResourceStatus);

    private sealed record CampaignDto(Guid Id, string Code, string Name, string Status);

    private sealed record StreamDto(Guid Id, Guid CampaignId, string Code, string Name);

    private sealed record TaskDto(Guid Id, Guid StreamId, string Code);

    private sealed record OperationDto(
        Guid Id, string Kind, string Result, string? RefusalCode, int? MasterId, string SourceOperationNumber);

    private sealed record EventDto(Guid Id, string EventNumber, string Result);

    private sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
}
