using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;
using Motiva.Domain.Audiences;

namespace Motiva.Infrastructure.Persistence.Stores;

public sealed class CampaignStore(MotivaDbContext db) : ICampaignCatalog
{
    public async Task<CampaignRec?> GetAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        var row = await LoadAsync(companyId, campaignId, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<CampaignRec>> ListAsync(
        Guid companyId, int? season, CampaignStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<CampaignCursor>(cursor);
        var query = db.Campaigns.Where(c => c.CompanyId == companyId && c.Status != "Deleted");
        if (season is { } seasonValue)
        {
            query = query.Where(c => c.Season == seasonValue);
        }

        if (status is { } statusValue)
        {
            query = query.Where(c => c.Status == statusValue.ToString());
        }

        if (boundary is not null)
        {
            query = query.Where(c => string.Compare(c.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(c => c.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new CampaignCursor(items[^1].Code)) : null;
        return new Page<CampaignRec>(items, next);
    }

    public async Task<(UpdateOutcome, CampaignRec?)> CreateAsync(
        Guid companyId, CampaignDraftInput input, DateTimeOffset utcNow, CancellationToken ct)
    {
        var duplicate = await db.Campaigns.AnyAsync(
            c => c.CompanyId == companyId && c.Season == input.Season && c.CodeNorm == input.CodeNorm && c.Status != "Deleted", ct);
        if (duplicate)
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new CampaignRow
        {
            CompanyId = companyId,
            Id = input.Id,
            CodeNorm = input.CodeNorm,
            Name = input.Name,
            Description = input.Description,
            Season = input.Season,
            OwnerMasterId = input.OwnerMasterId,
            StartsAt = input.StartsAt,
            EndsAt = input.EndsAt,
            CreatedAt = utcNow,
        };
        row.Tags = AudienceTags(input.Audience).Select(t => new CampaignTagRow { CampaignId = input.Id, Kind = t.kind, Tag = t.tag }).ToList();
        db.Campaigns.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, CampaignRec?)> PatchAsync(
        Guid companyId, Guid campaignId, int expectedVersion, string? name, string? description, AudienceRule? audience,
        CampaignStatus? status, CancellationToken ct)
    {
        await LockCampaignRowAsync(campaignId, ct);
        var row = await db.Campaigns.Include(c => c.Tags).FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == campaignId, ct);
        if (row is null || row.Status == "Deleted")
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (row.Version != expectedVersion)
        {
            return (UpdateOutcome.VersionMismatch, null);
        }

        if (name is not null)
        {
            row.Name = name;
        }

        if (description is not null)
        {
            row.Description = description;
        }

        if (audience is not null)
        {
            db.CampaignTags.RemoveRange(row.Tags);
            row.Tags = AudienceTags(audience).Select(t => new CampaignTagRow { CampaignId = row.Id, Kind = t.kind, Tag = t.tag }).ToList();
        }

        if (status == CampaignStatus.Published && row.Status == "Draft")
        {
            row.Status = "Published";
            row.PublishedAt = DateTimeOffset.UtcNow;
        }
        else if (status == CampaignStatus.Archived && row.Status != "Archived")
        {
            row.Status = "Archived";
        }
        else if (status is not null)
        {
            return (UpdateOutcome.InvalidTransition, null);
        }

        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<UpdateOutcome> DeleteDraftAsync(Guid companyId, Guid campaignId, int expectedVersion, CancellationToken ct)
    {
        await LockCampaignRowAsync(campaignId, ct);
        var row = await db.Campaigns.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == campaignId, ct);
        if (row is null || row.Status == "Deleted")
        {
            return UpdateOutcome.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return UpdateOutcome.VersionMismatch;
        }

        if (row.Status != "Draft")
        {
            return UpdateOutcome.InvalidTransition;
        }

        // Tombstone: settings cascade, financial history keeps its reference, the code is freed (§2.3).
        row.Status = "Deleted";
        row.CodeNorm = "#DEL-" + row.Id.ToString("N");
        await db.Streams.Where(s => s.CampaignId == campaignId).ExecuteDeleteAsync(ct);
        await db.CampaignTags.Where(t => t.CampaignId == campaignId).ExecuteDeleteAsync(ct);
        await db.CampaignResources.Where(r => r.CampaignId == campaignId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    public async Task<(UpdateOutcome, CampaignRec?)> PutOwnerAsync(
        Guid companyId, Guid campaignId, int expectedVersion, int ownerMasterId, CancellationToken ct)
    {
        await LockCampaignRowAsync(campaignId, ct);
        var row = await LoadAsync(companyId, campaignId, ct);
        if (row is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (row.Version != expectedVersion)
        {
            return (UpdateOutcome.VersionMismatch, null);
        }

        row.OwnerMasterId = ownerMasterId;
        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<IReadOnlyList<CampaignResourceRec>> ListResourcesAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        var rows = await (from cr in db.CampaignResources
                          join r in db.Resources on cr.ResourceId equals r.Id
                          where cr.CampaignId == campaignId && r.CompanyId == companyId
                          orderby r.CodeNorm
                          select new { r.Id, r.CodeNorm, r.Name, r.Status }).ToListAsync(ct);
        return rows.Select(x => new CampaignResourceRec(x.Id, x.CodeNorm, x.Name, Enum.Parse<ResourceStatus>(x.Status))).ToList();
    }

    public async Task<(UpdateOutcome, IReadOnlyList<CampaignResourceRec>?)> PutResourcesAsync(
        Guid companyId, Guid campaignId, int expectedVersion, IReadOnlyList<Guid> resourceIds, CancellationToken ct)
    {
        await LockCampaignRowAsync(campaignId, ct);
        var row = await LoadAsync(companyId, campaignId, ct);
        if (row is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (row.Version != expectedVersion)
        {
            return (UpdateOutcome.VersionMismatch, null);
        }

        if (row.Status != "Draft")
        {
            return (UpdateOutcome.InvalidTransition, null);
        }

        await db.CampaignResources.Where(r => r.CampaignId == campaignId).ExecuteDeleteAsync(ct);
        db.CampaignResources.AddRange(resourceIds.Select(id => new CampaignResourceRow { CampaignId = campaignId, ResourceId = id }));
        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, await ListResourcesAsync(companyId, campaignId, ct));
    }

    public async Task<IReadOnlyList<Guid>> ListCampaignResourceIdsAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        return await db.CampaignResources.Where(r => r.CampaignId == campaignId).Select(r => r.ResourceId).ToListAsync(ct);
    }

    public async Task<StreamRec?> GetStreamAsync(Guid companyId, Guid streamId, CancellationToken ct)
    {
        var row = await (from s in db.Streams
                         join c in db.Campaigns on s.CampaignId equals c.Id
                         where s.Id == streamId && c.CompanyId == companyId
                         select s).FirstOrDefaultAsync(ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<StreamRec>> ListStreamsAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<CampaignCursor>(cursor);
        var query = db.Streams.Where(s => s.CampaignId == campaignId);
        if (boundary is not null)
        {
            query = query.Where(s => string.Compare(s.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(s => s.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new CampaignCursor(items[^1].Code)) : null;
        return new Page<StreamRec>(items, next);
    }

    public async Task<(UpdateOutcome, StreamRec?)> CreateStreamAsync(
        Guid companyId, Guid id, Guid campaignId, string codeNorm, string name, CancellationToken ct)
    {
        var campaign = await LoadAsync(companyId, campaignId, ct);
        if (campaign is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (await db.Streams.AnyAsync(s => s.CampaignId == campaignId && s.CodeNorm == codeNorm, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new StreamRow { CampaignId = campaignId, Id = id, CodeNorm = codeNorm, Name = name };
        db.Streams.Add(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, StreamRec?)> PatchStreamAsync(
        Guid companyId, Guid streamId, int expectedVersion, string? name, ContentStatus? status, CancellationToken ct)
    {
        // §4.4: child mutations raise the aggregate version — every writer takes the campaign
        // row lock BEFORE comparing versions, so one If-Match version yields exactly one
        // successful transition (T04/T06).
        var campaignId = await db.Streams.Where(s => s.Id == streamId).Select(s => (Guid?)s.CampaignId).FirstOrDefaultAsync(ct);
        if (campaignId is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        await LockCampaignRowAsync(campaignId.Value, ct);
        var row = await db.Streams.FindAsync(new object[] { streamId }, ct);
        if (row is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        var campaign = await db.Campaigns.FindAsync(new object[] { campaignId }, ct);
        if (campaign is null || campaign.CompanyId != companyId)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (row.Version != expectedVersion)
        {
            return (UpdateOutcome.VersionMismatch, null);
        }

        if (row.Status == "Archived")
        {
            return (UpdateOutcome.InvalidTransition, null);
        }

        if (name is not null)
        {
            row.Name = name;
        }

        if (status is { } statusValue)
        {
            row.Status = statusValue.ToString();
        }

        row.Version++;
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<TaskRec?> GetTaskAsync(Guid companyId, Guid taskId, CancellationToken ct)
    {
        var row = await LoadTaskAsync(companyId, taskId, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<TaskRec>> ListTasksAsync(Guid companyId, Guid streamId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<CampaignCursor>(cursor);
        var query = db.Tasks.Include(t => t.Tags).Include(t => t.RewardItems).Where(t => t.StreamId == streamId);
        if (boundary is not null)
        {
            query = query.Where(t => string.Compare(t.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(t => t.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new CampaignCursor(items[^1].Code)) : null;
        return new Page<TaskRec>(items, next);
    }

    public async Task<Page<TaskRec>> ListVisibleTasksAsync(
        Guid companyId, Guid streamId, IReadOnlyCollection<string> readerTags, int limit, string? cursor, CancellationToken ct)
    {
        // Audience filtering with lossless pagination: keep walking store pages until `limit`
        // visible tasks are collected or the stream ends; the returned cursor points right
        // after the last returned task, so hidden tasks in between are skipped, not returned.
        var tags = readerTags.ToHashSet();
        var visible = new List<TaskRec>();
        string? walk = cursor;
        string? next = null;
        while (true)
        {
            var page = await ListTasksAsync(companyId, streamId, limit + 1, walk, ct);
            visible.AddRange(page.Items.Where(t => t.Audience.Matches(tags)));
            next = page.NextCursor;
            if (next is null || visible.Count > limit)
            {
                break;
            }

            walk = next;
        }

        var items = visible.Take(limit).ToList();
        var boundary = items.Count < visible.Count ? CursorCodec.Encode(new CampaignCursor(items[^1].Code)) : null;
        return new Page<TaskRec>(items, boundary);
    }

    public async Task<(UpdateOutcome, TaskRec?)> CreateTaskAsync(
        Guid companyId, Guid id, Guid streamId, string codeNorm, string name, string? description, long goal,
        Domain.Periods.PeriodKind period, long streamPoints, IReadOnlyList<RewardItemRec> rewardItems, AudienceRule audience,
        CancellationToken ct)
    {
        var stream = await db.Streams.FindAsync(new object[] { streamId }, ct);
        if (stream is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        var campaign = await db.Campaigns.FindAsync(new object[] { stream.CampaignId }, ct);
        if (campaign is null || campaign.CompanyId != companyId)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (await db.Tasks.AnyAsync(t => t.StreamId == streamId && t.CodeNorm == codeNorm, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new TaskRow
        {
            StreamId = streamId,
            Id = id,
            CodeNorm = codeNorm,
            Name = name,
            Description = description,
            Goal = goal,
            Period = period.ToString(),
            StreamPoints = streamPoints,
        };
        row.Tags = AudienceTags(audience).Select(t => new TaskTagRow { TaskId = id, Kind = t.kind, Tag = t.tag }).ToList();
        row.RewardItems = rewardItems.Select(i => new TaskRewardItemRow { TaskId = id, ResourceId = i.ResourceId, Amount = i.Amount }).ToList();
        db.Tasks.Add(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, TaskRec?)> PatchTaskAsync(
        Guid companyId, Guid taskId, int expectedVersion, string? name, string? description, long? goal,
        Domain.Periods.PeriodKind? period, long? streamPoints, IReadOnlyList<RewardItemRec>? rewardItems, AudienceRule? audience,
        ContentStatus? status, CancellationToken ct)
    {
        // Aggregate row lock before the version compare (§4.4) — see PatchStreamAsync.
        var campaignId = await (from t in db.Tasks
                                join s in db.Streams on t.StreamId equals s.Id
                                where t.Id == taskId
                                select (Guid?)s.CampaignId).FirstOrDefaultAsync(ct);
        if (campaignId is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        await LockCampaignRowAsync(campaignId.Value, ct);
        var row = await LoadTaskAsync(companyId, taskId, ct);
        if (row is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (row.Version != expectedVersion)
        {
            return (UpdateOutcome.VersionMismatch, null);
        }

        if (row.Status == "Archived")
        {
            return (UpdateOutcome.InvalidTransition, null);
        }

        if (name is not null)
        {
            row.Name = name;
        }

        if (description is not null)
        {
            row.Description = description;
        }

        if (goal is not null)
        {
            row.Goal = goal.Value;
        }

        if (period is not null)
        {
            row.Period = period.Value.ToString();
        }

        if (streamPoints is not null)
        {
            row.StreamPoints = streamPoints.Value;
        }

        if (rewardItems is not null)
        {
            db.TaskRewardItems.RemoveRange(row.RewardItems);
            row.RewardItems = rewardItems.Select(i => new TaskRewardItemRow { TaskId = taskId, ResourceId = i.ResourceId, Amount = i.Amount }).ToList();
        }

        if (audience is not null)
        {
            db.TaskTags.RemoveRange(row.Tags);
            row.Tags = AudienceTags(audience).Select(t => new TaskTagRow { TaskId = taskId, Kind = t.kind, Tag = t.tag }).ToList();
        }

        if (status is { } statusValue)
        {
            row.Status = statusValue.ToString();
        }

        row.Version++;
        var stream = await db.Streams.FindAsync(new object[] { row.StreamId }, ct);
        var campaign = stream is null ? null : await db.Campaigns.FindAsync(new object[] { stream.CampaignId }, ct);
        if (campaign is not null)
        {
            campaign.Version++;
        }

        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<MilestoneRec?> GetMilestoneAsync(Guid companyId, Guid milestoneId, CancellationToken ct)
    {
        var row = await (from m in db.Milestones
                         join s in db.Streams on m.StreamId equals s.Id
                         join c in db.Campaigns on s.CampaignId equals c.Id
                         where m.Id == milestoneId && c.CompanyId == companyId
                         select m).FirstOrDefaultAsync(ct);
        return row is null ? null : new MilestoneRec(row.Id, row.StreamId, row.Threshold, row.AchievementId, row.Version);
    }

    public async Task<Page<MilestoneRec>> ListMilestonesAsync(Guid companyId, Guid streamId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<ThresholdCursor>(cursor);
        var query = db.Milestones.Where(m => m.StreamId == streamId);
        if (boundary is not null)
        {
            query = query.Where(m => m.Threshold > boundary.Threshold);
        }

        var rows = await query.OrderBy(m => m.Threshold).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(m => new MilestoneRec(m.Id, m.StreamId, m.Threshold, m.AchievementId, m.Version)).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new ThresholdCursor(items[^1].Threshold)) : null;
        return new Page<MilestoneRec>(items, next);
    }

    public async Task<(UpdateOutcome, MilestoneRec?)> CreateMilestoneAsync(
        Guid companyId, Guid id, Guid streamId, long threshold, Guid achievementId, CancellationToken ct)
    {
        var stream = await db.Streams.FindAsync(new object[] { streamId }, ct);
        if (stream is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        var campaign = await db.Campaigns.FindAsync(new object[] { stream.CampaignId }, ct);
        if (campaign is null || campaign.CompanyId != companyId)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (await db.Milestones.AnyAsync(m => m.StreamId == streamId && m.Threshold == threshold && m.AchievementId == achievementId, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new MilestoneRow { StreamId = streamId, Id = id, Threshold = threshold, AchievementId = achievementId };
        db.Milestones.Add(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, new MilestoneRec(row.Id, row.StreamId, row.Threshold, row.AchievementId, row.Version));
    }

    public async Task<UpdateOutcome> DeleteMilestoneAsync(Guid companyId, Guid milestoneId, int expectedVersion, CancellationToken ct)
    {
        // Aggregate row lock before the version compare (§4.4) — see PatchStreamAsync.
        var campaignId = await (from m in db.Milestones
                                join s in db.Streams on m.StreamId equals s.Id
                                where m.Id == milestoneId
                                select (Guid?)s.CampaignId).FirstOrDefaultAsync(ct);
        if (campaignId is null)
        {
            return UpdateOutcome.NotFound;
        }

        await LockCampaignRowAsync(campaignId.Value, ct);
        var row = await db.Milestones.FindAsync(new object[] { milestoneId }, ct);
        if (row is null)
        {
            return UpdateOutcome.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return UpdateOutcome.VersionMismatch;
        }

        var stream = await db.Streams.FindAsync(new object[] { row.StreamId }, ct);
        var campaign = stream is null ? null : await db.Campaigns.FindAsync(new object[] { stream.CampaignId }, ct);
        if (campaign is null || campaign.CompanyId != companyId)
        {
            return UpdateOutcome.NotFound;
        }

        db.Milestones.Remove(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    public async Task<ChallengeRec?> GetChallengeAsync(Guid companyId, Guid challengeId, CancellationToken ct)
    {
        var row = await (from ch in db.Challenges
                         join c in db.Campaigns on ch.CampaignId equals c.Id
                         where ch.Id == challengeId && c.CompanyId == companyId
                         select ch).FirstOrDefaultAsync(ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<ChallengeRec>> ListChallengesAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<StartsAtCursor>(cursor);
        var query = db.Challenges.Where(ch => ch.CampaignId == campaignId);
        if (boundary is not null)
        {
            query = query.Where(ch => ch.StartsAt > boundary.At);
        }

        var rows = await query.OrderBy(ch => ch.StartsAt).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new StartsAtCursor(items[^1].StartsAt)) : null;
        return new Page<ChallengeRec>(items, next);
    }

    public async Task<(UpdateOutcome, ChallengeRec?)> CreateChallengeAsync(
        Guid companyId, Guid id, Guid campaignId, Guid streamId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        var campaign = await LoadAsync(companyId, campaignId, ct);
        if (campaign is null)
        {
            return (UpdateOutcome.NotFound, null);
        }

        if (await db.Challenges.AnyAsync(ch => ch.CampaignId == campaignId && ch.StreamId == streamId && ch.StartsAt == startsAt && ch.EndsAt == endsAt, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new ChallengeRow { CampaignId = campaignId, StreamId = streamId, Id = id, StartsAt = startsAt, EndsAt = endsAt };
        db.Challenges.Add(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<UpdateOutcome> DeleteChallengeAsync(Guid companyId, Guid challengeId, int expectedVersion, CancellationToken ct)
    {
        // Aggregate row lock before the version compare (§4.4) — see PatchStreamAsync.
        var campaignId = await db.Challenges.Where(ch => ch.Id == challengeId).Select(ch => (Guid?)ch.CampaignId).FirstOrDefaultAsync(ct);
        if (campaignId is null)
        {
            return UpdateOutcome.NotFound;
        }

        await LockCampaignRowAsync(campaignId.Value, ct);
        var row = await db.Challenges.FindAsync(new object[] { challengeId }, ct);
        if (row is null)
        {
            return UpdateOutcome.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return UpdateOutcome.VersionMismatch;
        }

        var campaign = await db.Campaigns.FindAsync(new object[] { campaignId }, ct);
        if (campaign is null || campaign.CompanyId != companyId)
        {
            return UpdateOutcome.NotFound;
        }

        db.Challenges.Remove(row);
        campaign.Version++;
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    public async Task<IReadOnlyList<ChallengeRec>> ListChallengesOfStreamAsync(Guid companyId, Guid streamId, CancellationToken ct)
    {
        var rows = await (from ch in db.Challenges
                          join c in db.Campaigns on ch.CampaignId equals c.Id
                          where ch.StreamId == streamId && c.CompanyId == companyId
                          select ch).ToListAsync(ct);
        return rows.Select(ToRec).ToList();
    }

    public async Task<IReadOnlyList<ChallengeRec>> ListChallengesOfCampaignAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        var rows = await db.Challenges.Where(ch => ch.CampaignId == campaignId).ToListAsync(ct);
        return rows.Select(ToRec).ToList();
    }

    public async Task MarkChallengeFinalizedAsync(Guid companyId, Guid challengeId, DateTimeOffset finalizedAt, CancellationToken ct)
    {
        await db.Challenges.Where(ch => ch.Id == challengeId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinalizedAt, finalizedAt), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<CampaignRow?> LoadAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        return await db.Campaigns.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == campaignId, ct);
    }

    private async Task LockCampaignRowAsync(Guid campaignId, CancellationToken ct)
    {
        // The aggregate version (ETag source) must change atomically: all writers take the
        // campaign row lock before comparing versions (§4.4, T06).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM campaigns WHERE id = {campaignId} FOR UPDATE", ct);
        db.ChangeTracker.Clear();
    }

    private async Task<TaskRow?> LoadTaskAsync(Guid companyId, Guid taskId, CancellationToken ct)
    {
        var row = await (from t in db.Tasks.Include(x => x.Tags).Include(x => x.RewardItems)
                         join s in db.Streams on t.StreamId equals s.Id
                         join c in db.Campaigns on s.CampaignId equals c.Id
                         where t.Id == taskId && c.CompanyId == companyId
                         select t).FirstOrDefaultAsync(ct);
        return row;
    }

    internal static CampaignRec ToRec(CampaignRow row)
    {
        var any = row.Tags.Where(t => t.Kind == "any").Select(t => t.Tag).ToHashSet();
        var all = row.Tags.Where(t => t.Kind == "all").Select(t => t.Tag).ToHashSet();
        var none = row.Tags.Where(t => t.Kind == "none").Select(t => t.Tag).ToHashSet();
        return new CampaignRec(
            row.Id, row.CodeNorm, row.Name, row.Description, row.Season, row.OwnerMasterId, row.StartsAt, row.EndsAt,
            Enum.Parse<CampaignStatus>(row.Status), new AudienceRule(any, all, none), row.Version, row.CreatedAt, row.PublishedAt);
    }

    internal static StreamRec ToRec(StreamRow row)
        => new(row.Id, row.CampaignId, row.CodeNorm, row.Name, Enum.Parse<ContentStatus>(row.Status), row.Version);

    internal static TaskRec ToRec(TaskRow row)
        => new(row.Id, row.StreamId, row.CodeNorm, row.Name, row.Description, row.Goal,
            Enum.Parse<Domain.Periods.PeriodKind>(row.Period), row.StreamPoints,
            row.RewardItems.Select(i => new RewardItemRec(i.ResourceId, i.Amount)).OrderBy(i => i.ResourceId).ToArray(),
            new AudienceRule(
                row.Tags.Where(t => t.Kind == "any").Select(t => t.Tag).ToHashSet(),
                row.Tags.Where(t => t.Kind == "all").Select(t => t.Tag).ToHashSet(),
                row.Tags.Where(t => t.Kind == "none").Select(t => t.Tag).ToHashSet()),
            Enum.Parse<ContentStatus>(row.Status), row.Version);

    internal static ChallengeRec ToRec(ChallengeRow row)
        => new(row.Id, row.CampaignId, row.StreamId, row.StartsAt, row.EndsAt, row.FinalizedAt, row.Version);

    internal static IEnumerable<(string kind, string tag)> AudienceTags(AudienceRule audience)
    {
        return audience.Any.Select(t => ("any", t))
            .Concat(audience.All.Select(t => ("all", t)))
            .Concat(audience.None.Select(t => ("none", t)));
    }

    public sealed record CampaignCursor(string Code);
    public sealed record ThresholdCursor(long Threshold);
    public sealed record StartsAtCursor(DateTimeOffset At);
}
