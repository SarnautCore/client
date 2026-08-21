namespace SarnautCore.UI;

/// <summary>
/// The render operations a native EULA screen supplies. It does not own flow.
/// </summary>
public interface IEulaViewPort
{
    void Present(EulaViewState state);
    void Dismiss();
}

/// <summary>
/// Connects UI input and scroll state to the engine-neutral EULA controller.
/// Product action identifiers remain the manifest adapter's responsibility.
/// </summary>
public sealed class EulaClientBinding
{
    private readonly EulaController _controller;
    private readonly IEulaViewPort _view;

    public EulaClientBinding(EulaController controller, IEulaViewPort view)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    public EulaViewState State => _controller.State;

    public void Start() => Render(_controller.Start());

    public void ScrollChanged(bool atEnd) => Render(_controller.SetScrollAtEnd(atEnd));

    public void Dispatch(EulaCommand command) => Render(_controller.Dispatch(command));

    private void Render(EulaViewState state)
    {
        if (state.IsVisible)
        {
            _view.Present(state);
        }
        else
        {
            _view.Dismiss();
        }
    }
}
