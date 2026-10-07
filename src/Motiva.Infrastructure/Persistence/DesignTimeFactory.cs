using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Motiva.Infrastructure.Persistence;

/// <summary>Design-time factory for `dotnet ef database update` (scripts/migrate.sh).</summary>
public sealed class MotivaDbContextFactory : IDesignTimeDbContextFactory<MotivaDbContext>
{
    public MotivaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
            ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";
        var options = new DbContextOptionsBuilder<MotivaDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new MotivaDbContext(options);
    }
}
