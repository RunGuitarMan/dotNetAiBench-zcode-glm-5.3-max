using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace Motiva.Tests.Http;

/// <summary>Date regression of the R2 sweep: a "Z"-suffixed timestamp must mean UTC on ANY
/// machine — a parser that reads the machine's timezone shifts the instant by the local offset
/// (observed: 2026-01-01T00:00:00Z parsed as 2025-12-31T21:00:00+00 on a UTC+3 host) and
/// wrongly rejects season-spanning windows (D09 family, T05).</summary>
[Collection("http")]
public sealed class StrictDatesZuluTests(MotivaHttpFixture fixture) : IAsyncLifetime
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

    [Fact]
    public void zulu_timestamps_parse_as_utc_regardless_of_machine_zone()
    {
        var parsed = Motiva.Api.Infrastructure.StrictDates.ParseRequiredUtc("2026-01-01T00:00:00Z", "startsAt");
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), parsed);
        var offsetForm = Motiva.Api.Infrastructure.StrictDates.ParseRequiredUtc("2026-01-01T03:00:00+03:00", "startsAt");
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), offsetForm);
    }

    [Fact]
    public async Task full_year_campaign_window_is_a_single_season_over_http()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/campaigns")
        {
            Content = JsonContent.Create(new
            {
                code = "YEAR",
                name = "n",
                ownerMasterId = 1,
                startsAt = "2026-01-01T00:00:00Z",
                endsAt = "2026-12-31T20:00:00Z",
            }),
        };
        request.Headers.Add("Idempotency-Key", "year-window");
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, (int)response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse(body.GetProperty("startsAt").GetString()!));
    }
}
