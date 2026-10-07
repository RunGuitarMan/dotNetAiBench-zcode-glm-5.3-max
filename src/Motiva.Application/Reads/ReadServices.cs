using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using NodaTime;
using DomainPeriod = Motiva.Domain.Periods;

namespace Motiva.Application.Reads;

/// <summary>Read surface (B34): wallets, operation histories, achievements, own progress and
/// campaign progress. Personal data is scoped by the token subject; the owner sees campaign
/// expenses only, never the participant wallets (B34.2, B35.5).</summary>
public sealed class ReadService(
    IEmployeeDirectory employees,
    ICampaignCatalog campaigns,
    IWalletLedger wallets,
    IOperationsReadStore operationsRead,
    IProgressLog progress,
    TimeProvider timeProvider)
{
    public async Task<WalletDto> GetOwnWalletAsync(ActorContext actor, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        var employee = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        Authz.EnsureActiveUser(actor, employee);
        var balances = await wallets.GetWalletAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        return new WalletDto(actor.MasterId!.Value, balances.Select(DtoMapper.ToDto).ToArray(), timeProvider.GetUtcNow());
    }

    public async Task<WalletDto> GetEmployeeWalletAsync(ActorContext actor, int masterId, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        Guard.MasterId(masterId);
        _ = await employees.GetAsync(actor.CompanyId, masterId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var balances = await wallets.GetWalletAsync(actor.CompanyId, masterId, ct);
        return new WalletDto(masterId, balances.Select(DtoMapper.ToDto).ToArray(), timeProvider.GetUtcNow());
    }

    public Task<Page<OperationRec>> ListOwnOperationsAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, OperationResult? result, CancellationToken ct)
    {
        return operationsRead.ListOwnAsync(actor, limit, cursor, from, toUtc, resourceId, kind, result, ct);
    }

    public async Task<Page<OperationRec>> ListEmployeeOperationsAsync(
        ActorContext actor, int masterId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        Guard.MasterId(masterId);
        _ = await employees.GetAsync(actor.CompanyId, masterId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        return await operationsRead.ListForEmployeeAsync(actor, masterId, limit, cursor, from, toUtc, resourceId, kind, ct);
    }

    public async Task<Page<OperationRec>> ListCampaignOperationsAsync(
        ActorContext actor, Guid campaignId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        BudgetService.EnsureOwnerOrAdmin(actor, campaign);
        return await operationsRead.ListForCampaignAsync(actor, campaignId, limit, cursor, from, toUtc, resourceId, ct);
    }

    public async Task<OperationRec> GetOperationAsync(ActorContext actor, Guid operationId, CancellationToken ct)
    {
        var operation = await operationsRead.GetAsync(actor, operationId, ct);
        if (operation is null)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        return operation;
    }

    public async Task<Page<AchievementGrantRec>> ListOwnAchievementsAsync(
        ActorContext actor, int? season, int limit, string? cursor, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        return await progress.ListAchievementsAsync(actor.CompanyId, actor.MasterId!.Value, season, limit, cursor, ct);
    }

    public async Task<CampaignProgressDto> GetOwnCampaignProgressAsync(ActorContext actor, Guid campaignId, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status == CampaignStatus.Deleted || campaign.Status == CampaignStatus.Draft)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        var employee = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        if (employee is null || !employee.IsActive)
        {
            throw new MotivaException(ErrorCode.AuthzEmployeeNotActive);
        }

        if (!campaign.Audience.Matches(new HashSet<string>(employee.Tags)))
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "The campaign is outside your audience.");
        }

        return await operationsRead.BuildCampaignProgressAsync(actor, campaign, actor.MasterId!.Value, ct);
    }

    public async Task<Page<ParticipantProgressDto>> ListCampaignProgressAsync(
        ActorContext actor, Guid campaignId, int limit, string? cursor, Guid? streamId, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        BudgetService.EnsureOwnerOrAdmin(actor, campaign);
        return await operationsRead.ListCampaignProgressAsync(actor, campaign, limit, cursor, streamId, ct);
    }

    public Task<Page<AuditRec>> ListAuditRecordsAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, string? entityType, string? entityId, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        return operationsRead.ListAuditAsync(actor.CompanyId, limit, cursor, from, toUtc, entityType, entityId, ct);
    }
}

/// <summary>Operation history reads with per-actor visibility (§3.3): the service reads only
/// Spend/SpendReversal rows of whole granted pairs of its company; employees only their own
/// wallet; admins the whole company; GET of a foreign personal object is a 404.</summary>
public interface IOperationsReadStore
{
    Task<Page<OperationRec>> ListOwnAsync(
        ActorContext actor, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, OperationResult? result, CancellationToken ct);

    Task<Page<OperationRec>> ListForEmployeeAsync(
        ActorContext actor, int masterId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, CancellationToken ct);

    Task<Page<OperationRec>> ListForCampaignAsync(
        ActorContext actor, Guid campaignId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId, CancellationToken ct);

    Task<OperationRec?> GetAsync(ActorContext actor, Guid operationId, CancellationToken ct);

    Task<Page<AuditRec>> ListAuditAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, string? entityType, string? entityId, CancellationToken ct);

    Task<CampaignProgressDto> BuildCampaignProgressAsync(ActorContext actor, CampaignRec campaign, int masterId, CancellationToken ct);

    Task<Page<ParticipantProgressDto>> ListCampaignProgressAsync(
        ActorContext actor, CampaignRec campaign, int limit, string? cursor, Guid? streamId, CancellationToken ct);
}
