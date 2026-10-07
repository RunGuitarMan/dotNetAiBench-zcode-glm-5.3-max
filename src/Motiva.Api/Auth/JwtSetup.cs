using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Motiva.Application.Common;

namespace Motiva.Api.Auth;

/// <summary>
/// JWT pipeline per 03_AUTH: HS256 with the Base64-decoded signing key, iss/aud/lifetime with a
/// ≤ 30 s clock skew, explicit claim mapping (no accidental renamed claims), and rejection of
/// duplicated or contradictory identity claims. A valid token never bypasses in-app checks of
/// profile activity and current grants (T03).
/// </summary>
public static class JwtSetup
{
    public const string CompanyIdClaim = "companyId";
    public const string ActorTypeClaim = "actorType";
    public const string MasterIdClaim = "masterId";
    public const string RoleClaim = "role";
    public const string SubjectClaim = "sub";

    public static IServiceCollection AddMotivaJwtAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration("Auth");
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((options, authOptions) =>
            {
                var keyBytes = Convert.FromBase64String(authOptions.Value.SigningKeyBase64);
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authOptions.Value.Issuer,
                    ValidAudience = authOptions.Value.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = RoleClaim,
                    NameClaimType = SubjectClaim,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!IdentityClaimsAreConsistent(context.Principal!))
                        {
                            context.Fail("Duplicated or contradictory identity claims.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Every identity claim must be unambiguous; roles may combine Employee+Admin but
    /// must not repeat the same value (03_AUTH).</summary>
    internal static bool IdentityClaimsAreConsistent(ClaimsPrincipal principal)
    {
        string[] singletonClaims = [SubjectClaim, CompanyIdClaim, ActorTypeClaim, MasterIdClaim];
        foreach (var claimType in singletonClaims)
        {
            if (principal.Claims.Count(c => string.Equals(c.Type, claimType, StringComparison.Ordinal)) > 1)
            {
                return false;
            }
        }

        var roles = principal.Claims.Where(c => string.Equals(c.Type, RoleClaim, StringComparison.Ordinal)).Select(c => c.Value).ToList();
        if (roles.Distinct().Count() != roles.Count)
        {
            return false;
        }

        var actorType = principal.FindFirst(ActorTypeClaim)?.Value;
        var masterId = principal.FindFirst(MasterIdClaim)?.Value;
        if (actorType == "user")
        {
            if (string.IsNullOrEmpty(masterId) || !int.TryParse(masterId, out var value) || value < 1)
            {
                return false;
            }
        }
        else if (actorType == "service")
        {
            if (!string.IsNullOrEmpty(masterId))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return Guid.TryParse(principal.FindFirst(CompanyIdClaim)?.Value, out _);
    }

    public sealed record JwtOptions
    {
        public string Issuer { get; init; } = "motiva-benchmark";

        public string Audience { get; init; } = "motiva-api";

        public string SigningKeyBase64 { get; init; } = string.Empty;
    }
}
