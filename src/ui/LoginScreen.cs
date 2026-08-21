using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SarnautCore.Shell;
using SarnautCore.UI;

namespace SarnautCore;

/// <summary>
/// The login screen's scene half: it binds nodes to
/// <see cref="LoginViewModel"/> and does nothing else.
/// </summary>
/// <remarks>
/// Everything decidable lives in the view model, in the plain-C# assembly, where
/// CI can run it. This adapter only joins validated native controls to that
/// behaviour.
/// </remarks>
public partial class LoginScreen : Control
{
    private SessionHost _session = null!;
    private LoginViewModel _model = null!;
    private CenterContainer _failure = null!;
    private Label _failureMessage = null!;
    private NativeLoginUiHost? _native;
    private CancellationTokenSource? _submitCancellation;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _model = new LoginViewModel(_session.Auth, _session.Player);

        GetNode<CanvasLayer>("Content").Visible = false;
        _failure = GetNode<CenterContainer>("%NativeFailure");
        _failureMessage = GetNode<Label>("%NativeFailureMessage");

        bool nativeMounted = NativeLoginUiHost.TryMount(
            this,
            _model,
            HandleNativeAction,
            out _native,
            out string nativeStatus);
        if (!nativeMounted)
        {
            ShowFailure(nativeStatus);
            return;
        }

        GD.Print($"Login screen: {nativeStatus}");
        Render();
    }

    public override void _ExitTree()
    {
        _submitCancellation?.Cancel();
    }

    private async void Submit()
    {
        if (!_model.CanSubmit)
        {
            Render();
            return;
        }

        SetInteractive(false);
        var cancellation = new CancellationTokenSource();
        _submitCancellation = cancellation;
        try
        {
            Task<bool> signIn = _model.SignInAsync(cancellation.Token);
            Render();
            bool signedIn = await signIn;
            Render();
            if (!signedIn)
            {
                _native?.ClearPassword();
                _native?.FocusPassword();
                return;
            }

            _native?.ClearPassword();
            _session.Flow.SignedIn();
            _session.Show(Screen.CharacterSelect);
        }
        catch (OperationCanceledException)
        {
            // Leaving the screen owns cancellation. It is not an authentication refusal.
        }
        catch (Exception exception)
        {
            // The view model answers every refusal it knows about. Anything left
            // is a bug in this build, and it is reported without the form's
            // contents.
            GD.PushError($"Login screen failed unexpectedly: {exception.GetType().Name}");
            ShowFailure("Something went wrong in the client. See the log.");
        }
        finally
        {
            if (ReferenceEquals(_submitCancellation, cancellation))
            {
                _submitCancellation = null;
            }

            cancellation.Dispose();
            if (IsInsideTree())
            {
                SetInteractive(true);
            }
        }
    }

    private void SetInteractive(bool interactive)
    {
        _native?.SetInteractive(interactive);
    }

    private void Render()
    {
        _failureMessage.Text = _model.Message;
        _failure.Visible = _model.Message.Length > 0;
        _failureMessage.AddThemeColorOverride(
            "font_color",
            _model.MessageIsError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        _native?.RenderModelState();
    }

    private void HandleNativeAction(string action)
    {
        switch (action)
        {
            case LoginAccountProduct.SubmitAction:
                Submit();
                break;
            case LoginAccountProduct.CancelAction:
                Cancel();
                break;
            case LoginAccountProduct.ToggleOptionsAction:
                // The native host applies the product-owned toggle variant.
                break;
            case LoginAccountProduct.LocalSessionAction:
                _session.Zone = ZoneRequest.Offline(
                    _session.Zone.MapName,
                    _session.Zone.ZoneId);
                GetTree().ChangeSceneToFile("res://scenes/zone_walkabout.tscn");
                break;
            case LoginAccountProduct.CreditsAction:
                ShowFailure("Credits are not available in this build.");
                break;
            case LoginAccountProduct.QuitAction:
                GetTree().Quit();
                break;
            default:
                GD.PushError($"Native login dispatched unknown action '{action}'.");
                break;
        }
    }

    private void Cancel()
    {
        _session.Flow.CancelSignIn();
        _session.Show(Screen.Start);
    }

    private void ShowFailure(string message)
    {
        _failureMessage.Text = message;
        _failureMessage.AddThemeColorOverride("font_color", UiTheme.ErrorInk);
        _failure.Visible = true;
    }
}
