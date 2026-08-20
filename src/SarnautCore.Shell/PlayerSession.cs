namespace SarnautCore.Shell;

/// <summary>
/// What one player is currently signed in as, and which character they picked.
/// </summary>
/// <remarks>
/// This is the cross-scene seam. It replaces the static fields that used to hang
/// off <c>ZoneWalkabout</c>: a static is reachable from anywhere, survives a
/// scene change by accident rather than by design, and cannot be reset between
/// tests. One instance of this lives on the <c>Session</c> autoload, and the zone
/// scene reads it instead of being handed parameters through class statics.
///
/// Nothing here is printable: <see cref="ToString"/> names identifiers only, and
/// the token and ticket are <see cref="Secret"/>.
/// </remarks>
public sealed class PlayerSession
{
    /// <summary>The signed-in account, or null before <see cref="SignIn"/>.</summary>
    public AccountSession? Account { get; private set; }

    /// <summary>The chosen character, or null until one is selected.</summary>
    public CharacterSummary? Character { get; private set; }

    /// <summary>The chargen row the chosen character was created from, when it is known.</summary>
    public ChargenOption? Option { get; private set; }

    /// <summary>
    /// The shard ticket minted for <see cref="Character"/>, held only long enough
    /// to reach <c>ClientHello</c>'s zone entry (ADR 0030 section 2: 60 seconds,
    /// single use).
    /// </summary>
    public ShardTicket? Ticket { get; private set; }

    public bool IsAuthenticated => Account is not null;

    /// <summary>The bearer credential for the account API, or none when signed out.</summary>
    public Secret Token => Account?.Token ?? Secret.None;

    public void SignIn(AccountSession account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Account = account;
        Character = null;
        Option = null;
        Ticket = null;
    }

    public void SignOut()
    {
        Account = null;
        Character = null;
        Option = null;
        Ticket = null;
    }

    public void SelectCharacter(CharacterSummary character, ChargenOption? option = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        Character = character;
        Option = option;
        Ticket = null;
    }

    /// <summary>Holds the ticket the shard is about to burn.</summary>
    public void HoldTicket(ShardTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        Ticket = ticket;
    }

    /// <summary>Drops the ticket once it has been presented. It is single use either way.</summary>
    public void ReleaseTicket() => Ticket = null;

    public override string ToString()
    {
        string account = Account is null ? "-" : Account.AccountId.ToString();
        string character = Character is null ? "-" : Character.CharacterId.ToString();
        return $"PlayerSession(account={account}, character={character})";
    }
}
