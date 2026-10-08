using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

/// <summary>Employee profiles: creation gives exactly one wallet in the same transaction (B01.5);
/// blocking/reactivation acts immediately for new requests (B05.1); every change is audited (B28.3).</summary>
public sealed class EmployeesService(
    CurrentRights rights,
    IEmployeeDirectory employees,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    private const string locationPrefix = "/api/v1/employees/";

    public async Task<CommandResponse> CreateAsync(
        ActorContext actor, int masterId, IReadOnlyList<string> tags, string? idempotencyKey, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        Guard.MasterId(masterId);
        Guard.Tags(tags);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { masterId, tags = tags.OrderBy(t => t).ToArray() });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "employee.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body, echo.Location is not null ? locationPrefix + echo.Location : null);
        }

        var outcome = await employees.CreateAsync(actor.CompanyId, masterId, tags, now, ct);
        if (outcome == UpdateOutcome.AlreadyExists)
        {
            throw new MotivaException(ErrorCode.ConflictState, "masterId already exists in the company.");
        }

        var created = await employees.GetAsync(actor.CompanyId, masterId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("employee.created", "Employee", masterId.ToString(), new[] { new AuditChangeRec("isActive", null, "true") }),
            now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(created));
        await gate.CompleteAsync(actor.CompanyId, actor, "employee.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/employees/" + masterId.ToString());
    }

    public async Task<CommandResponse> PatchAsync(
        ActorContext actor, int masterId, string? ifMatch, bool? isActive, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var version = ETags.ParseRequired(ifMatch);
        if (tags is not null)
        {
            Guard.Tags(tags);
        }

        if (isActive is null && tags is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "At least one field is required.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await employees.GetAsync(actor.CompanyId, masterId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        var outcome = await employees.PatchAsync(actor.CompanyId, masterId, version, isActive, tags, ct);
        if (outcome == UpdateOutcome.NotFound)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        if (outcome == UpdateOutcome.VersionMismatch)
        {
            throw new MotivaException(ErrorCode.PreconditionFailed);
        }

        var after = await employees.GetAsync(actor.CompanyId, masterId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var changes = new List<AuditChangeRec>();
        if (isActive is not null && isActive != before.IsActive)
        {
            changes.Add(new AuditChangeRec("isActive", before.IsActive.ToString(), isActive.ToString()));
        }

        if (tags is not null)
        {
            changes.Add(new AuditChangeRec("tags", string.Join(",", before.Tags.OrderBy(t => t)), string.Join(",", tags.OrderBy(t => t))));
        }

        await audit.AppendAsync(actor.CompanyId, actor, new AuditInput("employee.patched", "Employee", masterId.ToString(), changes), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(after));
        return new CommandResponse(200, body, ETag: ETags.Format(after.Version));
    }

    public async Task<EmployeeRec> GetAsync(ActorContext actor, int masterId, CancellationToken ct)
    {
        if (actor.IsAdmin)
        {
            return await employees.GetAsync(actor.CompanyId, masterId, ct)
                ?? throw new MotivaException(ErrorCode.NotFound);
        }

        await rights.EnsureActiveEmployeeAsync(actor, ct);
        if (actor.MasterId != masterId)
        {
            // A foreign personal object is indistinguishable from a missing one (B02.3, T04).
            throw new MotivaException(ErrorCode.NotFound);
        }

        return await employees.GetAsync(actor.CompanyId, masterId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
    }

    public async Task<Page<EmployeeRec>> ListAsync(ActorContext actor, bool? active, int limit, string? cursor, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        return await employees.ListAsync(actor.CompanyId, active, limit, cursor, ct);
    }
}
