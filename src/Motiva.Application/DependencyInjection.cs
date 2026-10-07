using Motiva.Application.Administration;
using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Competitions;
using Motiva.Application.Economy;
using Motiva.Application.Exports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;

namespace Motiva.Application;

public static class DependencyInjection
{
    /// <summary>Registers application use cases and ports; infrastructure implementations are
    /// attached by the composition roots (Api/Worker) via AddMotivaInfrastructure.</summary>
    public static IServiceCollection AddMotivaApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITestImpediments>(NoOpImpediments.Instance);
        services.AddScoped<IdempotencyGate>();

        services.AddScoped<EmployeesService>();
        services.AddScoped<ResourcesService>();
        services.AddScoped<AchievementsService>();
        services.AddScoped<PurchaseSystemsService>();
        services.AddScoped<IntegrationGrantsService>();
        services.AddScoped<BootstrapService>();

        services.AddScoped<CampaignsService>();
        services.AddScoped<CampaignContentService>();

        services.AddScoped<BudgetService>();
        services.AddScoped<ManualAwardsService>();
        services.AddScoped<SpendsService>();
        services.AddScoped<ReversalsService>();

        services.AddScoped<ProgressEventsService>();
        services.AddScoped<LeaderboardService>();
        services.AddScoped<FinalizeChallengeHandler>();

        services.AddScoped<ExportsService>();
        services.AddScoped<ExportFormationHandler>();
        services.AddScoped<CleanupHandler>();

        services.AddScoped<ReadService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<BackgroundJobRunner>();
        return services;
    }
}
