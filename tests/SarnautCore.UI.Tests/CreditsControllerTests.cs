namespace SarnautCore.UI.Tests;

public sealed class CreditsControllerTests
{
    [Fact]
    public void FormFadesForOneSecondBeforeTracksAndMusicStart()
    {
        CreditsRig rig = CreditsRig.Open();

        Assert.Equal(CreditsPlaybackState.FormFadeIn, rig.Controller.State);
        Assert.Equal(0, rig.Content.Last.FormOpacity);
        Assert.Null(rig.Content.Last.Text);
        Assert.Empty(rig.Music.Events);

        rig.At(0.5);
        Assert.Equal(0.5, rig.Content.Last.FormOpacity, 6);
        Assert.Empty(rig.Music.Events);

        rig.At(1);
        Assert.Equal(CreditsPlaybackState.Playing, rig.Controller.State);
        Assert.Equal(["stop-main", "play:credits_music"], rig.Music.Events);
        Assert.Equal("credits-text-001", rig.Content.Last.Text?.Id);
        Assert.Equal(0, rig.Content.Last.Text?.Opacity);
        Assert.Equal("credits-picture-01", rig.Content.Last.Picture?.Id);
        Assert.Equal("credits-background-01", rig.Content.Last.Background?.Id);
    }

    [Fact]
    public void TextAndVisualTracksUseIndependentAuthoredTiming()
    {
        CreditsRig rig = CreditsRig.Open();

        rig.At(1.5);
        Assert.Equal(0.5, rig.Content.Last.Text?.Opacity ?? -1, 6);
        Assert.Equal(0.125, rig.Content.Last.Picture?.Opacity ?? -1, 6);

        rig.At(8.5);
        Assert.Equal(0.5, rig.Content.Last.Text?.Opacity ?? -1, 6);
        Assert.Equal("credits-text-001", rig.Content.Last.Text?.Id);
        Assert.Equal(1, rig.Content.Last.Picture?.Opacity);

        rig.At(9);
        Assert.Equal("credits-text-002", rig.Content.Last.Text?.Id);
        Assert.Equal(0, rig.Content.Last.Text?.Opacity);

        rig.At(17);
        Assert.Equal("credits-picture-02", rig.Content.Last.Picture?.Id);
        Assert.Equal("credits-background-02", rig.Content.Last.Background?.Id);
        Assert.Equal(0, rig.Content.Last.Picture?.Opacity);
    }

    [Fact]
    public void VisualTracksLoopWithoutChangingTheTextClock()
    {
        CreditsRig rig = CreditsRig.Open();

        rig.At(321);

        Assert.Equal("credits-picture-01", rig.Content.Last.Picture?.Id);
        Assert.Equal("credits-background-05", rig.Content.Last.Background?.Id);
        Assert.Equal("credits-text-041", rig.Content.Last.Text?.Id);
    }

    [Fact]
    public void FinalTextFadeOutStartsTheOneSecondFormFadeAndThenCloses()
    {
        CreditsRig rig = CreditsRig.Open();

        rig.At(856.999);
        Assert.Equal("credits-text-107", rig.Content.Last.Text?.Id);
        Assert.Equal(CreditsPlaybackState.Playing, rig.Controller.State);

        rig.At(857);
        Assert.Equal(CreditsPlaybackState.FormFadeOut, rig.Controller.State);
        Assert.Equal(1, rig.Content.Last.FormOpacity);
        Assert.Null(rig.Content.Last.Text);
        Assert.Equal("credits-picture-14", rig.Content.Last.Picture?.Id);
        Assert.Equal(["stop-main", "play:credits_music"], rig.Music.Events);

        rig.At(857.5);
        Assert.Equal(0.5, rig.Content.Last.FormOpacity, 6);
        rig.At(858);
        Assert.Equal(CreditsPlaybackState.Closed, rig.Controller.State);
        Assert.Equal(["stop-main", "play:credits_music", "stop-credits", "play-main"], rig.Music.Events);
        Assert.Equal(1, rig.Content.CloseCount);
    }

    [Fact]
    public void PreviousAtFirstBlockIsANoopAndNavigationResetsOnlyTextFade()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(5);
        CreditsPresentation before = rig.Content.Last;

        rig.Controller.Dispatch(CreditsAction.Previous);
        Assert.Same(before, rig.Content.Last);

        rig.Controller.Dispatch(CreditsAction.Next);
        Assert.Equal(2, rig.Controller.CurrentTextNumber);
        Assert.Equal("credits-text-002", rig.Content.Last.Text?.Id);
        Assert.Equal(0, rig.Content.Last.Text?.Opacity);
        Assert.Equal(before.Picture?.Id, rig.Content.Last.Picture?.Id);
        Assert.Equal(before.Picture?.Opacity, rig.Content.Last.Picture?.Opacity);

