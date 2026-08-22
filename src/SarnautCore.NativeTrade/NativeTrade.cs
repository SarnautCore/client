using System.Globalization;

namespace SarnautCore.NativeTrade;

/// <summary>
/// Engine-neutral retail trade state machine. Callers supply authoritative observations,
/// dispatch typed local input, advance monotonic time, and execute returned commands.
/// </summary>
public sealed class NativeTrade : IDisposable
{
    private readonly TradeProduct _product;
    private TradeInvitationId _invitationId;
    private TradeParticipant? _invitationSender;
    private long _invitationDeadline;
    private bool _invitationResponseSent;
    private TradeSnapshot? _snapshot;
    private string _gold = "0";
    private string _silver = "0";
    private string _copper = "0";
    private TradeDenomination? _moneyFocus;
    private long _focusStarted;
    private bool _safeConfirmation;
    private bool _autoFinalRequested;
    private TradeHover? _hover;
    private TradeSessionState? _state;
    private TradeCloseReason _closeReason;
    private TradeStartResult? _startResult;
    private TradeError? _error;
    private long _lastTime;
    private bool _disposed;

    public NativeTrade(TradeProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        product.Validate();
        _product = product;
        _safeConfirmation = product.Policy.SafeConfirmationDefault;
    }

    public TradeProduct Product => _product;

    public TradeTransition Observe(TradeObservation observation, long nowMilliseconds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(observation);
        CheckTime(nowMilliseconds);
        ValidateObservation(observation);
        List<TradeCommand> commands = [];
        List<TradeCue> cues = [];
        switch (observation)
        {
            case TradeObservation.Invitation invitation:
                _invitationId = invitation.InvitationId;
                _invitationSender = invitation.Sender;
                _invitationDeadline = checked(nowMilliseconds + _product.Policy.InvitationTimeoutMilliseconds);
                _invitationResponseSent = false;
                _snapshot = null;
                _state = TradeSessionState.Invitation;
                _closeReason = TradeCloseReason.None;
                _startResult = null;
                _error = null;
                cues.Add(TradeCue.InvitationOpened);
                break;

            case TradeObservation.Snapshot observed:
                ObserveSnapshot(observed.Value, commands, cues);
                break;

            case TradeObservation.StartResult result:
                _startResult = result.Result;
                if (result.Result != TradeStartResult.Success)
                {
                    cues.Add(TradeCue.Error);
                }

                break;

            case TradeObservation.Error error:
                _error = error.Value;
                cues.Add(TradeCue.Error);
                break;

            case TradeObservation.Terminal terminal:
                if (_snapshot?.SessionId == terminal.SessionId)
                {
                    Close(ToCloseReason(terminal.State), terminal.State, cues);
                }

                break;

            case TradeObservation.PartnerDistance distance when IsOpen && distance.Meters > _product.Policy.MaximumDistanceMeters:
                Cancel(TradeCloseReason.OutOfRange, commands, cues);
                break;

            case TradeObservation.LocalDeath when IsOpen:
                Cancel(TradeCloseReason.LocalDeath, commands, cues);
                break;

            case TradeObservation.PartnerRemoved when IsOpen:
                Cancel(TradeCloseReason.PartnerRemoved, commands, cues);
                break;

            case TradeObservation.InventoryChanged when IsOpen:
                Cancel(TradeCloseReason.InventoryChanged, commands, cues);
                break;

            case TradeObservation.BagModeConflict when IsOpen:
                Cancel(TradeCloseReason.BagModeConflict, commands, cues);
                break;
        }

        return Transition(nowMilliseconds, commands, cues);
    }

