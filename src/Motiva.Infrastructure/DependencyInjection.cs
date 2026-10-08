using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Motiva.Application.Common;
using Motiva.Application.Ports;
using Motiva.Application.Reads;
using Motiva.Infrastructure.Caching;
using Motiva.Infrastructure.Files;
using Motiva.Infrastructure.Persistence;
using Motiva.Infrastructure.Persistence.Stores;

namespace Motiva.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Composition root attachment (T02): only Program.cs of Api/Worker and tools call
    /// this; endpoints and worker orchestration never resolve infrastructure types directly.</summary>
    public static IServiceCollection AddMotivaInfrastructure(
        this IServiceCollection services, Action<MotivaInfrastructureOptions>? configure = null)
    {
        services.AddOptions<MotivaInfrastructureOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddDbContext<MotivaDbContext>((sp, options) =>
        {
            var infraOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MotivaInfrastructureOptions>>().Value;
            options
                .UseNpgsql(infraOptions.PostgresConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DbCommandTimingInterceptor());
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICompanyDirectory, CompanyStore>();
        services.AddScoped<IEmployeeDirectory, EmployeeStore>();
        services.AddScoped<IResourceDirectory, ResourceStore>();
        services.AddScoped<IAchievementDirectory, AchievementStore>();
        services.AddScoped<IPurchaseSystemDirectory, PurchaseSystemStore>();
        services.AddScoped<IIntegrationDirectory, IntegrationGrantStore>();
        services.AddScoped<ICampaignCatalog, CampaignStore>();
        services.AddScoped<IBudgetLedger, BudgetStore>();
        services.AddScoped<IWalletLedger, WalletStore>();
        services.AddScoped<IOperationBook, OperationStore>();
        services.AddScoped<IProgressLog, ProgressStore>();
        services.AddScoped<ICompetitionBoard, CompetitionStore>();
        services.AddScoped<IExportStore, ExportStore>();
        services.AddScoped<ISnapshotRowSource, SnapshotRowSource>();
        services.AddScoped<IBackgroundJobs, OutboxStore>();
        services.AddScoped<IAuditLog, AuditStore>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IOperationsReadStore, OperationsReadStore>();

        services.AddSingleton<ICacheSnapshots, ValkeyCacheSnapshots>();
        services.AddScoped<HealthChecks>();
        services.AddSingleton<IFileStorage, S3FileStorage>();
        services.AddSingleton<Motiva.Application.Common.IRequestMetrics, RequestMetricsSink>();
        return services;
    }
}
