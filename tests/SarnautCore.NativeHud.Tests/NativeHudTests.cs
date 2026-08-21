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
        HudEvent item = HudEvent.ActionSlotChanged(Stamp(1), 0, Id("ability"), 10);
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
        Assert.Equal(Id("slash"), command.Value);
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

    private static NativeHud Open(InMemoryHudSession session, InMemoryHudWorld? world = null, HudProduct? product = null) =>
        NativeHud.Open(product ?? Product(), session, world ?? new InMemoryHudWorld());

    private static HudProduct Product(
        int actionCount = 36,
        int feedbackCount = 5,
        int questCount = 20,
        int maxPendingInputs = 64,
        int maxEntities = 128,
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
        return new HudProduct(
            actions,
            pools,
            quests,
            new HudCursorCatalog(Id("cursor-default"), Id("cursor-hover"), Id("cursor-text"), Id("cursor-drag")),
            timelines ?? HudTimelineCatalog.Retail,
            masked,
            maxEntities: maxEntities,
            maxPendingInputs: maxPendingInputs,
            maxChangesPerFrame: maxChangesPerFrame);
    }

    private static HudFrame Frame(long now) => new(now, Viewport);

    private static HudStamp Stamp(ulong revision) => new(1, revision, 0);

    private static HudId Id(string value) => new(value);
}
