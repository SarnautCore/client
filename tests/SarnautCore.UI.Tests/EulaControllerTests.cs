namespace SarnautCore.UI.Tests;

public sealed class EulaControllerTests
{
    private static readonly EulaDocument[] Documents =
    [
        new("eula-document-01", "First document"),
        new("eula-document-02", "Second document"),
        new("eula-document-03", "Third document"),
    ];

    [Fact]
    public void Starts_on_the_first_authored_document_with_accept_disabled()
    {
        var context = new Context();

        EulaViewState state = context.Controller.Start();

        Assert.Equal(EulaStatus.Presenting, state.Status);
        Assert.Equal(Documents[0], state.Document);
        Assert.Equal(1, state.DocumentNumber);
        Assert.Equal(3, state.DocumentCount);
        Assert.False(state.CanAccept);
        Assert.Equal(0, context.Continuation.Count);
    }

    [Fact]
    public void Starting_twice_does_not_reset_the_active_document_or_scroll_state()
    {
        var context = new Context();
        context.Controller.Start();
        context.Controller.SetScrollAtEnd(true);

        EulaViewState state = context.Controller.Start();

        Assert.Equal(Documents[0], state.Document);
        Assert.True(state.CanAccept);
        Assert.Equal(0, context.Continuation.Count);
    }

    [Fact]
    public void An_exact_accepted_version_bypasses_the_modal_and_continues_once()
    {
        var context = new Context(acceptedVersion: new GameVersion("1.2.3+release.4"));

        EulaViewState first = context.Controller.Start();
        EulaViewState second = context.Controller.Start();

        Assert.Equal(EulaStatus.Continued, first.Status);
        Assert.False(first.IsVisible);
        Assert.Null(first.Document);
        Assert.Equal(first, second);
        Assert.Equal(1, context.Continuation.Count);
        Assert.Equal(0, context.Exit.Count);
    }

    [Theory]
    [InlineData("1.2.3+release.04")]
    [InlineData("1.2.3+RELEASE.4")]
    [InlineData("1.2.3+release.4 ")]
    public void A_nonidentical_accepted_version_requires_the_eula(string acceptedVersion)
    {
        var context = new Context(acceptedVersion: new GameVersion(acceptedVersion));

        EulaViewState state = context.Controller.Start();

        Assert.Equal(EulaStatus.Presenting, state.Status);
        Assert.Equal(0, context.Continuation.Count);
    }

    [Fact]
    public void Scroll_end_enables_accept_and_scrolling_away_disables_it_again()
    {
        var context = new Context();
        context.Controller.Start();

        Assert.True(context.Controller.SetScrollAtEnd(true).CanAccept);
        Assert.False(context.Controller.SetScrollAtEnd(false).CanAccept);
    }

    [Fact]
    public void Accept_before_scroll_end_changes_nothing()
    {
        var context = new Context();
        EulaViewState before = context.Controller.Start();

        EulaViewState after = context.Controller.Dispatch(EulaCommand.Accept);

        Assert.Equal(before, after);
        Assert.Null(context.Store.AcceptedVersion);
        Assert.Equal(0, context.Continuation.Count);
    }

    [Fact]
    public void Accept_advances_in_authored_order_and_resets_scroll_each_time()
    {
        var context = new Context();
        context.Controller.Start();

        EulaViewState second = AcceptCurrent(context.Controller);
        EulaViewState third = AcceptCurrent(context.Controller);

        Assert.Equal(Documents[1], second.Document);
        Assert.Equal(2, second.DocumentNumber);
        Assert.False(second.CanAccept);
        Assert.Equal(Documents[2], third.Document);
        Assert.Equal(3, third.DocumentNumber);
        Assert.False(third.CanAccept);
        Assert.Null(context.Store.AcceptedVersion);
    }

    [Fact]
    public void Final_accept_persists_the_exact_current_version_then_continues()
    {
        var effects = new List<string>();
        var context = new Context(effects: effects);
        context.Controller.Start();
        AcceptCurrent(context.Controller);
        AcceptCurrent(context.Controller);

        EulaViewState state = AcceptCurrent(context.Controller);

        Assert.Equal(EulaStatus.Continued, state.Status);
        Assert.False(state.IsVisible);
        Assert.Equal(new GameVersion("1.2.3+release.4"), context.Store.AcceptedVersion);
        Assert.Equal(1, context.Store.AcceptCount);
        Assert.Equal(1, context.Continuation.Count);
        Assert.Equal(0, context.Exit.Count);
        Assert.Equal(["accept:1.2.3+release.4", "continue"], effects);
    }

