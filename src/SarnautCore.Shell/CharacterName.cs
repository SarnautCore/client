using System.Text;
using System.Text.RegularExpressions;

namespace SarnautCore.Shell;

/// <summary>
/// The client's mirror of the server's character-name rule (ADR 0032 section 3).
/// </summary>
/// <remarks>
/// A pre-submit hint and nothing more. ADR 0032's consequences say it plainly:
/// the server's answer is the one that counts, and the creation screen must
/// render <c>NAME_TAKEN</c> and <c>NAME_INVALID</c> from the server rather than
/// assuming this check was sufficient. Uniqueness is not checked here at all,
/// because only the unique index can answer it.
/// </remarks>
public static partial class CharacterName
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 16;

    /// <summary>
    /// The shape rule, phrased exactly as the server's RE2 pattern is.
    /// </summary>
    /// <remarks>
    /// The second quantifier is <c>[a-z]*</c>, not <c>[a-z]+</c>, on purpose:
    /// <c>[a-z]+</c> would require a lowercase letter between the initial capital
    /// and the first punctuation mark, and so would reject <c>O'brien</c>. The
    /// ASCII character classes are what reject a Cyrillic homoglyph such as
    /// <c>Аnne</c> rather than accidentally accepting it.
    /// </remarks>
    [GeneratedRegex(@"^[A-Z][a-z]*(?:['-][a-z]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShapePattern();

    /// <summary>Reports whether the name has the shape the server accepts.</summary>
    public static bool IsValid(string? name)
    {
        return name is not null
            && name.Length >= MinimumLength
            && name.Length <= MaximumLength
            && ShapePattern().IsMatch(name);
    }

    /// <summary>
    /// Produces the value the server's unique index is taken on: apostrophes and
    /// hyphens stripped, then lowercased.
    /// </summary>
    /// <remarks>
    /// <c>O'brien</c>, <c>Obrien</c> and <c>Ob-rien</c> therefore collide, which
    /// is the intent — impersonation by punctuation is the cheapest attack on a
    /// name system.
    /// </remarks>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (character is not ('\'' or '-'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Explains why a name is refused, or returns null when the shape is fine.
    /// </summary>
    /// <remarks>
    /// The sentences are the client's, because they are shown while the player is
    /// still typing and no request has been made. Once one has, the screen shows
    /// the server's sentence instead.
    /// </remarks>
    public static string? Explain(string? name)
    {
        string candidate = name ?? string.Empty;
        if (candidate.Length == 0)
        {
            return "Enter a name.";
        }

        if (candidate.Length < MinimumLength || candidate.Length > MaximumLength)
        {
            return $"A name is {MinimumLength} to {MaximumLength} characters.";
        }

        if (!ShapePattern().IsMatch(candidate))
        {
            return "A name starts with an uppercase letter, then lowercase letters, "
                + "with at most single apostrophes or hyphens between them.";
        }

        return null;
    }
}
