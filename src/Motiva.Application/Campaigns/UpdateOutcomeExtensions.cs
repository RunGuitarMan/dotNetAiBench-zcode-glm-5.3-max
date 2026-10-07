using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Campaigns;

/// <summary>Maps store outcomes of settings mutations to Problem Details codes.</summary>
internal static class UpdateOutcomeExtensions
{
    public static void EnsureOk(this UpdateOutcome outcome)
    {
        switch (outcome)
        {
            case UpdateOutcome.Ok:
                return;
            case UpdateOutcome.NotFound:
                throw new MotivaException(ErrorCode.NotFound);
            case UpdateOutcome.VersionMismatch:
                throw new MotivaException(ErrorCode.PreconditionFailed);
            default:
                throw new MotivaException(ErrorCode.ConflictState);
        }
    }
}
