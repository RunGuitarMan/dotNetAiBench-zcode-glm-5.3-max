using System.Text.RegularExpressions;

namespace Motiva.Domain;

/// <summary>Validation and normalization of business codes, tags and external numbers (T05, H0 Q07).</summary>
public static partial class Codes
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[a-z0-9-]{1,32}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[\x21-\x7E]{1,100}$")]
    private static partial Regex ExternalNumberPattern();

    /// <summary>Trims plain ASCII spaces, validates the alphabet, normalizes to upper case:
    /// codes are unique in the company ignoring case (B06.3, H0 Q07).</summary>
    public static bool TryNormalizeCode(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim(' ').ToUpperInvariant();
        return CodePattern().IsMatch(normalized);
    }

    public static string NormalizeCodeOrThrow(string? raw)
    {
        if (!TryNormalizeCode(raw, out var normalized))
        {
            throw new ArgumentException("Invalid code format.", nameof(raw));
        }

        return normalized;
    }

    public static bool IsValidTag(string? tag) => tag is not null && TagPattern().IsMatch(tag);

    public static bool IsValidExternalNumber(string? number)
        => number is not null && ExternalNumberPattern().IsMatch(number);
}
