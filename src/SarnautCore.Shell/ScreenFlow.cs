namespace SarnautCore.Shell;

/// <summary>The screens the shell moves between.</summary>
public enum Screen
{
    /// <summary>The development hub: asset viewer, offline walkabout, and the way in.</summary>
    Start,

    /// <summary>Register or log in against the account service.</summary>
    Login,

    /// <summary>The account's roster.</summary>
    CharacterSelect,

    /// <summary>The creation form, driven by the server's chargen options.</summary>
    CharacterCreate,

    /// <summary>A ticket is being minted and the shard connected to.</summary>
    EnteringWorld,

    /// <summary>The zone scene owns the screen.</summary>
    InWorld,
}

/// <summary>
/// The legal moves between screens, in one place rather than in each screen's
/// button handlers.
/// </summary>
/// <remarks>
/// Screens ask this what happens next; they never call
/// <c>ChangeSceneToFile</c> on a path they picked themselves. That is the whole
/// reason it is a type: "which screen comes after a successful login" is a rule,
/// and a rule spread across three button handlers is three rules that drift.
///
/// An illegal move throws. A shell that silently ignored one would leave the
/// player looking at a screen the session state does not support — a character
/// list with no token, most often.
/// </remarks>
public sealed class ScreenFlow
{
    private readonly List<Screen> _history = [];

    public ScreenFlow(Screen start = Screen.Start) => Current = start;

    public Screen Current { get; private set; }

    /// <summary>Every screen this flow has left, oldest first. Diagnostics only.</summary>
    public IReadOnlyList<Screen> History => _history;

    /// <summary>Raised after <see cref="Current"/> changes, so a host can swap scenes.</summary>
    public event Action<Screen>? Changed;

    /// <summary>The player asked to play: the hub hands over to the login form.</summary>
    public void BeginSignIn() => MoveTo(Screen.Login, Screen.Start, Screen.CharacterSelect);

    /// <summary>Credentials were accepted. The roster is the only place to go.</summary>
    public void SignedIn() => MoveTo(Screen.CharacterSelect, Screen.Login);

    /// <summary>The player asked for the creation form.</summary>
    public void CreateCharacter() => MoveTo(Screen.CharacterCreate, Screen.CharacterSelect);

    /// <summary>The creation form was left, whether or not a character was made.</summary>
    public void LeaveCreateCharacter() => MoveTo(Screen.CharacterSelect, Screen.CharacterCreate);

    /// <summary>A character was chosen and a ticket is being minted.</summary>
    public void EnterWorld() => MoveTo(Screen.EnteringWorld, Screen.CharacterSelect);

    /// <summary>The shard admitted the session.</summary>
    public void EnteredWorld() => MoveTo(Screen.InWorld, Screen.EnteringWorld);

    /// <summary>
    /// Entry failed — a refused ticket, an unreachable shard, a play lock held
    /// elsewhere. The player lands back on the roster with the reason, and
    /// reconnects with a fresh ticket (session spec rule 5.2.4).
    /// </summary>
    public void EnterWorldFailed() => MoveTo(Screen.CharacterSelect, Screen.EnteringWorld, Screen.InWorld);

    /// <summary>The zone scene was left cleanly.</summary>
    public void LeftWorld() => MoveTo(Screen.CharacterSelect, Screen.InWorld, Screen.EnteringWorld);

    /// <summary>
    /// The account session ended. Legal from anywhere: an expired token can
    /// surface on any screen that talks to the service.
    /// </summary>
    public void SignedOut() => MoveTo(Screen.Login);

    /// <summary>The player backed out of the login form to the hub.</summary>
    public void CancelSignIn() => MoveTo(Screen.Start, Screen.Login);

    private void MoveTo(Screen next, params Screen[] allowedFrom)
    {
        if (allowedFrom.Length > 0 && !allowedFrom.Contains(Current))
        {
            throw new InvalidOperationException(
                $"The shell cannot move from {Current} to {next}; "
                + $"that move is only legal from {string.Join(", ", allowedFrom)}.");
        }

        if (Current == next)
        {
            return;
        }

        _history.Add(Current);
        Current = next;
        Changed?.Invoke(next);
    }
}
