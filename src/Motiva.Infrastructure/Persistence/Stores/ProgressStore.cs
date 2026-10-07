using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

public sealed class ProgressStore(MotivaDbContext db) : IProgressLog
{
    public async Task<ProgressEventRec?> FindByNumberAsync(Guid companyId, string sourceSubject, string eventNumber, CancellationToken ct)
    {
        var row = await db.ProgressEvents.FirstOrDefaultAsync(
            e => e.CompanyId == companyId && e.SourceSubject == sourceSubject && e.EventNumber == eventNumber, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task InsertEventAsync(ProgressEventRec progressEvent, CancellationToken ct)
    {
        var row = new ProgressEventRow
        {
            CompanyId = progressEvent.CompanyId,
            Id = progressEvent.Id,
            SourceSubject = progressEvent.SourceSubject,
            EventNumber = progressEvent.EventNumber,
            Result = progressEvent.Result.ToString(),
            RejectReason = progressEvent.RejectReason?.ToString(),
            MasterId = progressEvent.MasterId,
            TaskId = progressEvent.TaskId,
            PeriodStart = progressEvent.PeriodStartUtc,
            TransmittedDelta = progressEvent.TransmittedDelta,
            CreditedDelta = progressEvent.CreditedDelta,
            AcceptedAt = progressEvent.AcceptedAtUtc,
            CompletionId = progressEvent.CompletionId,
            StreamPointsAdded = progressEvent.StreamPointsAdded,
            RewardOutcome = progressEvent.RewardOutcome?.ToString(),
            RewardOperationId = progressEvent.RewardOperationId,
            CompletedAt = progressEvent.AcceptedAtUtc,
            EssentialData = progressEvent.EssentialData,
            ResponseStatus = progressEvent.ResponseStatus,
            ResponseBody = progressEvent.ResponseBody,
        };
        db.ProgressEvents.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Page<ProgressEventRec>> ListForEmployeeAsync(
        Guid companyId, int masterId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? campaignId, Guid? taskId, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<OperationStore.TimeIdCursor>(cursor);
        var query = db.ProgressEvents.Where(e => e.CompanyId == companyId && e.MasterId == masterId);
        if (boundary is not null)
        {
            query = query.Where(e => e.AcceptedAt > boundary.At || (e.AcceptedAt == boundary.At && e.Id.CompareTo(boundary.Id) > 0));
        }

        if (from is { } fromValue)
        {
            query = query.Where(e => e.AcceptedAt >= fromValue);
        }

        if (toUtc is { } toValue)
        {
            query = query.Where(e => e.AcceptedAt < toValue);
        }

        if (taskId is { } task)
        {
            query = query.Where(e => e.TaskId == task);
        }

        if (campaignId is { } campaign)
        {
            var taskIds = TasksOfCampaignAsync(companyId, campaign, ct);
            var ids = await taskIds;
            query = query.Where(e => ids.Contains(e.TaskId));
        }

        var rows = await query.OrderBy(e => e.AcceptedAt).ThenBy(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new OperationStore.TimeIdCursor(rows[limit - 1].AcceptedAt, rows[limit - 1].Id))
            : null;
        return new Page<ProgressEventRec>(items, next);
    }

    public async Task<Page<ProgressEventRec>> ListForCompanyAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<OperationStore.TimeIdCursor>(cursor);
        var query = db.ProgressEvents.Where(e => e.CompanyId == companyId);
        if (boundary is not null)
        {
            query = query.Where(e => e.AcceptedAt > boundary.At || (e.AcceptedAt == boundary.At && e.Id.CompareTo(boundary.Id) > 0));
        }

        if (from is { } fromValue)
        {
            query = query.Where(e => e.AcceptedAt >= fromValue);
        }

        if (toUtc is { } toValue)
        {
            query = query.Where(e => e.AcceptedAt < toValue);
        }

        var rows = await query.OrderBy(e => e.AcceptedAt).ThenBy(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new OperationStore.TimeIdCursor(rows[limit - 1].AcceptedAt, rows[limit - 1].Id))
            : null;
        return new Page<ProgressEventRec>(items, next);
    }

    private async Task<List<Guid>> TasksOfCampaignAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        return await (from t in db.Tasks
                      join s in db.Streams on t.StreamId equals s.Id
                      where s.CampaignId == campaignId
                      select t.Id).ToListAsync(ct);
    }

    private sealed record StateRow(long Current);

    public async Task<ProgressStateRec> LockStateAsync(
        Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, long initialCurrent, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO progress_state (company_id, master_id, task_id, period_start, current)
            VALUES ({companyId}, {masterId}, {taskId}, {periodStart}, {initialCurrent}) ON CONFLICT DO NOTHING
            """, ct);
        var rows = await db.Database
            .SqlQuery<StateRow>($"""
                SELECT current FROM progress_state
                WHERE company_id = {companyId} AND master_id = {masterId} AND task_id = {taskId} AND period_start = {periodStart}
                FOR UPDATE
                """)
            .ToListAsync(ct);
        return new ProgressStateRec(taskId, masterId, periodStart, rows[0].Current);
    }

    public Task SaveStateAsync(Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, long current, CancellationToken ct)
    {
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE progress_state SET current = {current}
            WHERE company_id = {companyId} AND master_id = {masterId} AND task_id = {taskId} AND period_start = {periodStart}
            """, ct);
    }

