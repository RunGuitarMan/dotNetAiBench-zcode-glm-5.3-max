using Microsoft.Extensions.DependencyInjection;
using Motiva.Application;
using Motiva.Application.Administration;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>One PostgreSQL database per collection; one company per test through the trusted
/// bootstrap use case (stage-3 §3); tests inside a collection run sequentially.</summary>
public sealed class MotivaFunctionalFixture : IAsyncLifetime
{
    public MotivaTestHost Host { get; } = new();

    public Task InitializeAsync() => Host.InitializeAsync();

    public Task DisposeAsync() => Host.DisposeAsync();
}

[CollectionDefinition("functional")]
public sealed class FunctionalScenarioSet : ICollectionFixture<MotivaFunctionalFixture>;

public static class FixtureExtensions
{
    /// <summary>Creates a fresh company with an active administrator through bootstrap (T03).</summary>
    public static async Task<TestWorld> NewWorldAsync(this MotivaFunctionalFixture fixture, DateTimeOffset? clockStart = null)
    {
        fixture.Host.Impediments.Reset();
        var provider = fixture.Host.BuildServices(clockStart);
        var companyId = Guid.NewGuid();
        var actors = new[]
        {
            new BootstrapActor("admin", companyId, "user", 1, "Employee,Admin"),
        };
        await provider.GetRequiredService<BootstrapService>().RunAsync(actors, "Test", CancellationToken.None);
        return new TestWorld(provider, companyId);
    }
}
