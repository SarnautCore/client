namespace SarnautCore.NativeTrade.Tests;

public sealed class NativeTradeTests
{
    [Fact]
    public void Invitation_expires_to_an_explicit_default_decline_at_thirty_seconds()
    {
        using NativeTrade trade = NewTrade();
        TradeTransition opened = trade.Observe(
            new TradeObservation.Invitation(new TradeInvitationId(7), Partner),
            100);

        Assert.Equal(30_000, opened.View.InvitationMillisecondsRemaining);
        Assert.Empty(trade.Advance(30_099).Commands);
        TradeTransition expired = trade.Advance(30_100);

        TradeCommand.RespondInvitation command = Assert.IsType<TradeCommand.RespondInvitation>(Assert.Single(expired.Commands));
        Assert.False(command.Accept);
        Assert.Equal(new TradeInvitationId(7), command.InvitationId);
        Assert.Equal(TradeCloseReason.InvitationExpired, expired.View.CloseReason);
    }

    [Fact]
    public void Accepting_an_invitation_waits_for_the_authoritative_in_progress_snapshot()
    {
        using NativeTrade trade = NewTrade();
        trade.Observe(new TradeObservation.Invitation(new TradeInvitationId(7), Partner), 0);

        TradeTransition accepted = trade.Dispatch(new TradeInput.RespondInvitation(true), 1);

        Assert.True(Assert.IsType<TradeCommand.RespondInvitation>(Assert.Single(accepted.Commands)).Accept);
        Assert.Equal(TradeSessionState.Invitation, accepted.View.State);
        Assert.False(accepted.View.IsOpen);
        Assert.True(accepted.View.InvitationResponsePending);
        Assert.False(accepted.View.InvitationPromptVisible);
    }

    [Fact]
    public void Accepted_invitation_is_not_contradicted_by_a_later_timeout_or_second_response()
    {
        using NativeTrade trade = NewTrade();
        trade.Observe(new TradeObservation.Invitation(new TradeInvitationId(7), Partner), 100);

        TradeTransition accepted = trade.Dispatch(new TradeInput.RespondInvitation(true), 101);
        TradeTransition repeated = trade.Dispatch(new TradeInput.RespondInvitation(false), 102);
        TradeTransition closed = trade.Dispatch(new TradeInput.Close(), 103);
        TradeTransition elapsed = trade.Advance(30_100);

        Assert.True(Assert.IsType<TradeCommand.RespondInvitation>(Assert.Single(accepted.Commands)).Accept);
        Assert.Empty(repeated.Commands);
        Assert.Empty(closed.Commands);
        Assert.Empty(elapsed.Commands);
        Assert.Equal(TradeSessionState.Invitation, elapsed.View.State);
    }

    [Theory]
    [InlineData(false, TradeCloseReason.UserClosed)]
    [InlineData(true, TradeCloseReason.Escape)]
    public void Closing_an_invitation_declines_through_the_shared_prompt_callback(
        bool escape,
        TradeCloseReason expectedReason)
    {
        using NativeTrade trade = NewTrade();
        trade.Observe(new TradeObservation.Invitation(new TradeInvitationId(7), Partner), 0);

        TradeTransition closed = trade.Dispatch(
            escape ? new TradeInput.Escape() : new TradeInput.Close(),
            1);

        TradeCommand.RespondInvitation response = Assert.IsType<TradeCommand.RespondInvitation>(
            Assert.Single(closed.Commands));
        Assert.False(response.Accept);
        Assert.Equal(expectedReason, closed.View.CloseReason);
        Assert.Contains(TradeCue.InvitationClosed, closed.Cues);
    }

    [Fact]
    public void Trade_opens_only_from_an_authoritative_in_progress_snapshot()
    {
        using NativeTrade trade = NewTrade();

        TradeTransition opened = trade.Observe(new TradeObservation.Snapshot(Snapshot()), 0);

        Assert.True(opened.View.IsOpen);
        Assert.Equal("Partner", opened.View.Partner!.Name);
        Assert.Equal(5, opened.View.OwnOffer!.Slots.Count);
        Assert.Contains(TradeCue.TradeOpened, opened.Cues);
    }

