using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

public sealed class CompanyStore(MotivaDbContext db) : ICompanyDirectory
{
    public async Task<CompanyRec?> GetAsync(Guid companyId, CancellationToken ct)
    {
        var row = await db.Companies.FindAsync(new object[] { companyId }, ct);
        return row is null ? null : new CompanyRec(row.Id, row.Name, row.TimeZoneId);
    }

    public async Task<bool> EnsureCreatedAsync(Guid companyId, string name, string timeZoneId, CancellationToken ct)
    {
        if (await db.Companies.FindAsync(new object[] { companyId }, ct) is not null)
        {
            return false;
        }

        db.Companies.Add(new CompanyRow { Id = companyId, Name = name, TimeZoneId = timeZoneId });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class EmployeeStore(MotivaDbContext db) : IEmployeeDirectory
{
    public async Task<EmployeeRec?> GetAsync(Guid companyId, int masterId, CancellationToken ct)
    {
        var row = await db.Employees
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.MasterId == masterId, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<EmployeeRec>> ListAsync(Guid companyId, bool? active, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<MasterIdCursor>(cursor);
        var query = db.Employees.Include(e => e.Tags)
            .Where(e => e.CompanyId == companyId);
        if (active is { } value)
        {
            query = query.Where(e => e.IsActive == value);
        }

        if (boundary is not null)
        {
            query = query.Where(e => e.MasterId > boundary.MasterId);
        }

        var rows = await query.OrderBy(e => e.MasterId).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new MasterIdCursor(items[^1].MasterId)) : null;
        return new Page<EmployeeRec>(items, next);
    }

    public async Task<UpdateOutcome> CreateAsync(
        Guid companyId, int masterId, IReadOnlyList<string> tags, DateTimeOffset utcNow, CancellationToken ct)
    {
        if (await db.Employees.AnyAsync(e => e.CompanyId == companyId && e.MasterId == masterId, ct))
        {
            return UpdateOutcome.AlreadyExists;
        }

        db.Employees.Add(new EmployeeRow { CompanyId = companyId, MasterId = masterId, CreatedAt = utcNow, IsActive = true, Version = 1 });
        db.EmployeeTags.AddRange(tags.Select(t => new EmployeeTagRow { CompanyId = companyId, MasterId = masterId, Tag = t }));
        // Exactly one wallet per profile, created in the same transaction (B01.5).
        db.Wallets.Add(new WalletRow { CompanyId = companyId, MasterId = masterId, CreatedAt = utcNow });
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    public async Task<UpdateOutcome> PatchAsync(
        Guid companyId, int masterId, int expectedVersion, bool? isActive, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        var row = await db.Employees.Include(e => e.Tags)
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.MasterId == masterId, ct);
        if (row is null)
        {
            return UpdateOutcome.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return UpdateOutcome.VersionMismatch;
        }

        if (isActive is { } value)
        {
            row.IsActive = value;
        }

        if (tags is not null)
        {
            db.EmployeeTags.RemoveRange(row.Tags);
            db.EmployeeTags.AddRange(tags.Select(t => new EmployeeTagRow { CompanyId = companyId, MasterId = masterId, Tag = t }));
        }

        row.Version++;
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    internal static EmployeeRec ToRec(EmployeeRow row)
    {
        return new EmployeeRec(row.MasterId, row.IsActive, row.Tags.Select(t => t.Tag).OrderBy(t => t).ToArray(), row.Version, row.CreatedAt);
    }

    public sealed record MasterIdCursor(int MasterId);
}

public sealed class ResourceStore(MotivaDbContext db) : IResourceDirectory
{
    public async Task<ResourceRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var row = await db.Resources.FindAsync(new object[] { id }, ct);
        return row is null || row.CompanyId != companyId ? null : ToRec(row);
    }

    public async Task<ResourceRec?> GetByCodeAsync(Guid companyId, string codeNorm, CancellationToken ct)
    {
        var row = await db.Resources.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.CodeNorm == codeNorm, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<ResourceRec>> ListAsync(Guid companyId, ResourceStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<CodeCursor>(cursor);
        var query = db.Resources.Where(r => r.CompanyId == companyId);
        if (status is { } value)
        {
            query = query.Where(r => r.Status == value.ToString());
        }

        if (boundary is not null)
        {
            query = query.Where(r => string.Compare(r.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(r => r.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new CodeCursor(items[^1].Code)) : null;
        return new Page<ResourceRec>(items, next);
    }

    public async Task<(UpdateOutcome, ResourceRec?)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, DateTimeOffset utcNow, CancellationToken ct)
    {
        if (await db.Resources.AnyAsync(r => r.CompanyId == companyId && r.CodeNorm == codeNorm, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new ResourceRow { CompanyId = companyId, Id = id, CodeNorm = codeNorm, Name = name, CreatedAt = utcNow };
        db.Resources.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, ResourceRec?)> PatchAsync(
        Guid companyId, Guid id, int expectedVersion, string? name, ResourceStatus? status, CancellationToken ct)
    {
        var row = await db.Resources.FindAsync(new object[] { id }, ct);
        if (row is null || row.CompanyId != companyId)
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

        if (status is { } statusValue)
        {
            row.Status = statusValue.ToString();
        }

        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<IReadOnlyList<ResourceRec>> ListByIdsAsync(Guid companyId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Resources.Where(r => r.CompanyId == companyId && ids.Contains(r.Id)).ToListAsync(ct);
        return rows.Select(ToRec).ToList();
    }

    internal static ResourceRec ToRec(ResourceRow row)
        => new(row.Id, row.CodeNorm, row.Name, Enum.Parse<ResourceStatus>(row.Status), row.Version, row.CreatedAt);

    public sealed record CodeCursor(string Code);
}

public sealed class AchievementStore(MotivaDbContext db) : IAchievementDirectory
{
    public async Task<AchievementRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var row = await db.Achievements.FindAsync(new object[] { id }, ct);
        return row is null || row.CompanyId != companyId ? null : ToRec(row);
    }

    public async Task<Page<AchievementRec>> ListAsync(Guid companyId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<ResourceStore.CodeCursor>(cursor);
        var query = db.Achievements.Where(a => a.CompanyId == companyId);
        if (boundary is not null)
        {
            query = query.Where(a => string.Compare(a.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(a => a.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new ResourceStore.CodeCursor(items[^1].Code)) : null;
        return new Page<AchievementRec>(items, next);
    }

    public async Task<(UpdateOutcome, AchievementRec?)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, string? description, CancellationToken ct)
    {
        if (await db.Achievements.AnyAsync(a => a.CompanyId == companyId && a.CodeNorm == codeNorm, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new AchievementRow { CompanyId = companyId, Id = id, CodeNorm = codeNorm, Name = name, Description = description };
        db.Achievements.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, AchievementRec?)> PatchAsync(
        Guid companyId, Guid id, int expectedVersion, string? name, string? description, CancellationToken ct)
    {
        var row = await db.Achievements.FindAsync(new object[] { id }, ct);
        if (row is null || row.CompanyId != companyId)
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

        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    internal static AchievementRec ToRec(AchievementRow row) => new(row.Id, row.CodeNorm, row.Name, row.Description, row.Version);
}

public sealed class PurchaseSystemStore(MotivaDbContext db) : IPurchaseSystemDirectory
{
    public async Task<PurchaseSystemRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var row = await db.PurchaseSystems.Include(p => p.AcceptedResources)
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<PurchaseSystemRec>> ListAsync(Guid companyId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<ResourceStore.CodeCursor>(cursor);
        var query = db.PurchaseSystems.Include(p => p.AcceptedResources).Where(p => p.CompanyId == companyId);
        if (boundary is not null)
        {
            query = query.Where(p => string.Compare(p.CodeNorm, boundary.Code) > 0);
        }

        var rows = await query.OrderBy(p => p.CodeNorm).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new ResourceStore.CodeCursor(items[^1].Code)) : null;
        return new Page<PurchaseSystemRec>(items, next);
    }

    public async Task<(UpdateOutcome, PurchaseSystemRec?)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, IReadOnlyList<Guid> acceptedResourceIds, CancellationToken ct)
    {
        if (await db.PurchaseSystems.AnyAsync(p => p.CompanyId == companyId && p.CodeNorm == codeNorm, ct))
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new PurchaseSystemRow { CompanyId = companyId, Id = id, CodeNorm = codeNorm, Name = name };
        row.AcceptedResources = acceptedResourceIds.Select(r => new PurchaseSystemResourceRow { PurchaseSystemId = id, ResourceId = r }).ToList();
        db.PurchaseSystems.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<(UpdateOutcome, PurchaseSystemRec?)> PatchAsync(
        Guid companyId, Guid id, int expectedVersion, string? name, ContentStatus? status, IReadOnlyList<Guid>? acceptedResourceIds, CancellationToken ct)
    {
        var row = await db.PurchaseSystems.Include(p => p.AcceptedResources)
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);
        if (row is null)
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

        if (status is { } statusValue)
        {
            row.Status = statusValue.ToString();
        }

        if (acceptedResourceIds is not null)
        {
            db.PurchaseSystemResources.RemoveRange(row.AcceptedResources);
            row.AcceptedResources = acceptedResourceIds
                .Select(r => new PurchaseSystemResourceRow { PurchaseSystemId = id, ResourceId = r })
                .ToList();
        }

        row.Version++;
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    internal static PurchaseSystemRec ToRec(PurchaseSystemRow row)
        => new(row.Id, row.CodeNorm, row.Name, Enum.Parse<ContentStatus>(row.Status),
            row.AcceptedResources.Select(r => r.ResourceId).OrderBy(r => r).ToArray(), row.Version);
}

public sealed class IntegrationGrantStore(MotivaDbContext db) : IIntegrationDirectory
{
    public async Task<IntegrationGrantRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var row = await db.IntegrationGrants.FindAsync(new object[] { id }, ct);
        return row is null || row.CompanyId != companyId ? null : ToRec(row);
    }

    public async Task<Page<IntegrationGrantRec>> ListAsync(Guid companyId, string? subject, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<TimeIdCursor>(cursor);
        var query = db.IntegrationGrants.Where(g => g.CompanyId == companyId);
        if (subject is not null)
        {
            query = query.Where(g => g.Subject == subject);
        }

        if (boundary is not null)
        {
            query = query.Where(g => g.CreatedAt > boundary.At || (g.CreatedAt == boundary.At && g.Id.CompareTo(boundary.Id) > 0));
        }

        var rows = await query.OrderBy(g => g.CreatedAt).ThenBy(g => g.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new TimeIdCursor(rows[limit - 1].CreatedAt, rows[limit - 1].Id))
            : null;
        return new Page<IntegrationGrantRec>(items, next);
    }

    public async Task<(UpdateOutcome, IntegrationGrantRec?)> CreateAsync(
        Guid companyId, Guid id, string subject, GrantKind kind, Guid? campaignId, Guid? resourceId, Guid? purchaseSystemId,
        DateTimeOffset utcNow, CancellationToken ct)
    {
        var exists = await db.IntegrationGrants.AnyAsync(
            g => g.CompanyId == companyId && g.Subject == subject && g.Kind == kind.ToString()
                 && g.CampaignId == campaignId && g.ResourceId == resourceId && g.PurchaseSystemId == purchaseSystemId
                 && g.Status == "Active",
            ct);
        if (exists)
        {
            return (UpdateOutcome.AlreadyExists, null);
        }

        var row = new IntegrationGrantRow
        {
            CompanyId = companyId,
            Id = id,
            Subject = subject,
            Kind = kind.ToString(),
            CampaignId = campaignId,
            ResourceId = resourceId,
            PurchaseSystemId = purchaseSystemId,
            CreatedAt = utcNow,
        };
        db.IntegrationGrants.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<UpdateOutcome> RevokeAsync(Guid companyId, Guid id, int expectedVersion, DateTimeOffset utcNow, CancellationToken ct)
    {
        var row = await db.IntegrationGrants.FindAsync(new object[] { id }, ct);
        if (row is null || row.CompanyId != companyId)
        {
            return UpdateOutcome.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return UpdateOutcome.VersionMismatch;
        }

        row.Status = "Revoked";
        row.RevokedAt = utcNow;
        row.Version++;
        await db.SaveChangesAsync(ct);
        return UpdateOutcome.Ok;
    }

    public async Task<IntegrationGrantRec?> FindActiveAsync(
        Guid companyId, string subject, GrantKind kind, Guid? campaignId, Guid? resourceId, Guid? purchaseSystemId, CancellationToken ct)
    {
        var row = await db.IntegrationGrants.FirstOrDefaultAsync(
            g => g.CompanyId == companyId && g.Subject == subject && g.Kind == kind.ToString() && g.Status == "Active"
                 && g.CampaignId == campaignId && g.ResourceId == resourceId && g.PurchaseSystemId == purchaseSystemId,
            ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<IReadOnlyList<(Guid PurchaseSystemId, Guid ResourceId)>> ListActiveSpendPairsAsync(
        Guid companyId, string subject, CancellationToken ct)
    {
        var rows = await db.IntegrationGrants
            .Where(g => g.CompanyId == companyId && g.Subject == subject && g.Kind == "Spend" && g.Status == "Active")
            .ToListAsync(ct);
        return rows.Select(g => (g.PurchaseSystemId!.Value, g.ResourceId!.Value)).ToList();
    }

    internal static IntegrationGrantRec ToRec(IntegrationGrantRow row)
        => new(row.Id, row.Subject, Enum.Parse<GrantKind>(row.Kind), row.CampaignId, row.ResourceId, row.PurchaseSystemId,
            Enum.Parse<GrantStatus>(row.Status), row.Version, row.CreatedAt, row.RevokedAt);

    public sealed record TimeIdCursor(DateTimeOffset At, Guid Id);
}
