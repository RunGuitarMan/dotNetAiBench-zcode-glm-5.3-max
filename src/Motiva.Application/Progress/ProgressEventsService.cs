using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using NodaTime;
using DomainPeriod = Motiva.Domain.Periods;

namespace Motiva.Application.Progress;

/// <summary>
/// The progress-event vertical (B14–B17, B20, B29–B31) implementing the unified processing
/// order (§3.0), the lock order (§3.1) and the acceptedAt protocol (§3.2): advisory locks of
/// all challenges of the stream → a single TimeProvider read → periods/progress → completion →
/// milestones → reward decision → challenge scores, all in one transaction with the stored
/// canonical response (byte-identical replay).
/// </summary>
public sealed class ProgressEventsService(
    IEmployeeDirectory employees,
    ICampaignCatalog campaigns,
    ICompanyDirectory companies,
    IIntegrationDirectory grants,
    IResourceDirectory resources,
    IProgressLog progress,
    ICompetitionBoard board,
    IBudgetLedger budgets,
    IWalletLedger wallets,
    IOperationBook operations,
    IUnitOfWork uow,
    ITestImpediments impediments,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> PostAsync(
        ActorContext actor, string eventNumber, int masterId, Guid taskId, long delta, CancellationToken ct)
    {
        Authz.EnsureService(actor);
        Guard.ExternalNumber(eventNumber);
        Guard.MasterId(masterId);
        if (delta is < 1 or > Guard.MaxUnit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "delta must be between 1 and 1000000000.");
        }

        var essential = CanonicalJson.Serialize(new { masterId, taskId, delta });

        // §3.0 step 2 — before the number, for new requests and replays (H0 Q04/Q08):
        // the grant must be active for this campaign and the recipient must be an active employee.
        var task = await campaigns.GetTaskAsync(actor.CompanyId, taskId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, task.StreamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await campaigns.GetAsync(actor.CompanyId, stream.CampaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        Authz.EnsureGrant(await grants.FindActiveAsync(actor.CompanyId, actor.Subject, GrantKind.Progress, campaign.Id, null, null, ct));
        var employee = await employees.GetAsync(actor.CompanyId, masterId, ct);
        if (employee is null)
        {
            throw new MotivaException(ErrorCode.NotFound, "Recipient profile not found.");
        }

        if (!employee.IsActive)
        {
            throw new MotivaException(ErrorCode.AuthzEmployeeNotActive);
        }

        // §3.0 step 3 — the number: same data → stored response byte-identical; other data → 409.
        var existing = await progress.FindByNumberAsync(actor.CompanyId, actor.Subject, eventNumber, ct);
        if (existing is not null)
        {
            return Replay(existing, essential);
        }

        var company = await companies.GetAsync(actor.CompanyId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var zone = DateTimeZoneProviders.Tzdb[company.TimeZoneId];

        await using var scope = await uow.BeginAsync(ct);
        var racing = await progress.FindByNumberAsync(actor.CompanyId, actor.Subject, eventNumber, ct);
        if (racing is not null)
        {
            return Replay(racing, essential);
        }

        // §3.2 step 1 — advisory locks of ALL challenges of the stream in ascending id order.
        var challenges = (await campaigns.ListChallengesOfStreamAsync(actor.CompanyId, stream.Id, ct))
            .OrderBy(c => c.Id)
            .ToArray();
        await board.LockChallengesAsync(challenges.Select(c => c.Id).ToArray(), ct);

        await impediments.CheckpointAsync(Checkpoints.ProgressAfterLocksBeforeTime, ct);

        // §3.2 step 2 — one single read of time after the locks; nothing later moves acceptedAt.
        var acceptedAt = timeProvider.GetUtcNow();
        var acceptedInstant = Instant.FromDateTimeOffset(acceptedAt);

        var rejection = EvaluateRejection(campaign, stream, task, employee, acceptedInstant);
        if (rejection is not null)
        {
            var rejected = BuildResult(
                Guid.NewGuid(), eventNumber, ProgressEventOutcome.Rejected, rejection.Value, masterId, taskId,
                null, delta, 0, acceptedAt, null);
            var rejectedBody = CanonicalJson.Serialize(rejected);
            await progress.InsertEventAsync(ToRecord(actor, rejected, essential, 201, rejectedBody), ct);
            await impediments.CheckpointAsync(Checkpoints.ProgressBeforeCommit, ct);
            await scope.CommitAsync(ct);
            await impediments.CheckpointAsync(Checkpoints.ProgressAfterCommitBeforeResponse, ct);
            return new CommandResponse(201, rejectedBody, "/api/v1/progress-events/" + rejected.Id.ToString());
        }

        // §3.1 — progress_state is the first aggregate of this path.
        var (periodStart, periodEnd) = DomainPeriod.PeriodCalendar.EffectivePeriod(
            acceptedInstant, task.Period, zone, Instant.FromDateTimeOffset(campaign.StartsAt), Instant.FromDateTimeOffset(campaign.EndsAt));
        var state = await progress.LockStateAsync(actor.CompanyId, masterId, taskId, periodStart.ToDateTimeOffset(), 0, ct);
        var (current, credited) = Domain.Progress.ProgressRule.Apply(task.Goal, state.Current, delta);
        await progress.SaveStateAsync(actor.CompanyId, masterId, taskId, periodStart.ToDateTimeOffset(), current, ct);

        CompletionDto? completion = null;
        var season = DomainPeriod.Seasons.SeasonOf(acceptedInstant, zone);
        if (current >= task.Goal && state.Current < task.Goal)
        {
            // §3.1: stream_points (5) is locked before budgets (6) and wallet balances (7).
            var pointsBefore = await progress.LockStreamPointsAsync(actor.CompanyId, masterId, stream.Id, season, ct);
            var reward = await DecideRewardAsync(actor, campaign, masterId, task, eventNumber, acceptedAt, ct);
            var inserted = await progress.TryInsertCompletionAsync(
                new CompletionRec(
                    actor.CompanyId, Guid.NewGuid(), masterId, taskId, periodStart.ToDateTimeOffset(), task.StreamPoints,
                    reward.CompletionOutcome ?? RewardOutcome.NotProvided, reward.OperationId, acceptedAt),
                ct);
            if (!inserted)
            {
                throw new InvalidOperationException("Completion already exists under lock — invariant violation.");
            }

            await progress.AddStreamPointsAsync(actor.CompanyId, masterId, stream.Id, season, task.StreamPoints, ct);
            var pointsAfter = pointsBefore + task.StreamPoints;
            foreach (var milestone in await progress.GetCrossedMilestonesAsync(actor.CompanyId, stream.Id, pointsBefore, pointsAfter, ct))
            {
                await progress.TryGrantAchievementAsync(actor.CompanyId, masterId, milestone.AchievementId, season, acceptedAt, ct);
            }

            completion = new CompletionDto(
                taskId, periodStart.ToDateTimeOffset(), task.StreamPoints,
                new RewardDecisionDto(reward.Outcome.ToString(), reward.OperationId), acceptedAt);
        }

        // §3.2 step 4 — every challenge whose [startsAt, endsAt) contains acceptedAt gets the credited delta.
        foreach (var challenge in challenges)
        {
            if (challenge.StartsAt <= acceptedAt && acceptedAt < challenge.EndsAt)
            {
                await board.AddScoreAsync(challenge.Id, masterId, credited, ct);
            }
        }

        var result = BuildResult(
            Guid.NewGuid(), eventNumber, ProgressEventOutcome.Accepted, null, masterId, taskId,
            periodStart.ToDateTimeOffset(), delta, credited, acceptedAt, completion);
        var body = CanonicalJson.Serialize(result);
        await progress.InsertEventAsync(ToRecord(actor, result, essential, 201, body), ct);
        await impediments.CheckpointAsync(Checkpoints.ProgressBeforeCommit, ct);
        await scope.CommitAsync(ct);
        await impediments.CheckpointAsync(Checkpoints.ProgressAfterCommitBeforeResponse, ct);
        return new CommandResponse(201, body, "/api/v1/progress-events/" + result.Id.ToString());
    }

    public async Task<Page<ProgressEventRec>> ListOwnAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? campaignId, Guid? taskId, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        return await progress.ListForEmployeeAsync(actor.CompanyId, actor.MasterId!.Value, limit, cursor, from, toUtc, campaignId, taskId, ct);
    }

    public async Task<Page<ProgressEventRec>> ListCompanyAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        return await progress.ListForCompanyAsync(actor.CompanyId, limit, cursor, from, toUtc, ct);
    }

    private static ProgressRejectReason? EvaluateRejection(
        CampaignRec campaign, StreamRec stream, TaskRec task, EmployeeRec employee, Instant acceptedAt)
    {
        var campaignStart = Instant.FromDateTimeOffset(campaign.StartsAt);
        var campaignEnd = Instant.FromDateTimeOffset(campaign.EndsAt);
        if (campaign.Status == CampaignStatus.Archived || stream.Status == ContentStatus.Archived || task.Status == ContentStatus.Archived)
        {
            return ProgressRejectReason.TargetArchived;
        }

        if (campaign.Status != CampaignStatus.Published || acceptedAt < campaignStart || acceptedAt >= campaignEnd)
        {
            return ProgressRejectReason.CampaignClosed;
        }

        var tags = new HashSet<string>(employee.Tags);
        if (!campaign.Audience.Matches(tags) || !task.Audience.Matches(tags))
        {
            return ProgressRejectReason.AudienceMismatch;
        }

        return null;
    }

    private readonly record struct RewardDecision(RewardOutcome Outcome, Guid? OperationId, RewardOutcome? CompletionOutcome);

    private async Task<RewardDecision> DecideRewardAsync(
        ActorContext actor, CampaignRec campaign, int masterId, TaskRec task, string eventNumber, DateTimeOffset acceptedAt, CancellationToken ct)
    {
        if (task.RewardItems.Count == 0)
        {
            return new RewardDecision(RewardOutcome.NotProvided, null, RewardOutcome.NotProvided);
        }

        var items = task.RewardItems.OrderBy(i => i.ResourceId).ToArray();
        var resourceIds = items.Select(i => i.ResourceId).ToArray();
        var resourcesById = (await resources.ListByIdsAsync(actor.CompanyId, resourceIds, ct))
            .ToDictionary(r => r.Id);

        // H0 Q02: in a non-empty package the unavailability of any of its resources takes
        // priority over insufficient budget; an unrelated archived resource does not matter.
        if (resourceIds.Any(id => !resourcesById.TryGetValue(id, out var r) || r.Status == ResourceStatus.Archived))
        {
            return new RewardDecision(RewardOutcome.DeclinedResourceUnavailable, null, RewardOutcome.DeclinedResourceUnavailable);
        }

        // §3.1 — lock all budgets by resource id, then wallet balances.
        foreach (var item in items)
        {
            await budgets.LockAsync(actor.CompanyId, campaign.Id, item.ResourceId, ct);
        }

        foreach (var item in items)
        {
            await wallets.LockBalanceAsync(actor.CompanyId, masterId, item.ResourceId, ct);
        }

        foreach (var item in items)
        {
            var budget = await budgets.GetAsync(actor.CompanyId, campaign.Id, item.ResourceId, ct);
            if (budget is null || budget.Available < item.Amount)
            {
                return new RewardDecision(RewardOutcome.DeclinedInsufficientBudget, null, RewardOutcome.DeclinedInsufficientBudget);
            }
        }

        var operationItems = items
            .Select(i => new OperationItemRec(i.ResourceId, resourcesById[i.ResourceId].Code, i.Amount, IsDebit: false))
            .ToArray();
        foreach (var item in items)
        {
            await budgets.AddSpendingAsync(actor.CompanyId, campaign.Id, item.ResourceId, item.Amount, ct);
            await wallets.ApplyDeltaAsync(actor.CompanyId, masterId, item.ResourceId, item.Amount, ct);
        }

        var operation = new OperationRec(
            Guid.NewGuid(), OperationKind.TaskReward, OperationResult.Posted, null, actor, masterId, campaign.Id,
            null, null, null, eventNumber, operationItems, acceptedAt, null, null, null);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(operation));
        operation = operation with { ResponseStatus = 201, ResponseBody = body };
        await operations.InsertAsync(operation, ct);
        return new RewardDecision(RewardOutcome.Granted, operation.Id, RewardOutcome.Granted);
    }

    private static ProgressEventResultDto BuildResult(
        Guid id,
        string eventNumber,
        ProgressEventOutcome outcome,
        ProgressRejectReason? rejectReason,
        int masterId,
        Guid taskId,
        DateTimeOffset? periodStart,
        long transmittedDelta,
        long creditedDelta,
        DateTimeOffset acceptedAt,
        CompletionDto? completion)
    {
        return new ProgressEventResultDto(
            id, eventNumber, outcome.ToString(), rejectReason?.ToString(), masterId, taskId, periodStart,
            transmittedDelta, creditedDelta, acceptedAt, completion);
    }

    private static ProgressEventRec ToRecord(ActorContext actor, ProgressEventResultDto result, string essential, int status, string body)
    {
        return new ProgressEventRec(
            actor.CompanyId, actor.Subject, result.Id, result.EventNumber,
            result.Result == "Accepted" ? ProgressEventOutcome.Accepted : ProgressEventOutcome.Rejected,
            result.RejectReason is null ? null : Enum.Parse<ProgressRejectReason>(result.RejectReason),
            result.MasterId, result.TaskId, result.PeriodStartUtc, result.TransmittedDelta, result.CreditedDelta,
            result.AcceptedAtUtc,
            CompletionId: result.Completion is null ? null : Guid.NewGuid(),
            result.Completion?.StreamPointsAdded,
            result.Completion?.Reward is null ? null : Enum.Parse<RewardOutcome>(result.Completion.Reward.Outcome),
            result.Completion?.Reward?.OperationId,
            essential, status, body);
    }

    private static CommandResponse Replay(ProgressEventRec existing, string essential)
    {
        if (!string.Equals(existing.EssentialData, essential, StringComparison.Ordinal))
        {
            throw new MotivaException(ErrorCode.ConflictBusinessNumber);
        }

        return new CommandResponse(existing.ResponseStatus ?? 201, existing.ResponseBody ?? "{}", "/api/v1/progress-events/" + existing.Id.ToString());
    }
}
