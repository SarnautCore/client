using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class QuestLogViewModelTests
{
    [Fact]
    public void Active_quest_is_selectable_and_turn_in_removes_it()
    {
        var log = new QuestLogViewModel();
        string abandoned = string.Empty;
        log.AbandonRequested += questId => abandoned = questId;
        QuestUpdate accepted = Update(QuestClientState.Accepted);

        log.Apply(accepted);
        Assert.True(log.Select(accepted.QuestId));
        Assert.Equal(accepted.QuestId, log.SelectedQuest!.QuestId);
        Assert.True(log.RequestAbandonSelected());
        Assert.Equal(accepted.QuestId, abandoned);

        log.Apply(Update(QuestClientState.TurnedIn));

        Assert.Empty(log.Quests);
        Assert.Null(log.SelectedQuest);
    }

    private static QuestUpdate Update(QuestClientState state) => new(
        "quest.overlay.m2.slay-earth-elementals",
        "quest.overlay.m2.slay-earth-elementals.title",
        "quest.overlay.m2.slay-earth-elementals.description",
        state,
        [],
        CanAbandon: true);
}
