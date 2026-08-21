namespace SarnautCore.UI;

public sealed record CreditsTimeline(
    string Locale,
    string MediaNode,
    CreditsTextTrack Text,
    CreditsVisualTrack Pictures,
    CreditsVisualTrack Backgrounds,
    string MusicCue)
{
    public const string SchemaId = "sarnaut.ui-credits-timeline/v2";

    public void ValidateAuthoredContract()
    {
        if (string.IsNullOrWhiteSpace(Locale))
        {
            throw new InvalidDataException("Credits locale must not be empty");
        }

        if (MediaNode != "CreditsMedia")
        {
            throw new InvalidDataException("Credits media node must be 'CreditsMedia'");
        }

        if (MusicCue != "credits_music")
        {
            throw new InvalidDataException("Credits music cue must be 'credits_music'");
        }

        Text.Validate();
        Pictures.Validate("picture", 20, CreditsBlend.Multiply);
        Backgrounds.Validate("background", 8, CreditsBlend.Alpha);

        if (Text.Priority != 100
            || Text.Timing != CreditsTiming.Text
            || Pictures.Priority != 100
            || Pictures.Timing != CreditsTiming.Visual
            || Backgrounds.Priority != 0
            || Backgrounds.Timing != CreditsTiming.Visual)
        {
            throw new InvalidDataException("Credits tracks do not match the authored timings or priorities");
        }

        string[] textureIds = Pictures.Frames
            .Concat(Backgrounds.Frames)
            .Select(frame => frame.TextureId)
            .ToArray();
        if (textureIds.Distinct(StringComparer.Ordinal).Count() != textureIds.Length)
        {
            throw new InvalidDataException("Credits visual tracks repeat a texture");
        }
    }
}

public sealed record CreditsTextTrack(
    int Priority,
    CreditsTiming Timing,
    IReadOnlyList<CreditsTextEntry> Entries)
{
    internal void Validate()
    {
        if (Entries.Count != 107)
        {
            throw new InvalidDataException("Credits text track must contain 107 entries and an implicit terminator");
        }

        for (int index = 0; index < Entries.Count; index++)
        {
            CreditsTextEntry entry = Entries[index];
            string expected = $"credits-text-{index + 1:000}";
            if (entry.Id != expected || string.IsNullOrWhiteSpace(entry.Body))
            {
                throw new InvalidDataException($"Credits text entry {index + 1} is incompatible with the authored sequence");
            }
        }
    }
}

public sealed record CreditsTextEntry(string Id, string Body);

public sealed record CreditsVisualTrack(
    int Priority,
    CreditsBlend Blend,
    CreditsTiming Timing,
    IReadOnlyList<CreditsVisualFrame> Frames)
{
    internal void Validate(string label, int expectedCount, CreditsBlend expectedBlend)
    {
        if (Blend != expectedBlend || Frames.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"Credits {label} track has an incompatible blend or frame count");
        }

        for (int index = 0; index < Frames.Count; index++)
        {
            CreditsVisualFrame frame = Frames[index];
            string expected = $"credits-{label}-{index + 1:00}";
            if (frame.Id != expected || frame.TextureId != expected)
            {
                throw new InvalidDataException(
                    $"Credits {label} frame {index + 1} is outside the authored media sequence");
            }
        }
    }
}

public sealed record CreditsVisualFrame(string Id, string TextureId);

public readonly record struct CreditsTiming(
    TimeSpan FadeIn,
    TimeSpan Hold,
    TimeSpan FadeOut)
{
    public static CreditsTiming Text { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(1));

    public static CreditsTiming Visual { get; } = new(
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(4));

    public TimeSpan Duration => FadeIn + Hold + FadeOut;
}
