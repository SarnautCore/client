using System.Text;

namespace SarnautCore.Networking;

/// <summary>
/// Turns the <c>name_key</c> a snapshot carries into the text a nameplate
/// shows.
/// </summary>
/// <remarks>
/// <para>
/// The wire carries localization keys and never display strings (ADR 0007), so
/// a nameplate is a lookup. The lookup misses constantly in practice — a locale
/// table is a private content artifact and a developer build often has none —
/// and a miss must not put a raw key such as <c>TideCrab.Name.txt</c> over a
/// mob's head.
/// </para>
/// <para>
/// The fallback is therefore a slug of the key itself: readable, stable, and
/// obviously not a translation. It is deliberately lossy about the variant
/// suffix authored content uses to distinguish two versions of one creature,
/// because the level beside the name already says which one it is.
/// </para>
/// </remarks>
public static class EntityNaming
{
    /// <summary>Key segments that name the field rather than the thing.</summary>
    private static readonly string[] FieldSuffixes = ["txt", "name", "title", "label", "text"];

    /// <summary>
    /// The nameplate text for one key: the localized string when there is one,
    /// and a slug of the key when there is not.
    /// </summary>
    /// <param name="nameKey">The key from <c>EntitySnapshot.name_key</c>.</param>
    /// <param name="localized">
    /// What the locale table answered, or null. Godot's translation server
    /// echoes the key back on a miss, so a result equal to the key counts as a
    /// miss too.
    /// </param>
    public static string Resolve(string? nameKey, string? localized)
    {
        string key = nameKey?.Trim() ?? string.Empty;
        string translated = localized?.Trim() ?? string.Empty;
        if (translated.Length > 0 && !string.Equals(translated, key, StringComparison.Ordinal))
        {
            return translated;
        }

        return Slug(key);
    }

    /// <summary>
    /// A readable stand-in built from the key: the last meaningful segment,
    /// stripped of its variant suffix and split into words.
    /// </summary>
    public static string Slug(string? nameKey)
    {
        string key = nameKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            return string.Empty;
        }

        string segment = LastMeaningfulSegment(key);
        segment = StripFieldSuffix(segment, '_');
        segment = StripVariantSuffix(segment);
        string words = SplitWords(segment);
        return words.Length == 0 ? key : words;
    }

    /// <summary>
    /// The last dot-separated segment that names the thing. Dotted keys put the
    /// field last (<c>mob.harbor.tide-crab.name</c>) and file-shaped keys put
    /// the extension last (<c>TideCrab.Name.txt</c>); both are the same rule.
    /// </summary>
    private static string LastMeaningfulSegment(string key)
    {
        string[] segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int index = segments.Length - 1; index >= 0; index--)
        {
            if (!IsFieldSuffix(segments[index]))
            {
                return segments[index];
            }
        }

        return segments.Length == 0 ? key : segments[^1];
    }

    private static string StripFieldSuffix(string segment, char separator)
    {
        int cut = segment.LastIndexOf(separator);
        if (cut <= 0 || !IsFieldSuffix(segment[(cut + 1)..]))
        {
            return segment;
        }

        return segment[..cut];
    }

    private static bool IsFieldSuffix(string segment)
    {
        foreach (string suffix in FieldSuffixes)
        {
            if (segment.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops a trailing authored variant such as the <c>1_1</c> of
    /// <c>AirElemental1_1</c>, and never the whole segment.
    /// </summary>
    private static string StripVariantSuffix(string segment)
    {
        int end = segment.Length;
        while (end > 0)
        {
            int digitsEnd = end;
            int index = digitsEnd - 1;
            while (index >= 0 && char.IsAsciiDigit(segment[index]))
            {
                index--;
            }

            if (index == digitsEnd - 1 || index < 0)
            {
                break;
            }

            if (segment[index] is not ('_' or '-'))
            {
                // Digits welded to letters, as in `Rat1`. Cut them too, but only
                // when something is left to name the creature.
                if (index > 0 && char.IsAsciiLetter(segment[index]))
                {
                    end = index + 1;
                }

                break;
            }

            end = index;
        }

        string trimmed = segment[..end].TrimEnd('_', '-');
        return trimmed.Length == 0 ? segment : trimmed;
    }

    private static string SplitWords(string segment)
    {
        var builder = new StringBuilder(segment.Length + 8);
        bool startOfWord = true;
        for (int index = 0; index < segment.Length; index++)
        {
            char current = segment[index];
            if (current is '_' or '-' or ' ')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                startOfWord = true;
                continue;
            }

            bool boundary = index > 0
                && char.IsUpper(current)
                && (char.IsLower(segment[index - 1])
                    || (char.IsUpper(segment[index - 1]) && index + 1 < segment.Length && char.IsLower(segment[index + 1])));
            if (boundary && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
                startOfWord = true;
            }

            builder.Append(startOfWord ? char.ToUpperInvariant(current) : current);
            startOfWord = false;
        }

        return builder.ToString().Trim();
    }
}
