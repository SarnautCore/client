namespace SarnautCore.NativeTrade;

public sealed class TradeOffer
{
    private readonly TradeItem?[] _slots;
    private readonly IReadOnlyList<TradeItem?> _slotsView;

    public TradeOffer(
        IReadOnlyList<TradeItem?> slots,
        TradeMoney money,
        bool primaryConfirmed,
        bool finalConfirmed)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != TradeProduct.AuthoredSlotCount)
        {
            throw new ArgumentException("A trade offer must contain exactly five slots.", nameof(slots));
        }

        _slots = slots.ToArray();
        _slotsView = Array.AsReadOnly(_slots);
        Money = money;
        PrimaryConfirmed = primaryConfirmed;
        FinalConfirmed = finalConfirmed;
        if (FinalConfirmed && !PrimaryConfirmed)
        {
            throw new ArgumentException("A final confirmation requires primary confirmation.", nameof(finalConfirmed));
        }
    }

    public IReadOnlyList<TradeItem?> Slots => _slotsView;

    public TradeMoney Money { get; }

    public bool PrimaryConfirmed { get; }

    public bool FinalConfirmed { get; }

    internal TradeOffer Clone() => new(_slots, Money, PrimaryConfirmed, FinalConfirmed);
}

public sealed record TradeSnapshot(
    TradeSessionId SessionId,
    TradeSessionState State,
    TradeParticipant OwnParticipant,
    TradeParticipant Partner,
    bool OwnIsInviter,
    TradeMoney AvailableMoney,
    TradeOffer OwnOffer,
    TradeOffer PartnerOffer);

public abstract record TradeObservation
{
    private TradeObservation()
    {
    }

    public sealed record Invitation(
        TradeInvitationId InvitationId,
        TradeParticipant Sender) : TradeObservation;

    public sealed record Snapshot(TradeSnapshot Value) : TradeObservation;

    public sealed record StartResult(TradeStartResult Result) : TradeObservation;

    public sealed record Error(TradeError Value) : TradeObservation;

    public sealed record Terminal(TradeSessionId SessionId, TradeSessionState State) : TradeObservation;

    public sealed record PartnerDistance(double Meters) : TradeObservation;

    public sealed record LocalDeath : TradeObservation;

    public sealed record PartnerRemoved : TradeObservation;

    public sealed record InventoryChanged : TradeObservation;

    public sealed record BagModeConflict : TradeObservation;
}

public abstract record TradeInput
{
    private TradeInput()
    {
    }

    public sealed record InviteByName(string PlayerName) : TradeInput;

    public sealed record InviteSelectedTarget : TradeInput;

    public sealed record RespondInvitation(bool Accept) : TradeInput;

    public sealed record Close : TradeInput;

    public sealed record Escape : TradeInput;

    public sealed record OfferBagItem(int BagSlot, bool IsBound, int? PreferredOfferSlot = null) : TradeInput;

    public sealed record ChangeMoney(TradeDenomination Denomination, string Text) : TradeInput;

    public sealed record CommitMoney : TradeInput;

    public sealed record RevertMoney : TradeInput;

    public sealed record RightClickOwnSlot(int Index) : TradeInput;

    public sealed record HoverSlot(TradeSide Side, int Index, bool IsHovered) : TradeInput;

    public sealed record TogglePrimary : TradeInput;

    public sealed record ToggleSafeConfirmation : TradeInput;

    public sealed record ToggleFinal : TradeInput;
}

public abstract record TradeCommand
{
    private TradeCommand()
    {
    }

    public sealed record InviteByName(string PlayerName) : TradeCommand;

    public sealed record InviteSelectedTarget : TradeCommand;

    public sealed record RespondInvitation(TradeInvitationId InvitationId, bool Accept) : TradeCommand;

    public sealed record PutWholeBagStack(TradeSessionId SessionId, int BagSlot, int? PreferredOfferSlot) : TradeCommand;

    public sealed record SetMoney(TradeSessionId SessionId, TradeMoney Money) : TradeCommand;

    public sealed record RemoveOwnItem(TradeSessionId SessionId, int Slot) : TradeCommand;

    public sealed record SetPrimaryConfirmation(TradeSessionId SessionId, bool Confirmed) : TradeCommand;

    public sealed record SetFinalConfirmation(TradeSessionId SessionId, bool Confirmed) : TradeCommand;

    public sealed record Cancel(TradeSessionId SessionId, TradeCloseReason Reason) : TradeCommand;
}

public enum TradeCue
{
    InvitationOpened,
    InvitationClosed,
    TradeOpened,
    TradeClosed,
    OfferChanged,
    PrimaryConfirmationChanged,
    FinalConfirmationChanged,
    Error,
}
