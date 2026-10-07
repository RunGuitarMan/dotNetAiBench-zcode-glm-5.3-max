using Motiva.Application.Common;
using Motiva.Application.Competitions;
using Motiva.Application.Dto;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Challenges and finalization (T3-16): acceptedAt after locks (Ф-1), late commit
/// counted (Ф-2), immutability (Ф-3), interval boundaries (Ф-4), ranking/visibility (Ф-5, Q04).</summary>
[Collection("functional")]
public sealed class ChallengeTests(MotivaFunctionalFixture fixture)
{
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0, int second = 0)
        => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public async Task f1_accepted_at_is_read_after_challenge_locks()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1100);
        var setup = await world.CreatePublishedCampaignAsync(
            "F1", ("T", 5, "Day", 10, null),
            challenge: (At(4, 1, 0), At(4, 8, 0)));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        // Pause inside the use case after the advisory locks, before the time read; move the
        // clock across endsAt during the pause: acceptedAt must land after the locks (§3.2).
        fixture.Host.Clock.SetUtcNow(At(4, 7, 21, 59, 59).AddMilliseconds(500)); // 1 ms before endsAt (00:59:59.5+1ms)
        var endsAt = At(4, 8, 0);
        fixture.Host.Clock.SetUtcNow(endsAt.AddMilliseconds(-2));
        fixture.Host.Impediments.PauseOn(Checkpoints.ProgressAfterLocksBeforeTime);
        var sending = Task.Run(() => world.SendEventAsync("src", "E-1", 1100, setup.TaskId, 1));
        await Task.Delay(300);
        fixture.Host.Clock.SetUtcNow(endsAt.AddSeconds(5));
        fixture.Host.Impediments.Release(Checkpoints.ProgressAfterLocksBeforeTime);
        var result = await sending;

        var body = TestWorld.ParseJson(result.Body);
        Assert.Equal("Accepted", body.GetProperty("result").GetString());
        var acceptedAt = body.GetProperty("acceptedAtUtc").GetDateTimeOffset();
        Assert.True(acceptedAt >= endsAt, "acceptedAt must be read after the locks, not at request entry");
        // The score must NOT include the event: acceptedAt >= endsAt is outside [start, end).
        var leaderboard = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(1100), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Empty(leaderboard.Items);
    }

    [Fact]
    public async Task f2_event_accepted_before_end_committed_after_end_is_counted()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1101);
        var setup = await world.CreatePublishedCampaignAsync(
            "F2", ("T", 5, "Day", 10, null),
            challenge: (At(4, 1, 0), At(4, 8, 0)));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(At(4, 7, 23, 59)); // inside the interval
        fixture.Host.Impediments.PauseOn(Checkpoints.ProgressBeforeCommit);
        var sending = Task.Run(() => world.SendEventAsync("src", "E-1", 1101, setup.TaskId, 2));
        await Task.Delay(300);
        fixture.Host.Clock.SetUtcNow(At(4, 8, 1)); // after endsAt while the transaction is paused
        fixture.Host.Impediments.Release(Checkpoints.ProgressBeforeCommit);
        var result = await sending;
        Assert.Equal("Accepted", TestWorld.ParseJson(result.Body).GetProperty("result").GetString());

        var leaderboard = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(1101), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Equal(2, leaderboard.Items.Single().Score); // B33.2: counted despite the delayed response
    }

    [Fact]
    public async Task f3_finalization_is_immutable_and_within_tolerance()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1102);
        var setup = await world.CreatePublishedCampaignAsync(
            "F3", ("T", 5, "Day", 10, null),
            challenge: (At(4, 1, 0), At(4, 8, 0)));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(At(4, 2, 10));
        await world.SendEventAsync("src", "E-1", 1102, setup.TaskId, 3);
        fixture.Host.Clock.SetUtcNow(At(4, 9, 0));

        var finalizer = world.Resolve<FinalizeChallengeHandler>();
        Assert.True(await finalizer.HandleAsync(world.CompanyId, setup.ChallengeId!.Value, CancellationToken.None));
        var final = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(1102), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Equal("Final", final.State);
        Assert.Equal(3, final.Items.Single().Score);

        // A later event does not change the finalized result (B33.3); reward reversal neither (B33.4).
        fixture.Host.Clock.SetUtcNow(At(4, 10, 10));
        await world.SendEventAsync("src", "E-2", 1102, setup.TaskId, 2);
        Assert.True(await world.Resolve<FinalizeChallengeHandler>().HandleAsync(world.CompanyId, setup.ChallengeId.Value, CancellationToken.None));
        var still = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(1102), setup.ChallengeId.Value, 10, CancellationToken.None);
        Assert.Equal(3, still.Items.Single().Score);
        Assert.Equal(final.AsOfUtc, still.AsOfUtc);
    }

    [Fact]
    public async Task f4_interval_is_start_inclusive_end_exclusive()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1103);
        var setup = await world.CreatePublishedCampaignAsync(
            "F4", ("T", 10, "Day", 0, null),
            challenge: (At(4, 1, 12), At(4, 3, 12)));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Clock.SetUtcNow(At(4, 1, 12)); // == startsAt: included
        await world.SendEventAsync("src", "E-IN", 1103, setup.TaskId, 2);
        fixture.Host.Clock.SetUtcNow(At(4, 3, 12)); // == endsAt: excluded
        await world.SendEventAsync("src", "E-OUT", 1103, setup.TaskId, 5);

        var leaderboard = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(1103), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Equal(2, leaderboard.Items.Single().Score);
    }

    [Fact]
    public async Task f5_ranking_and_zero_participant_edge04()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(1 + 2000);
        await world.CreateEmployeeAsync(2 + 2000);
        await world.CreateEmployeeAsync(3 + 2000);
        await world.CreateEmployeeAsync(4 + 2000);
        var setup = await world.CreatePublishedCampaignAsync(
            "F5", ("T", 3, "Day", 0, null),
            challenge: (At(4, 1, 12), At(4, 8, 0)));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        // U reached the goal before the interval start (12:00 same day); a later event credits 0 but participates.
        fixture.Host.Clock.SetUtcNow(At(4, 1, 9));
        await world.SendEventAsync("src", "U1", 2004, setup.TaskId, 3);
        fixture.Host.Clock.SetUtcNow(At(4, 1, 14));
        await world.SendEventAsync("src", "U2", 2004, setup.TaskId, 1);

        fixture.Host.Clock.SetUtcNow(At(4, 2, 10));
        await world.SendEventAsync("src", "W1", 2001, setup.TaskId, 3); // goal 3 reached
        fixture.Host.Clock.SetUtcNow(At(4, 3, 10));
        await world.SendEventAsync("src", "W2", 2002, setup.TaskId, 3);
        fixture.Host.Clock.SetUtcNow(At(4, 4, 10));
        await world.SendEventAsync("src", "W3", 2003, setup.TaskId, 2);

        fixture.Host.Clock.SetUtcNow(At(4, 9, 0));
        await world.Resolve<FinalizeChallengeHandler>().HandleAsync(world.CompanyId, setup.ChallengeId!.Value, CancellationToken.None);

        var page = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(2004), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Equal("Final", page.State);
        var places = page.Items.ToDictionary(i => i.MasterId, i => i.Place);
        Assert.Equal(1, places[2001]);
        Assert.Equal(1, places[2002]);
        Assert.Equal(3, places[2003]);
        Assert.Equal(4, places[2004]);

        var me = await world.Resolve<LeaderboardService>().GetMeAsync(world.Employee(2004), setup.ChallengeId!.Value, CancellationToken.None);
        Assert.True(me.Participating);
        Assert.Equal(0, me.Score);
        Assert.Equal(4, me.Place);
    }

    [Fact]
    public async Task q04_visibility_before_and_after_season()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(2100, "vip");
        await world.CreateEmployeeAsync(2101);
        var setup = await world.CreatePublishedCampaignAsync(
            "Q4",
            ("T", 3, "Day", 0, null),
            employeeTags: null,
            startsAt: At(1, 1, 0),
            endsAt: At(6, 30, 21),
            challenge: (At(4, 1, 0), At(4, 8, 0)),
            audience: new AudienceDto(new List<string> { "vip" }, new List<string>(), new List<string>()));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        fixture.Host.Clock.SetUtcNow(At(4, 2, 10));
        await world.SendEventAsync("src", "E1", 2100, setup.TaskId, 2);

        // Before the end of the season the current audience reads, even after the campaign ended;
        // an employee outside the audience does not (H0 Q04).
        fixture.Host.Clock.SetUtcNow(At(7, 1, 10));
        var visible = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(2100), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Single(visible.Items);
        await Assert.ThrowsAsync<MotivaException>(() =>
            world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(2101), setup.ChallengeId!.Value, 10, CancellationToken.None));

        fixture.Host.Clock.SetUtcNow(At(7, 1, 10) + TimeSpan.FromDays(180));
        await world.Resolve<FinalizeChallengeHandler>().HandleAsync(world.CompanyId, setup.ChallengeId!.Value, CancellationToken.None);

        // After the season (2027-01-01 Tallinn): only participants with their own row.
        fixture.Host.Clock.SetUtcNow(At(1, 5, 10) + TimeSpan.FromDays(365));
        var participant = await world.Resolve<LeaderboardService>().GetMeAsync(world.Employee(2100), setup.ChallengeId!.Value, CancellationToken.None);
        Assert.True(participant.Participating);
        await Assert.ThrowsAsync<MotivaException>(() =>
            world.Resolve<LeaderboardService>().GetMeAsync(world.Employee(2101), setup.ChallengeId!.Value, CancellationToken.None));
    }
}