    public TradeTransition Dispatch(TradeInput input, long nowMilliseconds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(input);
        CheckTime(nowMilliseconds);
        List<TradeCommand> commands = [];
        List<TradeCue> cues = [];
        switch (input)
        {
            case TradeInput.InviteByName invite:
                if (_state is TradeSessionState.Invitation or TradeSessionState.InProgress)
                {
                    _startResult = TradeStartResult.InviterAvatarIsBusy;
                    cues.Add(TradeCue.Error);
                    break;
                }

                string name = invite.PlayerName.Trim();
                if (name.Length == 0)
                {
                    throw new ArgumentException("A named trade invitation needs a player name.", nameof(input));
                }

                commands.Add(new TradeCommand.InviteByName(name));
                break;

            case TradeInput.InviteSelectedTarget:
                if (_state is TradeSessionState.Invitation or TradeSessionState.InProgress)
                {
                    _startResult = TradeStartResult.InviterAvatarIsBusy;
                    cues.Add(TradeCue.Error);
                    break;
                }

                commands.Add(new TradeCommand.InviteSelectedTarget());
                break;

            case TradeInput.RespondInvitation response when
                _state == TradeSessionState.Invitation && !_invitationResponseSent:
                commands.Add(new TradeCommand.RespondInvitation(_invitationId, response.Accept));
                _invitationResponseSent = true;
                if (!response.Accept)
                {
                    Close(TradeCloseReason.InvitationDeclined, TradeSessionState.Canceled, cues);
                }

                break;

            case TradeInput.Close when
                _state == TradeSessionState.Invitation && !_invitationResponseSent:
                commands.Add(new TradeCommand.RespondInvitation(_invitationId, false));
                _invitationResponseSent = true;
                Close(TradeCloseReason.UserClosed, TradeSessionState.Canceled, cues);
                break;

            case TradeInput.Escape when
                _state == TradeSessionState.Invitation && !_invitationResponseSent:
                commands.Add(new TradeCommand.RespondInvitation(_invitationId, false));
                _invitationResponseSent = true;
                Close(TradeCloseReason.Escape, TradeSessionState.Canceled, cues);
                break;

            case TradeInput.Close when IsOpen:
                Cancel(TradeCloseReason.UserClosed, commands, cues);
                break;

            case TradeInput.Escape when IsOpen:
                Cancel(TradeCloseReason.Escape, commands, cues);
                break;

            case TradeInput.OfferBagItem offer when IsOpen:
                OfferBagItem(offer, commands, cues);
                break;

            case TradeInput.ChangeMoney change when IsOpen:
                ChangeMoney(change.Denomination, change.Text, nowMilliseconds);
                break;

            case TradeInput.CommitMoney when IsOpen:
                CommitMoney(commands);
                break;

            case TradeInput.RevertMoney when IsOpen:
                SynchronizeMoneyDraft();
                break;

            case TradeInput.RightClickOwnSlot remove when IsOpen:
                ValidateSlot(remove.Index);
                if (_snapshot!.OwnOffer.Slots[remove.Index] is not null)
                {
                    commands.Add(new TradeCommand.RemoveOwnItem(_snapshot.SessionId, remove.Index));
                }

                break;

            case TradeInput.HoverSlot hover when IsOpen:
                ValidateSlot(hover.Index);
                TradeItem? item = Offer(hover.Side).Slots[hover.Index];
                _hover = hover.IsHovered && item is not null
                    ? new TradeHover(hover.Side, hover.Index, item)
                    : null;
                break;

            case TradeInput.TogglePrimary when IsOpen:
                commands.Add(new TradeCommand.SetPrimaryConfirmation(
                    _snapshot!.SessionId,
                    !_snapshot.OwnOffer.PrimaryConfirmed));
                break;

            case TradeInput.ToggleSafeConfirmation when IsOpen:
                _safeConfirmation = !_safeConfirmation;
                if (!_safeConfirmation)
                {
                    RequestAutomaticFinal(commands);
                }

                break;

            case TradeInput.ToggleFinal when IsOpen && BothPrimaryConfirmed:
                bool confirm = !_snapshot!.OwnOffer.FinalConfirmed;
                commands.Add(new TradeCommand.SetFinalConfirmation(_snapshot.SessionId, confirm));
                if (!confirm)
                {
                    commands.Add(new TradeCommand.SetPrimaryConfirmation(_snapshot.SessionId, false));
                }

                break;
        }

        return Transition(nowMilliseconds, commands, cues);
    }

    public TradeTransition Advance(long nowMilliseconds)
    {
        ThrowIfDisposed();
        CheckTime(nowMilliseconds);
        List<TradeCommand> commands = [];
        List<TradeCue> cues = [];
        if (_state == TradeSessionState.Invitation && !_invitationResponseSent &&
            nowMilliseconds >= _invitationDeadline)
        {
            commands.Add(new TradeCommand.RespondInvitation(_invitationId, false));
            _invitationResponseSent = true;
            Close(TradeCloseReason.InvitationExpired, TradeSessionState.Canceled, cues);
        }

        return Transition(nowMilliseconds, commands, cues);
    }