        rig.At(5.5);
        Assert.Equal(0.5, rig.Content.Last.Text?.Opacity ?? -1, 6);
        rig.Controller.Dispatch(CreditsAction.Previous);
        Assert.Equal("credits-text-001", rig.Content.Last.Text?.Id);
        Assert.Equal(0, rig.Content.Last.Text?.Opacity);
    }

    [Fact]
    public void NextFromTheFinalBlockStartsTheFormFadeOut()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(1);
        for (int index = 1; index < 107; index++)
        {
            rig.Controller.Dispatch(CreditsAction.Next);
        }

        Assert.Equal(107, rig.Controller.CurrentTextNumber);
        rig.Controller.Dispatch(CreditsAction.Next);

        Assert.Equal(CreditsPlaybackState.FormFadeOut, rig.Controller.State);
        Assert.Equal(0, rig.Content.CloseCount);
        rig.At(2);
        Assert.Equal(CreditsPlaybackState.Closed, rig.Controller.State);
        Assert.Equal(1, rig.Content.CloseCount);
    }

    [Fact]
    public void ExitDuringFormFadeUsesTheAuthoredHalfOpacityFade()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(0.5);

        rig.Controller.Dispatch(CreditsAction.Close);

        Assert.Equal(CreditsPlaybackState.FormFadeOut, rig.Controller.State);
        Assert.Equal(0.5, rig.Content.Last.FormOpacity);
        Assert.Empty(rig.Music.Events);
        rig.At(1);
        Assert.Equal(0.25, rig.Content.Last.FormOpacity, 6);
        rig.At(1.5);
        Assert.Equal(CreditsPlaybackState.Closed, rig.Controller.State);
        Assert.Equal(1, rig.Content.CloseCount);
    }

    [Fact]
    public void ExitDuringPlaybackFreezesSolidTextAndPictureWhileBackgroundAdvances()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(5);

        rig.Controller.Dispatch(CreditsAction.Close);
        Assert.Equal(CreditsPlaybackState.FormFadeOut, rig.Controller.State);
        Assert.Equal(1, rig.Content.Last.FormOpacity);
        Assert.Equal(1, rig.Content.Last.Text?.Opacity);
        Assert.Equal(1, rig.Content.Last.Picture?.Opacity);

        rig.At(5.5);
        Assert.Equal(0.5, rig.Content.Last.FormOpacity, 6);
        Assert.Equal(0.5, rig.Content.Last.Text?.Opacity ?? -1, 6);
        Assert.Equal(0.5, rig.Content.Last.Picture?.Opacity ?? -1, 6);
        Assert.Equal(1, rig.Content.Last.Background?.Opacity);
        rig.At(6);

        Assert.Equal(["stop-main", "play:credits_music", "stop-credits", "play-main"], rig.Music.Events);
        Assert.Equal(1, rig.Content.CloseCount);
    }

    [Fact]
    public void CancellationImmediatelyRestoresMusicAndStopsFurtherPresentation()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(1);
        int presentations = rig.Content.Presentations.Count;

        rig.Controller.Cancel();
        rig.Controller.Cancel();
        rig.At(100);

        Assert.Equal(["stop-main", "play:credits_music", "stop-credits", "play-main"], rig.Music.Events);
        Assert.Equal(1, rig.Content.CloseCount);
        Assert.Equal(presentations, rig.Content.Presentations.Count);
    }

    [Fact]
    public void TooltipActionsUseTypedProductIdsAndCloseHidesTheTooltip()
    {
        CreditsRig rig = CreditsRig.Open();

        rig.Controller.Dispatch(CreditsAction.ShowTooltip("next-button"));
        rig.Controller.Dispatch(CreditsAction.HideTooltip);
        rig.Controller.Dispatch(CreditsAction.ShowTooltip("exit-button"));
        rig.Controller.Cancel();

        Assert.Equal(["show:next-button", "hide", "show:exit-button", "hide"], rig.Tooltip.Events);
    }

    [Fact]
    public void ClockMustRemainMonotonic()
    {
        CreditsRig rig = CreditsRig.Open();
        rig.At(0.5);
        rig.Clock.Now = TimeSpan.FromSeconds(0.25);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(rig.Controller.Tick);

        Assert.Contains("backwards", error.Message, StringComparison.Ordinal);
    }

    private sealed class CreditsRig
    {
        private CreditsRig()
        {
            Controller = new CreditsController(
                CreditsFixture.Timeline(),
                Clock,
                Music,
                Tooltip,
                Content);
        }

        public FakeClock Clock { get; } = new();
        public FakeMusic Music { get; } = new();
        public FakeTooltip Tooltip { get; } = new();
        public FakeContent Content { get; } = new();
        public CreditsController Controller { get; }

        public static CreditsRig Open()
        {
            var rig = new CreditsRig();
            rig.Controller.Open();
            return rig;
        }

        public void At(double seconds)
        {
            Clock.Now = TimeSpan.FromSeconds(seconds);
            Controller.Tick();
        }
    }

    private sealed class FakeClock : ICreditsClock
    {
        public TimeSpan Now { get; set; }
    }

    private sealed class FakeMusic : ICreditsMusic
    {
        public List<string> Events { get; } = [];
        public void StopMainMenu() => Events.Add("stop-main");
        public void PlayCredits(string cue) => Events.Add($"play:{cue}");
        public void StopCredits() => Events.Add("stop-credits");
        public void PlayMainMenu() => Events.Add("play-main");
    }

    private sealed class FakeTooltip : ICreditsTooltip
    {
        public List<string> Events { get; } = [];
        public void Show(string productId) => Events.Add($"show:{productId}");
        public void Hide() => Events.Add("hide");
    }

    private sealed class FakeContent : ICreditsContent
    {
        public List<CreditsPresentation> Presentations { get; } = [];
        public CreditsPresentation Last => Presentations[^1];
        public int CloseCount { get; private set; }
        public void Present(CreditsPresentation presentation) => Presentations.Add(presentation);
        public void Close() => CloseCount++;
    }
}
