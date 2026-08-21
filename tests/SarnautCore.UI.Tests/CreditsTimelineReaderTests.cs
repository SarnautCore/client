namespace SarnautCore.UI.Tests;

public sealed class CreditsTimelineReaderTests
{
    [Fact]
    public void ReadsTheAuthoredTimelineContract()
    {
        CreditsTimeline timeline = CreditsFixture.Timeline("rus");

        Assert.Equal("rus", timeline.Locale);
        Assert.Equal(107, timeline.Text.Entries.Count);
        Assert.Equal(CreditsTiming.Text, timeline.Text.Timing);
        Assert.Equal(20, timeline.Pictures.Frames.Count);
        Assert.Equal(CreditsTiming.Visual, timeline.Pictures.Timing);
        Assert.Equal(CreditsBlend.Multiply, timeline.Pictures.Blend);
        Assert.Equal(8, timeline.Backgrounds.Frames.Count);
        Assert.Equal(CreditsBlend.Alpha, timeline.Backgrounds.Blend);
        Assert.Equal("credits_music", timeline.MusicCue);
    }

    [Fact]
    public void RejectsUnknownProductFields()
    {
        byte[] json = CreditsFixture.Json(writer => writer.WriteString("source_xdb", "private"));
        using var stream = new MemoryStream(json);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CreditsTimelineReader.Parse(stream));

        Assert.Contains("unsupported field 'source_xdb'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidJsonAsContentFailure()
    {
        using var stream = new MemoryStream("{"u8.ToArray());

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CreditsTimelineReader.Parse(stream));

        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }
}
