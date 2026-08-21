using System.Net;
using System.Text.RegularExpressions;

namespace SarnautCore.UI;

public static partial class UiPresentationPolicy
{
    public static string RequireMusicCue(string cue) => cue switch
    {
        "credits_music" or "main_menu_music" => cue,
        _ => throw new ArgumentException(
            $"Unsupported native UI music cue '{cue}'",
            nameof(cue)),
    };

    public static string RequireProductId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label} id '{value}' is not a lowercase kebab product id");
        }

        UiRuntimeKey.Validate(value, $"{label} id");
        return value;
    }

    public static float RequireOpacity(double opacity, string parameter)
    {
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
        {
            throw new ArgumentOutOfRangeException(parameter, opacity, "Opacity must be within 0..1");
        }

        return (float)opacity;
    }

    public static string ProductMarkupToPlainText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = ProductLineBreaks().Replace(normalized, "\n");
        normalized = ProductTags().Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(normalized);
        return ExcessBlankLines().Replace(normalized, "\n\n").Trim();
    }

    [GeneratedRegex(
        @"<\s*(?:br\s*/?|/p|/header|/body)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductLineBreaks();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex ProductTags();

    [GeneratedRegex(@"\n(?:[ \t]*\n){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLines();
}

public sealed class UiEulaPresentationState
{
    public string? DocumentId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public bool CanAccept { get; private set; }

    public bool Apply(string documentId, string body, bool canAccept)
    {
        UiPresentationPolicy.RequireProductId(documentId, "EULA document");
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("EULA presentation requires a body", nameof(body));
        }

        bool documentChanged = !string.Equals(DocumentId, documentId, StringComparison.Ordinal);
        if (documentChanged)
        {
            DocumentId = documentId;
            Body = UiPresentationPolicy.ProductMarkupToPlainText(body);
        }
        CanAccept = canAccept;
        return documentChanged;
    }
}
