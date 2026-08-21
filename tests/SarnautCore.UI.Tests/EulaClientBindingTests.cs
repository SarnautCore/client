namespace SarnautCore.UI.Tests;

public sealed class EulaClientBindingTests
{
    private static readonly EulaDocument[] Documents =
    [
        new("one", "First"),
        new("two", "Second"),
        new("three", "Third"),
    ];

    [Fact]
    public void Binding_presents_each_state_and_exposes_accept_availability()
    {
        var view = new View();
        var binding = CreateBinding(view);

        binding.Start();
        binding.ScrollChanged(true);
        binding.Dispatch(EulaCommand.Accept);

        Assert.Equal(3, view.Presented.Count);
        Assert.False(view.Presented[0].CanAccept);
        Assert.True(view.Presented[1].CanAccept);
        Assert.Equal("two", view.Presented[2].Document?.Id);
        Assert.False(view.Presented[2].CanAccept);
        Assert.Equal(0, view.DismissCount);
    }

    [Fact]
    public void Binding_dismisses_after_final_accept()
    {
        var view = new View();
        var binding = CreateBinding(view);
        binding.Start();

        for (int index = 0; index < 3; index++)
        {
            binding.ScrollChanged(true);
            binding.Dispatch(EulaCommand.Accept);
        }

        Assert.Equal(EulaStatus.Continued, binding.State.Status);
        Assert.Equal(1, view.DismissCount);
    }

    [Theory]
    [InlineData(EulaCommand.Decline)]
    [InlineData(EulaCommand.Close)]
    public void Binding_dismisses_after_refusal(EulaCommand command)
    {
        var view = new View();
        var binding = CreateBinding(view);
        binding.Start();

        binding.Dispatch(command);

        Assert.Equal(EulaStatus.ExitRequested, binding.State.Status);
        Assert.Equal(1, view.DismissCount);
    }

    [Fact]
    public void Binding_dismisses_immediately_when_this_exact_version_was_accepted()
    {
        var view = new View();
        var binding = CreateBinding(view, new GameVersion("current"));

        binding.Start();

        Assert.Empty(view.Presented);
        Assert.Equal(1, view.DismissCount);
    }

    private static EulaClientBinding CreateBinding(View view, GameVersion? accepted = null)
    {
        var store = new Store { AcceptedVersion = accepted };
        var controller = new EulaController(
            Documents,
            new VersionSource(new GameVersion("current")),
            store,
            new Exit(),
            new Continuation());
        return new EulaClientBinding(controller, view);
    }

    private sealed class View : IEulaViewPort
    {
        public List<EulaViewState> Presented { get; } = [];
        public int DismissCount { get; private set; }
        public void Present(EulaViewState state) => Presented.Add(state);
        public void Dismiss() => DismissCount++;
    }

    private sealed record VersionSource(GameVersion Current) : IGameVersionSource;

    private sealed class Store : IEulaAcceptanceStore
    {
        public GameVersion? AcceptedVersion { get; set; }
        public void Accept(GameVersion version) => AcceptedVersion = version;
        public void Clear() => AcceptedVersion = null;
    }

    private sealed class Exit : IApplicationExitRequest
    {
        public void RequestExit() { }
    }

    private sealed class Continuation : IEulaContinuation
    {
        public void ContinueAfterEula() { }
    }
}
