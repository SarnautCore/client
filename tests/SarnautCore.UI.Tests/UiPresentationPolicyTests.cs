namespace SarnautCore.UI.Tests;

public sealed class UiPresentationPolicyTests
{
    [Theory]
    [InlineData("credits_music")]
    [InlineData("main_menu_music")]
    public void AcceptsOnlyFrozenProductMusicCues(string cue) =>
        Assert.Equal(cue, UiPresentationPolicy.RequireMusicCue(cue));

    [Theory]
    [InlineData("MainTitle")]
    [InlineData("Credits_Music")]
    [InlineData("unknown")]
    [InlineData("")]
    public void RejectsUnknownOrProvenanceMusicCues(string cue) =>
        Assert.Throws<ArgumentException>(() => UiPresentationPolicy.RequireMusicCue(cue));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5f)]
    [InlineData(1, 1)]
    public void AcceptsFiniteOpacityWithinUnitRange(double value, float expected) =>
        Assert.Equal(expected, UiPresentationPolicy.RequireOpacity(value, "opacity"));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidOpacity(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UiPresentationPolicy.RequireOpacity(value, "opacity"));

    [Fact]
    public void ConvertsProductMarkupToPlainDocumentText()
    {
        string text = UiPresentationPolicy.ProductMarkupToPlainText(
            "<header>Terms</header><p>A &amp; B<br/>Line</p>\r\n\r\n\r\nEnd");

        Assert.Equal("Terms\nA & B\nLine\n\nEnd", text);
    }

    [Fact]
    public void SameEulaDocumentUpdatePreservesDocumentState()
    {
        var state = new UiEulaPresentationState();
        Assert.True(state.Apply("eula-document-01", "<p>Original</p>", canAccept: false));

        Assert.False(state.Apply("eula-document-01", "Replacement", canAccept: true));

        Assert.Equal("eula-document-01", state.DocumentId);
        Assert.Equal("Original", state.Body);
        Assert.True(state.CanAccept);
    }

    [Fact]
    public void NewEulaDocumentReplacesNormalizedBodyAndRequiresProductId()
    {
        var state = new UiEulaPresentationState();
        state.Apply("eula-document-01", "First", canAccept: true);

        Assert.True(state.Apply("eula-document-02", "<p>Second &amp; final</p>", canAccept: false));
        Assert.Equal("Second & final", state.Body);
        Assert.False(state.CanAccept);
        Assert.Throws<InvalidDataException>(
            () => state.Apply("EulaDocument", "Body", canAccept: false));
    }
}
