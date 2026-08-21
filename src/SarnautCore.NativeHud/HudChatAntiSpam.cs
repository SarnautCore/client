using System.Globalization;
using System.Text;

namespace SarnautCore.NativeHud;

public abstract record HudChatAntiSpamFilter
{
    private HudChatAntiSpamFilter(int weightHundredths)
    {
        if (weightHundredths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weightHundredths));
        }

        WeightHundredths = weightHundredths;
    }

    public int WeightHundredths { get; }

    public sealed record CapsLock : HudChatAntiSpamFilter
    {
        public CapsLock(int weightHundredths) : base(weightHundredths)
        {
        }
    }

    public sealed record Trash : HudChatAntiSpamFilter
    {
        public Trash(int weightHundredths, string symbols) : base(weightHundredths)
        {
            ArgumentNullException.ThrowIfNull(symbols);
            Symbols = symbols;
        }

        public string Symbols { get; }
    }

    public sealed record WeightedWildcards : HudChatAntiSpamFilter
    {
        private readonly HudChatAntiSpamPattern[] _patterns;

        public WeightedWildcards(
            int weightHundredths,
            string trashSymbols,
            IEnumerable<HudChatAntiSpamPattern> patterns) : base(weightHundredths)
        {
            ArgumentNullException.ThrowIfNull(trashSymbols);
            ArgumentNullException.ThrowIfNull(patterns);
            TrashSymbols = trashSymbols;
            _patterns = patterns.ToArray();
            if (_patterns.Length == 0)
            {
                throw new ArgumentException("A weighted-wildcard filter requires patterns.", nameof(patterns));
            }
        }

        public string TrashSymbols { get; }

        public ReadOnlySpan<HudChatAntiSpamPattern> Patterns => _patterns;
    }
}

public readonly record struct HudChatAntiSpamPattern(string Pattern, int WeightHundredths)
{
    public HudChatAntiSpamPattern Validate()
    {
        if (string.IsNullOrEmpty(Pattern) || WeightHundredths is < 0 or > 100)
        {
            throw new ArgumentException("Anti-spam wildcard patterns require text and a weight from 0 through 100.");
        }

        return this;
    }
}

public sealed class HudChatAntiSpamCategory
{
    private readonly HudChatAntiSpamFilter[] _filters;

    public HudChatAntiSpamCategory(string id, int weightHundredths, IEnumerable<HudChatAntiSpamFilter> filters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(filters);
        if (weightHundredths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weightHundredths));
        }

        _filters = filters.ToArray();
        if (_filters.Length == 0)
        {
            throw new ArgumentException("An anti-spam category requires filters.", nameof(filters));
        }

        foreach (HudChatAntiSpamFilter.WeightedWildcards words in _filters.OfType<HudChatAntiSpamFilter.WeightedWildcards>())
        {
            foreach (HudChatAntiSpamPattern pattern in words.Patterns)
            {
                pattern.Validate();
            }
        }

        Id = id;
        WeightHundredths = weightHundredths;
    }

    public string Id { get; }

    public int WeightHundredths { get; }

    public ReadOnlySpan<HudChatAntiSpamFilter> Filters => _filters;
}

/// <summary>Source-free retail 1.1 anti-spam scoring over a private baked rule catalog.</summary>
public sealed class HudChatAntiSpamCatalog
{
    public const string Schema = "sarnaut.chat-antispam/v1";
    public const string ProductKey = "chat-antispam";
    public const string ProductRelativePath = "catalogs/chat-antispam.json";

    private readonly HudChatAntiSpamCategory[] _categories;
    private readonly CultureInfo _caseCulture;

