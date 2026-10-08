using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Motiva.Tests.Http;

/// <summary>R2 HTTP regression tests: the blocked-admin data leak via a still-valid JWT (D01),
/// the child-entity If-Match race (D07) and the full replay contract of employee creation
/// including the Location header (D09).</summary>
[Collection("http")]
public sealed class HttpR2RegressionTests(MotivaHttpFixture fixture) : IAsyncLifetime
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

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object payload, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, url + " -> " + ((int)response.StatusCode) + ": " + await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task blocked_admin_get_of_foreign_operation_is_403_over_http()
    {
        var client = Client(_admin);

        // The active admin awards 5 to employee 7 — then the admin profile is blocked.
        await PostAsync(client, "/api/v1/employees", new { masterId = 7, tags = Array.Empty<string>() }, "d01-emp");
        var resourceId = (await PostAsync(client, "/api/v1/resources", new { code = "D01R", name = "star" }, "d01-res")).GetProperty("id").GetString();
        var campaignId = (await PostAsync(client, "/api/v1/campaigns", new
        {
            code = "D01H",
            name = "n",
            ownerMasterId = 1,
            startsAt = "2026-04-01T00:00:00Z",
            endsAt = "2026-11-30T20:00:00Z",
        }, "d01-camp")).GetProperty("id").GetString();
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/campaigns/" + campaignId + "/resources")
        {
            Content = JsonContent.Create(new { resourceIds = new[] { Guid.Parse(resourceId!) } }),
        })
        {
            put.Headers.Add("If-Match", "\"v1\"");
            Assert.True((await client.SendAsync(put)).IsSuccessStatusCode);
        }

        using (var publish = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/campaigns/" + campaignId)
        {
            Content = JsonContent.Create(new { status = "Published" }),
        })
        {
            publish.Headers.Add("If-Match", "\"v2\"");
            Assert.True((await client.SendAsync(publish)).IsSuccessStatusCode);
        }

        await PostAsync(client, "/api/v1/campaigns/" + campaignId + "/budget-allocations",
            new { resourceId = Guid.Parse(resourceId!), amount = (long)100, reason = "base", operationNumber = "D01-ALLOC" }, "d01-alloc");
        var operationId = (await PostAsync(client, "/api/v1/campaigns/" + campaignId + "/manual-awards",
            new { masterId = 7, resourceId = Guid.Parse(resourceId!), amount = (long)5, reason = "r", operationNumber = "D01-MA" }, "d01-award")).GetProperty("id").GetString();

        // Block the admin through the API; the SAME token identity keeps asking.
        var profile = await client.GetAsync("/api/v1/employees/1");
        var etag = profile.Headers.ETag!.Tag!;
        using (var block = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/employees/1")
        {
            Content = JsonContent.Create(new { isActive = false }),
        })
        {
            block.Headers.Add("If-Match", etag);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(block)).StatusCode);
        }

        // The R1 leak: this returned 200 with masterId=7. It must be a 403 without a body leak.
        var leaked = await client.GetAsync("/api/v1/operations/" + operationId);
        Assert.Equal(HttpStatusCode.Forbidden, leaked.StatusCode);
        var leakedBody = await leaked.Content.ReadAsStringAsync();
        Assert.DoesNotContain("masterId", leakedBody);
    }

    [Fact]
    public async Task concurrent_task_patch_with_one_if_match_yields_single_200()
    {
        var client = Client(_admin);
        var campaignId = (await PostAsync(client, "/api/v1/campaigns", new
        {
            code = "D07H",
            name = "n",
            ownerMasterId = 1,
            startsAt = "2026-04-01T00:00:00Z",
            endsAt = "2026-11-30T20:00:00Z",
        }, "d07-camp")).GetProperty("id").GetString();
        var streamId = (await PostAsync(client, "/api/v1/campaigns/" + campaignId + "/streams",
            new { code = "S1", name = "Stream" }, "d07-stream")).GetProperty("id").GetString();
        var taskId = (await PostAsync(client, "/api/v1/streams/" + streamId + "/tasks",
            new { code = "T1", name = "Task", goal = 5, period = "Month", streamPoints = 1 }, "d07-task")).GetProperty("id").GetString();

        var current = await client.GetAsync("/api/v1/tasks/" + taskId);
        var etag = current.Headers.ETag!.Tag!;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await barrier.Task;
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/tasks/" + taskId)
            {
                Content = JsonContent.Create(new { name = "name-" + i }),
            };
            request.Headers.Add("If-Match", etag);
            return await client.SendAsync(request);
        })).ToArray();
        barrier.SetResult();
        var responses = await Task.WhenAll(tasks);
        Assert.Equal(1, responses.Count(r => (int)r.StatusCode == 200));
        Assert.Equal(7, responses.Count(r => (int)r.StatusCode == 412));

        // The final state is confirmed by a FRESH read: version exactly 2 (§4.4).
        var after = await client.GetAsync("/api/v1/tasks/" + taskId);
        Assert.Equal("\"v2\"", after.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task employee_creation_replay_keeps_location_over_http()
    {
        var client = Client(_admin);
        const int masterId = 4242;
        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employees")
        {
            Content = JsonContent.Create(new { masterId, tags = Array.Empty<string>() }),
        })
        {
            firstRequest.Headers.Add("Idempotency-Key", "emp-http-key");
            var first = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal("/api/v1/employees/" + masterId, first.Headers.Location!.ToString());
        }

        // The replay keeps the FULL original contract: 201, same body AND the Location header.
        using (var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employees")
        {
            Content = JsonContent.Create(new { masterId, tags = Array.Empty<string>() }),
        })
        {
            replayRequest.Headers.Add("Idempotency-Key", "emp-http-key");
            var replay = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.NotNull(replay.Headers.Location);
            Assert.Equal("/api/v1/employees/" + masterId, replay.Headers.Location!.ToString());
        }
    }
}
