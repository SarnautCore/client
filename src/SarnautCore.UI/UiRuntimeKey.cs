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
    }
}
