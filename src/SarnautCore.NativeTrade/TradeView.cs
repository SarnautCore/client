namespace SarnautCore.NativeTrade;

public sealed record TradeMoneyDraft(
    string Gold,
    string Silver,
    string Copper,
    TradeDenomination? Focus,
    bool CursorVisible);

public sealed record TradeHover(TradeSide Side, int Slot, TradeItem Item);

public sealed class TradeView
{
    internal TradeView(
        TradeSessionState? state,
        TradeParticipant? invitationSender,
        long invitationMillisecondsRemaining,
        bool invitationResponsePending,
        TradeParticipant? ownParticipant,
        TradeParticipant? partner,
        bool ownIsInviter,
        TradeOffer? ownOffer,
        TradeOffer? partnerOffer,
        TradeMoney availableMoney,
        TradeMoneyDraft moneyDraft,
        bool safeConfirmation,
        bool confirmationPanelVisible,
        TradeHover? hover,
        TradeCloseReason closeReason,
        TradeStartResult? startResult,
        TradeError? error)
    {
        State = state;
        InvitationSender = invitationSender;
        InvitationMillisecondsRemaining = invitationMillisecondsRemaining;
        InvitationResponsePending = invitationResponsePending;
        OwnParticipant = ownParticipant;
        Partner = partner;
        OwnIsInviter = ownIsInviter;
        OwnOffer = ownOffer?.Clone();
        PartnerOffer = partnerOffer?.Clone();
        AvailableMoney = availableMoney;
        MoneyDraft = moneyDraft;
        SafeConfirmation = safeConfirmation;
        ConfirmationPanelVisible = confirmationPanelVisible;
        Hover = hover;
        CloseReason = closeReason;
        StartResult = startResult;
        Error = error;
    }

    public TradeSessionState? State { get; }

    public bool IsOpen => State == TradeSessionState.InProgress;

    public TradeParticipant? InvitationSender { get; }

    public long InvitationMillisecondsRemaining { get; }

    public bool InvitationResponsePending { get; }

    public bool InvitationPromptVisible =>
        State == TradeSessionState.Invitation && !InvitationResponsePending;

    public TradeParticipant? OwnParticipant { get; }

    public TradeParticipant? Partner { get; }

    public bool OwnIsInviter { get; }

    public TradeOffer? OwnOffer { get; }

    public TradeOffer? PartnerOffer { get; }

    public TradeMoney AvailableMoney { get; }

    public TradeMoneyDraft MoneyDraft { get; }

    public bool SafeConfirmation { get; }

    public bool ConfirmationPanelVisible { get; }

    public TradeHover? Hover { get; }

    public TradeCloseReason CloseReason { get; }

    public TradeStartResult? StartResult { get; }

    public TradeError? Error { get; }

    public bool BothPrimaryConfirmed =>
        OwnOffer?.PrimaryConfirmed == true && PartnerOffer?.PrimaryConfirmed == true;

    public bool OwnFinalConfirmed => OwnOffer?.FinalConfirmed == true;
}

public sealed record TradeTransition(
    TradeView View,
    IReadOnlyList<TradeCommand> Commands,
    IReadOnlyList<TradeCue> Cues);
