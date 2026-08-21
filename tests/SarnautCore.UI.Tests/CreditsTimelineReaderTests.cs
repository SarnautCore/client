namespace SarnautCore.UI.Tests;

public sealed class CreditsTimelineReaderTests
{
    [Fact]
    public void ReadsTheAuthoredTimelineContract()
    {
        CreditsTimeline timeline = CreditsFixture.Timeline("rus");

        Assert.Equal("rus", timeline.Locale);
        Assert.Equal("CreditsMedia", timeline.MediaNode);
        Assert.Equal(107, timeline.Text.Entries.Count);
        Assert.Equal(CreditsTiming.Text, timeline.Text.Timing);
        Assert.Equal(20, timeline.Pictures.Frames.Count);
        Assert.Equal(CreditsTiming.Visual, timeline.Pictures.Timing);
        Assert.Equal(CreditsBlend.Multiply, timeline.Pictures.Blend);
        Assert.Equal(8, timeline.Backgrounds.Frames.Count);
        Assert.Equal(CreditsBlend.Alpha, timeline.Backgrounds.Blend);
        Assert.Equal("credits_music", timeline.MusicCue);
        Assert.Equal("credits-picture-01", timeline.Pictures.Frames[0].TextureId);
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

    [Fact]
    public void RejectsTheOldLooseTexturePathContract()
    {
        byte[] json = CreditsFixture.Json();
        string old = System.Text.Encoding.UTF8.GetString(json)
            .Replace(
                "\"schema_id\":\"sarnaut.ui-credits-timeline/v2\"",
                "\"schema_id\":\"sarnaut.ui-credits-timeline/v1\"",
                StringComparison.Ordinal)
            .Replace(
                "\"texture_id\":\"credits-picture-01\"",
                "\"texture\":\"media/credits/picture-01.png\"",
                StringComparison.Ordinal);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(old));

        Assert.Throws<InvalidDataException>(() => CreditsTimelineReader.Parse(stream));
    }

    [Fact]
    public void RejectsAFrameThatDoesNotNameItsPreloadedTexture()
    {
        byte[] json = CreditsFixture.Json();
        string mismatched = System.Text.Encoding.UTF8.GetString(json).Replace(
            "\"texture_id\":\"credits-picture-01\"",
            "\"texture_id\":\"credits-picture-02\"",
            StringComparison.Ordinal);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mismatched));

        Assert.Throws<InvalidDataException>(() => CreditsTimelineReader.Parse(stream));
    }
}