    [Fact]
    public void Money_uses_five_two_two_digits_and_clamps_to_available_copper()
    {
        using NativeTrade trade = OpenTrade(available: 12_345);
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Gold, "99999"), 1);
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Silver, "99"), 2);
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Copper, "99"), 3);

        TradeTransition committed = trade.Dispatch(new TradeInput.CommitMoney(), 4);

        TradeCommand.SetMoney command = Assert.IsType<TradeCommand.SetMoney>(Assert.Single(committed.Commands));
        Assert.Equal(12_345, command.Money.Copper);
        Assert.Equal(new TradeMoneyDraft("1", "23", "45", null, false), committed.View.MoneyDraft);
    }

    [Fact]
    public void Money_edit_rejects_non_digits_and_excess_digits()
    {
        using NativeTrade trade = OpenTrade();

        Assert.Throws<ArgumentException>(() =>
            trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Gold, "100000"), 1));
        Assert.Throws<ArgumentException>(() =>
            trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Silver, "1x"), 1));
    }

    [Fact]
    public void Money_escape_reverts_the_authoritative_offer()
    {
        using NativeTrade trade = OpenTrade(ownMoney: 20_304);
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Gold, "9"), 1);

        TradeTransition reverted = trade.Dispatch(new TradeInput.RevertMoney(), 2);

        Assert.Empty(reverted.Commands);
        Assert.Equal(new TradeMoneyDraft("2", "3", "4", null, false), reverted.View.MoneyDraft);
    }

    [Fact]
    public void Edit_cursor_uses_the_authored_five_hundred_millisecond_blink()
    {
        using NativeTrade trade = OpenTrade();
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Copper, "1"), 100);

        Assert.True(trade.Read(599).MoneyDraft.CursorVisible);
        Assert.False(trade.Read(600).MoneyDraft.CursorVisible);
        Assert.True(trade.Read(1_100).MoneyDraft.CursorVisible);
    }

    [Fact]
    public void Whole_unbound_bag_stacks_can_target_a_slot_or_first_free()
    {
        using NativeTrade trade = OpenTrade();

        TradeCommand.PutWholeBagStack explicitSlot = Assert.IsType<TradeCommand.PutWholeBagStack>(Assert.Single(
            trade.Dispatch(new TradeInput.OfferBagItem(12, false, 3), 1).Commands));
        TradeCommand.PutWholeBagStack firstFree = Assert.IsType<TradeCommand.PutWholeBagStack>(Assert.Single(
            trade.Dispatch(new TradeInput.OfferBagItem(13, false), 2).Commands));

        Assert.Equal(3, explicitSlot.PreferredOfferSlot);
        Assert.Null(firstFree.PreferredOfferSlot);
    }

    [Fact]
    public void Bound_item_is_refused_before_a_command_leaves_the_runtime()
    {
        using NativeTrade trade = OpenTrade();

        TradeTransition refused = trade.Dispatch(new TradeInput.OfferBagItem(12, true), 1);

        Assert.Empty(refused.Commands);
        Assert.Equal(TradeError.ItemIsBound, refused.View.Error);
        Assert.Contains(TradeCue.Error, refused.Cues);
    }

    [Fact]
    public void Right_click_removes_only_a_present_own_slot()
    {
        TradeItem item = new(new TradeItemId(9), 2);
        using NativeTrade trade = OpenTrade(ownSlots: [item, null, null, null, null]);

        TradeTransition own = trade.Dispatch(new TradeInput.RightClickOwnSlot(0), 1);
        TradeTransition empty = trade.Dispatch(new TradeInput.RightClickOwnSlot(1), 2);

        Assert.Equal(0, Assert.IsType<TradeCommand.RemoveOwnItem>(Assert.Single(own.Commands)).Slot);
        Assert.Empty(empty.Commands);
    }

    [Fact]
    public void Item_count_prefers_stack_then_counter_and_hides_one()
    {
        Assert.Equal(5, new TradeItem(new TradeItemId(1), 5, 8).VisibleCount);
        Assert.Equal(8, new TradeItem(new TradeItemId(1), 1, 8).VisibleCount);
        Assert.Equal(0, new TradeItem(new TradeItemId(1), 1, 1).VisibleCount);
    }

    [Fact]
    public void Trade_offer_does_not_expose_its_mutable_slot_array()
    {
        TradeItem item = new(new TradeItemId(1), 2);
        TradeItem?[] source = [item, null, null, null, null];
        TradeOffer offer = new(source, default, false, false);

        source[0] = null;

        Assert.Equal(item, offer.Slots[0]);
        Assert.False(offer.Slots is TradeItem?[]);
        IList<TradeItem?> list = Assert.IsAssignableFrom<IList<TradeItem?>>(offer.Slots);
        Assert.Throws<NotSupportedException>(() => list[0] = null);
    }

    [Fact]
    public void Cooldown_ticks_refresh_the_view_without_resetting_hover_or_money_editing()
    {
        TradeItem first = new(new TradeItemId(9), 2, CooldownRemainingMilliseconds: 5_000);
        using NativeTrade trade = OpenTrade(ownSlots: [first, null, null, null, null]);
        trade.Dispatch(new TradeInput.ChangeMoney(TradeDenomination.Gold, "9"), 1);
        trade.Dispatch(new TradeInput.HoverSlot(TradeSide.Own, 0, true), 2);
        TradeItem updated = first with { CooldownRemainingMilliseconds = 4_000 };

        TradeTransition tick = trade.Observe(
            new TradeObservation.Snapshot(Snapshot(ownSlots: [updated, null, null, null, null])),
            3);

        Assert.DoesNotContain(TradeCue.OfferChanged, tick.Cues);
        Assert.Equal("9", tick.View.MoneyDraft.Gold);
        Assert.Equal(TradeDenomination.Gold, tick.View.MoneyDraft.Focus);
        Assert.Equal(4_000, tick.View.Hover!.Item.CooldownRemainingMilliseconds);
    }

    [Fact]
    public void Primary_toggle_emits_the_inverse_authoritative_state()
    {
        using NativeTrade trade = OpenTrade(ownPrimary: true);

        TradeCommand.SetPrimaryConfirmation command = Assert.IsType<TradeCommand.SetPrimaryConfirmation>(
            Assert.Single(trade.Dispatch(new TradeInput.TogglePrimary(), 1).Commands));

        Assert.False(command.Confirmed);
    }

    [Fact]
    public void Enabling_safe_confirmation_shows_the_second_stage_after_both_primary_accept()
    {
        using NativeTrade trade = OpenTrade(ownPrimary: true, partnerPrimary: true);

        TradeTransition enabled = trade.Dispatch(new TradeInput.ToggleSafeConfirmation(), 1);

        Assert.True(enabled.View.ConfirmationPanelVisible);
        Assert.Empty(enabled.Commands);
    }

    [Fact]
    public void Safe_confirmation_returns_to_the_product_default_for_each_session()
    {
        using NativeTrade trade = OpenTrade();
        Assert.True(trade.Dispatch(new TradeInput.ToggleSafeConfirmation(), 1).View.SafeConfirmation);
        trade.Dispatch(new TradeInput.Close(), 2);

        TradeTransition next = trade.Observe(new TradeObservation.Snapshot(Snapshot()), 3);

        Assert.False(next.View.SafeConfirmation);
    }

    [Fact]
    public void Authored_safe_default_is_off_and_auto_requests_final_accept_once()
    {
        using NativeTrade trade = NewTrade();

        TradeTransition observed = trade.Observe(
            new TradeObservation.Snapshot(Snapshot(ownPrimary: true, partnerPrimary: true)),
            0);
        TradeTransition repeated = trade.Observe(
            new TradeObservation.Snapshot(Snapshot(ownPrimary: true, partnerPrimary: true)),
            1);

        Assert.True(Assert.IsType<TradeCommand.SetFinalConfirmation>(Assert.Single(observed.Commands)).Confirmed);
        Assert.Empty(repeated.Commands);
        Assert.False(observed.View.ConfirmationPanelVisible);
    }

    [Fact]
    public void Final_toggle_off_also_clears_primary_confirmation()
    {
        using NativeTrade trade = OpenTrade(
            ownPrimary: true,
            partnerPrimary: true,
            ownFinal: true,
            partnerFinal: false);

        TradeTransition toggled = trade.Dispatch(new TradeInput.ToggleFinal(), 1);

        Assert.Collection(
            toggled.Commands,
            command => Assert.False(Assert.IsType<TradeCommand.SetFinalConfirmation>(command).Confirmed),
            command => Assert.False(Assert.IsType<TradeCommand.SetPrimaryConfirmation>(command).Confirmed));
    }

    [Theory]
    [InlineData(TradeSessionState.Completed, TradeCloseReason.Completed)]
    [InlineData(TradeSessionState.Canceled, TradeCloseReason.Canceled)]
    [InlineData(TradeSessionState.Failed, TradeCloseReason.Failed)]
    [InlineData(TradeSessionState.NoBagSpace, TradeCloseReason.NoBagSpace)]
    [InlineData(TradeSessionState.Lost, TradeCloseReason.Lost)]
    public void Every_retail_terminal_state_closes_with_its_typed_reason(
        TradeSessionState state,
        TradeCloseReason reason)
    {
        using NativeTrade trade = OpenTrade();

        TradeTransition closed = trade.Observe(
            new TradeObservation.Terminal(new TradeSessionId(11), state),
            1);

        Assert.False(closed.View.IsOpen);
        Assert.Equal(reason, closed.View.CloseReason);
        Assert.Contains(TradeCue.TradeClosed, closed.Cues);
    }

    [Fact]
    public void Distance_over_five_meters_cancels_but_five_does_not()
    {
        using NativeTrade trade = OpenTrade();

        Assert.Empty(trade.Observe(new TradeObservation.PartnerDistance(5), 1).Commands);
        TradeTransition canceled = trade.Observe(new TradeObservation.PartnerDistance(5.001), 2);

        Assert.Equal(TradeCloseReason.OutOfRange, Assert.IsType<TradeCommand.Cancel>(Assert.Single(canceled.Commands)).Reason);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("removed")]
    [InlineData("inventory")]
    [InlineData("bag")]
    public void Retail_local_invalidation_cancels_the_open_trade(string kind)
    {
        using NativeTrade trade = OpenTrade();
        TradeObservation observation = kind switch
        {
            "death" => new TradeObservation.LocalDeath(),
            "removed" => new TradeObservation.PartnerRemoved(),
            "inventory" => new TradeObservation.InventoryChanged(),
            "bag" => new TradeObservation.BagModeConflict(),
            _ => throw new InvalidOperationException(),
        };

        Assert.IsType<TradeCommand.Cancel>(Assert.Single(trade.Observe(observation, 1).Commands));
    }

    [Fact]
    public void Named_and_selected_target_invites_remain_distinct_typed_host_actions()
    {
        using NativeTrade trade = NewTrade();

        TradeCommand.InviteByName named = Assert.IsType<TradeCommand.InviteByName>(Assert.Single(
            trade.Dispatch(new TradeInput.InviteByName("  Partner  "), 0).Commands));
        TradeCommand.InviteSelectedTarget selected = Assert.IsType<TradeCommand.InviteSelectedTarget>(Assert.Single(
            trade.Dispatch(new TradeInput.InviteSelectedTarget(), 1).Commands));

        Assert.Equal("Partner", named.PlayerName);
        Assert.NotNull(selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_busy_session_refuses_an_additional_invite(bool invitation)
    {
        using NativeTrade trade = invitation ? NewTrade() : OpenTrade();
        if (invitation)
        {
            trade.Observe(new TradeObservation.Invitation(new TradeInvitationId(7), Partner), 0);
        }

        TradeTransition refused = trade.Dispatch(new TradeInput.InviteByName("Someone"), 1);

        Assert.Empty(refused.Commands);
        Assert.Equal(TradeStartResult.InviterAvatarIsBusy, refused.View.StartResult);
        Assert.Contains(TradeCue.Error, refused.Cues);
    }

    [Fact]
    public void Retail_enum_ordinals_are_pinned()
    {
        Assert.Equal(6, (int)TradeSessionState.Lost);
        Assert.Equal(5, (int)TradeError.ItemIsBound);
        Assert.Equal(8, (int)TradeStartResult.YouAreInvisible);
    }

    [Fact]
    public void Undefined_authoritative_enums_are_rejected_at_the_boundary()
    {
        using NativeTrade trade = NewTrade();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            trade.Observe(new TradeObservation.StartResult((TradeStartResult)99), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            trade.Observe(new TradeObservation.Error((TradeError)99), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            trade.Observe(
                new TradeObservation.Terminal(new TradeSessionId(11), (TradeSessionState)99),
                0));
    }

    private static readonly TradeParticipant Own = new(new TradeParticipantId(1), "Own");
    private static readonly TradeParticipant Partner = new(new TradeParticipantId(2), "Partner");

    private static NativeTrade NewTrade() => new(Product());

    private static NativeTrade OpenTrade(
        long available = 100_000,
        long ownMoney = 0,
        bool ownPrimary = false,
        bool partnerPrimary = false,
        bool ownFinal = false,
        bool partnerFinal = false,
        IReadOnlyList<TradeItem?>? ownSlots = null,
        IReadOnlyList<TradeItem?>? partnerSlots = null)
    {
        NativeTrade trade = NewTrade();
        trade.Observe(
            new TradeObservation.Snapshot(Snapshot(
                available,
                ownMoney,
                ownPrimary,
                partnerPrimary,
                ownFinal,
                partnerFinal,
                ownSlots,
                partnerSlots)),
            0);
        return trade;
    }

    private static TradeSnapshot Snapshot(
        long available = 100_000,
        long ownMoney = 0,
        bool ownPrimary = false,
        bool partnerPrimary = false,
        bool ownFinal = false,
        bool partnerFinal = false,
        IReadOnlyList<TradeItem?>? ownSlots = null,
        IReadOnlyList<TradeItem?>? partnerSlots = null) =>
        new(
            new TradeSessionId(11),
            TradeSessionState.InProgress,
            Own,
            Partner,
            true,
            new TradeMoney(available),
            new TradeOffer(ownSlots ?? EmptySlots(), new TradeMoney(ownMoney), ownPrimary, ownFinal),
            new TradeOffer(partnerSlots ?? EmptySlots(), default, partnerPrimary, partnerFinal));

    private static TradeItem?[] EmptySlots() => new TradeItem?[5];

    internal static TradeProduct Product()
    {
        return new TradeProduct(
            TradeProduct.SchemaId,
            "screens/trade.tscn",
            Array.Empty<TradeResourceReference>(),
            "hud.items.inst-league1",
            new TradeArtPolicy("classic-1.1", true),
            new TradePlacement(1500, -11, 155, 475, 485),
            new TradePanelPlacement(17, 25, 221, 445),
            new TradePanelPlacement(241, 25, 221, 445),
            new TradePanelPlacement(11, 371, 456, 100),
            new[] { "UserFrame", "CustomerFrame", "GoldenCorner", "FramePanel", "WindowHeader", "TradeConfirmation" },
            new[] { "FramePanel", "UserFrame", "CustomerFrame", "GoldenCorner", "WindowHeader", "TradeConfirmation" },
            new TradeRoles(
                "Root",
                "Root/Close",
                Side("Root/Own", "Primary"),
                Side("Root/Partner", "PartnerPrimary"),
                new TradeConfirmationRoles(
                    "Root/Own/Safe",
                    "Root/Confirm",
                    "Root/Confirm/Final",
                    "Root/Confirm/Pending",
                    "Root/Confirm/Accepted")),
            new TradePolicy(
                5, 5, 2, 2, 30_000, "trade_invitation", false,
                true, true, true, 500, 5, false, false),
            Semantics());
    }

    private static TradeSideRoles Side(string root, string primary)
    {
        TradeSlotRoles[] slots = Enumerable.Range(1, 5)
            .Select(index =>
            {
                string slot = $"{root}/Slot{index:00}";
                return new TradeSlotRoles(
                    slot,
                    $"{slot}/ItemIcon",
                    $"{slot}/Icon",
                    $"{slot}/Count",
                    $"{slot}/Cooldown",
                    $"{slot}/ItemName");
            })
            .ToArray();
        return new TradeSideRoles(
            root,
            $"{root}/Name",
            slots,
            $"{root}/Gold",
            $"{root}/Silver",
            $"{root}/Copper",
            $"{root}/{primary}",
            $"{root}/AcceptMark");
    }

    private static TradeSemantics Semantics() => new(
        Ordinals("invitation", "in-progress", "completed", "canceled", "failed", "no-bag-space", "lost"),
        Ordinals("money-not-enough", "primary-confirmation-required", "item-not-found", "slot-is-used", "item-is-used", "item-is-bound"),
        Ordinals("success", "error", "invited-avatar-is-busy", "inviter-avatar-is-busy", "invited-avatar-not-found", "too-far", "invited-avatar-is-dead", "inviter-avatar-is-dead", "you-are-invisible"),
        new[]
        {
            "invite.accept", "invite.decline", "trade.cancel", "offer.put-whole-stack",
            "offer.remove-own-slot", "offer.hover-slot", "offer.set-money",
            "confirmation.set-primary", "confirmation.toggle-safe-local", "confirmation.set-final",
        },
        true,
        true,
        true,
        false,
        true,
        true,
        true,
        true,
        true,
        true,
        2,
        2,
        true,
        true,
        new[]
        {
            "client-close", "escape", "force-close", "inventory-mutation", "avatar-removal",
            "non-trade-bag-open", "distance-exceeded", "participant-death",
        },
        Array.Empty<string>());

    private static TradeOrdinal[] Ordinals(params string[] ids) =>
        ids.Select((id, value) => new TradeOrdinal(id, value)).ToArray();
}
