using System.Security.Claims;
using Motiva.Application.Common;

namespace Motiva.Api.Auth;

/// <summary>Builds the verified ActorContext from the validated principal. The token subject is
/// authoritative: masterId from a request body never overrides it (T03).</summary>
public static class ActorContextResolver
{
    public static ActorContext Resolve(ClaimsPrincipal principal)
    {
        var companyId = Guid.Parse(principal.FindFirst(JwtSetup.CompanyIdClaim)!.Value);
        var actorType = principal.FindFirst(JwtSetup.ActorTypeClaim)!.Value == "user" ? ActorType.User : ActorType.Service;
        var masterIdRaw = principal.FindFirst(JwtSetup.MasterIdClaim)?.Value;
        var subject = principal.FindFirst(JwtSetup.SubjectClaim)?.Value ?? string.Empty;
        var isAdmin = principal.IsInRole("Admin");
        if (actorType == ActorType.User && (string.IsNullOrEmpty(masterIdRaw) || !int.TryParse(masterIdRaw, out _)))
        {
            throw new MotivaException(ErrorCode.AuthInvalidToken);
        }

        return new ActorContext(
            companyId,
            actorType,
            actorType == ActorType.User ? int.Parse(masterIdRaw!) : null,
            subject,
            isAdmin);
    }
}
