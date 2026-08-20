namespace SarnautCore.Shell;

/// <summary>
/// The roster screen's behaviour: list the account's characters, pick one, and
/// mint the ticket that admits it to a shard.
/// </summary>
/// <remarks>
/// Selection is entirely out of band (session spec rule 5.3). The shard serves
/// no character list and no character-select message, so everything this screen
/// does is an HTTP call, and the game connection does not exist yet.
/// </remarks>
public sealed class CharacterSelectViewModel
{
    private readonly AuthClient _auth;
    private readonly PlayerSession _session;
    private IReadOnlyList<CharacterSummary> _characters = [];
    private int _selectedIndex = -1;

    public CharacterSelectViewModel(AuthClient auth, PlayerSession session)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(session);
        _auth = auth;
        _session = session;
    }

    public IReadOnlyList<CharacterSummary> Characters => _characters;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = value >= 0 && value < _characters.Count ? value : -1;
    }

    public CharacterSummary? Selected => _selectedIndex >= 0 ? _characters[_selectedIndex] : null;

    public bool Busy { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public bool MessageIsError { get; private set; }

    public AuthFailure? LastFailure { get; private set; }

    /// <summary>True when the roster is empty and the only move is to create one.</summary>
    public bool IsEmpty => _characters.Count == 0;

    public bool CanEnterWorld => !Busy && Selected is not null;

    /// <summary>Reads the roster, keeping the current selection by id where it survives.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Guid previous = Selected?.CharacterId ?? Guid.Empty;
        Busy = true;
        MessageIsError = false;
        LastFailure = null;
        Message = "Reading characters...";
        try
        {
            _characters = await _auth.ListCharactersAsync(_session.Token, cancellationToken).ConfigureAwait(false);
            int restored = IndexOf(previous);
            _selectedIndex = restored >= 0 ? restored : (_characters.Count > 0 ? 0 : -1);
            Message = IsEmpty ? "This account has no characters yet." : string.Empty;
            return true;
        }
        catch (AuthException exception)
        {
            _characters = [];
            _selectedIndex = -1;
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

    /// <summary>Selects a character by id, for a host restoring a previous choice.</summary>
    public bool SelectById(Guid characterId)
    {
        int index = IndexOf(characterId);
        if (index < 0)
        {
            return false;
        }

        _selectedIndex = index;
        return true;
    }

    /// <summary>
    /// Mints the shard ticket for the selected character and records the choice
    /// on the session.
    /// </summary>
    /// <remarks>
    /// The ticket is opaque, single use, and lives 60 seconds — long enough to
    /// reach the handshake and no longer. The zone scene presents it in
    /// <c>EnterZoneRequest</c> and drops it.
    /// </remarks>
    public async Task<ShardTicket?> EnterWorldAsync(
        ChargenOption? option = null,
        CancellationToken cancellationToken = default)
    {
        CharacterSummary? character = Selected;
        if (Busy || character is null)
        {
            return null;
        }

        Busy = true;
        MessageIsError = false;
        LastFailure = null;
        Message = $"Entering the world as {character.Name}...";
        try
        {
            ShardTicket ticket = await _auth
                .MintTicketAsync(_session.Token, character.CharacterId, cancellationToken)
                .ConfigureAwait(false);
            _session.SelectCharacter(character, option);
            _session.HoldTicket(ticket);
            return ticket;
        }
        catch (AuthException exception)
        {
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

    private int IndexOf(Guid characterId)
    {
        for (int index = 0; index < _characters.Count; index++)
        {
            if (_characters[index].CharacterId == characterId)
            {
                return index;
            }
        }

        return -1;
    }
}
