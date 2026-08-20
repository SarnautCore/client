using System.Globalization;

namespace SarnautCore.Shell;

/// <summary>
/// One row of the creation screen's option list, ready to render.
/// </summary>
/// <remarks>
/// Every string here is derived from the pack row the server sent. There is no
/// race name, class name or level in client source, so a second playable option
/// is a data change (ADR 0032 section 2) and this list changes shape without a
/// client rebuild.
/// </remarks>
public sealed record ChargenOptionView(
    string Id,
    string Title,
    string Subtitle,
    string Description,
    string VisualRef,
    string SpawnZoneId,
    uint StartingLevel)
{
    /// <summary>
    /// Builds the row for one option, resolving display text through
    /// <paramref name="localize"/>.
    /// </summary>
    /// <param name="localize">
    /// A locale-key lookup (ADR 0007: display names are <c>loc_ref</c> lookups,
    /// never literals). When it has no answer — which is the M2 state, because
    /// the client carries no locale table yet — the canonical race and class ids
    /// are shown instead. Those are still the server's, so the list stays
    /// server-driven either way.
    /// </param>
    public static ChargenOptionView From(ChargenOption option, Func<string, string?>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(option);
        string title = Resolve(localize, option.NameKey) ?? Humanize($"{option.Race} {option.Class}");
        string description = Resolve(localize, option.DescriptionKey) ?? string.Empty;
        string subtitle = string.Join(
            " · ",
            new[] { Humanize(option.Faction), Humanize(option.Sex), $"Level {option.StartingLevel}" }
                .Where(part => part.Length > 0));
        return new ChargenOptionView(
            option.Id,
            title,
            subtitle,
            description,
            option.VisualRef,
            option.SpawnZoneId,
            option.StartingLevel);
    }

    private static string? Resolve(Func<string, string?>? localize, string key)
    {
        if (localize is null || string.IsNullOrEmpty(key))
        {
            return null;
        }

        string? text = localize(key);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Turns a canonical id such as <c>league</c> or <c>kanian.warrior</c> into
    /// something readable, without inventing a name for it.
    /// </summary>
    private static string Humanize(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        string spaced = id.Replace('.', ' ').Replace('-', ' ').Replace('_', ' ');
        return string.Join(
            ' ',
            spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..]));
    }
}
