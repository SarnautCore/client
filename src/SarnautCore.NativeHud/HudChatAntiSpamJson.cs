using System.Text.Json;

namespace SarnautCore.NativeHud;

public static class HudChatAntiSpamJson
{
    public static HudChatAntiSpamCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray());
        JsonElement root = RequireObject(document.RootElement, "root");
        RequireProperties(root,
            "schema", "score_scale", "empty_score", "normalization", "case_culture",
            "category_aggregation", "categories");
        RequireString(root, "schema", HudChatAntiSpamCatalog.Schema);
        RequireInteger(root, "score_scale", 100);
        RequireInteger(root, "empty_score", 100);
        RequireString(root, "category_aggregation", "maximum");

        JsonElement normalization = RequireObject(Require(root, "normalization"), "normalization");
        RequireProperties(normalization, "trim_both", "collapse_ascii_space", "case_fold");
        RequireBoolean(normalization, "trim_both", true);
        RequireBoolean(normalization, "collapse_ascii_space", true);
        RequireString(normalization, "case_fold", "locale-wchar");

        string caseCulture = RequireNonemptyString(root, "case_culture");
        JsonElement categoriesElement = RequireArray(Require(root, "categories"), "categories");
        var categories = new List<HudChatAntiSpamCategory>();
        foreach (JsonElement categoryElement in categoriesElement.EnumerateArray())
        {
            JsonElement category = RequireObject(categoryElement, "category");
            RequireProperties(category, "id", "aggregation", "filters", "weight_hundredths");
            string id = RequireNonemptyString(category, "id");
            RequireString(category, "aggregation", "sum");
            int categoryWeight = OptionalNonnegativeInteger(category, "weight_hundredths", 100);
            JsonElement filtersElement = RequireArray(Require(category, "filters"), "filters");
            var filters = new List<HudChatAntiSpamFilter>();
            foreach (JsonElement filterElement in filtersElement.EnumerateArray())
            {
                filters.Add(ParseFilter(filterElement));
            }

            categories.Add(new HudChatAntiSpamCategory(id, categoryWeight, filters));
        }

        return new HudChatAntiSpamCatalog(caseCulture, categories);
    }

    private static HudChatAntiSpamFilter ParseFilter(JsonElement filterElement)
    {
        JsonElement filter = RequireObject(filterElement, "filter");
        string kind = RequireNonemptyString(filter, "kind");
        int weight = RequireNonnegativeInteger(filter, "weight_hundredths");
        return kind switch
        {
            "caps-lock" => ParseCaps(filter, weight),
            "trash" => ParseTrash(filter, weight),
            "weighted-wildcards" => ParseWords(filter, weight),
            _ => throw Error($"Unsupported anti-spam filter kind '{kind}'."),
        };
    }

    private static HudChatAntiSpamFilter ParseCaps(JsonElement filter, int weight)
    {
        RequireProperties(filter, "kind", "weight_hundredths");
        return new HudChatAntiSpamFilter.CapsLock(weight);
    }

    private static HudChatAntiSpamFilter ParseTrash(JsonElement filter, int weight)
    {
        RequireProperties(filter, "kind", "weight_hundredths", "symbols");
        return new HudChatAntiSpamFilter.Trash(weight, RequireString(filter, "symbols"));
    }

    private static HudChatAntiSpamFilter ParseWords(JsonElement filter, int weight)
    {
        RequireProperties(filter, "kind", "weight_hundredths", "trash_symbols", "patterns");
        string trashSymbols = RequireString(filter, "trash_symbols");
        JsonElement patternsElement = RequireArray(Require(filter, "patterns"), "patterns");
        var patterns = new List<HudChatAntiSpamPattern>();
        foreach (JsonElement patternElement in patternsElement.EnumerateArray())
        {
            JsonElement pattern = RequireObject(patternElement, "pattern");
            RequireProperties(pattern, "pattern", "weight_hundredths");
            patterns.Add(new HudChatAntiSpamPattern(
                RequireNonemptyString(pattern, "pattern"),
                RequireIntegerInRange(pattern, "weight_hundredths", 0, 100)));
        }

        return new HudChatAntiSpamFilter.WeightedWildcards(weight, trashSymbols, patterns);
    }

    private static JsonElement Require(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement result)
            ? result
            : throw Error($"Missing anti-spam property '{property}'.");

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object ? value : throw Error($"Anti-spam {name} must be an object.");

    private static JsonElement RequireArray(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Array ? value : throw Error($"Anti-spam {name} must be an array.");

    private static string RequireString(JsonElement value, string property)
    {
        JsonElement element = Require(value, property);
        return element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw Error($"Anti-spam property '{property}' must be a string.");
    }

    private static string RequireNonemptyString(JsonElement value, string property)
    {
        string result = RequireString(value, property);
        return result.Length > 0 ? result : throw Error($"Anti-spam property '{property}' cannot be empty.");
    }

    private static void RequireString(JsonElement value, string property, string expected)
    {
        if (!string.Equals(RequireString(value, property), expected, StringComparison.Ordinal))
        {
            throw Error($"Anti-spam property '{property}' has an unsupported value.");
        }
    }

    private static void RequireBoolean(JsonElement value, string property, bool expected)
    {
        JsonElement element = Require(value, property);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || element.GetBoolean() != expected)
        {
            throw Error($"Anti-spam property '{property}' has an unsupported value.");
        }
    }

    private static int RequireNonnegativeInteger(JsonElement value, string property) =>
        RequireIntegerInRange(value, property, 0, int.MaxValue);

    private static int OptionalNonnegativeInteger(JsonElement value, string property, int fallback) =>
        value.TryGetProperty(property, out _) ? RequireNonnegativeInteger(value, property) : fallback;

    private static void RequireInteger(JsonElement value, string property, int expected)
    {
        if (RequireIntegerInRange(value, property, int.MinValue, int.MaxValue) != expected)
        {
            throw Error($"Anti-spam property '{property}' has an unsupported value.");
        }
    }

    private static int RequireIntegerInRange(JsonElement value, string property, int minimum, int maximum)
    {
        JsonElement element = Require(value, property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int result) || result < minimum || result > maximum)
        {
            throw Error($"Anti-spam property '{property}' must be an integer from {minimum} through {maximum}.");
        }

        return result;
    }

    private static void RequireProperties(JsonElement value, params string[] allowed)
    {
        var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Error($"Anti-spam property '{property.Name}' is unknown or duplicated.");
            }
        }
    }

    private static JsonException Error(string message) => new(message);
}
