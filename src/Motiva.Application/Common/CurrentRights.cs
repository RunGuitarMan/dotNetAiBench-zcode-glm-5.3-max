using Motiva.Application.Ports;

namespace Motiva.Application.Common;

/// <summary>
/// Current-rights checks (B05, §3.0 step 2): the verified token identity must map to an ACTIVE
/// business profile for every action — writes, reads and replays alike. The Admin role only
/// exists for user actors; a blocked initiator (even an administrator) gets 403 with no effect.
/// </summary>
public sealed class CurrentRights(IEmployeeDirectory employees)
{
    public async Task<EmployeeRec> EnsureAdminAsync(ActorContext actor, CancellationToken ct)
    {
        var employee = await EnsureActiveEmployeeAsync(actor, ct);
        if (!actor.IsAdmin)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Administrator role required.");
        }

        return employee;
    }

    public async Task<EmployeeRec> EnsureActiveEmployeeAsync(ActorContext actor, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        var employee = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        Authz.EnsureActiveUser(actor, employee);
        return employee!;
    }

    /// <summary>Owner-of-campaign check combined with an active profile (B03.2, B05.1).</summary>
    public async Task<EmployeeRec> EnsureOwnerOrAdminAsync(ActorContext actor, CampaignRec campaign, CancellationToken ct)
    {
        var employee = await EnsureActiveEmployeeAsync(actor, ct);
        if (!actor.IsAdmin && campaign.OwnerMasterId != actor.MasterId)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Only the current campaign owner or an administrator may do this.");
        }

        return employee;
    }
}
