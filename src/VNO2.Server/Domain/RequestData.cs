using System.Text.Json;

namespace VNO2.Server.Domain;

public static class RequestData
{
    public static string Text(this JsonElement data, string name, int max = 200, bool required = true)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            if (!required) return "";
            throw new DomainException("invalid_request", $"{name} is required");
        }
        if (property.ValueKind != JsonValueKind.String) throw new DomainException("invalid_request", $"{name} must be text");
        var text = property.GetString()!.Trim();
        if ((required && text.Length == 0) || text.Length > max || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
            throw new DomainException("invalid_request", $"{name} has an invalid length or character");
        return text;
    }

    public static int Number(this JsonElement data, string name, int min, int max, int fallback = 0)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value) || value < min || value > max)
            throw new DomainException("invalid_request", $"{name} must be between {min} and {max}");
        return value;
    }

    public static long Money(this JsonElement data, string name, long fallback = 0)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value) || value < 0 || value > 1_000_000_000)
            throw new DomainException("invalid_request", $"{name} must be between 0 and 1000000000");
        return value;
    }

    public static bool Flag(this JsonElement data, string name, bool fallback = false)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new DomainException("invalid_request", $"{name} must be boolean");
        return property.GetBoolean();
    }

    public static double Scale(this JsonElement data, string name, double fallback)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || !double.IsFinite(value) ||
            (name == "scaleX" ? Math.Abs(value) : value) is < 0.1 or > 4)
            throw new DomainException("invalid_request", $"{name} must have a magnitude between 0.1 and 4; only scaleX supports reflection");
        return value;
    }

    public static string[] Strings(this JsonElement data, string name, int maxCount = 100, int maxLength = 200)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maxCount)
            throw new DomainException("invalid_request", $"{name} must be a bounded array");
        return property.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text &&
            text.Length <= maxLength && text == text.Trim() && !text.Any(char.IsControl)
            ? text : throw new DomainException("invalid_request", $"{name} contains an invalid value")).Distinct(StringComparer.Ordinal).ToArray();
    }
}
