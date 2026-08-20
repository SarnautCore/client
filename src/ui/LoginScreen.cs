using System;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The login screen's scene half: it binds nodes to
/// <see cref="LoginViewModel"/> and does nothing else.
/// </summary>
/// <remarks>
/// Everything decidable lives in the view model, in the plain-C# assembly, where
/// CI can run it. A Godot headless smoke needs converted assets and libmsquic
/// and cannot run on CI, so a rule that lives in this file is a rule that ships
/// untested.
/// </remarks>
public partial class LoginScreen : Control
{
    private SessionHost _session = null!;
    private LoginViewModel _model = null!;
    private LineEdit _email = null!;
    private LineEdit _password = null!;
    private Label _message = null!;
    private Label _service = null!;
    private Button _signIn = null!;
    private Button _register = null!;
    private Button _back = null!;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _model = new LoginViewModel(_session.Auth, _session.Player);

        _email = GetNode<LineEdit>("%Email");
        _password = GetNode<LineEdit>("%Password");
        _message = GetNode<Label>("%Message");
        _service = GetNode<Label>("%Service");
        _signIn = GetNode<Button>("%SignIn");
        _register = GetNode<Button>("%Register");
        _back = GetNode<Button>("%Back");

        _service.Text = $"{_session.Auth.ServiceAddress}   ·   {ConvertedChrome.Mount(this, ConvertedChrome.LoginForm)}";
        _message.AddThemeColorOverride("font_color", UiTheme.ErrorInk);

        _email.TextChanged += text => Bind(email: text);
        _password.TextChanged += text => Bind(password: text);
        _email.TextSubmitted += _ => Submit(register: false);
        _password.TextSubmitted += _ => Submit(register: false);
        _signIn.Pressed += () => Submit(register: false);
        _register.Pressed += () => Submit(register: true);
        _back.Pressed += () =>
        {
            _session.Flow.CancelSignIn();
            _session.Show(Screen.Start);
        };

        _email.GrabFocus();
        Render();
    }

    private void Bind(string? email = null, string? password = null)
    {
        if (email is not null)
        {
            _model.Email = email;
        }

        if (password is not null)
        {
            // Straight into a Secret: the plaintext never reaches a field that
            // something could print.
            _model.Password = new Secret(password);
        }

        Render();
    }

    private async void Submit(bool register)
    {
        Bind(_email.Text, _password.Text);
        if (!_model.CanSubmit)
        {
            Render();
            return;
        }

        SetInteractive(false);
        try
        {
            bool signedIn = register ? await _model.RegisterAsync() : await _model.SignInAsync();
            Render();
            if (!signedIn)
            {
                _password.Clear();
                _password.GrabFocus();
                return;
            }

            _password.Clear();
            _session.Flow.SignedIn();
            _session.Show(Screen.CharacterSelect);
        }
        catch (Exception exception)
        {
            // The view model answers every refusal it knows about. Anything left
            // is a bug in this build, and it is reported without the form's
            // contents.
            GD.PushError($"Login screen failed unexpectedly: {exception.GetType().Name}");
            _message.Text = "Something went wrong in the client. See the log.";
        }
        finally
        {
            SetInteractive(true);
        }
    }

    private void SetInteractive(bool interactive)
    {
        _signIn.Disabled = !interactive;
        _register.Disabled = !interactive;
        _email.Editable = interactive;
        _password.Editable = interactive;
    }

    private void Render()
    {
        _message.Text = _model.Message;
        _message.Visible = _model.Message.Length > 0;
        _message.AddThemeColorOverride(
            "font_color",
            _model.MessageIsError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        _signIn.Disabled = !_model.CanSubmit;
        _register.Disabled = !_model.CanSubmit;
    }
}
