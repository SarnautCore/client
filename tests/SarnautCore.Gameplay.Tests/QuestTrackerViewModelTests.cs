using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class QuestTrackerViewModelTests
{
    [Fact]
    public void Tracker_follows_accept_progress_complete_and_turn_in()
    {
        var tracker = new QuestTrackerViewModel();

        tracker.Apply(Update(QuestClientState.Accepted, 0));
        Assert.Single(tracker.Quests);
        Assert.Equal(0, tracker.Quests[0].Objectives[0].Current);

        tracker.Apply(Update(QuestClientState.InProgress, 2));
        Assert.Equal(2, tracker.Quests[0].Objectives[0].Current);
        Assert.False(tracker.Quests[0].Complete);

        tracker.Apply(Update(QuestClientState.Completable, 3));
        Assert.True(tracker.Quests[0].Complete);

        tracker.Apply(Update(QuestClientState.TurnedIn, 3));
        Assert.Empty(tracker.Quests);
    }

    private static QuestUpdate Update(QuestClientState state, int current) => new(
        "quest.overlay.m2.slay-earth-elementals",
        "quest.overlay.m2.slay-earth-elementals.title",
        "quest.overlay.m2.slay-earth-elementals.description",
        state,
        [new QuestObjectiveUpdate("quest.kill-earth-elementals", current, 3, true, false)]);
}
