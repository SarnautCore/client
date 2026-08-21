using System.Text.Json;

namespace SarnautCore.UI;

internal static class UiManifestJson
{
    public static void Object(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be Object");
        }
    }

    public static void Only(JsonElement element, string context, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"{context} repeats field '{property.Name}'");
            }

            if (!names.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unsupported field '{property.Name}'");
            }
        }
    }

    public static JsonElement Required(
        JsonElement parent,
        string property,
        JsonValueKind? kind,
        string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement element))
        {
            throw new InvalidDataException($"{context}.{property} is required");
        }

        if (kind.HasValue && element.ValueKind != kind)
        {
            throw new InvalidDataException($"{context}.{property} must be {kind.Value}");
        }

        return element;
    }

    public static string String(JsonElement parent, string property, string context)
    {
        JsonElement element = Required(parent, property, JsonValueKind.String, context);
        string value = element.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new InvalidDataException($"{context}.{property} must not be empty");
        }

        return value;
    }

    public static string Key(JsonElement parent, string property, string context)
    {
        string value = String(parent, property, context);
        UiRuntimeKey.Validate(value, $"{context}.{property}");
        return value;
    }

    public static string Key(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{context} must be String");
        }

        string value = element.GetString() ?? string.Empty;
        UiRuntimeKey.Validate(value, context);
        return value;
    }

    public static string? OptionalKey(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement element))
        {
            return null;
        }

        return Key(element, $"{context}.{property}");
    }

    public static string? OptionalCatalogKey(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{context}.{property} must be String");
        }

        string value = element.GetString() ?? string.Empty;
        UiRuntimeKey.ValidateCatalogKey(value, $"{context}.{property}");
        return value;
    }

    public static bool Bool(JsonElement parent, string property, string context)
    {
        JsonElement element = Required(parent, property, null, context);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{context}.{property} must be Boolean");
        }

        return element.GetBoolean();
    }

    public static NativeContentPath Path(
        JsonElement parent,
        string property,
        string extension,
        string context)
    {
        string value = String(parent, property, context);
        if (value.StartsWith('/')
            || value.Contains('\\')
            || value.Contains(':')
            || value.Split('/').Any(segment => segment is "" or "." or "..")
            || !value.EndsWith(extension, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{context}.{property} must be a confined product-relative {extension} path");
        }

        UiRuntimeKey.RejectNonProductVocabulary(value, $"{context}.{property}");
        return new NativeContentPath(value);
    }

    public static string Node(JsonElement parent, string property, string context)
    {
        string value = String(parent, property, context);
        if (value.StartsWith('/')
            || value.Contains('\\')
            || value.Contains(':')
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"{context}.{property} must be a confined scene node address");
        }

        UiRuntimeKey.RejectNonProductVocabulary(value, $"{context}.{property}");
        return value;
    }

    public static TEnum Enum<TEnum>(JsonElement parent, string property, string context)
        where TEnum : struct, System.Enum
    {
        string value = String(parent, property, context);
        foreach (TEnum candidate in System.Enum.GetValues<TEnum>())
        {
            if (ToKebabCase(candidate.ToString()) == value)
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"{context}.{property} has unsupported value '{value}'");
    }

    public static T[] Array<T>(
        JsonElement parent,
        string property,
        Func<JsonElement, T> read,
        string context)
    {
        JsonElement array = Required(parent, property, JsonValueKind.Array, context);
        return array.EnumerateArray().Select(read).ToArray();
    }

    public static void Unique(IEnumerable<string> values, string context)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!unique.Add(value))
            {
                throw new InvalidDataException($"Duplicate {context} '{value}'");
            }
        }
    }

    private static string ToKebabCase(string value)
    {
        var characters = new List<char>(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                characters.Add('-');
            }

            characters.Add(char.ToLowerInvariant(character));
        }

        return new string([.. characters]);
    }

}
