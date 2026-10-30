using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class JsonSupport
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false
    };

    public static string? GetString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue.ToString();
            }
        }

        return null;
    }

    public static JsonNode? GetProperty(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(propertyName, out var value))
        {
            return value;
        }

        return null;
    }

    public static string? FirstNonEmptyString(params JsonNode?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var value = GetString(candidate);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    public static JsonArray CloneArray(IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value?.DeepClone());
        }

        return array;
    }
}

