using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class QuestStateUpdateAdapterTests
{
    [Fact]
    public void MapsStateObjectivesRefusalAndRewardsFromTheWirePayload()
    {
        var message = new QuestStateUpdate
        {
            QuestId = "quest.paper-harbor.tide-tally",
            State = QuestState.TurnedIn,
            Refusal = QuestRefusal.None,
            Experience = 8,
            Money = 2,
            Honor = 1,
        };
        message.Objectives.Add(new QuestObjectiveProgress
        {
            Index = 4,
            Counter = 3,
            Limit = 3,
            ShowCount = true,
            CounterKey = "quest.paper-harbor.tide-tally.crabs",
        });
        message.Items.Add(new LootItem { ItemId = "item.paper-harbor.reward", Count = 2 });

        Assert.True(new QuestStateUpdateAdapter().TryMap(message, out QuestUpdate update));

        Assert.Equal("quest.paper-harbor.tide-tally.title", update.TitleKey);
        Assert.Equal(QuestClientState.TurnedIn, update.State);
        Assert.Equal(QuestClientRefusal.None, update.Refusal);
        QuestObjectiveUpdate objective = Assert.Single(update.Objectives);
        Assert.Equal((uint)4, objective.Index);
        Assert.Equal(3, objective.Current);
        Assert.Equal("quest.paper-harbor.tide-tally.crabs", objective.TextKey);
        Assert.False(objective.Internal);
        Assert.Equal(8, update.Reward!.Experience);
        Assert.Equal(2, update.Reward.Money);
        Assert.Equal(1, update.Reward.Honor);
        Assert.Equal(new QuestRewardItemUpdate("item.paper-harbor.reward", 2), Assert.Single(update.Reward.Items));
    }

    [Fact]
    public void RefusesUnsetOrUnknownWireValues()
    {
        var adapter = new QuestStateUpdateAdapter();

        Assert.False(adapter.TryMap(new QuestStateUpdate
        {
            QuestId = "quest.test",
            State = QuestState.Unspecified,
            Refusal = QuestRefusal.None,
        }, out _));
        Assert.False(adapter.TryMap(new QuestStateUpdate
        {
            QuestId = "quest.test",
            State = (QuestState)99,
            Refusal = QuestRefusal.None,
        }, out _));
        Assert.False(adapter.TryMap(new QuestStateUpdate
        {
            QuestId = "quest.test",
            State = QuestState.Offered,
            Refusal = (QuestRefusal)99,
        }, out _));
    }
}
