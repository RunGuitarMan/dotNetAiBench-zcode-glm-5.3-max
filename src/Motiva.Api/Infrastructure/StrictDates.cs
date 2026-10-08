using System.Globalization;
using Motiva.Application.Common;

namespace Motiva.Api.Infrastructure;

/// <summary>
/// External dates must carry an explicit UTC offset (T05): "2026-01-01T00:00:00Z" or
/// "+03:00" are accepted and normalized to UTC; a local wall-clock timestamp is a 400.
/// </summary>
public static class StrictDates
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
    ];

    public static DateTimeOffset ParseRequiredUtc(string? value, string field)
    {
        if (string.IsNullOrEmpty(value)
            || !DateTimeOffset.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, field + " must be an ISO-8601 timestamp with a UTC offset (e.g. 2026-04-01T00:00:00Z or +03:00).");
        }

        return parsed.ToUniversalTime();
    }

    public static DateTimeOffset? ParseOptionalUtc(string? value, string field)
        => string.IsNullOrEmpty(value) ? null : ParseRequiredUtc(value, field);
}
