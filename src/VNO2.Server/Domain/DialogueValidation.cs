using System.Text.Json;
using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class DialogueValidation
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    { "text", "bold", "italic", "color", "pauseAfterMs", "charactersPerSecond" };

    public static (string Text, DialogueSpan[] Spans) Read(JsonElement data)
    {
        if (!data.TryGetProperty("spans", out var value))
        {
            var plain = data.Text("text", 4000);
            return (plain, [new DialogueSpan(plain)]);
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 64)
            throw new DomainException("invalid_spans", "Dialogue spans must be an array of 1 to 64 objects");
        var spans = new List<DialogueSpan>();
        var textLength = 0;
        var pauses = 0;
        foreach (var span in value.EnumerateArray())
        {
            if (span.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_spans", "Each dialogue span must be an object");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in span.EnumerateObject())
                if (!AllowedFields.Contains(field.Name) || !fields.Add(field.Name))
                    throw new DomainException("invalid_spans", "Unknown or duplicate dialogue span field");
            if (!span.TryGetProperty("text", out var spanText) || spanText.ValueKind != JsonValueKind.String)
                throw new DomainException("invalid_spans", "Every dialogue span needs text");
            var text = spanText.GetString()!;
            textLength += text.Length;
            if (textLength > 4000 || text.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
                throw new DomainException("invalid_spans", "Dialogue text exceeds 4000 characters or contains unsupported controls");
            string? color = null;
            if (span.TryGetProperty("color", out var colorValue) && colorValue.ValueKind != JsonValueKind.Null)
            {
                if (colorValue.ValueKind != JsonValueKind.String) throw new DomainException("invalid_spans", "Span colors must be #RRGGBB or null");
                color = colorValue.GetString();
                if (color == null || color.Length != 7 || color[0] != '#' || !color.AsSpan(1).ToString().All(char.IsAsciiHexDigit))
                    throw new DomainException("invalid_spans", "Span colors must use #RRGGBB");
            }
            var pause = span.Number("pauseAfterMs", 0, 5000);
            pauses += pause;
            if (pauses > 15000) throw new DomainException("invalid_spans", "A dialogue line may contain at most 15 seconds of explicit pauses");
            var speed = 35d;
            if (span.TryGetProperty("charactersPerSecond", out var speedValue) &&
                (speedValue.ValueKind != JsonValueKind.Number || !speedValue.TryGetDouble(out speed) || !double.IsFinite(speed) || speed is < 1 or > 120))
                throw new DomainException("invalid_spans", "Dialogue speed must be between 1 and 120 characters per second");
            spans.Add(new(text, span.Flag("bold"), span.Flag("italic"), color, pause, speed));
        }
        var normalized = string.Concat(spans.Select(span => span.Text));
        if (string.IsNullOrWhiteSpace(normalized)) throw new DomainException("invalid_spans", "Dialogue must contain visible text");
        if (data.TryGetProperty("text", out var provided) &&
            (provided.ValueKind != JsonValueKind.String || !string.Equals(provided.GetString(), normalized, StringComparison.Ordinal)))
            throw new DomainException("dialogue_text_mismatch", "Plain text must exactly match the concatenated span text");
        return (normalized, spans.ToArray());
    }
}
