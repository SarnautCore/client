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
    private const string LoginScreenId = "login-account";
    private const string AccountValueId = "account-name";
    private const string PasswordValueId = "account-password";
    private const string EnterRoleId = "enter-button";
    private const string PasswordRoleId = "password-input";
    private const string SubmitActionId = "submit-login";
    private const string CancelActionId = "cancel-login";
    private const string QuitActionId = "quit";
    private const string UpdateAccountActionId = "update-account-name";
    private const string UpdatePasswordActionId = "update-account-password";
    private const string FocusNextActionId = "focus-next";

    private SessionHost _session = null!;
    private LoginViewModel _model = null!;
    private CenterContainer _failure = null!;
    private Label _failureMessage = null!;
    private NativeUiProductHost? _native;
    private UiScreenDefinition _loginScreen = null!;
    private UiValueBinding _accountValue = null!;
    private UiValueBinding _passwordValue = null!;
    private UiRoleDefinition _enterRole = null!;
    private UiRoleDefinition _passwordRole = null!;
    private CancellationTokenSource? _submitCancellation;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _model = new LoginViewModel(_session.Auth, _session.Player);

        _failure = GetNode<CenterContainer>("%NativeFailure");
        _failureMessage = GetNode<Label>("%NativeFailureMessage");

        bool nativeMounted = NativeUiProductHost.TryMount(
            this,
            out _native,
            out string nativeStatus);
        if (!nativeMounted)
        {
            ShowFailure(nativeStatus);
            return;
        }

        _loginScreen = _native!.GetScreen(LoginScreenId);
        _accountValue = _native.GetValue(_loginScreen, AccountValueId);
        _passwordValue = _native.GetValue(_loginScreen, PasswordValueId);
        _enterRole = _native.GetRole(_loginScreen, EnterRoleId);
        _passwordRole = _native.GetRole(_loginScreen, PasswordRoleId);
        _native.RegisterController(_loginScreen, HandleNativeAction);
        foreach (UiScreenDefinition screen in _native.Manifest.Screens)
        {
            _native.SetScreenVisible(screen, visible: false, focusFirst: false);
        }
        _native.SetText(_loginScreen, _accountValue, _model.Email);
        _native.SetText(_loginScreen, _passwordValue, string.Empty);
        _native.SetScreenVisible(_loginScreen, visible: true);

        GD.Print($"Login screen: {nativeStatus}");
        Render();
    }

    public override void _ExitTree()
    {
        _submitCancellation?.Cancel();
        if (_model is not null)
        {
            _model.Password = Secret.None;
        }
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
            if (cancellation.IsCancellationRequested || !IsInsideTree())
            {
                return;
            }

            Render();
            if (!signedIn)
            {
                ForgetPassword();
                if (_native is not null)
                {
                    _native.Focus(_loginScreen, _passwordRole);
                }
                return;
            }

            ForgetPassword();
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
            ForgetPassword();
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
        if (_native is not null)
        {
            _native.SetInteractive(_loginScreen, interactive);
        }
    }

    private void Render()
    {
        _failureMessage.Text = _model.Message;
        _failure.Visible = _model.Message.Length > 0;
        _failureMessage.AddThemeColorOverride(
            "font_color",
            _model.MessageIsError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        if (_native is not null)
        {
            _native.SetRoleEnabled(_loginScreen, _enterRole, _model.CanSubmit);
        }
    }

    private bool HandleNativeAction(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case SubmitActionId:
                Submit();
                return true;
            case CancelActionId:
                Cancel();
                return true;
            case QuitActionId:
                GetTree().Quit();
                return true;
            case UpdateAccountActionId:
                _model.Email = _native!.ReadText(_loginScreen, _accountValue);
                Render();
                return true;
            case UpdatePasswordActionId:
                _model.Password = new Secret(
                    _native!.ReadText(_loginScreen, _passwordValue));
                Render();
                return true;
            case FocusNextActionId:
                return true;
            default:
                return false;
        }
    }

    private void Cancel()
    {
        ForgetPassword();
        _session.Flow.CancelSignIn();
        _session.Show(Screen.Start);
    }

    private void ForgetPassword()
    {
        _model.Password = Secret.None;
        if (_native is not null)
        {
            _native.SetText(_loginScreen, _passwordValue, string.Empty);
        }
    }

    private void ShowFailure(string message)
    {
        _failureMessage.Text = message;
        _failureMessage.AddThemeColorOverride("font_color", UiTheme.ErrorInk);
        _failure.Visible = true;
    }
}