    public TradeView Read(long nowMilliseconds)
    {
        ThrowIfDisposed();
        CheckTime(nowMilliseconds);
        return BuildView(nowMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _snapshot = null;
        _invitationSender = null;
        _hover = null;
    }

    private bool IsOpen => _state == TradeSessionState.InProgress && _snapshot is not null;

    private bool BothPrimaryConfirmed =>
        _snapshot?.OwnOffer.PrimaryConfirmed == true && _snapshot.PartnerOffer.PrimaryConfirmed;

    private void ObserveSnapshot(
        TradeSnapshot snapshot,
        List<TradeCommand> commands,
        List<TradeCue> cues)
    {
        bool opening = !IsOpen || _snapshot!.SessionId != snapshot.SessionId;
        bool offerChanged = !opening && OffersDiffer(_snapshot!, snapshot);
        bool primaryChanged = !opening &&
            (_snapshot!.OwnOffer.PrimaryConfirmed != snapshot.OwnOffer.PrimaryConfirmed ||
             _snapshot.PartnerOffer.PrimaryConfirmed != snapshot.PartnerOffer.PrimaryConfirmed);
        bool finalChanged = !opening &&
            (_snapshot!.OwnOffer.FinalConfirmed != snapshot.OwnOffer.FinalConfirmed ||
             _snapshot.PartnerOffer.FinalConfirmed != snapshot.PartnerOffer.FinalConfirmed);

        _snapshot = CloneSnapshot(snapshot);
        _state = TradeSessionState.InProgress;
        _invitationId = default;
        _invitationSender = null;
        _closeReason = TradeCloseReason.None;
        _startResult = TradeStartResult.Success;
        _error = null;
        if (opening)
        {
            _safeConfirmation = _product.Policy.SafeConfirmationDefault;
            _autoFinalRequested = false;
        }

        if (opening || offerChanged)
        {
            SynchronizeMoneyDraft();
            _hover = null;
        }
        else if (_hover is not null)
        {
            TradeItem? current = Offer(_hover.Side).Slots[_hover.Slot];
            _hover = current is null ? null : new TradeHover(_hover.Side, _hover.Slot, current);
        }

        if (!BothPrimaryConfirmed)
        {
            _autoFinalRequested = false;
        }

        if (opening)
        {
            cues.Add(TradeCue.TradeOpened);
        }
        else
        {
            if (offerChanged)
            {
                cues.Add(TradeCue.OfferChanged);
            }

            if (primaryChanged)
            {
                cues.Add(TradeCue.PrimaryConfirmationChanged);
            }

            if (finalChanged)
            {
                cues.Add(TradeCue.FinalConfirmationChanged);
            }
        }

        RequestAutomaticFinal(commands);
    }

    private void OfferBagItem(
        TradeInput.OfferBagItem offer,
        List<TradeCommand> commands,
        List<TradeCue> cues)
    {
        if (offer.BagSlot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offer.BagSlot));
        }

        if (offer.PreferredOfferSlot.HasValue)
        {
            ValidateSlot(offer.PreferredOfferSlot.Value);
        }

        if (offer.IsBound)
        {
            _error = TradeError.ItemIsBound;
            cues.Add(TradeCue.Error);
            return;
        }

