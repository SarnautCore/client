using Godot;

namespace SarnautCore;

/// <summary>
/// Owns the native out-of-game product from login through world entry.
/// The compiled product owns layout, while NativeOutOfGameBinding joins it to
/// the plain flow, EULA, Credits, and account controllers.
/// </summary>
public partial class LoginScreen : Control
{
    private CenterContainer _failure = null!;
    private Label _failureMessage = null!;
    private NativeUiProductHost? _native;
    private NativeOutOfGameBinding? _binding;

    public override void _Ready()
    {
        _failure = GetNode<CenterContainer>("%NativeFailure");
        _failureMessage = GetNode<Label>("%NativeFailureMessage");

        if (!NativeUiProductHost.TryMount(this, out _native, out string status))
        {
            ShowStatus(status, isError: true);
            SetProcess(false);
            return;
        }

        try
        {
            _binding = NativeOutOfGameBinding.Open(
                this,
                _native!,
                SessionHost.Of(this),
                ShowStatus);
            GD.Print($"Out-of-game UI: {status}");
        }
        catch (System.Exception exception)
        {
            GD.PushError($"Out-of-game UI failed: {exception}");
            _binding?.Dispose();
            _binding = null;
            _native?.QueueFree();
            _native = null;
            ShowStatus(
                $"Native out-of-game content is incompatible: {exception.Message}",
                isError: true);
            SetProcess(false);
        }
    }

    public override void _Process(double delta)
    {
        _binding?.Tick();
    }

    public override void _ExitTree()
    {
        _binding?.Dispose();
        _binding = null;
    }

    private void ShowStatus(string message, bool isError)
    {
        _failureMessage.Text = message;
        _failureMessage.AddThemeColorOverride(
            "font_color",
            isError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        _failure.Visible = message.Length > 0;
    }
}
