using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

/// <summary>Achievement definitions (B29.5, B30.1): created by the administrator; the code is
/// unique in the company ignoring case and immutable; archiving is not provided by the source.</summary>
public sealed class AchievementsService(
    IAchievementDirectory achievements,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> CreateAsync(
        ActorContext actor, string? rawCode, string name, string? description, string? idempotencyKey, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        Guard.Description(description);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { code, name });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "achievement.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await achievements.CreateAsync(actor.CompanyId, id, code, name, description, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Achievement code already exists.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("achievement.created", "Achievement", id.ToString(), new[] { new AuditChangeRec("code", null, code) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "achievement.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/achievements/" + id.ToString());
    }

    public async Task<CommandResponse> PatchAsync(
        ActorContext actor, Guid id, string? ifMatch, string? name, string? description, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        var version = ETags.ParseRequired(ifMatch);
        if (name is not null)
        {
            Guard.Name(name);
        }
        Guard.Description(description);
        if (name is null && description is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "At least one field is required.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await achievements.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var (outcome, value) = await achievements.PatchAsync(actor.CompanyId, id, version, name, description, ct);
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

        if (description is not null && description != before.Description)
        {
            changes.Add(new AuditChangeRec("description", before.Description, description));
        }

        await audit.AppendAsync(actor.CompanyId, actor, new AuditInput("achievement.patched", "Achievement", id.ToString(), changes), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<AchievementRec> GetAsync(ActorContext actor, Guid id, CancellationToken ct)
        => await achievements.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);

    public Task<Page<AchievementRec>> ListAsync(ActorContext actor, int limit, string? cursor, CancellationToken ct)
        => achievements.ListAsync(actor.CompanyId, limit, cursor, ct);
}
