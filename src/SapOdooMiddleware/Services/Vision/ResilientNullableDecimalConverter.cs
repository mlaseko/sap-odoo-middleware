using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Vision;

/// <summary>
/// Tolerant JSON converter for decimal? fields populated by an LLM.
/// Accepts JSON numbers, null, US-format strings (1,068.00),
/// European-format strings (1.068,00), percent-suffixed strings (100 %),
/// and empty strings. Returns null for anything unparseable rather than throwing.
/// </summary>
public sealed class ResilientNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetDecimal(out var d)) return d;
                return null;

            case JsonTokenType.String:
                var raw = reader.GetString();
                return ParseFlexible(raw);

            default:
                // Any unexpected token type — skip and return null instead of throwing.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }

    internal static decimal? ParseFlexible(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();

        // Strip a trailing percent sign and any whitespace before it.
        if (s.EndsWith("%"))
            s = s.Substring(0, s.Length - 1).TrimEnd();

        // Strip currency symbols and common unit tokens that occasionally bleed in.
        // Be conservative — only strip leading/trailing non-numeric characters.
        s = s.Trim(' ', '\t', '€', '$', '£', '¥');

        if (s.Length == 0) return null;

        // Decide which character is the decimal separator by position.
        // If both ',' and '.' appear, the LAST of them is the decimal sep.
        // If only one appears and is followed by exactly 1-2 digits at end, it's the decimal.
        // If only one appears and is followed by exactly 3 digits at end, it's a thousands sep.
        int lastComma = s.LastIndexOf(',');
        int lastDot = s.LastIndexOf('.');

        string normalized;
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Both present — last one wins as decimal separator.
            if (lastComma > lastDot)
            {
                // European: "1.068,00"
                normalized = s.Replace(".", "").Replace(",", ".");
            }
            else
            {
                // US: "1,068.00"
                normalized = s.Replace(",", "");
            }
        }
        else if (lastComma >= 0)
        {
            // Only comma present.
            var tail = s.Length - lastComma - 1;
            if (tail == 1 || tail == 2)
            {
                // "1068,5" or "1068,50" — decimal comma.
                normalized = s.Replace(",", ".");
            }
            else
            {
                // "1,068" — thousands comma.
                normalized = s.Replace(",", "");
            }
        }
        else if (lastDot >= 0)
        {
            var tail = s.Length - lastDot - 1;
            if (tail == 1 || tail == 2)
            {
                // "1068.5" or "1068.50" — decimal dot.
                normalized = s;
            }
            else
            {
                // "1.068" — thousands dot (rare).
                normalized = s.Replace(".", "");
            }
        }
        else
        {
            // No separator — plain integer.
            normalized = s;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
