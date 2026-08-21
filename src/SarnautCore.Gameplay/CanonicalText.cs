using System.Globalization;
using System.Text.RegularExpressions;
using SarnautCore.Networking;

namespace SarnautCore.Gameplay;

/// <summary>Readable text for a canonical id when no translated key resolves.</summary>
public static partial class CanonicalText
{
    private static readonly HashSet<string> MetadataSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "title",
        "description",
        "text",
        "label",
    };

    public static string Fallback(string? canonicalId)
    {
        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            return string.Empty;
        }

        string normalized = canonicalId.Trim().Replace('\\', '/');

        // For any path-shaped id (containing '/'), use EntityNaming.Slug to ensure
        // consistent handling and prevent path separators from leaking into display text.
        if (normalized.Contains('/'))
        {
            return EntityNaming.Slug(normalized);
        }

        // For non-path ids, process as before: split on dots and extract meaningful segment.
        string[] segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string slug = segments.LastOrDefault(segment => !MetadataSegments.Contains(segment)) ?? normalized;
        foreach (string suffix in new[] { "-item-resource", "_item_resource", "-resource", "_resource" })
        {
            if (slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                slug = slug[..^suffix.Length];
                break;
            }
        }

        slug = slug.Replace('-', ' ').Replace('_', ' ');
        slug = CamelBoundary().Replace(slug, " ");
        slug = Whitespace().Replace(slug, " ").Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.ToLowerInvariant());
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