    private sealed record PointsRow(long Points);

    public async Task<long> LockStreamPointsAsync(Guid companyId, int masterId, Guid streamId, int season, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO stream_points (company_id, master_id, stream_id, season, points)
            VALUES ({companyId}, {masterId}, {streamId}, {season}, 0) ON CONFLICT DO NOTHING
            """, ct);
        var rows = await db.Database
            .SqlQuery<PointsRow>($"""
                SELECT points FROM stream_points
                WHERE company_id = {companyId} AND master_id = {masterId} AND stream_id = {streamId} AND season = {season}
                FOR UPDATE
                """)
            .ToListAsync(ct);
        return rows[0].Points;
    }

    public Task AddStreamPointsAsync(Guid companyId, int masterId, Guid streamId, int season, long delta, CancellationToken ct)
    {
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE stream_points SET points = points + {delta}
            WHERE company_id = {companyId} AND master_id = {masterId} AND stream_id = {streamId} AND season = {season}
            """, ct);
    }

    public async Task<bool> TryInsertCompletionAsync(CompletionRec completion, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO completions (company_id, master_id, task_id, period_start, id, stream_points, award_operation_id, reward_outcome, completed_at)
            VALUES ({completion.CompanyId}, {completion.MasterId}, {completion.TaskId},
                    {completion.PeriodStart}, {completion.Id}, {completion.StreamPointsAdded}, {completion.RewardOperationId},
                    {completion.RewardOutcome.ToString()}, {completion.CompletedAtUtc})
            ON CONFLICT DO NOTHING
            """, ct);
        return affected > 0;
    }

    public async Task<CompletionRec?> FindCompletionAsync(
        Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, CancellationToken ct)
    {
        var row = await db.Completions.FirstOrDefaultAsync(
            c => c.CompanyId == companyId && c.MasterId == masterId && c.TaskId == taskId && c.PeriodStart == periodStart, ct);
        return row is null ? null : new CompletionRec(row.CompanyId, row.Id, row.MasterId, row.TaskId, row.PeriodStart, row.StreamPoints,
            Enum.Parse<RewardOutcome>(row.RewardOutcome), row.AwardOperationId, row.CompletedAt);
    }

    public async Task<bool> TryGrantAchievementAsync(
        Guid companyId, int masterId, Guid achievementId, int season, DateTimeOffset grantedAt, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO achievement_grants (company_id, master_id, achievement_id, season, granted_at)
            VALUES ({companyId}, {masterId}, {achievementId}, {season}, {grantedAt})
            ON CONFLICT DO NOTHING
            """, ct);
        return affected > 0;
    }

    public async Task<Page<AchievementGrantRec>> ListAchievementsAsync(
        Guid companyId, int masterId, int? season, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<SeasonTimeCursor>(cursor);
        var query = from g in db.AchievementGrants
                    join a in db.Achievements on new { g.CompanyId, Id = g.AchievementId } equals new { a.CompanyId, a.Id }
                    where g.CompanyId == companyId && g.MasterId == masterId
                    select new { g, a };
        if (season is { } seasonValue)
        {
            query = query.Where(x => x.g.Season == seasonValue);
        }

        if (boundary is not null)
        {
            query = query.Where(x => x.g.Season > boundary.Season || (x.g.Season == boundary.Season && x.g.GrantedAt > boundary.At));
        }

        var rows = await query.OrderBy(x => x.g.Season).ThenBy(x => x.g.GrantedAt).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit)
            .Select(x => new AchievementGrantRec(x.g.AchievementId, x.a.CodeNorm, x.a.Name, x.g.Season, x.g.GrantedAt))
            .ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new SeasonTimeCursor(items[^1].Season, items[^1].GrantedAtUtc))
            : null;
        return new Page<AchievementGrantRec>(items, next);
    }

    public Task<IReadOnlyList<MilestoneRec>> GetCrossedMilestonesAsync(
        Guid companyId, Guid streamId, long pointsBefore, long pointsAfter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<MilestoneRec>>(
            db.Milestones
                .Where(m => m.StreamId == streamId && m.Threshold > pointsBefore && m.Threshold <= pointsAfter)
                .OrderBy(m => m.Threshold)
                .Select(m => new MilestoneRec(m.Id, m.StreamId, m.Threshold, m.AchievementId, m.Version))
                .ToList());
    }

    internal static ProgressEventRec ToRec(ProgressEventRow row)
    {
        return new ProgressEventRec(
            row.CompanyId, row.SourceSubject, row.Id, row.EventNumber,
            Enum.Parse<ProgressEventOutcome>(row.Result),
            row.RejectReason is null ? null : Enum.Parse<ProgressRejectReason>(row.RejectReason),
            row.MasterId, row.TaskId, row.PeriodStart, row.TransmittedDelta, row.CreditedDelta, row.AcceptedAt,
            row.CompletionId,
            row.StreamPointsAdded,
            row.RewardOutcome is null ? null : Enum.Parse<RewardOutcome>(row.RewardOutcome),
            row.RewardOperationId,
            row.EssentialData, row.ResponseStatus, row.ResponseBody);
    }

    internal sealed record SeasonTimeCursor(int Season, DateTimeOffset At);
}