        commands.Add(new TradeCommand.PutWholeBagStack(
            _snapshot!.SessionId,
            offer.BagSlot,
            offer.PreferredOfferSlot));
    }

    private void RequestAutomaticFinal(List<TradeCommand> commands)
    {
        if (IsOpen && BothPrimaryConfirmed && !_safeConfirmation &&
            !_snapshot!.OwnOffer.FinalConfirmed && !_autoFinalRequested)
        {
            commands.Add(new TradeCommand.SetFinalConfirmation(_snapshot.SessionId, true));
            _autoFinalRequested = true;
        }
    }

    private void ChangeMoney(TradeDenomination denomination, string text, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(text);
        int digits = denomination switch
        {
            TradeDenomination.Gold => _product.Policy.GoldDigits,
            TradeDenomination.Silver => _product.Policy.SilverDigits,
            TradeDenomination.Copper => _product.Policy.CopperDigits,
            _ => throw new ArgumentOutOfRangeException(nameof(denomination)),
        };
        if (text.Length > digits || text.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException("Trade money edits accept only the authored number of decimal digits.", nameof(text));
        }

        string canonical = text.Length == 0
            ? string.Empty
            : int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        switch (denomination)
        {
            case TradeDenomination.Gold:
                _gold = canonical;
                break;
            case TradeDenomination.Silver:
                _silver = ClampMinor(canonical);
                break;
            case TradeDenomination.Copper:
                _copper = ClampMinor(canonical);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(denomination));
        }

        _moneyFocus = denomination;
        _focusStarted = nowMilliseconds;
    }

    private void CommitMoney(List<TradeCommand> commands)
    {
        int gold = ParseDraft(_gold);
        int silver = ParseDraft(_silver);
        int copper = ParseDraft(_copper);
        TradeMoney desired = TradeMoney.FromParts(gold, silver, copper).Clamp(_snapshot!.AvailableMoney);
        _gold = desired.Gold.ToString(CultureInfo.InvariantCulture);
        _silver = desired.Silver.ToString(CultureInfo.InvariantCulture);
        _copper = desired.CopperRemainder.ToString(CultureInfo.InvariantCulture);
        _moneyFocus = null;
        if (desired != _snapshot.OwnOffer.Money)
        {
            commands.Add(new TradeCommand.SetMoney(_snapshot.SessionId, desired));
        }
    }

    private void SynchronizeMoneyDraft()
    {
        TradeMoney money = _snapshot?.OwnOffer.Money ?? default;
        _gold = money.Gold.ToString(CultureInfo.InvariantCulture);
        _silver = money.Silver.ToString(CultureInfo.InvariantCulture);
        _copper = money.CopperRemainder.ToString(CultureInfo.InvariantCulture);
        _moneyFocus = null;
    }

    private void Cancel(
        TradeCloseReason reason,
        List<TradeCommand> commands,
        List<TradeCue> cues)
    {
        commands.Add(new TradeCommand.Cancel(_snapshot!.SessionId, reason));
        Close(reason, TradeSessionState.Canceled, cues);
    }

    private void Close(TradeCloseReason reason, TradeSessionState state, List<TradeCue> cues)
    {
        bool invitation = _state == TradeSessionState.Invitation;
        bool open = IsOpen;
        _state = state;
        _closeReason = reason;
        _invitationId = default;
        _invitationSender = null;
        _invitationResponseSent = false;
        _snapshot = null;
        _hover = null;
        _moneyFocus = null;
        _autoFinalRequested = false;
        if (invitation)
        {
            cues.Add(TradeCue.InvitationClosed);
        }
        else if (open)
        {
            cues.Add(TradeCue.TradeClosed);
        }
    }

    private TradeTransition Transition(
        long nowMilliseconds,
        List<TradeCommand> commands,
        List<TradeCue> cues) =>
        new(BuildView(nowMilliseconds), commands.ToArray(), cues.ToArray());

    private TradeView BuildView(long nowMilliseconds)
    {
        long invitationRemaining = _state == TradeSessionState.Invitation
            ? Math.Max(0, _invitationDeadline - nowMilliseconds)
            : 0;
        bool cursorVisible = _moneyFocus.HasValue &&
            ((nowMilliseconds - _focusStarted) / _product.Policy.EditCursorBlinkMilliseconds) % 2 == 0;
        return new TradeView(
            _state,
            _invitationSender,
            invitationRemaining,
            _invitationResponseSent,
            _snapshot?.OwnParticipant,
            _snapshot?.Partner,
            _snapshot?.OwnIsInviter ?? false,
            _snapshot?.OwnOffer,
            _snapshot?.PartnerOffer,
            _snapshot?.AvailableMoney ?? default,
            new TradeMoneyDraft(_gold, _silver, _copper, _moneyFocus, cursorVisible),
            _safeConfirmation,
            IsOpen && BothPrimaryConfirmed && _safeConfirmation,
            _hover,
            _closeReason,
            _startResult,
            _error);
    }

    private TradeOffer Offer(TradeSide side) => side switch
    {
        TradeSide.Own => _snapshot!.OwnOffer,
        TradeSide.Partner => _snapshot!.PartnerOffer,
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    private static bool OffersDiffer(TradeSnapshot previous, TradeSnapshot next)
    {
        return previous.AvailableMoney != next.AvailableMoney ||
            !OfferContentEquals(previous.OwnOffer, next.OwnOffer) ||
            !OfferContentEquals(previous.PartnerOffer, next.PartnerOffer);
    }

    private static bool OfferContentEquals(TradeOffer left, TradeOffer right)
    {
        if (left.Money != right.Money)
        {
            return false;
        }

        for (int index = 0; index < TradeProduct.AuthoredSlotCount; index++)
        {
            if (!OfferItemContentEquals(left.Slots[index], right.Slots[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OfferItemContentEquals(TradeItem? left, TradeItem? right) =>
        left is null
            ? right is null
            : right is not null && left.Id == right.Id && left.StackCount == right.StackCount &&
                left.CounterCount == right.CounterCount;

    private static TradeSnapshot CloneSnapshot(TradeSnapshot snapshot) =>
        new(
            snapshot.SessionId,
            snapshot.State,
            snapshot.OwnParticipant,
            snapshot.Partner,
            snapshot.OwnIsInviter,
            snapshot.AvailableMoney,
            snapshot.OwnOffer.Clone(),
            snapshot.PartnerOffer.Clone());

    private static void ValidateObservation(TradeObservation observation)
    {
        switch (observation)
        {
            case TradeObservation.Invitation invitation:
                ValidateParticipant(invitation.Sender);
                if (invitation.InvitationId.IsEmpty)
                {
                    throw new ArgumentException("A trade invitation ID cannot be empty.", nameof(observation));
                }

                break;
            case TradeObservation.Snapshot observed:
                TradeSnapshot snapshot = observed.Value;
                ArgumentNullException.ThrowIfNull(snapshot);
                ArgumentNullException.ThrowIfNull(snapshot.OwnOffer);
                ArgumentNullException.ThrowIfNull(snapshot.PartnerOffer);
                ValidateParticipant(snapshot.OwnParticipant);
                ValidateParticipant(snapshot.Partner);
                if (snapshot.SessionId.IsEmpty || snapshot.State != TradeSessionState.InProgress ||
                    snapshot.OwnParticipant.Id == snapshot.Partner.Id ||
                    snapshot.OwnOffer.Money.Copper > snapshot.AvailableMoney.Copper ||
                    ((snapshot.OwnOffer.FinalConfirmed || snapshot.PartnerOffer.FinalConfirmed) &&
                     !(snapshot.OwnOffer.PrimaryConfirmed && snapshot.PartnerOffer.PrimaryConfirmed)))
                {
                    throw new ArgumentException("The authoritative trade snapshot violates the retail state contract.", nameof(observation));
                }

                foreach (TradeItem? item in snapshot.OwnOffer.Slots.Concat(snapshot.PartnerOffer.Slots))
                {
                    if (item is not null)
                    {
                        ValidateItem(item);
                    }
                }

                break;
            case TradeObservation.StartResult result when !Enum.IsDefined(result.Result):
                throw new ArgumentOutOfRangeException(nameof(observation));
            case TradeObservation.Error error when !Enum.IsDefined(error.Value):
                throw new ArgumentOutOfRangeException(nameof(observation));
            case TradeObservation.Terminal terminal when !Enum.IsDefined(terminal.State):
                throw new ArgumentOutOfRangeException(nameof(observation));
            case TradeObservation.Terminal terminal when terminal.SessionId.IsEmpty ||
                terminal.State is TradeSessionState.Invitation or TradeSessionState.InProgress:
                throw new ArgumentException("A terminal observation needs a session and terminal state.", nameof(observation));
            case TradeObservation.PartnerDistance distance when !double.IsFinite(distance.Meters) || distance.Meters < 0:
                throw new ArgumentOutOfRangeException(nameof(observation));
        }
    }

    private static void ValidateParticipant(TradeParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (participant.Id.IsEmpty || string.IsNullOrWhiteSpace(participant.Name))
        {
            throw new ArgumentException("A trade participant needs a non-empty ID and name.", nameof(participant));
        }
    }

    private static void ValidateItem(TradeItem item)
    {
        if (item.Id.IsEmpty || item.StackCount <= 0 || item.CounterCount < 0 ||
            item.CooldownRemainingMilliseconds < 0)
        {
            throw new ArgumentException("A trade item violates the product view contract.", nameof(item));
        }
    }

    private static int ParseDraft(string text) =>
        text.Length == 0 ? 0 : int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string ClampMinor(string text) =>
        text.Length == 0 ? string.Empty : Math.Min(ParseDraft(text), 99).ToString(CultureInfo.InvariantCulture);

    private static void ValidateSlot(int slot)
    {
        if (slot is < 0 or >= TradeProduct.AuthoredSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void CheckTime(long nowMilliseconds)
    {
        if (nowMilliseconds < _lastTime)
        {
            throw new ArgumentOutOfRangeException(nameof(nowMilliseconds), "Trade time must be monotonic.");
        }

        _lastTime = nowMilliseconds;
    }

    private static TradeCloseReason ToCloseReason(TradeSessionState state) => state switch
    {
        TradeSessionState.Completed => TradeCloseReason.Completed,
        TradeSessionState.Canceled => TradeCloseReason.Canceled,
        TradeSessionState.Failed => TradeCloseReason.Failed,
        TradeSessionState.NoBagSpace => TradeCloseReason.NoBagSpace,
        TradeSessionState.Lost => TradeCloseReason.Lost,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