    public HudChatAntiSpamCatalog(string caseCulture, IEnumerable<HudChatAntiSpamCategory> categories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseCulture);
        ArgumentNullException.ThrowIfNull(categories);
        _caseCulture = CultureInfo.GetCultureInfo(caseCulture);
        _categories = categories.ToArray();
        if (_categories.Length == 0 || _categories.Select(category => category.Id).Distinct(StringComparer.Ordinal).Count() != _categories.Length)
        {
            throw new ArgumentException("The anti-spam catalog requires uniquely named categories.", nameof(categories));
        }
    }

    public int Score(
        HudChatChannel channel,
        string message,
        string senderName,
        IEnumerable<string> friendNames)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(senderName);
        ArgumentNullException.ThrowIfNull(friendNames);
        if (channel is not (HudChatChannel.Say or HudChatChannel.Zone))
        {
            return 0;
        }

        if (friendNames.Contains(senderName, StringComparer.Ordinal))
        {
            return 0;
        }

        if (message.Length == 0)
        {
            return 100;
        }

        string normalized = Normalize(message);
        if (normalized.Length == 0)
        {
            return 0;
        }

        string lower = LowerCodeUnits(normalized);
        float maximum = 0;
        foreach (HudChatAntiSpamCategory category in _categories)
        {
            float sum = 0;
            foreach (HudChatAntiSpamFilter filter in category.Filters)
            {
                float raw = filter switch
                {
                    HudChatAntiSpamFilter.CapsLock => CapsRatio(normalized),
                    HudChatAntiSpamFilter.Trash trash => TrashRatio(normalized, trash.Symbols),
                    HudChatAntiSpamFilter.WeightedWildcards words => WordWeight(lower, words),
                    _ => throw new InvalidOperationException("The baked anti-spam filter kind is unsupported."),
                };
                sum += (filter.WeightHundredths / 100f) * raw;
            }

            maximum = MathF.Max(maximum, (category.WeightHundredths / 100f) * sum);
        }

        return (int)(MathF.Max(maximum, 0) * 100f);
    }

    private string Normalize(string message)
    {
        string trimmed = message.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var result = new StringBuilder(trimmed.Length);
        bool previousSpace = false;
        foreach (char value in trimmed)
        {
            if (value == ' ' && previousSpace)
            {
                continue;
            }

            result.Append(value);
            previousSpace = value == ' ';
        }

        return result.ToString();
    }

    private string LowerCodeUnits(string value)
    {
        return string.Create(value.Length, (value, _caseCulture), static (destination, state) =>
        {
            for (int index = 0; index < state.value.Length; index++)
            {
                destination[index] = char.ToLower(state.value[index], state._caseCulture);
            }
        });
    }

    private string LowerCodeUnits(ReadOnlySpan<char> value)
    {
        string copy = value.ToString();
        return LowerCodeUnits(copy);
    }

    private float CapsRatio(string normalized)
    {
        int count = 0;
        for (int index = 0; index < normalized.Length; index++)
        {
            if (char.ToLower(normalized[index], _caseCulture) != normalized[index])
            {
                count++;
            }
        }

        return count / (float)normalized.Length;
    }

    private static float TrashRatio(string normalized, string symbols)
    {
        int matches = 0;
        foreach (char symbol in symbols)
        {
            foreach (char value in normalized)
            {
                if (value == symbol)
                {
                    matches++;
                }
            }
        }

        return matches / (float)normalized.Length;
    }

    private float WordWeight(string lower, HudChatAntiSpamFilter.WeightedWildcards words)
    {
        string clear = lower;
        foreach (char trash in words.TrashSymbols)
        {
            clear = clear.Replace(trash.ToString(), string.Empty, StringComparison.Ordinal);
        }

        float sum = 0;
        foreach (HudChatAntiSpamPattern pattern in words.Patterns)
        {
            string lowerPattern = LowerCodeUnits(pattern.Pattern.AsSpan());
            if (WildcardMatch(lowerPattern, clear))
            {
                sum += pattern.WeightHundredths / 100f;
            }
        }

        return sum;
    }

    internal static bool WildcardMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        int patternIndex = 0;
        int valueIndex = 0;
        int starIndex = -1;
        int starValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || pattern[patternIndex] == value[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
