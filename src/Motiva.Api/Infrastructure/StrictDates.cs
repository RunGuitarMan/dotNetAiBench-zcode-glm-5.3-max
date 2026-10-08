using System.Globalization;
using System.Text.RegularExpressions;
using Motiva.Application.Common;

namespace Motiva.Api.Infrastructure;

/// <summary>
/// External dates must carry an explicit UTC offset (T05): "2026-01-01T00:00:00Z" or
/// "+03:00" are accepted and normalized to UTC; a local wall-clock timestamp is a 400.
/// </summary>
public static partial class StrictDates
{
    // 'K' (not a quoted 'Z' literal!) matches the UTC designator "Z" as offset +00 and an
    // explicit "zzz" offset as itself — a literal 'Z' would silently read the timestamp in the
    // MACHINE's timezone and shift every Z-form date by the local offset.
    private static readonly string[] Formats = ["yyyy-MM-ddTHH:mm:ss.FFFFFFFK"];

    // 'K' also matches an EMPTY offset, so the explicit presence of "Z" or ±hh:mm is verified
    // separately: a local wall-clock timestamp stays a 400 (T05).
    [GeneratedRegex("(Z|[+-]\\d{2}:\\d{2})$")]
    private static partial Regex ExplicitOffset();

    public static DateTimeOffset ParseRequiredUtc(string? value, string field)
    {
        if (string.IsNullOrEmpty(value)
            || !ExplicitOffset().IsMatch(value)
            || !DateTimeOffset.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, field + " must be an ISO-8601 timestamp with a UTC offset (e.g. 2026-04-01T00:00:00Z or +03:00).");
        }

        return parsed.ToUniversalTime();
    }

    public static DateTimeOffset? ParseOptionalUtc(string? value, string field)
        => string.IsNullOrEmpty(value) ? null : ParseRequiredUtc(value, field);
}
