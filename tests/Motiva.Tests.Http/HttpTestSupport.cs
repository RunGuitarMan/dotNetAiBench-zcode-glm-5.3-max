using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Motiva.Application;
using Motiva.Application.Administration;
using Motiva.Infrastructure;
using Motiva.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Motiva.Tests.Http;

/// <summary>Test JWT issuer (allowed by 03_AUTH: test code may prepare signed tokens). The key
/// is a test-only value configured for the in-process host — never the stand secret.</summary>
public static class TestTokens
{
    public const string Issuer = "motiva-benchmark";
    public const string Audience = "motiva-api";
    public static readonly SymmetricSecurityKey Key = new(
        Convert.FromBase64String(Environment.GetEnvironmentVariable("MOTIVA_TEST_SIGNING_KEY") ?? "dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1hYmMtZGVmIQ=="));

    public static string Issue(
        Guid companyId,
        string actorType = "user",
        int? masterId = null,
        string subject = "test",
        bool admin = false,
        string? issuer = null,
        string? audience = null,
        DateTime? expires = null,
        string? overrideMasterId = null,
        bool duplicateMasterId = false,
        bool duplicateCompanyId = false,
        bool roles = true,
        bool forceRoleForService = false)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("companyId", companyId.ToString()),
            new("actorType", actorType),
        };
        if (masterId is not null || overrideMasterId is not null)
        {
            claims.Add(new Claim("masterId", overrideMasterId ?? masterId!.ToString()!));
            if (duplicateMasterId)
            {
                claims.Add(new Claim("masterId", (masterId + 1).ToString()!));
            }
        }

        if (duplicateCompanyId)
        {
            claims.Add(new Claim("companyId", Guid.NewGuid().ToString()));
        }

        if (actorType == "service" && forceRoleForService)
        {
            claims.Add(new Claim("role", "Admin"));
        }

        var credentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity(claims),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            IssuedAt = DateTime.UtcNow,
            NotBefore = expires is null ? DateTime.UtcNow : expires.Value.AddHours(-2),
        };
        if (actorType == "user" && roles)
        {
            descriptor.Subject.AddClaim(new Claim("role", "Employee"));
            if (admin)
            {
                descriptor.Subject.AddClaim(new Claim("role", "Admin"));
            }
        }

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public static string SignedWithOtherKey(Guid companyId)
    {
        var otherKey = new SymmetricSecurityKey(Convert.FromBase64String("b3RoZXItc2lnbmluZy1rZXktMzItYnl0ZXMtYWJjLWRlZg=="));
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(otherKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(new List<Claim>
            {
                new("sub", "attacker"),
                new("companyId", companyId.ToString()),
                new("actorType", "user"),
                new("masterId", "123"),
                new("role", "Employee"),
            }),
            Expires = DateTime.UtcNow.AddHours(1),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}

/// <summary>HTTP contract host: the real Program with real middleware; PostgreSQL from the
/// compose stand (one database per class); Valkey and S3 shared. Arrange goes through HTTP or
/// the application use cases — never direct SQL (T08).</summary>
public sealed class MotivaHttpFixture : WebApplicationFactory<Program>
{
    private static readonly string BaseConnection = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
        ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";

    private readonly string _databaseName = "motiva_http_" + Guid.NewGuid().ToString("N")[..12];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var connection = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = _databaseName }.ConnectionString;
            services.RemoveAll<DbContextOptions<MotivaDbContext>>();
            services.AddDbContext<MotivaDbContext>(options => options.UseNpgsql(connection).UseSnakeCaseNamingConvention());
            services.AddMotivaApplication();
            services.AddMotivaInfrastructure(options =>
            {
                options.PostgresConnectionString = connection;
                options.ValkeyEndpoint = Environment.GetEnvironmentVariable("Motiva__ValkeyEndpoint") ?? "localhost:6380";
                options.S3ServiceUrl = Environment.GetEnvironmentVariable("Motiva__S3ServiceUrl") ?? "http://localhost:9100";
                options.S3AccessKey = "motiva";
                options.S3SecretKey = Environment.GetEnvironmentVariable("Motiva__S3SecretKey") ?? "motiva-stand-secret";
                options.S3Bucket = "motiva-http-exports";
            });
        });
    }

    private int _initializations;

    public async Task InitializeDatabaseAsync()
    {
        if (Interlocked.Increment(ref _initializations) > 1)
        {
            return; // one database per fixture; companies are per test
        }

        await using var admin = new NpgsqlConnection(BaseConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + _databaseName, admin);
        await drop.ExecuteNonQueryAsync();
        await using var create = new NpgsqlCommand("CREATE DATABASE " + _databaseName, admin);
        await create.ExecuteNonQueryAsync();
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MotivaDbContext>();
        await context.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        // The database lives for the whole collection; only the fixture disposal removes it.
        await base.DisposeAsync();
        await using var admin = new NpgsqlConnection(BaseConnection);
        await admin.OpenAsync();
        await using var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '" + _databaseName + "'", admin);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + _databaseName, admin);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Bootstraps a fresh company through the trusted use case and returns its id.</summary>
    public async Task<Guid> NewCompanyAsync()
    {
        using var scope = Services.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapService>();
        var companyId = Guid.NewGuid();
        await bootstrap.RunAsync(new[] { new BootstrapActor("admin", companyId, "user", 1, "Employee,Admin") }, "Http", CancellationToken.None);
        return companyId;
    }
}

[CollectionDefinition("http")]
public sealed class HttpScenarioSet : ICollectionFixture<MotivaHttpFixture>;

internal static class AuthEnvironment
{
    [ModuleInitializer]
    internal static void ConfigureTestAuth()
    {
        // The test host validates test tokens with the test-only key (03_AUTH allows test-signed
        // tokens); the stand secret never enters the test process.
        Environment.SetEnvironmentVariable("Auth__Issuer", TestTokens.Issuer);
        Environment.SetEnvironmentVariable("Auth__Audience", TestTokens.Audience);
        Environment.SetEnvironmentVariable("Auth__SigningKeyBase64", Convert.ToBase64String(TestTokens.Key.Key));
    }
}
