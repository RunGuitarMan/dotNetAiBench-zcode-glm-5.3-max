using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

/// <summary>Resources (B06–B07): the code is unique in the company ignoring case, immutable,
/// never freed by archiving; archiving is irreversible; balances of a new resource are zero.</summary>
public sealed class ResourcesService(
    CurrentRights rights,
    IResourceDirectory resources,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    private const string locationPrefix = "/api/v1/resources/";

    public async Task<CommandResponse> CreateAsync(
        ActorContext actor, string? rawCode, string name, string? idempotencyKey, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { code, name });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "resource.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body, echo.Location is not null ? locationPrefix + echo.Location : null);
        }

        if (await resources.GetByCodeAsync(actor.CompanyId, code, ct) is not null)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Resource code already exists in the company.");
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await resources.CreateAsync(actor.CompanyId, id, code, name, now, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Resource code already exists in the company.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("resource.created", "Resource", id.ToString(), new[] { new AuditChangeRec("code", null, code) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "resource.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/resources/" + id.ToString());
    }

    public async Task<CommandResponse> PatchAsync(
        ActorContext actor, Guid id, string? ifMatch, string? name, ResourceStatus? status, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var version = ETags.ParseRequired(ifMatch);
        if (name is null && status is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "At least one field is required.");
        }

        if (name is not null)
        {
            Guard.Name(name);
        }
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await resources.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var (outcome, value) = await resources.PatchAsync(actor.CompanyId, id, version, name, status, ct);
        if (outcome == UpdateOutcome.NotFound)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        if (outcome == UpdateOutcome.VersionMismatch)
        {
            throw new MotivaException(ErrorCode.PreconditionFailed);
        }

        var changes = new List<AuditChangeRec>();
        if (name is not null && name != before.Name)
        {
            changes.Add(new AuditChangeRec("name", before.Name, name));
        }

        if (status is not null && status != before.Status)
        {
            if (before.Status == ResourceStatus.Archived)
            {
                throw new MotivaException(ErrorCode.ConflictState, "Archiving is irreversible.");
            }

            changes.Add(new AuditChangeRec("status", before.Status.ToString(), status.ToString()));
        }

        await audit.AppendAsync(actor.CompanyId, actor, new AuditInput("resource.patched", "Resource", id.ToString(), changes), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<ResourceRec> GetAsync(ActorContext actor, Guid id, CancellationToken ct)
        => await resources.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);

    public async Task<Page<ResourceRec>> ListAsync(ActorContext actor, ResourceStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        await rights.EnsureActiveEmployeeAsync(actor, ct); // any company participant reads the catalog
        return await resources.ListAsync(actor.CompanyId, status, limit, cursor, ct);
    }
}
