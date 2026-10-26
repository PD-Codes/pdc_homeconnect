using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PdcHomeConnect.Core.Protocol;

/// <summary>
/// Shared JSON settings. The wire protocol and all config files use snake_case.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);

    public static readonly JsonSerializerOptions Indented = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static T? Deserialize<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Parses a message and returns it as an object, or null if it is not a JSON object.</summary>
    public static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(this JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }
        return node?.ToString();
    }

    public static bool? GetBool(this JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
            if (value.TryGetValue<int>(out var i)) return i != 0;
        }
        return null;
    }

    public static double? GetDouble(this JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
        }
        return null;
    }

    public static long? GetLong(this JsonObject obj, string name)
    {
        var d = obj.GetDouble(name);
        return d is null ? null : (long)d.Value;
    }

    public static JsonObject GetObject(this JsonObject obj, string name) => obj[name] as JsonObject ?? new JsonObject();

    public static JsonArray? GetArray(this JsonObject obj, string name) => obj[name] as JsonArray;
}
