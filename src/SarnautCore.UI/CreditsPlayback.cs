namespace SarnautCore.UI;

public interface ICreditsClock
{
    TimeSpan Now { get; }
}

public interface ICreditsMusic
{
    void StopMainMenu();
    void PlayCredits(string cue);
    void StopCredits();
    void PlayMainMenu();
}

public interface ICreditsTooltip
{
    void Show(string productId);
    void Hide();
}

public interface ICreditsContent
{
    void Present(CreditsPresentation presentation);
    void Close();
}

public sealed record CreditsPresentation(
    double FormOpacity,
    CreditsTextPresentation? Text,
    CreditsVisualPresentation? Picture,
    CreditsVisualPresentation? Background);

public sealed record CreditsTextPresentation(
    string Id,
    string Body,
    int Priority,
    double Opacity);

public sealed record CreditsVisualPresentation(
    string Id,
    NativeContentPath Texture,
    int Priority,
    CreditsBlend Blend,
    double Opacity);

public enum CreditsBlend
{
    Alpha,
    Multiply,
}

public enum CreditsPlaybackState
{
    Created,
    FormFadeIn,
    Playing,
    FormFadeOut,
    Closed,
}

internal enum CreditsCloseKind
{
    SequenceEnd,
    Exit,
}

public sealed class CreditsController : IDisposable
{
    public static readonly TimeSpan FormFadeIn = TimeSpan.FromSeconds(1);

    private readonly CreditsTimeline _timeline;
    private readonly ICreditsClock _clock;
    private readonly ICreditsMusic _music;
    private readonly ICreditsTooltip _tooltip;
    private readonly ICreditsContent _content;
    private TimeSpan _lastObserved;
    private TimeSpan _formStartedAt;
    private TimeSpan _tracksStartedAt;
    private TimeSpan _textStartedAt;
    private TimeSpan _fadeOutStartedAt;
    private double _fadeOutStartOpacity;
    private int _textIndex;
    private bool _creditsMusicPlaying;
    private bool _tooltipVisible;
    private CreditsCloseKind _closeKind;
    private CreditsTextPresentation? _exitText;
    private CreditsVisualPresentation? _exitPicture;

