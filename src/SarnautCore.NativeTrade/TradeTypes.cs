namespace SarnautCore.NativeTrade;

public readonly record struct TradeParticipantId(ulong Value)
{
    public bool IsEmpty => Value == 0;
}

public readonly record struct TradeSessionId(ulong Value)
{
    public bool IsEmpty => Value == 0;
}

public readonly record struct TradeInvitationId(ulong Value)
{
    public bool IsEmpty => Value == 0;
}

public readonly record struct TradeItemId(ulong Value)
{
    public bool IsEmpty => Value == 0;
}

public sealed record TradeParticipant(TradeParticipantId Id, string Name);

public sealed record TradeItem(
    TradeItemId Id,
    int StackCount,
    int CounterCount = 0,
    long CooldownRemainingMilliseconds = 0)
{
    public int VisibleCount => StackCount > 1 ? StackCount : CounterCount > 1 ? CounterCount : 0;
}

public enum TradeSide
{
    Own,
    Partner,
}

public enum TradeDenomination
{
    Gold,
    Silver,
    Copper,
}

public readonly record struct TradeMoney
{
    public const long CopperPerSilver = 100;
    public const long CopperPerGold = 10_000;
    public const long MaximumCopper = 999_999_999;

    public TradeMoney(long copper)
    {
        if (copper is < 0 or > MaximumCopper)
        {
            throw new ArgumentOutOfRangeException(nameof(copper));
        }

        Copper = copper;
    }

    public long Copper { get; }

    public int Gold => checked((int)(Copper / CopperPerGold));

    public int Silver => checked((int)((Copper / CopperPerSilver) % 100));

    public int CopperRemainder => checked((int)(Copper % 100));

    public static TradeMoney FromParts(int gold, int silver, int copper)
    {
        if (gold is < 0 or > 99_999)
        {
            throw new ArgumentOutOfRangeException(nameof(gold));
        }

        if (silver is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(silver));
        }

        if (copper is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(copper));
        }

        return new TradeMoney(checked((gold * CopperPerGold) + (silver * CopperPerSilver) + copper));
    }

    public TradeMoney Clamp(TradeMoney available) => Copper <= available.Copper ? this : available;
}

public enum TradeSessionState
{
    Invitation = 0,
    InProgress = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
    NoBagSpace = 5,
    Lost = 6,
}

public enum TradeError
{
    MoneyNotEnough = 0,
    PrimaryConfirmationRequired = 1,
    ItemNotFound = 2,
    SlotIsUsed = 3,
    ItemIsUsed = 4,
    ItemIsBound = 5,
}

public enum TradeStartResult
{
    Success = 0,
    Error = 1,
    InvitedAvatarIsBusy = 2,
    InviterAvatarIsBusy = 3,
    InvitedAvatarNotFound = 4,
    TooFar = 5,
    InvitedAvatarIsDead = 6,
    InviterAvatarIsDead = 7,
    YouAreInvisible = 8,
}

public enum TradeCloseReason
{
    None,
    Completed,
    Canceled,
    Failed,
    NoBagSpace,
    Lost,
    InvitationDeclined,
    InvitationExpired,
    UserClosed,
    Escape,
    InventoryChanged,
    BagModeConflict,
    OutOfRange,
    LocalDeath,
    PartnerRemoved,
}
