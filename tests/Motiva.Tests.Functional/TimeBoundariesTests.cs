using Motiva.Application.Ports;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Period and season boundaries with FakeTimeProvider (T3-15): SCN-05, EDGE-01, EDGE-05.</summary>
[Collection("functional")]
public sealed class TimeBoundariesTests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task scn05_midnight_boundary_and_replay_stays_in_old_day()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1000);
        var setup = await world.CreatePublishedCampaignAsync("SCN5", ("T", 2, "Day", 1, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 4, 11, 20, 59, 50, TimeSpan.Zero)); // 23:59:50 Tallinn
        var first = await world.SendEventAsync("src", "E-1", 1000, setup.TaskId, 2);
        var firstBody = TestWorld.ParseJson(first.Body);
        Assert.Equal(2, firstBody.GetProperty("creditedDelta").GetInt64());

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 4, 11, 21, 0, 1, TimeSpan.Zero)); // 00:00:01 Tallinn next day
        var second = await world.SendEventAsync("src", "E-2", 1000, setup.TaskId, 1);
        var secondBody = TestWorld.ParseJson(second.Body);
        Assert.NotEqual(
            firstBody.GetProperty("periodStartUtc").GetString(),
            secondBody.GetProperty("periodStartUtc").GetString());

        // Replay of E-1 processed after midnight keeps the old day (B16.2, B13.4).
        var replay = await world.SendEventAsync("src", "E-1", 1000, setup.TaskId, 2);
        Assert.Equal(first.Body, replay.Body);
    }

    [Fact]
    public async Task edge01_short_first_month_second_completion_in_new_period()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(1001);
        var setup = await world.CreatePublishedCampaignAsync(
            "EDG1",
            ("T", 5, "Month", 5, new[] { (a, 10L) }),
            startsAt: new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 6, 30, 21, 0, 0, TimeSpan.Zero));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero));
        var first = await world.SendEventAsync("src", "E-1", 1001, setup.TaskId, 5);
        var firstBody = TestWorld.ParseJson(first.Body);
        Assert.Equal(new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero).UtcDateTime,
            firstBody.GetProperty("periodStartUtc").GetDateTimeOffset().UtcDateTime); // shortened first month
        Assert.Equal(10, (await world.WalletAsync(1001))["A"]);
        Assert.Equal(90, await world.BudgetAvailableAsync(setup.CampaignId, a));

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero));
        var second = await world.SendEventAsync("src", "E-2", 1001, setup.TaskId, 7);
        var secondBody = TestWorld.ParseJson(second.Body);
        Assert.Equal(5, secondBody.GetProperty("creditedDelta").GetInt64()); // min(5, 0+7), excess not carried
        Assert.Equal(7, secondBody.GetProperty("transmittedDelta").GetInt64());
        Assert.Equal(20, (await world.WalletAsync(1001))["A"]);
        Assert.Equal(80, await world.BudgetAvailableAsync(setup.CampaignId, a));
        // Season points: 5 + 5 = 10.
        var progress = await world.Resolve<ReadService>().GetOwnCampaignProgressAsync(world.Employee(1001), setup.CampaignId, CancellationToken.None);
        Assert.Equal(10, progress.Streams.Single().Points);
    }

    [Fact]
    public async Task edge05_replay_across_season_boundary_and_new_season_from_scratch()
    {
        var world = await fixture.NewWorldAsync(new DateTimeOffset(2025, 12, 1, 8, 0, 0, TimeSpan.Zero));
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(1002);
        var setup = await world.CreatePublishedCampaignAsync(
            "EDG5",
            ("T", 1, "Day", 4, new[] { (a, 1L) }),
            startsAt: new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2025, 12, 31, 21, 0, 0, TimeSpan.Zero));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2025, 12, 30, 14, 0, 0, TimeSpan.Zero));
        var original = await world.SendEventAsync("src", "E-9", 1002, setup.TaskId, 1);

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
        var replay = await world.SendEventAsync("src", "E-9", 1002, setup.TaskId, 1);
        Assert.Equal(original.Body, replay.Body); // season 2025 untouched, nothing re-accounted in 2026

        // New event of the 2025 campaign in 2026: CampaignClosed (B10.3) — old tasks stop accepting.
        var closed = await world.SendEventAsync("src", "E-10", 1002, setup.TaskId, 1);
        Assert.Equal("Rejected", TestWorld.ParseJson(closed.Body).GetProperty("result").GetString());
        Assert.Equal("CampaignClosed", TestWorld.ParseJson(closed.Body).GetProperty("rejectReason").GetString());

        // A 2026 campaign: progress starts from zero; the wallet carries over (B10.4).
        var setup26 = await world.CreatePublishedCampaignAsync(
            "EDG5B",
            ("T", 1, "Day", 1, new[] { (a, 1L) }),
            startsAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero));
        await world.AllocateAsync(setup26.CampaignId, a, 100, "A2");
        await world.CreateGrantAsync("src2", "Progress", setup26.CampaignId);
        var e10 = await world.SendEventAsync("src2", "E-10", 1002, setup26.TaskId, 1);
        Assert.Equal("Accepted", TestWorld.ParseJson(e10.Body).GetProperty("result").GetString());
        Assert.Equal(2, (await world.WalletAsync(1002))["A"]);
    }
}