    public CreditsController(
        CreditsTimeline timeline,
        ICreditsClock clock,
        ICreditsMusic music,
        ICreditsTooltip tooltip,
        ICreditsContent content)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _music = music ?? throw new ArgumentNullException(nameof(music));
        _tooltip = tooltip ?? throw new ArgumentNullException(nameof(tooltip));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _timeline.ValidateAuthoredContract();
    }

    public CreditsPlaybackState State { get; private set; } = CreditsPlaybackState.Created;
    public int CurrentTextNumber => State == CreditsPlaybackState.Playing ? _textIndex + 1 : 0;

    public void Open()
    {
        if (State != CreditsPlaybackState.Created)
        {
            throw new InvalidOperationException("Credits playback is single-use");
        }

        _formStartedAt = ReadClock();
        State = CreditsPlaybackState.FormFadeIn;
        Present(_formStartedAt);
    }

    public void Tick()
    {
        if (State == CreditsPlaybackState.Created)
        {
            throw new InvalidOperationException("Credits playback has not opened");
        }

        if (State == CreditsPlaybackState.Closed)
        {
            return;
        }

        TimeSpan now = ReadClock();
        if (State == CreditsPlaybackState.FormFadeIn
            && now - _formStartedAt >= FormFadeIn)
        {
            StartTracks(_formStartedAt + FormFadeIn);
        }

        if (State == CreditsPlaybackState.Playing)
        {
            AdvanceText(now);
        }

        if (State == CreditsPlaybackState.FormFadeOut
            && now - _fadeOutStartedAt >= FormFadeIn)
        {
            CompleteClose();
        }

        if (State != CreditsPlaybackState.Closed)
        {
            Present(now);
        }
    }

    public void Dispatch(CreditsAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (State is CreditsPlaybackState.Created
            or CreditsPlaybackState.FormFadeOut
            or CreditsPlaybackState.Closed)
        {
            return;
        }

        switch (action.Kind)
        {
            case CreditsActionKind.Previous:
                Navigate(-1);
                break;
            case CreditsActionKind.Next:
                Navigate(1);
                break;
            case CreditsActionKind.Close:
                BeginExit();
                break;
            case CreditsActionKind.ShowTooltip:
                _tooltip.Show(action.ProductId!);
                _tooltipVisible = true;
                break;
            case CreditsActionKind.HideTooltip:
                HideTooltip();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
        }
    }

    public void Cancel()
    {
        if (State is CreditsPlaybackState.Created or CreditsPlaybackState.Closed)
        {
            return;
        }

        CompleteClose();
    }

    public void Dispose() => Cancel();

    private TimeSpan ReadClock()
    {
        TimeSpan now = _clock.Now;
        if (now < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Credits clock must not be negative");
        }

        if (State != CreditsPlaybackState.Created && now < _lastObserved)
        {
            throw new InvalidOperationException("Credits clock moved backwards");
        }

        _lastObserved = now;
        return now;
    }

    private void StartTracks(TimeSpan startedAt)
    {
        State = CreditsPlaybackState.Playing;
        _tracksStartedAt = startedAt;
        _textStartedAt = startedAt;
        _textIndex = 0;
        _music.StopMainMenu();
        _music.PlayCredits(_timeline.MusicCue);
        _creditsMusicPlaying = true;
    }

    private void AdvanceText(TimeSpan now)
    {
        TimeSpan duration = _timeline.Text.Timing.Duration;
        while (now - _textStartedAt >= duration)
        {
            _textIndex++;
            _textStartedAt += duration;
            if (_textIndex == _timeline.Text.Entries.Count)
            {
                BeginSequenceClose(_textStartedAt);
                return;
            }
        }
    }

    private void Navigate(int direction)
    {
        if (State != CreditsPlaybackState.Playing)
        {
            return;
        }

        if (direction < 0 && _textIndex == 0)
        {
            return;
        }

        TimeSpan now = ReadClock();
        int target = _textIndex + direction;
        if (target >= _timeline.Text.Entries.Count)
        {
            BeginSequenceClose(now);
            Present(now);
            return;
        }

        _textIndex = target;
        _textStartedAt = now;
        Present(now);
    }

    private void Present(TimeSpan now)
    {
        if (State == CreditsPlaybackState.FormFadeIn)
        {
            double opacity = UnitInterval((now - _formStartedAt).TotalSeconds / FormFadeIn.TotalSeconds);
            _content.Present(new CreditsPresentation(opacity, null, null, null));
            return;
        }

        if (State == CreditsPlaybackState.FormFadeOut)
        {
            PresentFadeOut(now);
            return;
        }

        TimeSpan visualElapsed = now - _tracksStartedAt;
        CreditsTextEntry text = _timeline.Text.Entries[_textIndex];
        _content.Present(new CreditsPresentation(
            1,
            new CreditsTextPresentation(
                text.Id,
                text.Body,
                _timeline.Text.Priority,
                Opacity(now - _textStartedAt, _timeline.Text.Timing)),
            VisualAt(_timeline.Pictures, visualElapsed),
            VisualAt(_timeline.Backgrounds, visualElapsed)));
    }

    private void PresentFadeOut(TimeSpan now)
    {
        double fade = UnitInterval(
            1 - ((now - _fadeOutStartedAt).TotalSeconds / FormFadeIn.TotalSeconds));
        double formOpacity = _fadeOutStartOpacity * fade;
        if (!_creditsMusicPlaying)
        {
            _content.Present(new CreditsPresentation(formOpacity, null, null, null));
            return;
        }

        TimeSpan visualElapsed = now - _tracksStartedAt;
        CreditsTextPresentation? text = _closeKind == CreditsCloseKind.Exit
            ? WithOpacity(_exitText, fade)
            : null;
        CreditsVisualPresentation? picture = _closeKind == CreditsCloseKind.Exit
            ? WithOpacity(_exitPicture, fade)
            : VisualAt(_timeline.Pictures, visualElapsed);
        _content.Present(new CreditsPresentation(
            formOpacity,
            text,
            picture,
            VisualAt(_timeline.Backgrounds, visualElapsed)));
    }

    private void BeginExit()
    {
        TimeSpan now = ReadClock();
        HideTooltip();
        _fadeOutStartedAt = now;
        _fadeOutStartOpacity = State == CreditsPlaybackState.Playing ? 1 : 0.5;
        _closeKind = CreditsCloseKind.Exit;
        if (State == CreditsPlaybackState.Playing)
        {
            TimeSpan textElapsed = now - _textStartedAt;
            if (IsSolid(textElapsed, _timeline.Text.Timing))
            {
                CreditsTextEntry entry = _timeline.Text.Entries[_textIndex];
                _exitText = new CreditsTextPresentation(
                    entry.Id,
                    entry.Body,
                    _timeline.Text.Priority,
                    1);
            }

            TimeSpan visualElapsed = now - _tracksStartedAt;
            CreditsVisualPresentation picture = VisualAt(_timeline.Pictures, visualElapsed);
            TimeSpan pictureElapsed = TimeSpan.FromTicks(
                visualElapsed.Ticks % _timeline.Pictures.Timing.Duration.Ticks);
            if (IsSolid(pictureElapsed, _timeline.Pictures.Timing))
            {
                _exitPicture = picture with { Opacity = 1 };
            }
        }

        State = CreditsPlaybackState.FormFadeOut;
        Present(now);
    }

    private void BeginSequenceClose(TimeSpan now)
    {
        HideTooltip();
        _fadeOutStartedAt = now;
        _fadeOutStartOpacity = 1;
        _closeKind = CreditsCloseKind.SequenceEnd;
        State = CreditsPlaybackState.FormFadeOut;
    }

    private void CompleteClose()
    {
        HideTooltip();
        if (_creditsMusicPlaying)
        {
            _music.StopCredits();
            _music.PlayMainMenu();
            _creditsMusicPlaying = false;
        }

        State = CreditsPlaybackState.Closed;
        _content.Close();
    }

    private static bool IsSolid(TimeSpan elapsed, CreditsTiming timing) =>
        elapsed >= timing.FadeIn && elapsed < timing.FadeIn + timing.Hold;

    private static CreditsTextPresentation? WithOpacity(
        CreditsTextPresentation? presentation,
        double opacity) => presentation is null ? null : presentation with { Opacity = opacity };

    private static CreditsVisualPresentation? WithOpacity(
        CreditsVisualPresentation? presentation,
        double opacity) => presentation is null ? null : presentation with { Opacity = opacity };

    private static CreditsVisualPresentation VisualAt(
        CreditsVisualTrack track,
        TimeSpan elapsed)
    {
        long durationTicks = track.Timing.Duration.Ticks;
        long cycleTicks = checked(durationTicks * track.Frames.Count);
        long offsetTicks = elapsed.Ticks % cycleTicks;
        int index = checked((int)(offsetTicks / durationTicks));
        TimeSpan frameElapsed = TimeSpan.FromTicks(offsetTicks % durationTicks);
        CreditsVisualFrame frame = track.Frames[index];
        return new CreditsVisualPresentation(
            frame.Id,
            frame.Texture,
            track.Priority,
            track.Blend,
            Opacity(frameElapsed, track.Timing));
    }

    private static double Opacity(TimeSpan elapsed, CreditsTiming timing)
    {
        if (elapsed < timing.FadeIn)
        {
            return UnitInterval(elapsed.TotalSeconds / timing.FadeIn.TotalSeconds);
        }

        TimeSpan fadeOutStart = timing.FadeIn + timing.Hold;
        if (elapsed < fadeOutStart)
        {
            return 1;
        }

        return UnitInterval(
            1 - ((elapsed - fadeOutStart).TotalSeconds / timing.FadeOut.TotalSeconds));
    }

    private static double UnitInterval(double value) => Math.Clamp(value, 0, 1);

    private void HideTooltip()
    {
        if (!_tooltipVisible)
        {
            return;
        }

        _tooltip.Hide();
        _tooltipVisible = false;
    }
}
