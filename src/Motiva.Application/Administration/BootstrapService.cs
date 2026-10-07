using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Administration;

public sealed record BootstrapActor(string Sub, Guid CompanyId, string ActorType, int? MasterId, string? Role);

/// <summary>
/// Trusted bootstrap (T03): the only allowed direct creation of companies and their active
/// administrators through application use cases. All other data goes through the regular
/// administrative API. No manual INSERTs, no business shortcuts.
/// </summary>
public sealed class BootstrapService(
    ICompanyDirectory companies,
    IEmployeeDirectory employees,
    IAuditLog audit,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task RunAsync(IReadOnlyList<BootstrapActor> actors, string companyNamePrefix, CancellationToken ct)
    {
        var byCompany = actors.GroupBy(a => a.CompanyId);
        foreach (var group in byCompany)
        {
            await using var scope = await uow.BeginAsync(ct);
            var created = await companies.EnsureCreatedAsync(
                group.Key, companyNamePrefix + "-" + group.Key.ToString()[..8], "Europe/Tallinn", ct);
            if (created)
            {
                await AuditAsync(group.Key, "company.created", "Company", group.Key.ToString(), "Europe/Tallinn", ct);
            }

            foreach (var actor in group.Where(a => a.ActorType == "user" && a.Role?.Contains("Admin", StringComparison.Ordinal) == true))
            {
                var masterId = actor.MasterId!.Value;
                var existing = await employees.GetAsync(group.Key, masterId, ct);
                if (existing is not null)
                {
                    continue;
                }

                await employees.CreateAsync(group.Key, masterId, Array.Empty<string>(), timeProvider.GetUtcNow(), ct);
                await AuditAsync(group.Key, "employee.created", "Employee", masterId.ToString(), "admin", ct);
            }

            await scope.CommitAsync(ct);
        }
    }

    private static ActorContext BootActor()
        => new(Guid.Empty, ActorType.Service, null, "motiva-bootstrap", IsAdmin: false);

    private Task AuditAsync(Guid companyId, string action, string entityType, string entityId, string detail, CancellationToken ct)
    {
        return audit.AppendAsync(
            companyId,
            BootActor(),
            new AuditInput(action, entityType, entityId, new[] { new AuditChangeRec("detail", null, detail) }),
            timeProvider.GetUtcNow(),
            ct);
    }
}
