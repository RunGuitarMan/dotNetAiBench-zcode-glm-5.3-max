using System.Text.Json;

namespace Motiva.Application.Common;

/// <summary>List envelope: items + opaque nextCursor, only present when a next page exists (T04).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Opaque keyset cursor codec. Cursors are JSON payload in base64url.</summary>
public static class CursorCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Encode<T>(T? payload) where T : class
    {
        if (payload is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(payload, Options);
        return Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static T? Decode<T>(string? cursor) where T : class
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var bytes = Base64Url.Decode(cursor);
            return JsonSerializer.Deserialize<T>(bytes, Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Malformed cursor.");
        }
    }
}

public static class Base64Url
{
    public static string Encode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>Guard helpers for list parameters (T04: 1..100, default 50).</summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public static int NormalizeLimit(int? limit)
    {
        if (limit is null)
        {
            return DefaultLimit;
        }

        if (limit is < 1 or > MaxLimit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "limit must be between 1 and 100.");
        }

        return limit.Value;
    }
}
