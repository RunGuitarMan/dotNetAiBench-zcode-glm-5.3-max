using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Motiva.Tests.Architecture;

/// <summary>Directed dependency rules of stage-2 §1.2 (L01). The planted-violation script
/// scripts/verify-arch-gate.sh proves that a compilable violation fails this group.</summary>
public static class ArchitectureRules
{
    public static readonly Assembly Domain = typeof(Motiva.Domain.Codes).Assembly;
    public static readonly Assembly Application = typeof(Motiva.Application.Common.ActorContext).Assembly;
    public static readonly Assembly Infrastructure = typeof(Motiva.Infrastructure.DependencyInjection).Assembly;
    public static readonly Assembly Api = typeof(Program).Assembly;
    public static readonly Assembly Worker = typeof(Motiva.Worker.OutboxDispatcherService).Assembly;

    public const string ArchitectureGroup = "ArchitectureRules";
}

public sealed class ProjectDependencyTests
{
    private const string Group = ArchitectureRules.ArchitectureGroup;

    [Fact]
    public void Domain_has_no_dependencies_beyond_bcl_and_nodatime()
    {
        var result = Types.InAssembly(ArchitectureRules.Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Motiva.Application",
                "Motiva.Infrastructure",
                "Motiva.Api",
                "Motiva.Worker",
                "StackExchange.Redis",
                "Amazon",
                "Microsoft.AspNetCore")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_or_ef()
    {
        var result = Types.InAssembly(ArchitectureRules.Application)
            .ShouldNot()
            .HaveDependencyOnAny("Motiva.Infrastructure", "Microsoft.EntityFrameworkCore", "Npgsql", "Amazon", "StackExchange.Redis")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Infrastructure_does_not_reference_api_or_worker()
    {
        var result = Types.InAssembly(ArchitectureRules.Infrastructure)
            .ShouldNot()
            .HaveDependencyOnAny("Motiva.Api", "Motiva.Worker")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Endpoints_and_worker_orchestration_never_touch_dbcontext_npgsql_or_s3()
    {
        // Only Program.cs (composition root) may reference Infrastructure types.
        var offenderResult = Types.InAssembly(ArchitectureRules.Api)
            .That()
            .DoNotHaveName("Program")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Motiva.Infrastructure.Persistence",
                "Motiva.Infrastructure",
                "Npgsql",
                "Amazon.S3")
            .GetResult();
        var offenders = offenderResult.FailingTypeNames ?? [];
        Assert.True(offenders.Count == 0,
            "Api endpoints must go through application use cases; offenders: " + string.Join(", ", offenders));

        var workerResult = Types.InAssembly(ArchitectureRules.Worker)
            .That()
            .DoNotHaveName("Program")
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Motiva.Infrastructure.Persistence", "Npgsql", "Amazon.S3")
            .GetResult();
        var workerOffenders = workerResult.FailingTypeNames ?? [];
        Assert.True(workerOffenders.Count == 0,
            "Worker orchestration must go through application use cases; offenders: " + string.Join(", ", workerOffenders));
    }

    [Fact]
    public void Feature_grouping_exists_in_application()
    {
        // T02: grouping by functionality, not a single folder of all handlers.
        var namespaces = Types.InAssembly(ArchitectureRules.Application).GetTypes().Select(t => t.Namespace ?? string.Empty).ToList();
        Assert.Contains(namespaces, n => n.Contains("Progress", StringComparison.Ordinal));
        Assert.Contains(namespaces, n => n.Contains("Campaigns", StringComparison.Ordinal));
        Assert.Contains(namespaces, n => n.Contains("Economy", StringComparison.Ordinal) || n.Contains("Budgets", StringComparison.Ordinal));
        Assert.Contains(namespaces, n => n.Contains("Exports", StringComparison.Ordinal));
        Assert.Contains(namespaces, n => n.Contains("Competitions", StringComparison.Ordinal));
    }
}
