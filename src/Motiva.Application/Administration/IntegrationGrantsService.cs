using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

/// <summary>Integration grants with strict target combinations (B04, A2-05): Progress → campaign;
/// Award → campaign + resource from the campaign set; Spend → resource + purchase system.
/// Revocation acts for new requests immediately, including replays (B05.1).</summary>
public sealed class IntegrationGrantsService(
    IIntegrationDirectory grants,
    ICampaignCatalog campaigns,
    IResourceDirectory resources,
    IPurchaseSystemDirectory systems,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> CreateAsync(
        ActorContext actor,
        string subject,
        GrantKind kind,
        Guid? campaignId,
        Guid? resourceId,
        Guid? purchaseSystemId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 100)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "subject must be 1..100 characters.");
        }

        var target = await ValidateTarget(actor, kind, campaignId, resourceId, purchaseSystemId, ct);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { subject, kind = kind.ToString(), campaignId, resourceId, purchaseSystemId });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "grant.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await grants.CreateAsync(
            actor.CompanyId, id, subject, kind, campaignId, resourceId, purchaseSystemId, now, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Identical grant already exists.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput(
                "grant.created", "IntegrationGrant", id.ToString(),
                new[] { new AuditChangeRec("target", null, kind + ":" + target) }),
            now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "grant.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/integration-grants/" + id.ToString());
    }

    public async Task<CommandResponse> RevokeAsync(ActorContext actor, Guid id, string? ifMatch, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        var version = ETags.ParseRequired(ifMatch);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await grants.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (before.Status == GrantStatus.Revoked)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Grant is already revoked.");
        }

        var outcome = await grants.RevokeAsync(actor.CompanyId, id, version, now, ct);
        if (outcome == UpdateOutcome.VersionMismatch)
        {
            throw new MotivaException(ErrorCode.PreconditionFailed);
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("grant.revoked", "IntegrationGrant", id.ToString(), new[] { new AuditChangeRec("status", "Active", "Revoked") }),
            now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(204, string.Empty);
    }

    public async Task<IntegrationGrantRec> GetAsync(ActorContext actor, Guid id, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        return await grants.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
    }

    public Task<Page<IntegrationGrantRec>> ListAsync(ActorContext actor, string? subject, int limit, string? cursor, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        return grants.ListAsync(actor.CompanyId, subject, limit, cursor, ct);
    }

    private async Task<string> ValidateTarget(
        ActorContext actor,
        GrantKind kind,
        Guid? campaignId,
        Guid? resourceId,
        Guid? purchaseSystemId,
        CancellationToken ct)
    {
        switch (kind)
        {
            case GrantKind.Progress:
                if (campaignId is null || resourceId is not null || purchaseSystemId is not null)
                {
                    throw new MotivaException(ErrorCode.ValidationGrantTarget);
                }

                await EnsureCampaignExists(actor, campaignId!.Value, ct);
                return "campaign:" + campaignId.Value;
            case GrantKind.Award:
                if (campaignId is null || resourceId is null || purchaseSystemId is not null)
                {
                    throw new MotivaException(ErrorCode.ValidationGrantTarget);
                }

                await EnsureCampaignExists(actor, campaignId.Value, ct);
                var campaignResourceIds = await campaigns.ListCampaignResourceIdsAsync(actor.CompanyId, campaignId.Value, ct);
                if (!campaignResourceIds.Contains(resourceId.Value))
                {
                    throw new MotivaException(ErrorCode.ValidationGrantTarget, "Award resource must be part of the campaign set.");
                }

                return "campaign:" + campaignId.Value + "/resource:" + resourceId.Value;
            case GrantKind.Spend:
                if (resourceId is null || purchaseSystemId is null || campaignId is not null)
                {
                    throw new MotivaException(ErrorCode.ValidationGrantTarget);
                }

                var resource = await resources.GetAsync(actor.CompanyId, resourceId.Value, ct)
                    ?? throw new MotivaException(ErrorCode.NotFound);
                var system = await systems.GetAsync(actor.CompanyId, purchaseSystemId.Value, ct)
                    ?? throw new MotivaException(ErrorCode.NotFound);
                return "resource:" + resource.Id + "/system:" + system.Id;
            default:
                throw new MotivaException(ErrorCode.ValidationGrantTarget);
        }
    }

    private async Task<CampaignRec> EnsureCampaignExists(ActorContext actor, Guid campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct);
        if (campaign is null)
        {
            throw new MotivaException(ErrorCode.NotFound, "Campaign not found.");
        }

        return campaign;
    }
}