    [Theory]
    [InlineData(EulaCommand.Decline)]
    [InlineData(EulaCommand.Close)]
    public void Refusal_clears_acceptance_and_requests_exit(EulaCommand command)
    {
        var effects = new List<string>();
        var context = new Context(
            acceptedVersion: new GameVersion("older"),
            effects: effects);
        context.Controller.Start();
        context.Controller.SetScrollAtEnd(true);

        EulaViewState state = context.Controller.Dispatch(command);

        Assert.Equal(EulaStatus.ExitRequested, state.Status);
        Assert.False(state.IsVisible);
        Assert.Null(context.Store.AcceptedVersion);
        Assert.Equal(1, context.Store.ClearCount);
        Assert.Equal(1, context.Exit.Count);
        Assert.Equal(0, context.Continuation.Count);
        Assert.Equal(["clear", "exit"], effects);
    }

    [Fact]
    public void Events_after_a_terminal_transition_have_no_side_effects()
    {
        var context = new Context();
        context.Controller.Start();
        context.Controller.Dispatch(EulaCommand.Close);

        context.Controller.SetScrollAtEnd(true);
        context.Controller.Dispatch(EulaCommand.Close);
        context.Controller.Dispatch(EulaCommand.Accept);
        context.Controller.Start();

        Assert.Equal(EulaStatus.ExitRequested, context.Controller.Status);
        Assert.Equal(1, context.Store.ClearCount);
        Assert.Equal(1, context.Exit.Count);
        Assert.Equal(0, context.Continuation.Count);
    }

    [Fact]
    public void Rejects_any_document_set_other_than_three_ordered_nonempty_unique_documents()
    {
        Assert.Throws<ArgumentException>(() => new Context(documents: Documents[..2]));
        Assert.Throws<ArgumentException>(() => new Context(documents:
        [
            Documents[0],
            Documents[1],
            Documents[1],
        ]));
        Assert.Throws<ArgumentException>(() => new Context(documents:
        [
            Documents[0],
            Documents[1],
            new EulaDocument("eula-document-03", " "),
        ]));
    }

    private static EulaViewState AcceptCurrent(EulaController controller)
    {
        controller.SetScrollAtEnd(true);
        return controller.Dispatch(EulaCommand.Accept);
    }

    private sealed class Context
    {
        public Context(
            GameVersion? acceptedVersion = null,
            IReadOnlyList<EulaDocument>? documents = null,
            List<string>? effects = null)
        {
            Store.Effects = effects;
            Exit.Effects = effects;
            Continuation.Effects = effects;
            Store.AcceptedVersion = acceptedVersion;
            Controller = new EulaController(
                documents ?? Documents,
                new VersionSource(new GameVersion("1.2.3+release.4")),
                Store,
                Exit,
                Continuation);
        }

        public AcceptanceStore Store { get; } = new();
        public CounterExit Exit { get; } = new();
        public CounterContinuation Continuation { get; } = new();
        public EulaController Controller { get; }
    }

    private sealed record VersionSource(GameVersion Current) : IGameVersionSource;

    private sealed class AcceptanceStore : IEulaAcceptanceStore
    {
        public GameVersion? AcceptedVersion { get; set; }
        public int AcceptCount { get; private set; }
        public int ClearCount { get; private set; }
        public List<string>? Effects { get; set; }

        public void Accept(GameVersion version)
        {
            AcceptedVersion = version;
            AcceptCount++;
            Effects?.Add($"accept:{version}");
        }

        public void Clear()
        {
            AcceptedVersion = null;
            ClearCount++;
            Effects?.Add("clear");
        }
    }

    private sealed class CounterExit : IApplicationExitRequest
    {
        public int Count { get; private set; }
        public List<string>? Effects { get; set; }

        public void RequestExit()
        {
            Count++;
            Effects?.Add("exit");
        }
    }

    private sealed class CounterContinuation : IEulaContinuation
    {
        public int Count { get; private set; }
        public List<string>? Effects { get; set; }

        public void ContinueAfterEula()
        {
            Count++;
            Effects?.Add("continue");
        }
    }
}
