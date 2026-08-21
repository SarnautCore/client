using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class NativeHudTests
{
    private static readonly HudViewport Viewport = new(0, 0, 1920, 1080);

    [Fact]
    public void ProductRequiresExactAuthoredPools()
    {
        HudProduct valid = Product();
        Assert.Equal(36, valid.ActionSlots.Length);
        Assert.All(valid.FeedbackPools, pool => Assert.Equal(5, pool.Elements.Length));
        Assert.Equal(20, valid.QuestTrackerRows.Length);

        Assert.Throws<ArgumentException>(() => Product(actionCount: 35));
        Assert.Throws<ArgumentException>(() => Product(feedbackCount: 6));
        Assert.Throws<ArgumentException>(() => Product(questCount: 21));
        Assert.Throws<ArgumentException>(() => Product(unitPlateCount: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Product(maxEntities: 4, maxOvertips: 5));
        Assert.Throws<ArgumentException>(() => Product(timelines: HudTimelineCatalog.Retail with { MessageFadeOutMilliseconds = 899 }));
    }

    [Fact]
    public void NewerAuthorityWinsAndStaleOrConflictingAuthorityDoesNot()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.ActionSlotChanged(Stamp(2), 0, Id("new"), 20));
        hud.Advance(Frame(0));
        session.TryQueue(HudEvent.ActionSlotChanged(Stamp(1), 0, Id("old"), 10));
        session.TryQueue(HudEvent.ActionSlotChanged(Stamp(2), 0, Id("conflict"), 30));

        HudDiff diff = hud.Advance(Frame(1));

        HudActionSlotView slot = diff.ReadModel.ActionSlots[0];
        Assert.Equal(Id("new"), slot.AbilityId);
        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.StaleAuthority);
        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.AuthorityConflict);
    }

    [Fact]
    public void EqualIdenticalAuthorityIsIdempotent()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudEvent item = HudEvent.ActionSlotChanged(Stamp(1), 0, Id("ability"), 0);
        session.TryQueue(item);
        hud.Advance(Frame(0));
        session.TryQueue(item);

        HudDiff diff = hud.Advance(Frame(1));

        Assert.Empty(diff.Errors.ToArray());
        Assert.Empty(diff.Changes.ToArray());
    }

    [Fact]
    public void UnitTombstoneRejectsStaleResurrectionAndCancelsFeedback()
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(7, new HudProjection(new HudPoint(400, 300), 2, true, false));
        using NativeHud hud = Open(session, world);
        session.TryQueue(HudEvent.UnitChanged(Stamp(1), 7, Id("unit"), 10, 10));
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(2), Id("hit"), HudFeedbackKind.Enemy, 7, 12));
        hud.Advance(Frame(0));
        session.TryQueue(HudEvent.UnitRemoved(Stamp(4), 7));
        hud.Advance(Frame(1));
        session.TryQueue(HudEvent.UnitChanged(Stamp(3), 7, Id("unit"), 10, 10));

        HudDiff diff = hud.Advance(Frame(2));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.StaleAuthority);
        Assert.DoesNotContain(diff.ReadModel.Feedback.ToArray(), feedback => feedback.Active);
    }

    [Fact]
    public void UnitUpdatePublishesStablePlateAndOvertipViewsWithRevisionAndChangeAreas()
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(7, new HudProjection(new HudPoint(440, 330), 2, true, false));
        using NativeHud hud = Open(session, world);
        var presentation = new HudUnitPresentation(new HudPlateAssignment(Id("target")), true);
        session.TryQueue(HudEvent.UnitChanged(Stamp(3), 7, Id("wolf"), 45, 60, presentation));

        HudDiff diff = hud.Advance(Frame(0));

        HudUnitView unit = Assert.Single(diff.ReadModel.Units.ToArray(), item => item.Active);
        Assert.Equal(7UL, unit.EntityId);
        Assert.Equal(Id("wolf"), unit.NameId);
        Assert.Equal(45, unit.Health);
        Assert.Equal(60, unit.MaximumHealth);
        Assert.Equal(Stamp(3), unit.Revision);
        Assert.Equal(Id("plate-target"), unit.PlateElement);
        Assert.True(unit.PlateVisible);
        Assert.Equal(Id("overtip-prototype"), unit.OvertipElement);
        Assert.True(unit.OvertipVisible);
        Assert.Equal(new HudPoint(440, 330), unit.OvertipPosition);
        HudUnitPlateView targetPlate = Assert.Single(diff.ReadModel.UnitPlates.ToArray(), item => item.Assignment == presentation.Plate);
        Assert.True(targetPlate.Occupied);
        Assert.Equal(7UL, targetPlate.EntityId);
        HudOvertipView lane = diff.ReadModel.Overtips[0];
        Assert.Equal(0, lane.Lane);
        Assert.True(lane.Occupied);
        Assert.True(lane.Visible);
        Assert.Equal(7UL, lane.EntityId);
        Assert.Contains(diff.Changes.ToArray(), change =>
            change.Kind == HudChangeKind.UnitPlate && change.Element == Id("plate-target") &&
            change.Value == 45 && change.SecondaryValue == 60 && change.Revision == Stamp(3) &&
            change.UnitAreas.HasFlag(HudUnitChangeAreas.Vitality));
        Assert.Contains(diff.Changes.ToArray(), change =>
            change.Kind == HudChangeKind.Overtip && change.Element == Id("overtip-prototype") && change.Generation == 0 && change.Visible &&
            change.UnitAreas.HasFlag(HudUnitChangeAreas.Projection));
    }

    [Fact]
    public void UnitRemovalHidesAndReleasesStableAssignments()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        var presentation = new HudUnitPresentation(new HudPlateAssignment(Id("target")), true);
        session.TryQueue(HudEvent.UnitChanged(Stamp(1), 7, Id("wolf"), 45, 60, presentation));
        hud.Advance(Frame(0));
        session.TryQueue(HudEvent.UnitRemoved(Stamp(2), 7));

        HudDiff diff = hud.Advance(Frame(1));

        HudUnitView unit = Assert.Single(diff.ReadModel.Units.ToArray(), item => item.EntityId == 7);
        Assert.False(unit.Active);
        Assert.True(unit.PlateElement.IsEmpty);
        Assert.True(unit.OvertipElement.IsEmpty);
        Assert.False(Assert.Single(diff.ReadModel.UnitPlates.ToArray(), item => item.Assignment == presentation.Plate).Occupied);
        Assert.False(diff.ReadModel.Overtips[0].Occupied);
        Assert.Contains(diff.Changes.ToArray(), change =>
            change.Kind == HudChangeKind.UnitPlate && !change.Visible &&
            change.UnitAreas.HasFlag(HudUnitChangeAreas.Removal));
        Assert.Contains(diff.Changes.ToArray(), change =>
            change.Kind == HudChangeKind.Overtip && !change.Visible &&
            change.UnitAreas.HasFlag(HudUnitChangeAreas.Removal));
    }

    [Fact]
    public void PlateOwnershipUsesAuthorityOrderAndReportsEqualConflicts()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        var targetOnly = new HudUnitPresentation(new HudPlateAssignment(Id("target")), false);
        session.TryQueue(HudEvent.UnitChanged(new HudStamp(1, 1, 0), 1, Id("one"), 1, 1, targetOnly));
        session.TryQueue(HudEvent.UnitChanged(new HudStamp(1, 1, 0), 2, Id("two"), 1, 1, targetOnly));
        HudDiff conflict = hud.Advance(Frame(0));
        Assert.Contains(conflict.Errors.ToArray(), error => error.Code == HudErrorCode.UnitPlateAssignmentConflict);
        Assert.Equal(Id("plate-target"), Assert.Single(conflict.ReadModel.Units.ToArray(), item => item.EntityId == 1).PlateElement);
        Assert.True(Assert.Single(conflict.ReadModel.Units.ToArray(), item => item.EntityId == 2).PlateElement.IsEmpty);

        session.TryQueue(HudEvent.UnitChanged(Stamp(2), 2, Id("two"), 1, 1, targetOnly));
        HudDiff displaced = hud.Advance(Frame(1));
        Assert.True(Assert.Single(displaced.ReadModel.Units.ToArray(), item => item.EntityId == 1).PlateElement.IsEmpty);
        Assert.Equal(Id("plate-target"), Assert.Single(displaced.ReadModel.Units.ToArray(), item => item.EntityId == 2).PlateElement);
    }

    [Fact]
    public void OvertipCandidatesUseBoundedStableAuthoredPool()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        for (ulong entity = 1; entity <= 5; entity++)
        {
            session.TryQueue(HudEvent.UnitChanged(new HudStamp(1, 1, (uint)entity), entity, Id($"unit-{entity}"), 1, 1));
        }

        HudDiff diff = hud.Advance(Frame(0));

        Assert.Equal(5, diff.ReadModel.Units.ToArray().Count(item => item.Active));
        Assert.Equal(4, diff.ReadModel.Units.ToArray().Count(item => !item.OvertipElement.IsEmpty));
        Assert.Equal(4, diff.ReadModel.Overtips.Length);
        Assert.Equal([0, 1, 2, 3], diff.ReadModel.Overtips.ToArray().Select(item => item.Lane).ToArray());
        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.OvertipCapacityExceeded);
    }

    [Fact]
    public void ActionInputEmitsTypedCommandOnlyForEnabledAuthoredSlot()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.ActionSlotChanged(Stamp(1), 3, Id("slash"), 0));
        hud.Advance(Frame(0));
        Assert.Equal(HudDispatchStatus.Accepted, hud.Dispatch(HudInput.ActivateAction(3)).Status);
        hud.Advance(Frame(1));

        Assert.True(session.TryReadCommand(out HudCommand command));
        Assert.Equal(HudCommandKind.ActivateAction, command.Kind);
        Assert.Equal(3, command.Slot);
        Assert.True(command.Value.IsEmpty);
        Assert.Equal(Stamp(1), command.ExpectedRevision);
        Assert.False(session.TryReadCommand(out _));
    }

    [Fact]
    public void InputQueueOverflowIsExplicitAndBounded()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session, product: Product(maxPendingInputs: 1));
        Assert.Equal(HudDispatchStatus.Accepted, hud.Dispatch(HudInput.Cancel()).Status);
        Assert.Equal(HudDispatchStatus.RejectedQueueFull, hud.Dispatch(HudInput.Cancel()).Status);

        HudDiff diff = hud.Advance(Frame(0));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.InputQueueOverflow);
    }

    [Fact]
    public void SessionOverflowFaultIsVisible()
    {
        var session = new InMemoryHudSession(eventCapacity: 1);
        using NativeHud hud = Open(session);
        Assert.True(session.TryQueue(HudEvent.ActionSlotCleared(Stamp(1), 0)));
        Assert.False(session.TryQueue(HudEvent.ActionSlotCleared(Stamp(2), 1)));

        HudDiff diff = hud.Advance(Frame(0));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.SessionEventOverflow);
        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.SessionFaulted);
    }

    [Fact]
    public void CommandOverflowIsExplicit()
    {
        var session = new InMemoryHudSession(commandCapacity: 1);
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.ActionSlotChanged(new HudStamp(1, 1, 0), 0, Id("one"), 0));
        session.TryQueue(HudEvent.ActionSlotChanged(new HudStamp(1, 1, 1), 1, Id("two"), 0));
        hud.Advance(Frame(0));
        hud.Dispatch(HudInput.ActivateAction(0));
        hud.Dispatch(HudInput.ActivateAction(1));

        HudDiff diff = hud.Advance(Frame(1));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.CommandQueueFull);
    }

    [Fact]
    public void UnsupportedCommandFamilyIsExplicit()
    {
        var session = new InMemoryHudSession(capabilities: new HudSessionCapabilities(HudEventFamilies.All, HudCommandFamilies.None));
        using NativeHud hud = Open(session);
        hud.Dispatch(HudInput.SelectWorldEntity(9));

        HudDiff diff = hud.Advance(Frame(0));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.UnsupportedCommand);
    }

    [Fact]
    public void EntityCapacityIsBounded()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session, product: Product(maxEntities: 1));
        session.TryQueue(HudEvent.UnitChanged(new HudStamp(1, 1, 0), 1, Id("one"), 1, 1));
        session.TryQueue(HudEvent.UnitChanged(new HudStamp(1, 1, 1), 2, Id("two"), 1, 1));

        HudDiff diff = hud.Advance(Frame(0));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.EntityCapacityExceeded);
    }

    [Fact]
    public void DiffOverflowRequiresReadModelRefresh()
    {
        using NativeHud hud = Open(new InMemoryHudSession(), product: Product(maxChangesPerFrame: 51));

        HudDiff diff = hud.Advance(Frame(0));

        Assert.True(diff.RequiresFullRefresh);
        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.DiffOverflow);
        Assert.Equal(36, diff.ReadModel.ActionSlots.Length);
        Assert.Equal(HudRefreshAreas.All, diff.RequiredRefreshAreas);
    }

    [Fact]
    public void FullRefreshReadModelContainsCurrentProjectionDependentState()
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(9, new HudProjection(new HudPoint(500, 350), 2, true, false));
        using NativeHud hud = Open(session, world, Product(maxChangesPerFrame: 51));
        session.TryQueue(HudEvent.UnitChanged(Stamp(1), 9, Id("unit"), 8, 10));
        session.TryQueue(HudEvent.FeedbackRaised(new HudStamp(1, 1, 1), Id("hit"), HudFeedbackKind.Enemy, 9, 2));
        session.TryQueue(HudEvent.ChatReceived(new HudStamp(1, 1, 2),
            new HudChatMessage(Id("chat"), Id("say"), 9, Id("unit"), "hi", true)));
        hud.Dispatch(HudInput.RequestFocus(HudFocus.Chat));
        hud.Dispatch(HudInput.PointerEvent(
            HudInputKind.PointerMoved,
            Id("action-00"),
            new HudPoint(12, 13),
            HudPointerSource.Controller));

        HudDiff diff = hud.Advance(Frame(0));

        Assert.True(diff.RequiresFullRefresh);
        Assert.Equal(HudRefreshAreas.All, diff.RequiredRefreshAreas);
        Assert.Equal(1, diff.FrameRevision);
        Assert.Equal(1, diff.ReadModel.FrameRevision);
        Assert.Equal(Viewport, diff.ReadModel.Viewport);
        Assert.True(Assert.Single(diff.ReadModel.Units.ToArray(), item => item.EntityId == 9).OvertipVisible);
        Assert.True(Assert.Single(diff.ReadModel.Feedback.ToArray(), item => item.EventId == Id("hit")).Projected);
        Assert.True(Assert.Single(diff.ReadModel.Chat.ToArray(), item => item.EventId == Id("chat")).Projected);
        Assert.Equal(HudFocus.Chat, diff.ReadModel.Focus);
        Assert.Equal(Id("cursor-text"), diff.ReadModel.CursorId);
        Assert.Equal(HudPointerSource.Controller, diff.ReadModel.PointerSource);
        Assert.Equal(new HudPoint(12, 13), diff.ReadModel.Pointer);
    }

    [Fact]
    public void ClockRegressionIsReportedAndCannotRewindTimelines()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(1), Id("hit"), HudFeedbackKind.Enemy, 1, 1));
        hud.Advance(Frame(100));

        HudDiff diff = hud.Advance(Frame(50));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.ClockRegressed);
        Assert.Contains(diff.ReadModel.Feedback.ToArray(), item => item.Active);
    }

    [Fact]
    public void FeedbackPoolRecyclesOldestOfExactlyFiveAndChangesGeneration()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        for (int index = 0; index < 6; index++)
        {
            session.TryQueue(HudEvent.FeedbackRaised(
                new HudStamp(1, (ulong)(index + 1), 0),
                Id($"hit-{index}"),
                HudFeedbackKind.Enemy,
                9,
                index + 1));
        }

        HudDiff diff = hud.Advance(Frame(0));
        HudFeedbackView[] enemy = diff.ReadModel.Feedback.ToArray().Where(item => item.Kind == HudFeedbackKind.Enemy).ToArray();

        Assert.Equal(5, enemy.Length);
        Assert.DoesNotContain(enemy, item => item.EventId == Id("hit-0"));
        HudFeedbackView recycled = Assert.Single(enemy, item => item.EventId == Id("hit-5"));
        Assert.Equal(2, recycled.Generation);
    }

    [Fact]
    public void AuthoredFeedbackTimelinesControlVisibilityAndMovementLifetime()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(1), Id("avatar"), HudFeedbackKind.Avatar, 2, 5));
        hud.Advance(Frame(0));

        HudDiff hidden = hud.Advance(Frame(1410));
        HudFeedbackView avatar = Assert.Single(hidden.ReadModel.Feedback.ToArray(), item => item.EventId == Id("avatar"));
        Assert.True(avatar.Active);
        Assert.Contains(hidden.Changes.ToArray(), change => change.Kind == HudChangeKind.Feedback && !change.Visible);

        HudDiff expired = hud.Advance(Frame(1510));
        avatar = Assert.Single(expired.ReadModel.Feedback.ToArray(), item => item.EventId == Id("avatar"));
        Assert.False(avatar.Active);
    }

    [Theory]
    [InlineData(-1, 300, 1, true)]
    [InlineData(960, 540, -1, true)]
    [InlineData(960, 540, 1, false)]
    [InlineData(2500, 540, 1, true)]
    public void ProjectionHidesInvalidBehindOccludedAndOffscreenInsteadOfCentering(
        double x,
        double y,
        double depth,
        bool inFrustum)
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(3, new HudProjection(new HudPoint(x, y), depth, inFrustum, false));
        using NativeHud hud = Open(session, world);
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(1), Id("hit"), HudFeedbackKind.Enemy, 3, 4));

        HudDiff diff = hud.Advance(Frame(0));
        HudFeedbackView feedback = Assert.Single(diff.ReadModel.Feedback.ToArray(), item => item.EventId == Id("hit"));

        Assert.False(feedback.Projected);
        Assert.NotEqual(new HudPoint(960, 540), feedback.Position);
    }

    [Fact]
    public void ProjectionPublishesVisibleFinitePoint()
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(3, new HudProjection(new HudPoint(700, 400), 1, true, false));
        using NativeHud hud = Open(session, world);
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(1), Id("hit"), HudFeedbackKind.Enemy, 3, 4));

        HudDiff diff = hud.Advance(Frame(0));

        HudFeedbackView feedback = Assert.Single(diff.ReadModel.Feedback.ToArray(), item => item.EventId == Id("hit"));
        Assert.True(feedback.Projected);
        Assert.Equal(new HudPoint(700, 400), feedback.Position);
    }

    [Fact]
    public void HigherFocusWinsAndCursorFollowsFocus()
    {
        using NativeHud hud = Open(new InMemoryHudSession());
        hud.Dispatch(HudInput.RequestFocus(HudFocus.Modal));
        hud.Dispatch(HudInput.RequestFocus(HudFocus.Chat));
        HudDiff modal = hud.Advance(Frame(0));
        Assert.Equal(HudFocus.Modal, modal.ReadModel.Focus);

        hud.Dispatch(HudInput.RequestFocus(HudFocus.Drag));
        HudDiff drag = hud.Advance(Frame(1));
        Assert.Equal(HudFocus.Drag, drag.ReadModel.Focus);
        Assert.Equal(Id("cursor-drag"), drag.ReadModel.CursorId);

        hud.Dispatch(HudInput.Cancel());
        HudDiff cancelled = hud.Advance(Frame(2));
        Assert.Equal(HudFocus.World, cancelled.ReadModel.Focus);
        Assert.Equal(Id("cursor-default"), cancelled.ReadModel.CursorId);
    }

    [Fact]
    public void PointerMaskConsumptionAndSourceArbitrationStayInCore()
    {
        HudProduct product = Product(masked: [Id("action-00")]);
        using NativeHud hud = Open(new InMemoryHudSession(), product: product);
        HudInput transparent = HudInput.PointerEvent(
            HudInputKind.PointerPrimaryPressed,
            Id("action-00"),
            new HudPoint(4, 5),
            HudPointerSource.Mouse,
            0.1f,
            true);
        HudInput opaque = HudInput.PointerEvent(
            HudInputKind.PointerPrimaryPressed,
            Id("action-00"),
            new HudPoint(8, 9),
            HudPointerSource.Controller,
            0.9f,
            true);

        Assert.False(hud.Dispatch(transparent).Consumed);
        Assert.True(hud.Dispatch(opaque).Consumed);
        HudDiff diff = hud.Advance(Frame(0));
        Assert.Equal(HudPointerSource.Controller, diff.ReadModel.PointerSource);
        Assert.Equal(new HudPoint(8, 9), diff.ReadModel.Pointer);
        Assert.Equal(Id("cursor-hover"), diff.ReadModel.CursorId);
    }

    [Fact]
    public void ExplicitCancellationInvalidatesTimelineGeneration()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.FeedbackRaised(Stamp(1), Id("hit"), HudFeedbackKind.Experience, 4, 10));
        hud.Advance(Frame(0));
        int before = Assert.Single(hud.Advance(Frame(1)).ReadModel.Feedback.ToArray(), item => item.EventId == Id("hit")).Generation;
        session.TryQueue(HudEvent.FeedbackCancelled(Stamp(2), Id("hit")));

        HudDiff diff = hud.Advance(Frame(2));
        HudFeedbackView after = Assert.Single(diff.ReadModel.Feedback.ToArray(), item => item.EventId == Id("hit"));
        Assert.False(after.Active);
        Assert.True(after.Generation > before);
    }

    [Fact]
    public void QuestSnapshotReplacementIsAtomicAndUsesStableAuthoredRow()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        var first = new HudQuestSnapshot(Id("quest"), Id("quest-title"), false,
            [new HudQuestObjective(0, Id("objective-a"), 1, 3, true)]);
        var second = new HudQuestSnapshot(Id("quest"), Id("quest-title"), true,
            [
                new HudQuestObjective(0, Id("objective-a"), 3, 3, true),
                new HudQuestObjective(1, Id("objective-b"), 1, 1, false),
            ]);
        session.TryQueue(HudEvent.QuestTracked(Stamp(1), first));
        hud.Advance(Frame(0));
        session.TryQueue(HudEvent.QuestTracked(Stamp(2), second));

        HudDiff diff = hud.Advance(Frame(1));
        HudQuestView quest = Assert.Single(diff.ReadModel.Quests.ToArray(), item => item.Tracked);
        Assert.Equal(Id("quest-row-00"), quest.Element);
        Assert.True(quest.Completable);
        Assert.Equal(2, quest.Snapshot!.Objectives.Length);
    }

    [Fact]
    public void QuestTombstoneBlocksStaleReplayButReleasesAuthoredRow()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        var oldQuest = new HudQuestSnapshot(Id("old-quest"), Id("old-title"), false, []);
        var newQuest = new HudQuestSnapshot(Id("new-quest"), Id("new-title"), false, []);
        session.TryQueue(HudEvent.QuestTracked(Stamp(1), oldQuest));
        hud.Advance(Frame(0));
        session.TryQueue(HudEvent.QuestUntracked(Stamp(3), Id("old-quest")));
        hud.Advance(Frame(1));
        session.TryQueue(HudEvent.QuestTracked(Stamp(2), oldQuest));
        session.TryQueue(HudEvent.QuestTracked(Stamp(4), newQuest));

        HudDiff diff = hud.Advance(Frame(2));

        Assert.Contains(diff.Errors.ToArray(), error => error.Code == HudErrorCode.StaleAuthority);
        HudQuestView tracked = Assert.Single(diff.ReadModel.Quests.ToArray(), item => item.Tracked);
        Assert.Equal(Id("new-quest"), tracked.QuestId);
        Assert.Equal(Id("quest-row-00"), tracked.Element);
    }

    [Fact]
    public void WorldChatUsesSameHideNotCenterProjectionPolicy()
    {
        var session = new InMemoryHudSession();
        var world = new InMemoryHudWorld();
        world.SetProjection(7, new HudProjection(new HudPoint(4000, 300), 2, true, false));
        using NativeHud hud = Open(session, world);
        var message = new HudChatMessage(Id("chat-1"), Id("say"), 7, Id("sender"), "hello", true);
        session.TryQueue(HudEvent.ChatReceived(Stamp(1), message));

        HudDiff diff = hud.Advance(Frame(0));
        HudChatView chat = Assert.Single(diff.ReadModel.Chat.ToArray(), item => item.Active);
        Assert.False(chat.Projected);
        Assert.Equal(default, chat.Position);
    }

    [Fact]
    public void SteadyEmptyFramesAllocateNothing()
    {
        using NativeHud hud = Open(new InMemoryHudSession());
        hud.Advance(Frame(0));
        for (int index = 1; index < 32; index++)
        {
            hud.Advance(Frame(index));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 32; index < 1032; index++)
        {
            hud.Advance(Frame(index));
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void DisposeIsIdempotentAndRejectsFurtherUse()
    {
        NativeHud hud = Open(new InMemoryHudSession());
        hud.Dispose();
        hud.Dispose();

        Assert.Equal(HudDispatchStatus.Disposed, hud.Dispatch(HudInput.Cancel()).Status);
        Assert.Throws<ObjectDisposedException>(() => hud.Advance(Frame(0)));
    }

    [Fact]
    public void ContextProductPreservesRetailCensusesAndCharacterRoles()
    {
        HudContextProduct contexts = Product().Contexts;

        Assert.Equal([12, 16, 18, 24, 30, 36, 42, 48, 54, 60],
            contexts.Inventory.Layouts.ToArray().Select(layout => layout.Capacity));
        Assert.Equal(20, contexts.Loot.MaxEntries);
        Assert.Equal(4, contexts.Loot.PageSlots.Length);
        Assert.Equal(20, contexts.QuestLog.Entries.Length);
        Assert.Equal(3, contexts.QuestLog.Bookmarks.Length);
        Assert.Equal(15, contexts.QuestLog.SecretComponents.Length);
        Assert.Equal(20, contexts.QuestInfo.TalkOptions.Length);
        Assert.Equal(6, contexts.QuestInfo.Objectives.Length);
        Assert.Equal(21, contexts.Character.EquipmentSlots.Length);
        Assert.Equal(contexts.Character.EquipmentSlots[19], contexts.Character.BagSlot);
        Assert.Equal(contexts.Character.EquipmentSlots[20], contexts.Character.DeathInsuranceSlot);
        Assert.Equal(14, contexts.Character.StatRows.Length);

        Assert.Throws<ArgumentException>(() => new HudLootProduct(
            Id("loot"), Enumerable.Range(1, 4).Select(index => Id($"loot-{index}")).ToArray(), 19));
        Assert.Throws<ArgumentException>(() => new HudCharacterProduct(
            Id("character"), Enumerable.Range(1, 20).Select(index => Id($"slot-{index}")).ToArray(),
            Enumerable.Range(1, 14).Select(index => Id($"stat-{index}")).ToArray()));
    }

    [Fact]
    public void InventoryReplacementSelectsExactLayoutAndEmitsTypedCommands()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudItemStack?[] slots = new HudItemStack?[16];
        slots[2] = new HudItemStack(Id("item.sword"), 3, 2003, CounterValue: 7, Bound: true,
            IsQuestOperator: true);
        session.TryQueue(HudEvent.InventoryReplaced(
            Stamp(1), new HudInventorySnapshot(16, 42, new HudItemReference(Id("bag.16"), 2000), slots)));

        HudDiff replaced = hud.Advance(Frame(0));
        HudInventorySlotView view = replaced.ReadModel.Inventory.Slots[2];
        Assert.True(replaced.ReadModel.Inventory.HasAuthority);
        Assert.Equal(Id("multibag-16"), replaced.ReadModel.Inventory.LayoutElement);
        Assert.Equal(Id("item.sword"), view.ItemId);
        Assert.Equal(7, view.CounterValue);
        Assert.True(view.Bound);
        Assert.True(view.IsQuestOperator);

        hud.Dispatch(HudInput.MoveInventoryItem(2, 5, moveNoMore: true));
        hud.Dispatch(HudInput.DropInventoryItem(2, 2));
        hud.Dispatch(HudInput.UseInventoryItem(2));
        hud.Advance(Frame(1));

        Assert.True(session.TryReadCommand(out HudCommand move));
        Assert.Equal(HudCommandKind.MoveInventoryItem, move.Kind);
        Assert.Equal(2, move.Slot);
        Assert.Equal(5, move.Auxiliary);
        Assert.True(move.Flag);
        Assert.Equal(Stamp(1), move.ExpectedRevision);
        Assert.True(session.TryReadCommand(out HudCommand drop));
        Assert.Equal(HudCommandKind.DropInventoryItem, drop.Kind);
        Assert.Equal(2, drop.Count);
        Assert.True(session.TryReadCommand(out HudCommand use));
        Assert.Equal(HudCommandKind.UseInventoryItem, use.Kind);
    }

    [Fact]
    public void InventoryReplacementRejectsDuplicateOrEquippedBagInstanceIds()
    {
        HudItemStack?[] duplicate = new HudItemStack?[12];
        duplicate[0] = new HudItemStack(Id("item.a"), 1, 50);
        duplicate[1] = new HudItemStack(Id("item.b"), 1, 50);
        Assert.Throws<ArgumentException>(() => new HudInventorySnapshot(
            12, 0, new HudItemReference(Id("bag"), 40), duplicate));

        duplicate[1] = null;
        Assert.Throws<ArgumentException>(() => new HudInventorySnapshot(
            12, 0, new HudItemReference(Id("bag"), 50), duplicate));
    }

    [Fact]
    public void LootUsesFourVisibleRowsFivePagesAndAbsoluteEntryCommands()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudLootItem[] items = Enumerable.Range(0, 20)
            .Select(index => new HudLootItem(Id($"loot.{index:00}"), index + 1))
            .ToArray();
        session.TryQueue(HudEvent.LootReplaced(Stamp(1), new HudLootSnapshot(77, 10, items)));
        HudDiff opened = hud.Advance(Frame(0));

        Assert.Equal(5, opened.ReadModel.Loot.PageCount);
        Assert.Equal(4, opened.ReadModel.Loot.PageSlots.Length);
        hud.Dispatch(HudInput.LootNextPage());
        hud.Dispatch(HudInput.TakeLootItem(7));
        hud.Dispatch(HudInput.TakeLootMoney());
        hud.Dispatch(HudInput.TakeAllLoot());
        HudDiff next = hud.Advance(Frame(1));

        Assert.Equal(1, next.ReadModel.Loot.Page);
        Assert.Equal(4, next.ReadModel.Loot.PageSlots[0].Entry);
        Assert.True(session.TryReadCommand(out HudCommand item));
        Assert.Equal(HudCommandKind.TakeLootItem, item.Kind);
        Assert.Equal(7, item.Slot);
        Assert.True(session.TryReadCommand(out HudCommand money));
        Assert.Equal(HudCommandKind.TakeLootMoney, money.Kind);
        Assert.True(session.TryReadCommand(out HudCommand all));
        Assert.Equal(HudCommandKind.TakeAllLoot, all.Kind);
    }

    [Fact]
    public void QuestAbandonRequiresThirtySecondConfirmationAndTurnInCarriesRewardIndex()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudQuestDocument quest = Quest(Id("quest.one"), HudQuestClientState.Completable, canAbandon: true);
        session.TryQueue(HudEvent.QuestLogReplaced(Stamp(1), new HudQuestLogSnapshot([quest])));
        hud.Advance(Frame(100));
        hud.Dispatch(HudInput.AbandonQuest(quest.QuestId));
        HudDiff pending = hud.Advance(Frame(101));
        Assert.Equal(quest.QuestId, pending.ReadModel.QuestLog.PendingAbandonQuestId);
        Assert.Equal(30_101, pending.ReadModel.QuestLog.AbandonConfirmationExpiresAtMilliseconds);
        Assert.False(session.TryReadCommand(out _));

        hud.Dispatch(HudInput.ConfirmAbandonQuest(quest.QuestId));
        hud.Advance(Frame(102));
        Assert.True(session.TryReadCommand(out HudCommand abandon));
        Assert.Equal(HudCommandKind.AbandonQuest, abandon.Kind);

        var rewards = new HudQuestRewardSnapshot(
            1, 2, 3, [], [new HudRewardItem(Id("reward.a"), 1), new HudRewardItem(Id("reward.b"), 1)], [], []);
        session.TryQueue(HudEvent.QuestInfoReplaced(
            Stamp(2), new HudQuestInfoSnapshot(HudQuestInfoMode.TurnIn, quest, 88, rewards)));
        hud.Advance(Frame(103));
        hud.Dispatch(HudInput.SelectQuestReward(1));
        hud.Dispatch(HudInput.TurnInQuest());
        HudDiff selected = hud.Advance(Frame(104));
        Assert.Equal(1, selected.ReadModel.QuestInfo.SelectedRewardIndex);
        Assert.True(session.TryReadCommand(out HudCommand turnIn));
        Assert.Equal(HudCommandKind.TurnInQuest, turnIn.Kind);
        Assert.Equal(1, turnIn.Slot);
    }

    [Fact]
    public void QuestShareInvitationIsBoundedAndResponseCarriesLogRevision()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudQuestDocument quest = Quest(Id("quest.shared"), HudQuestClientState.InProgress);
        var invitation = new HudQuestShareInvitation(Id("share.1"), quest.QuestId, Id("sharer.name"));
        session.TryQueue(HudEvent.QuestLogReplaced(
            Stamp(3), new HudQuestLogSnapshot([quest], shareInvitation: invitation)));
        HudDiff offered = hud.Advance(Frame(500));
        Assert.Equal(invitation, offered.ReadModel.QuestLog.ShareInvitation);
        Assert.Equal(30_500, offered.ReadModel.QuestLog.ShareInvitationExpiresAtMilliseconds);

        hud.Dispatch(HudInput.AcceptSharedQuest(invitation.ShareId, invitation.QuestId));
        hud.Advance(Frame(501));
        Assert.True(session.TryReadCommand(out HudCommand command));
        Assert.Equal(HudCommandKind.AcceptSharedQuest, command.Kind);
        Assert.Equal(Stamp(3), command.ExpectedRevision);
    }

    [Fact]
    public void CharacterUsesTwentyOneNumberedSlotsAndKeepsMissingAuthorityExplicit()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        HudDiff initial = hud.Advance(Frame(0));
        Assert.False(initial.ReadModel.Character.HasAuthority);
        Assert.Equal(21, initial.ReadModel.Character.Equipment.Length);

        HudItemStack?[] equipment = new HudItemStack?[21];
        equipment[19] = new HudItemStack(Id("bag.item"), 1, 4001);
        equipment[20] = new HudItemStack(Id("death.insurance"), 1, 4002);
        HudCharacterStat[] stats = Enumerable.Range(1, 14)
            .Select(index => new HudCharacterStat(Id($"stat.{index:00}"), index, index + 1, index + 2))
            .ToArray();
        session.TryQueue(HudEvent.CharacterReplaced(
            Stamp(1), new HudCharacterSnapshot(Id("hero"), 7, equipment, stats)));
        HudDiff updated = hud.Advance(Frame(1));

        Assert.True(updated.ReadModel.Character.HasAuthority);
        Assert.Equal(HudCharacterEquipmentRole.Bag, updated.ReadModel.Character.Equipment[19].Role);
        Assert.Equal(HudCharacterEquipmentRole.DeathInsurance, updated.ReadModel.Character.Equipment[20].Role);
        Assert.Equal(Id("bag.item"), updated.ReadModel.Character.Bag.ItemId);
        Assert.Equal(14, updated.ReadModel.Character.Stats.Length);
    }

    [Fact]
    public void EscapeClosesTheLastOpenedContextAndSelectedTargetIsAuthoritative()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.TargetSelectionChanged(Stamp(1), 55));
        session.TryQueue(HudEvent.ActionSlotChanged(Stamp(1), 0, Id("ability.one"), 0, enabled: true));
        hud.Dispatch(HudInput.ToggleInventory());
        hud.Dispatch(HudInput.ToggleCharacter());
        HudDiff opened = hud.Advance(Frame(0));
        Assert.Equal(55UL, opened.ReadModel.SelectedTarget.EntityId);
        Assert.True(opened.ReadModel.SelectedTarget.HasAuthority);
        Assert.True(opened.ReadModel.ActionSlots[0].HasAuthority);
        Assert.True(opened.ReadModel.Inventory.Open);
        Assert.True(opened.ReadModel.Character.Open);

        hud.Dispatch(HudInput.Cancel());
        HudDiff escaped = hud.Advance(Frame(1));
        Assert.True(escaped.ReadModel.Inventory.Open);
        Assert.False(escaped.ReadModel.Character.Open);
        Assert.Equal(HudFocus.Hud, escaped.ReadModel.Focus);
    }

    [Fact]
    public void ActionCooldownDecaysFromReceiptAndSuppressesActivationUntilZero()
    {
        var session = new InMemoryHudSession();
        using NativeHud hud = Open(session);
        session.TryQueue(HudEvent.ActionSlotChanged(
            Stamp(1), 4, Id("ability.cooldown"), 100, enabled: true, cooldownDurationMilliseconds: 500));
        HudDiff received = hud.Advance(Frame(1_000));
        Assert.Equal(100, received.ReadModel.ActionSlots[4].CooldownMilliseconds);
        Assert.Equal(500, received.ReadModel.ActionSlots[4].CooldownDurationMilliseconds);

        hud.Dispatch(HudInput.ActivateAction(4));
        HudDiff halfway = hud.Advance(Frame(1_050));
        Assert.Equal(50, halfway.ReadModel.ActionSlots[4].CooldownMilliseconds);
        Assert.False(session.TryReadCommand(out _));

        hud.Dispatch(HudInput.ActivateAction(4));
        HudDiff ready = hud.Advance(Frame(1_101));
        Assert.Equal(0, ready.ReadModel.ActionSlots[4].CooldownMilliseconds);
        Assert.True(session.TryReadCommand(out HudCommand command));
        Assert.Equal(HudCommandKind.ActivateAction, command.Kind);
        Assert.Equal(4, command.Slot);
        Assert.True(command.Value.IsEmpty);
    }

    private static NativeHud Open(InMemoryHudSession session, InMemoryHudWorld? world = null, HudProduct? product = null) =>
        NativeHud.Open(product ?? Product(), session, world ?? new InMemoryHudWorld());

    private static HudProduct Product(
        int actionCount = 36,
        int feedbackCount = 5,
        int questCount = 20,
        int maxPendingInputs = 64,
        int maxEntities = 128,
        int unitPlateCount = 10,
        int? maxOvertips = null,
        int maxChangesPerFrame = 256,
        HudId[]? masked = null,
        HudTimelineCatalog? timelines = null)
    {
        HudId[] actions = Enumerable.Range(0, actionCount).Select(index => Id($"action-{index:00}")).ToArray();
        HudFeedbackPoolProduct[] pools = Enum.GetValues<HudFeedbackKind>()
            .Select(kind => new HudFeedbackPoolProduct(
                kind,
                Enumerable.Range(0, feedbackCount).Select(index => Id($"{kind.ToString().ToLowerInvariant()}-{index:00}")).ToArray()))
            .ToArray();
        HudId[] quests = Enumerable.Range(0, questCount).Select(index => Id($"quest-row-{index:00}")).ToArray();
        HudUnitPlateProduct[] allPlates =
        [
            new(new HudPlateAssignment(Id("avatar")), Id("plate-avatar")),
            new(new HudPlateAssignment(Id("target")), Id("plate-target")),
            new(new HudPlateAssignment(Id("target-target")), Id("plate-target-target")),
            new(new HudPlateAssignment(Id("pet")), Id("plate-pet")),
            new(new HudPlateAssignment(Id("mount")), Id("plate-mount")),
            new(new HudPlateAssignment(Id("party-01")), Id("plate-party-01")),
            new(new HudPlateAssignment(Id("party-02")), Id("plate-party-02")),
            new(new HudPlateAssignment(Id("party-03")), Id("plate-party-03")),
            new(new HudPlateAssignment(Id("party-04")), Id("plate-party-04")),
            new(new HudPlateAssignment(Id("party-05")), Id("plate-party-05")),
        ];
        HudUnitPlateProduct[] plates = allPlates.Take(unitPlateCount).ToArray();
        return new HudProduct(
            actions,
            pools,
            quests,
            plates,
            Id("overtip-prototype"),
            new HudCursorCatalog(Id("cursor-default"), Id("cursor-hover"), Id("cursor-text"), Id("cursor-drag")),
            timelines ?? HudTimelineCatalog.Retail,
            ContextProduct(),
            masked,
            maxEntities: maxEntities,
            maxOvertips: maxOvertips ?? Math.Min(4, maxEntities),
            maxPendingInputs: maxPendingInputs,
            maxChangesPerFrame: maxChangesPerFrame);
    }

    private static HudContextProduct ContextProduct()
    {
        int[][] partitions =
        [
            [12], [16], [12, 6], [16, 8], [30], [8, 8, 8, 6, 6], [30, 12],
            [12, 12, 12, 12], [30, 12, 12], [30, 30],
        ];
        int[] capacities = [12, 16, 18, 24, 30, 36, 42, 48, 54, 60];
        HudInventoryLayoutProduct[] layouts = capacities.Select((capacity, layoutIndex) =>
        {
            HudId[] slots = Enumerable.Range(1, capacity)
                .Select(index => Id($"multibag-{capacity}-slot-{index:00}"))
                .ToArray();
            int first = 0;
            HudInventoryPartitionProduct[] bags = partitions[layoutIndex].Select((count, bagIndex) =>
            {
                var bag = new HudInventoryPartitionProduct(Id($"multibag-{capacity}-partition-{bagIndex + 1:00}"), first, count);
                first += count;
                return bag;
            }).ToArray();
            return new HudInventoryLayoutProduct(Id($"multibag-{capacity}"), capacity, slots, bags);
        }).ToArray();

        static HudId[] Roles(string prefix, int count) => Enumerable.Range(1, count)
            .Select(index => Id($"{prefix}-{index:00}"))
            .ToArray();

        return new HudContextProduct(
            new HudInventoryProduct(Id("multibag"), layouts),
            new HudLootProduct(Id("loot-bag"), Roles("loot-item", HudProduct.LootPageSize)),
            new HudQuestLogProduct(
                Id("quest-log"), Roles("quest-log-row", 20), Roles("quest-log-bookmark", 3),
                Roles("quest-log-objective", 5), Roles("quest-log-choice", 5),
                Roles("quest-log-mandatory", 5), Roles("quest-log-reputation", 5),
                Roles("quest-log-currency", 5), Roles("quest-log-secret", 15)),
            new HudQuestInfoProduct(
                Id("quest-info"), Id("npc-talk"), Roles("quest-talk-option", 20), Roles("quest-info-objective", 6),
                Roles("quest-info-choice", 5), Roles("quest-info-mandatory", 5),
                Roles("quest-info-reputation", 5), Roles("quest-info-currency", 5)),
            new HudCharacterProduct(
                Id("character"), Roles("character-equipment", 21), Roles("character-stat", 14)));
    }

    private static HudFrame Frame(long now) => new(now, Viewport);

    private static HudQuestDocument Quest(HudId questId, HudQuestClientState state, bool canAbandon = false) =>
        new(questId, Id($"{questId.Value}.title"), Id($"{questId.Value}.description"), state, canAbandon,
            [new HudQuestObjective(0, Id($"{questId.Value}.objective"), 1, 1, true)]);

    private static HudStamp Stamp(ulong revision) => new(1, revision, 0);

    private static HudId Id(string value) => new(value);
}
