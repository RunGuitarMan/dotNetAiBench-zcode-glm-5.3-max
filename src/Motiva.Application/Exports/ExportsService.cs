using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Exports;

/// <summary>Export orders and download links (B35–B37, T07): the data set is fixed at the first
/// successful formation start, not at the order; links live ≤ 60 s; deletion forbids new links
/// immediately and the cleanup worker removes the bytes.</summary>
public sealed class ExportsService(
    IExportStore exports,
    IResourceDirectory resources,
    IEmployeeDirectory employees,
    IFileStorage storage,
    IBackgroundJobs jobs,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> CreateAsync(
        ActorContext actor,
        ExportScope scope,
        int? masterId,
        Guid? resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? idempotencyKey,
        CancellationToken ct)
    {
        if (actor.ActorType == ActorType.Service)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Exports are ordered by employees and administrators only.");
        }

        Guard.Range(fromUtc, toUtc);
        if (scope != ExportScope.Own && !actor.IsAdmin)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Only administrators may order employee/company exports.");
        }

        if (scope == ExportScope.Employee && masterId is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "masterId is required for scope=Employee.");
        }

        if (masterId is not null)
        {
            Guard.MasterId(masterId.Value);
            _ = await employees.GetAsync(actor.CompanyId, masterId.Value, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        }

        string? resourceCode = null;
        if (resourceId is not null)
        {
            var resource = await resources.GetAsync(actor.CompanyId, resourceId.Value, ct)
                ?? throw new MotivaException(ErrorCode.NotFound);
            resourceCode = resource.Code;
        }

        int? targetMasterId = scope switch
        {
            ExportScope.Own => actor.MasterId!.Value,
            ExportScope.Employee => masterId!.Value,
            _ => null,
        };
        var essential = CanonicalJson.Serialize(new { scope = scope.ToString(), masterId = targetMasterId, resourceId, fromUtc = fromUtc.UtcDateTime, toUtc = toUtc.UtcDateTime });
        var now = timeProvider.GetUtcNow();
        await using var scope2 = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "export.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await exports.CreateAsync(
            actor.CompanyId, id, actor, scope, targetMasterId, resourceId, fromUtc, toUtc, now, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState);
        }

        value = value! with { ResourceCode = resourceCode };

        await jobs.EnqueueAsync("form-export", CanonicalJson.Serialize(new { exportId = id }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "export.create", "-", idempotencyKey, 201, body, ct);
        await scope2.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/exports/" + id.ToString());
    }

    public async Task<ExportRec> GetAsync(ActorContext actor, Guid exportId, CancellationToken ct)
    {
        var export = await exports.GetAsync(actor.CompanyId, exportId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureRequesterAsync(actor, export, ct);
        return export;
    }

    public async Task<Page<ExportRec>> ListAsync(ActorContext actor, int limit, string? cursor, CancellationToken ct)
    {
        if (actor.IsAdmin)
        {
            return await exports.ListAsync(actor.CompanyId, null, limit, cursor, ct);
        }

        Authz.EnsureUser(actor);
        return await exports.ListAsync(actor.CompanyId, actor.MasterId!.Value, limit, cursor, ct);
    }

    public async Task<CommandResponse> DeleteAsync(ActorContext actor, Guid exportId, CancellationToken ct)
    {
        var export = await exports.GetAsync(actor.CompanyId, exportId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureRequesterAsync(actor, export, ct);
        if (export.Status == ExportStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.ConflictState, "The export is already deleted.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var marked = await exports.MarkDeletedAsync(actor.CompanyId, exportId, actor, now, ct);
        if (!marked)
        {
            throw new MotivaException(ErrorCode.ConflictState);
        }

        await jobs.EnqueueAsync("cleanup-export", CanonicalJson.Serialize(new { exportId }), now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(204, string.Empty);
    }

    public async Task<CommandResponse> CreateDownloadLinkAsync(ActorContext actor, Guid exportId, string? idempotencyKey, CancellationToken ct)
    {
        var export = await exports.GetAsync(actor.CompanyId, exportId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureRequesterAsync(actor, export, ct);
        if (export.Status == ExportStatus.Deleted)
        {
            // T05 exception: a deleted export never returns even the previously stored link.
            throw new MotivaException(ErrorCode.ConflictState, "The export is deleted.");
        }

        if (export.Status != ExportStatus.Ready)
        {
            throw new MotivaException(ErrorCode.ConflictState, "The export is not Ready.");
        }

        var essential = CanonicalJson.Serialize(new { exportId, s3Key = export.Id.ToString() + "/" + export.Generation.ToString() });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "download-link.create", exportId.ToString(), idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var now = timeProvider.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(60);
        DownloadLinkRec link;
        try
        {
            // Verify the immutable bytes are reachable before issuing the short-lived link;
            // an S3 outage yields a bounded 424 rather than a hanging request or a dead link (T06).
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await storage.ExistsAsync(S3Key(export.Id, export.Generation), bounded.Token))
            {
                throw new MotivaException(ErrorCode.ConflictState, "The export bytes are missing.");
            }

            link = await storage.CreateDownloadLinkAsync(S3Key(export.Id, export.Generation), lifetime, ct);
        }
        catch (Exception ex) when (ex is not MotivaException)
        {
            throw new MotivaException(ErrorCode.DependencyUnavailable, "S3 is temporarily unavailable.");
        }

        var body = CanonicalJson.Serialize(DtoMapper.ToDto(link));
        await gate.CompleteAsync(actor.CompanyId, actor, "download-link.create", exportId.ToString(), idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/exports/" + exportId.ToString() + "/download-links/" + link.Id.ToString());
    }

    public static string S3Key(Guid exportId, int generation) => "exports/" + exportId.ToString() + "/" + generation.ToString() + "/data.csv";

    private async Task EnsureRequesterAsync(ActorContext actor, ExportRec export, CancellationToken ct)
    {
        // The requester keeps their current rights: only the ordering employee/administrator
        // reads the export; the initiator must still be an active profile (B37.1, B05).
        var requester = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        Authz.EnsureActiveUser(actor, requester);
        if (!actor.IsAdmin && actor.MasterId != export.RequestedByMasterId)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }
    }
}
