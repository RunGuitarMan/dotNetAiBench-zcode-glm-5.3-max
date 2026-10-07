using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Motiva.Application.Common;

/// <summary>
/// One canonical JSON configuration for every response body, including the stored replay
/// bodies of economic operations and progress events (byte-identical replay, §3.0).
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enum names are written exactly as declared: wire enums of the contract are
        // PascalCase (Posted, Active, …) except the lowercase direction, which is a string.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
