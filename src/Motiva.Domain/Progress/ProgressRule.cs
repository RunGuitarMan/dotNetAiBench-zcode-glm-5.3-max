namespace Motiva.Domain.Progress;

/// <summary>
/// The min-rule of progress accounting (B14.4, E03): the new progress equals
/// min(goal, old + transmitted); the credited delta is what actually got accounted,
/// possibly zero after the goal was already reached (B15.3).
/// </summary>
public static class ProgressRule
{
    public static (long Current, long CreditedDelta) Apply(long goal, long current, long transmittedDelta)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(goal);
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transmittedDelta);

        var candidate = current + transmittedDelta;
        var next = Math.Min(goal, candidate);
        var credited = next - current;
        return (next, credited);
    }
}
