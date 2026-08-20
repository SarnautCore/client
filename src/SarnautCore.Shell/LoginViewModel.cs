namespace SarnautCore.Shell;

/// <summary>
/// The login screen's behaviour, with no Godot type anywhere in it.
/// </summary>
/// <remarks>
/// The split is deliberate and load-bearing: a Godot headless smoke needs
/// converted assets and libmsquic, neither of which CI has, so anything left in
/// a <c>Control</c> subclass ships untested. The scene binds a
/// <c>LineEdit</c> to <see cref="Email"/> and a button to
/// <see cref="SignInAsync"/>; every decision about what those mean is here.
/// </remarks>
public sealed class LoginViewModel
{
    private readonly AuthClient _auth;
    private readonly PlayerSession _session;

    public LoginViewModel(AuthClient auth, PlayerSession session)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(session);
        _auth = auth;
        _session = session;
    }

    public string Email { get; set; } = string.Empty;

    /// <summary>The typed password. A <see cref="Secret"/>, so no log can print it.</summary>
    public Secret Password { get; set; } = Secret.None;

    /// <summary>True while a request is in flight, so the screen can refuse a second one.</summary>
    public bool Busy { get; private set; }

    /// <summary>The sentence to show. Empty when there is nothing to say.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>True when <see cref="Message"/> is a refusal rather than progress.</summary>
    public bool MessageIsError { get; private set; }

    /// <summary>The case of the last refusal, for a screen that reacts to one specifically.</summary>
    public AuthFailure? LastFailure { get; private set; }

    /// <summary>The account this screen signed in, or null.</summary>
    public AccountSession? Account { get; private set; }

    public bool CanSubmit => !Busy && Email.Trim().Length > 0 && !Password.IsEmpty;

    /// <summary>Logs in. Returns false and sets <see cref="Message"/> on every refusal.</summary>
    public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        return AttemptAsync(
            token => _auth.LoginAsync(Email.Trim(), Password, token),
            "Signing in...",
            cancellationToken);
    }

    /// <summary>
    /// Registers, then signs in with the same credentials.
    /// </summary>
    /// <remarks>
    /// Registration returns an account id and no token, so the second call is
    /// not a convenience — it is the only way to get one.
    /// </remarks>
    public Task<bool> RegisterAsync(CancellationToken cancellationToken = default)
    {
        return AttemptAsync(
            async token =>
            {
                await _auth.RegisterAsync(Email.Trim(), Password, token).ConfigureAwait(false);
                return await _auth.LoginAsync(Email.Trim(), Password, token).ConfigureAwait(false);
            },
            "Creating the account...",
            cancellationToken);
    }

    private async Task<bool> AttemptAsync(
        Func<CancellationToken, Task<AccountSession>> attempt,
        string progress,
        CancellationToken cancellationToken)
    {
        if (Busy)
        {
            return false;
        }

        if (!CanSubmit)
        {
            Fail(null, "Enter an email address and a password.");
            return false;
        }

        Busy = true;
        MessageIsError = false;
        LastFailure = null;
        Message = progress;
        try
        {
            AccountSession account = await attempt(cancellationToken).ConfigureAwait(false);
            Account = account;
            _session.SignIn(account);
            // The password is not kept: the token is the credential from here on.
            Password = Secret.None;
            Message = string.Empty;
            return true;
        }
        catch (AuthException exception)
        {
            Fail(exception.Failure, exception.Message);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Fail(AuthFailure? failure, string message)
    {
        LastFailure = failure;
        Message = message;
        MessageIsError = true;
    }
}
