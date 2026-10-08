using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

/// <summary>Purchase systems and their accepted resources (B23.4) — administered by the company admin.</summary>
public sealed class PurchaseSystemsService(
    CurrentRights rights,
    IPurchaseSystemDirectory systems,
    IResourceDirectory resources,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    private const string locationPrefix = "/api/v1/purchase-systems/";

    public async Task<CommandResponse> CreateAsync(
        ActorContext actor, string? rawCode, string name, IReadOnlyList<Guid> acceptedResourceIds, string? idempotencyKey, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { code, name, acceptedResourceIds = acceptedResourceIds.OrderBy(r => r).ToArray() });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "purchase-system.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body, echo.Location is not null ? locationPrefix + echo.Location : null);
        }

        await EnsureResourcesExistAsync(actor, acceptedResourceIds, ct);
        var id = Guid.NewGuid();
        var (outcome, value) = await systems.CreateAsync(actor.CompanyId, id, code, name, acceptedResourceIds, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Purchase system code already exists.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("purchase-system.created", "PurchaseSystem", id.ToString(), new[] { new AuditChangeRec("code", null, code) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "purchase-system.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/purchase-systems/" + id.ToString());
    }

    public async Task<CommandResponse> PatchAsync(
        ActorContext actor,
        Guid id,
        string? ifMatch,
        string? name,
        ContentStatus? status,
        IReadOnlyList<Guid>? acceptedResourceIds,
        CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var version = ETags.ParseRequired(ifMatch);
        if (name is not null)
        {
            Guard.Name(name);
        }
        if (name is null && status is null && acceptedResourceIds is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "At least one field is required.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await systems.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (acceptedResourceIds is not null)
        {
            await EnsureResourcesExistAsync(actor, acceptedResourceIds, ct);
        }

        var (outcome, value) = await systems.PatchAsync(actor.CompanyId, id, version, name, status, acceptedResourceIds, ct);
        if (outcome == UpdateOutcome.NotFound)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        if (outcome == UpdateOutcome.VersionMismatch)
        {
            throw new MotivaException(ErrorCode.PreconditionFailed);
        }

        if (status is not null && before.Status == ContentStatus.Archived && status != ContentStatus.Archived)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Archiving is irreversible.");
        }

        var changes = new List<AuditChangeRec>();
        if (name is not null && name != before.Name)
        {
            changes.Add(new AuditChangeRec("name", before.Name, name));
        }

        if (status is not null && status != before.Status)
        {
            changes.Add(new AuditChangeRec("status", before.Status.ToString(), status.ToString()));
        }

        if (acceptedResourceIds is not null)
        {
            changes.Add(new AuditChangeRec(
                "acceptedResourceIds",
                string.Join(",", before.AcceptedResourceIds.OrderBy(r => r)),
                string.Join(",", acceptedResourceIds.OrderBy(r => r))));
        }

        await audit.AppendAsync(actor.CompanyId, actor, new AuditInput("purchase-system.patched", "PurchaseSystem", id.ToString(), changes), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<PurchaseSystemRec> GetAsync(ActorContext actor, Guid id, CancellationToken ct)
        => await systems.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);

    public async Task<Page<PurchaseSystemRec>> ListAsync(ActorContext actor, int limit, string? cursor, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        return await systems.ListAsync(actor.CompanyId, limit, cursor, ct);
    }

    private async Task EnsureResourcesExistAsync(ActorContext actor, IReadOnlyList<Guid> acceptedResourceIds, CancellationToken ct)
    {
        if (acceptedResourceIds.Count == 0)
        {
            return;
        }

        var found = await resources.ListByIdsAsync(actor.CompanyId, acceptedResourceIds, ct);
        if (found.Count != acceptedResourceIds.Distinct().Count())
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "acceptedResourceIds must reference company resources.");
        }

        if (found.Any(r => r.Status == ResourceStatus.Archived))
        {
            // An archived resource cannot enter new settings (B07.2).
            throw new MotivaException(ErrorCode.ValidationFailed, "Archived resources cannot be accepted by a purchase system.");
        }
    }
}
