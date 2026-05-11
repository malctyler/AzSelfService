using System.Text.Json;

namespace AzSelfService.API.Utils;

public static class JsonHelpers
{
    public static JsonElement ParseJsonOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public static JsonElement? ParseNullableJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}