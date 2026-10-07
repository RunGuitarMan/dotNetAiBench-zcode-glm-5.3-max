namespace Motiva.Domain;

/// <summary>
/// Exact integer arithmetic with overflow detection (T05): an overflow is an error,
/// never a silent wraparound, and must leave no partial effects.
/// </summary>
public static class SafeMath
{
    public static bool TryAdd(long left, long right, out long result)
    {
        try
        {
            result = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    public static long AddOrThrow(long left, long right)
    {
        return checked(left + right);
    }

    public static bool TrySubtract(long left, long right, out long result)
    {
        try
        {
            result = checked(left - right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    public static bool TryMultiply(long left, long right, out long result)
    {
        try
        {
            result = checked(left * right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
