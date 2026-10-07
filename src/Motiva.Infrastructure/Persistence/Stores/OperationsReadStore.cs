using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using Motiva.Application.Reads;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>Per-actor operation visibility (§3.3) and progress views (B34).</summary>
public sealed class OperationsReadStore(
    MotivaDbContext db,
    IOperationBook operations,
    IIntegrationDirectory grants,
    IAuditLog auditLog,
    TimeProvider timeProvider) : IOperationsReadStore
{
    public async Task<Page<OperationRec>> ListOwnAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, OperationResult? result, CancellationToken ct)
    {
        if (actor.ActorType == ActorType.Service)
        {
            var pairs = await grants.ListActiveSpendPairsAsync(actor.CompanyId, actor.Subject, ct);
            if (pairs.Count == 0)
            {
                throw new MotivaException(ErrorCode.AuthzGrantMissing);
            }

            return await operations.ListForSpendPairsAsync(actor.CompanyId, pairs, limit, cursor, from, toUtc, resourceId, kind, result, ct);
        }

        return await operations.ListForEmployeeAsync(actor.CompanyId, actor.MasterId!.Value, limit, cursor, from, toUtc, resourceId, kind, result, ct);
    }

    public Task<Page<OperationRec>> ListForEmployeeAsync(
        ActorContext actor, int masterId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, CancellationToken ct)
    => operations.ListForEmployeeAsync(actor.CompanyId, masterId, limit, cursor, from, toUtc, resourceId, kind, null, ct);

    public Task<Page<OperationRec>> ListForCampaignAsync(
        ActorContext actor, Guid campaignId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId, CancellationToken ct)
        => operations.ListForCampaignAsync(actor.CompanyId, campaignId, limit, cursor, from, toUtc, resourceId, ct);

    public async Task<OperationRec?> GetAsync(ActorContext actor, Guid operationId, CancellationToken ct)
    {
        var operation = await operations.GetAsync(actor.CompanyId, operationId, ct);
        if (operation is null)
        {
            return null;
        }

        if (actor.IsAdmin)
        {
            return operation;
        }

        if (actor.ActorType == ActorType.Service)
        {
            var pairs = await grants.ListActiveSpendPairsAsync(actor.CompanyId, actor.Subject, ct);
            if (pairs.Count == 0)
            {
                throw new MotivaException(ErrorCode.AuthzGrantMissing);
            }

            var pairSet = pairs.Select(p => (p.PurchaseSystemId, p.ResourceId)).ToHashSet();
            var allowed = operation.Kind is OperationKind.Spend or OperationKind.SpendReversal
                && operation.PurchaseSystemId is { } system
                && operation.Items.Any(i => pairSet.Contains((system, i.ResourceId)));
            return allowed ? operation : null;
        }

        return operation.MasterId == actor.MasterId ? operation : null;
    }

    public Task<Page<AuditRec>> ListAuditAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, string? entityType, string? entityId, CancellationToken ct)
        => auditLog.ListAsync(companyId, limit, cursor, from, toUtc, entityType, entityId, ct);

    public async Task<CampaignProgressDto> BuildCampaignProgressAsync(ActorContext actor, CampaignRec campaign, int masterId, CancellationToken ct)
    {
        var company = await db.Companies.FindAsync(new object[] { actor.CompanyId }, ct);
        var zone = NodaTime.DateTimeZoneProviders.Tzdb[company?.TimeZoneId ?? "Europe/Tallinn"];
        var nowInstant = NodaTime.Instant.FromDateTimeOffset(timeProvider.GetUtcNow());
        var streams = new List<StreamProgressDto>();
        var streamRows = await db.Streams.Where(s => s.CampaignId == campaign.Id).OrderBy(s => s.CodeNorm).ToListAsync(ct);
        foreach (var streamRow in streamRows)
        {
            var taskRows = await db.Tasks.Where(t => t.StreamId == streamRow.Id).OrderBy(t => t.CodeNorm).ToListAsync(ct);
            var taskDtos = new List<TaskProgressDto>();
            foreach (var taskRow in taskRows)
            {
                var period = Domain.Periods.PeriodCalendar.EffectivePeriod(
                    nowInstant,
                    Enum.Parse<Domain.Periods.PeriodKind>(taskRow.Period),
                    zone,
                    NodaTime.Instant.FromDateTimeOffset(campaign.StartsAt),
                    NodaTime.Instant.FromDateTimeOffset(campaign.EndsAt));
                var state = await db.ProgressStates.FirstOrDefaultAsync(
                    s => s.CompanyId == actor.CompanyId && s.MasterId == masterId && s.TaskId == taskRow.Id && s.PeriodStart == period.Start.ToDateTimeOffset(), ct);
                var completed = await db.Completions.AnyAsync(
                    c => c.CompanyId == actor.CompanyId && c.MasterId == masterId && c.TaskId == taskRow.Id && c.PeriodStart == period.Start.ToDateTimeOffset(), ct);
                taskDtos.Add(new TaskProgressDto(
                    taskRow.Id, taskRow.CodeNorm, state?.Current ?? 0, taskRow.Goal, completed,
                    period.Start.ToDateTimeOffset(), period.End.ToDateTimeOffset()));
            }

            var points = await db.StreamPoints
                .Where(p => p.CompanyId == actor.CompanyId && p.MasterId == masterId && p.StreamId == streamRow.Id && p.Season == campaign.Season)
                .Select(p => p.Points)
                .FirstOrDefaultAsync(ct);
            var milestoneRows = await db.Milestones.Where(m => m.StreamId == streamRow.Id).OrderBy(m => m.Threshold).ToListAsync(ct);
            var milestoneDtos = new List<MilestoneProgressDto>();
            foreach (var milestone in milestoneRows)
            {
                var earned = await db.AchievementGrants.AnyAsync(
                    g => g.CompanyId == actor.CompanyId && g.MasterId == masterId && g.AchievementId == milestone.AchievementId && g.Season == campaign.Season, ct);
                milestoneDtos.Add(new MilestoneProgressDto(milestone.Threshold, milestone.AchievementId, earned));
            }

            streams.Add(new StreamProgressDto(streamRow.Id, streamRow.CodeNorm, points, taskDtos, milestoneDtos));
        }

        return new CampaignProgressDto(campaign.Id, campaign.Season, streams);
    }

    public async Task<Page<ParticipantProgressDto>> ListCampaignProgressAsync(
        ActorContext actor, CampaignRec campaign, int limit, string? cursor, Guid? streamId, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<EmployeeStore.MasterIdCursor>(cursor);
        var streamIds = db.Streams.Where(s => s.CampaignId == campaign.Id).Select(s => s.Id);
        var taskIds = db.Tasks.Where(t => streamIds.Contains(t.StreamId)).Select(t => t.Id);
        var participants = await db.ProgressStates
            .Where(s => s.CompanyId == actor.CompanyId && taskIds.Contains(s.TaskId))
            .Select(s => s.MasterId)
            .Distinct()
            .ToListAsync(ct);
        var ordered = participants
            .Where(m => boundary is null || m > boundary.MasterId)
            .OrderBy(m => m)
            .ToList();
        var pageMasters = ordered.Take(limit).ToList();
        var result = new List<ParticipantProgressDto>();
        foreach (var masterId in pageMasters)
        {
            var progress = await BuildCampaignProgressAsync(actor, campaign, masterId, ct);
            var streams = streamId is null ? progress.Streams : progress.Streams.Where(s => s.StreamId == streamId).ToList();
            result.Add(new ParticipantProgressDto(masterId, streams));
        }

        var next = ordered.Count > limit && pageMasters.Count > 0
            ? CursorCodec.Encode(new EmployeeStore.MasterIdCursor(pageMasters[^1]))
            : null;
        return new Page<ParticipantProgressDto>(result, next);
    }
}
