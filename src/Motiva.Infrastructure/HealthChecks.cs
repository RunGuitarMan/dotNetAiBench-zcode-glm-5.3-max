using Microsoft.EntityFrameworkCore;
using Motiva.Infrastructure.Persistence;

namespace Motiva.Infrastructure;

/// <summary>Readiness probe: the primary data store must be reachable to serve core flows
/// (T06); liveness stays alive regardless of external dependencies.</summary>
public sealed class HealthChecks(MotivaDbContext db)
{
    public async Task<bool> DatabaseReachableAsync()
    {
        return await db.Database.CanConnectAsync();
    }
}
