using Motiva.Application.Ports;

namespace Motiva.Application.Common;

/// <summary>Authorization guards implementing the §3.0 processing order: identity and current
/// rights are checked before any business number lookup; an access failure never creates a record.</summary>
public static class Authz
{
    public static void EnsureAdmin(ActorContext actor)
    {
        // The Admin role exists only for user actors; a service token never gains it (03_AUTH).
        if (actor.ActorType != ActorType.User || !actor.IsAdmin)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Administrator role required.");
        }
    }

    public static void EnsureService(ActorContext actor)
    {
        if (actor.ActorType != ActorType.Service)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "A service token is required.");
        }
    }

    public static void EnsureUser(ActorContext actor)
    {
        if (actor.ActorType != ActorType.User)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "A user token is required.");
        }
    }

    /// <summary>An acting user must be an active employee: any action on behalf of a blocked
    /// profile — including replays — is denied without any effect (B05.1, §3.3).</summary>
    public static void EnsureActiveUser(ActorContext actor, EmployeeRec? employee)
    {
        EnsureUser(actor);
        if (employee is null || !employee.IsActive)
        {
            throw new MotivaException(ErrorCode.AuthzEmployeeNotActive);
        }
    }

    public static void EnsureGrant(IntegrationGrantRec? grant)
    {
        if (grant is null || grant.Status != GrantStatus.Active)
        {
            throw new MotivaException(ErrorCode.AuthzGrantMissing);
        }
    }
}

/// <summary>Strong ETag helpers: versions are plain integers, wire form is "vN" (§4.4).</summary>
public static class ETags
{
    public static string Format(int version) => "\"v" + version.ToString() + "\"";

    public static int ParseRequired(string? ifMatchHeader)
    {
        if (string.IsNullOrWhiteSpace(ifMatchHeader))
        {
            throw new MotivaException(ErrorCode.PreconditionRequired);
        }

        var value = ifMatchHeader.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            throw new MotivaException(ErrorCode.PreconditionFailed, "A strong ETag is required.");
        }

        value = value.Trim('"');
        if (!value.StartsWith('v') || !int.TryParse(value[1..], out var version))
        {
            throw new MotivaException(ErrorCode.PreconditionFailed, "Malformed ETag.");
        }

        return version;
    }

    public static void EnsureCurrent(string? ifMatchHeader, int actualVersion)
    {
        var expected = ParseRequired(ifMatchHeader);
        if (expected != actualVersion)
        {
            throw new MotivaException(ErrorCode.PreconditionFailed);
        }
    }
}

/// <summary>Input validation of numeric ranges and strings with T05 codes.</summary>
public static class Guard
{
    public const long MaxUnit = 1_000_000_000;
    public const long MaxTotal = 1_000_000_000_000_000;

    public static void MasterId(int masterId)
    {
        if (masterId < 1)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "masterId must be a positive Int32.");
        }
    }

    public static void PositiveAmount(long amount, string field = "amount")
    {
        if (amount is < 1 or > MaxUnit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, field + " must be between 1 and 1000000000.");
        }
    }

    public static void Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "name must be 1..200 characters.");
        }
    }

    public static void Description(string? description)
    {
        if (description is not null && description.Length > 2000)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "description must be at most 2000 characters.");
        }
    }

    public static void Tags(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            if (!Domain.Codes.IsValidTag(tag))
            {
                throw new MotivaException(ErrorCode.ValidationFailed, "Invalid tag format: " + tag);
            }
        }
    }

    public static void ExternalNumber(string? number)
    {
        if (!Domain.Codes.IsValidExternalNumber(number))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Invalid external number.");
        }
    }

    public static string Code(string? raw)
    {
        if (!Domain.Codes.TryNormalizeCode(raw, out var normalized))
        {
            throw new MotivaException(ErrorCode.ValidationCodeFormat);
        }

        return normalized;
    }

    public static void Range(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "from must be strictly before to.");
        }
    }
}
