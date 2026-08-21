namespace SarnautCore.UI;

internal static class UiRuntimeKey
{
    public static void Validate(string value, string name)
    {
        string[] parts = value.Split('-');
        if (value.Length == 0
            || parts.Any(part => part.Length == 0)
            || parts.Any(part => part.Any(
                character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))))
        {
            throw new InvalidDataException($"{name} '{value}' is not a lowercase kebab identifier");
        }
        RejectNonProductVocabulary(value, name);
    }

    public static void ValidateCatalogKey(string value, string name)
    {
        string[] parts = value.Split('_');
        if (value.Length == 0
            || parts.Any(part => part.Length == 0)
            || parts.Any(part => part.Any(
                character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))))
        {
            throw new InvalidDataException($"{name} '{value}' is not a lowercase snake identifier");
        }
        RejectNonProductVocabulary(value, name);
    }

    public static void RejectNonProductVocabulary(string value, string name)
    {
        string lowered = value.ToLowerInvariant();
        string[] reserved =
        [
            ".xdb",
            "xpointer",
            "widget",
            "reaction",
            ".lua",
            "lua_",
            "/lua",
            "fmod",
            ".cur",
            "interface/",
            "source_path",
            "source_class",
        ];
        string? token = reserved.FirstOrDefault(
            candidate => lowered.Contains(candidate, StringComparison.Ordinal));
        if (token is not null)
        {
            throw new InvalidDataException($"{name} contains reserved non-product token '{token}'");
        }
    }
}
