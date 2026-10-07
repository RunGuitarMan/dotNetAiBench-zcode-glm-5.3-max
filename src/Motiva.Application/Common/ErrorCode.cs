namespace Motiva.Application.Common;

public enum ErrorCode
{
    ValidationFailed,
    ValidationOverflow,
    ValidationCodeFormat,
    ValidationGrantTarget,
    AuthInvalidToken,
    AuthzForbidden,
    AuthzGrantMissing,
    AuthzEmployeeNotActive,
    NotFound,
    ConflictBusinessNumber,
    ConflictIdempotencyData,
    ConflictState,
    ConflictPublishCheck,
    PreconditionRequired,
    PreconditionFailed,
    DependencyUnavailable,
    ServiceUnavailable,
}

/// <summary>
/// A business failure mapped to Problem Details with a constant machine-readable code and
/// HTTP status (stage-2 §4.2). Carries no secret material.
/// </summary>
public sealed class MotivaException : Exception
{
    public MotivaException(ErrorCode code, string? detail = null, IReadOnlyDictionary<string, string>? errors = null)
        : base(detail ?? code.ToString())
    {
        Code = code;
        Detail = detail;
        Errors = errors ?? new Dictionary<string, string>();
    }

    public ErrorCode Code { get; }

    public string? Detail { get; }

    public IReadOnlyDictionary<string, string> Errors { get; }

    public static int HttpStatusOf(ErrorCode code)
    {
        return code switch
        {
            ErrorCode.ValidationFailed or ErrorCode.ValidationOverflow or ErrorCode.ValidationCodeFormat
                or ErrorCode.ValidationGrantTarget => 400,
            ErrorCode.AuthInvalidToken => 401,
            ErrorCode.AuthzForbidden or ErrorCode.AuthzGrantMissing or ErrorCode.AuthzEmployeeNotActive => 403,
            ErrorCode.NotFound => 404,
            ErrorCode.ConflictBusinessNumber or ErrorCode.ConflictIdempotencyData or ErrorCode.ConflictState
                or ErrorCode.ConflictPublishCheck => 409,
            ErrorCode.PreconditionFailed => 412,
            ErrorCode.PreconditionRequired => 428,
            ErrorCode.DependencyUnavailable => 424,
            ErrorCode.ServiceUnavailable => 503,
            _ => 400,
        };
    }

    public static string StringCodeOf(ErrorCode code)
    {
        return code switch
        {
            ErrorCode.ValidationFailed => "validation.failed",
            ErrorCode.ValidationOverflow => "validation.overflow",
            ErrorCode.ValidationCodeFormat => "validation.code-format",
            ErrorCode.ValidationGrantTarget => "validation.grant-target",
            ErrorCode.AuthInvalidToken => "auth.invalid-token",
            ErrorCode.AuthzForbidden => "authz.forbidden",
            ErrorCode.AuthzGrantMissing => "authz.grant-missing",
            ErrorCode.AuthzEmployeeNotActive => "authz.employee-not-active",
            ErrorCode.NotFound => "not-found",
            ErrorCode.ConflictBusinessNumber => "conflict.business-number",
            ErrorCode.ConflictIdempotencyData => "conflict.idempotency-data",
            ErrorCode.ConflictState => "conflict.state",
            ErrorCode.ConflictPublishCheck => "conflict.publish-check",
            ErrorCode.PreconditionRequired => "precondition.required",
            ErrorCode.PreconditionFailed => "precondition.failed",
            ErrorCode.DependencyUnavailable => "dependency.unavailable",
            ErrorCode.ServiceUnavailable => "service.unavailable",
            _ => "validation.failed",
        };
    }
}
