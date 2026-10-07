namespace Motiva.Application.Common;

public enum ActorType
{
    User,
    Service,
}

/// <summary>
/// Verified initiator identity taken from the validated token (T03). masterId from a
/// request body never overrides the subject of the token.
/// </summary>
public sealed record ActorContext(
    Guid CompanyId,
    ActorType ActorType,
    int? MasterId,
    string Subject,
    bool IsAdmin)
{
    public string InitiatorKey => ActorType == ActorType.User
        ? "user:" + MasterId!.ToString()
        : "svc:" + Subject;
}
