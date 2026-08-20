namespace SarnautCore.Shell;

/// <summary>
/// The creation screen's behaviour: the server's option list, the name rule, and
/// the one place the submit payload is built.
/// </summary>
public sealed class CharacterCreateViewModel
{
    private readonly AuthClient _auth;
    private readonly PlayerSession _session;
    private readonly Func<string, string?>? _localize;
    private IReadOnlyList<ChargenOption> _options = [];
    private IReadOnlyList<ChargenOptionView> _views = [];
    private int _selectedIndex = -1;
    private string _name = string.Empty;

    public CharacterCreateViewModel(
        AuthClient auth,
        PlayerSession session,
        Func<string, string?>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(session);
        _auth = auth;
        _session = session;
        _localize = localize;
    }

    /// <summary>The rows to render, in the order the server sent them.</summary>
    public IReadOnlyList<ChargenOptionView> Options => _views;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = value >= 0 && value < _views.Count ? value : -1;
    }

    public ChargenOption? Selected => _selectedIndex >= 0 ? _options[_selectedIndex] : null;

    public ChargenOptionView? SelectedView => _selectedIndex >= 0 ? _views[_selectedIndex] : null;

    /// <summary>The typed name. Trimmed on submit, never on keystroke.</summary>
    public string Name
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            NameMessage = CharacterName.Explain(_name.Trim());
        }
    }

    /// <summary>Why the typed name is refused, or null when its shape is fine.</summary>
    public string? NameMessage { get; private set; } = CharacterName.Explain(string.Empty);

    public bool Busy { get; private set; }

    /// <summary>The last refusal from the service, or empty.</summary>
    public string Message { get; private set; } = string.Empty;

    public bool MessageIsError { get; private set; }

    public AuthFailure? LastFailure { get; private set; }

    public bool CanSubmit => !Busy && Selected is not null && NameMessage is null;

    /// <summary>
    /// Loads the options the server offers.
    /// </summary>
    /// <remarks>
    /// There is no built-in list to fall back to. A client that invented one
    /// would render a race the server refuses to create, which is a worse
    /// screen than an honest empty one.
    /// </remarks>
    public async Task<bool> LoadOptionsAsync(CancellationToken cancellationToken = default)
    {
        Busy = true;
        MessageIsError = false;
        Message = "Reading character options...";
        try
        {
            Apply(await _auth.ListChargenOptionsAsync(cancellationToken).ConfigureAwait(false));
            Message = _views.Count == 0
                ? "The server offers no playable character options."
                : string.Empty;
            MessageIsError = _views.Count == 0;
            return _views.Count > 0;
        }
        catch (AuthException exception)
        {
            Apply([]);
            LastFailure = exception.Failure;
            Message = exception.Message;
            MessageIsError = true;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Replaces the option list without a request, for a host that already has
    /// one and for the tests that prove this screen renders whatever it is given.
    /// </summary>
    public void SetOptions(IReadOnlyList<ChargenOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Apply(options);
    }

    /// <summary>
    /// Builds the body <c>POST /v1/characters</c> receives.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No option is selected, or the name does not have the server's shape.
    /// Building a payload that is known to be refused would turn a form error
    /// into a round trip.
    /// </exception>
    public CreateCharacterSubmission BuildSubmission()
    {
        ChargenOption? option = Selected;
        if (option is null)
        {
            throw new InvalidOperationException("No character-creation option is selected.");
        }

        string trimmed = _name.Trim();
        if (!CharacterName.IsValid(trimmed))
        {
            throw new InvalidOperationException(
                CharacterName.Explain(trimmed) ?? "That name is not usable.");
        }

        return new CreateCharacterSubmission(trimmed, option.Id);
    }

    /// <summary>
    /// Submits the form. Returns the created character, or null with
    /// <see cref="Message"/> set to the server's own sentence.
    /// </summary>
    public async Task<CharacterSummary?> SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (Busy)
        {
            return null;
        }

        if (!CanSubmit)
        {
            Message = NameMessage ?? "Choose a character option.";
            MessageIsError = true;
            return null;
        }

        Busy = true;
        MessageIsError = false;
        LastFailure = null;
        Message = "Creating the character...";
        try
        {
            CharacterSummary character = await _auth
                .CreateCharacterAsync(_session.Token, BuildSubmission(), cancellationToken)
                .ConfigureAwait(false);
            Message = string.Empty;
            return character;
        }
        catch (AuthException exception)
        {
            // NAME_TAKEN and NAME_INVALID are rendered from the server, never
            // assumed away by the client check above (ADR 0032 consequences).
            LastFailure = exception.Failure;
            Message = exception.Message;
            MessageIsError = true;
            return null;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Apply(IReadOnlyList<ChargenOption> options)
    {
        _options = options;
        _views = [.. options.Select(option => ChargenOptionView.From(option, _localize))];
        _selectedIndex = _views.Count > 0 ? 0 : -1;
    }
}
