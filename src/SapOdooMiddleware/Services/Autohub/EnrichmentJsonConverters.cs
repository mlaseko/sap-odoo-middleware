using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Tolerant string reader for enrichment fields DGX may emit with an inconsistent shape across paths
/// (e.g. a URL sometimes a bare string, sometimes a one-element array). A single malformed optional field
/// must never fail the whole line's enrichment — so we coerce arrays to a comma-joined string, numbers/bools
/// to text, and objects to null, rather than throwing. Serializes back out as a plain string.
/// </summary>
public sealed class LenientStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.True:  return "true";
            case JsonTokenType.False: return "false";
            case JsonTokenType.StartArray:
            {
                var parts = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var s = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) parts.Add(s!);
                    }
                    else
                    {
                        reader.Skip();   // ignore non-string elements (objects, nested arrays)
                    }
                }
                return parts.Count == 0 ? null : string.Join(",", parts);
            }
            default:
                reader.Skip();           // object or anything else → treat as absent
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// Tolerant <c>List&lt;string&gt;</c> reader for enrichment list fields whose element shape can vary by DGX
/// path (bare strings on one path, category objects on another). Accepts an array of strings/numbers/objects
/// (pulling a name-ish field from objects), a single bare string, or null — never throws. Serializes back out
/// as a plain string array.
/// </summary>
public sealed class LenientStringListJsonConverter : JsonConverter<List<string>?>
{
    private static readonly HashSet<string> NameKeys = new(StringComparer.OrdinalIgnoreCase)
        { "name", "label", "category", "category_name", "title", "value", "text" };

    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                return string.IsNullOrWhiteSpace(s) ? new List<string>() : new List<string> { s! };
            }
            case JsonTokenType.StartArray:
            {
                var list = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            var s = reader.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                            break;
                        case JsonTokenType.Number:
                            list.Add(reader.TryGetInt64(out var l)
                                ? l.ToString(CultureInfo.InvariantCulture)
                                : reader.GetDouble().ToString(CultureInfo.InvariantCulture));
                            break;
                        case JsonTokenType.StartObject:
                            var name = ExtractName(ref reader);
                            if (!string.IsNullOrWhiteSpace(name)) list.Add(name!);
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
                return list;
            }
            default:
                reader.Skip();
                return new List<string>();
        }
    }

    /// <summary>Reader is positioned on StartObject; consume it fully and return the first name-ish string.</summary>
    private static string? ExtractName(ref Utf8JsonReader reader)
    {
        string? found = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) { reader.Skip(); continue; }
            var prop = reader.GetString();
            reader.Read();   // advance to the value
            if (found is null && reader.TokenType == JsonTokenType.String && prop is not null && NameKeys.Contains(prop))
                found = reader.GetString();
            else
                reader.Skip();
        }
        return found;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
