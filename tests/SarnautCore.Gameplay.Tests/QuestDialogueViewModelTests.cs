using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class QuestDialogueViewModelTests
{
    [Fact]
    public void Dialogue_offers_accepts_and_turns_in_authoritatively()
    {
        var dialogue = new QuestDialogueViewModel();
        QuestCommandRequest? accepted = null;
        QuestCommandRequest? turnedIn = null;
        dialogue.AcceptRequested += request => accepted = request;
        dialogue.TurnInRequested += request => turnedIn = request;

        QuestUpdate offer = Update(QuestClientState.Offered, starter: 70, finisher: 71);
        Assert.True(dialogue.ShowOffer(offer, 70));
        Assert.Equal(QuestDialogueMode.Offer, dialogue.Mode);
        Assert.True(dialogue.RequestAccept());
        Assert.Equal(new QuestCommandRequest(offer.QuestId, 70), accepted);

        dialogue.Apply(Update(QuestClientState.Accepted, 70, 71));
        Assert.False(dialogue.IsOpen);

        QuestUpdate complete = Update(QuestClientState.Completable, 70, 71);
        Assert.True(dialogue.ShowTurnIn(complete, 71));
        Assert.True(dialogue.RequestTurnIn());
        Assert.Equal(new QuestCommandRequest(offer.QuestId, 71), turnedIn);

        dialogue.Apply(Update(QuestClientState.TurnedIn, 70, 71));
        Assert.False(dialogue.IsOpen);
    }

    private static QuestUpdate Update(QuestClientState state, ulong starter, ulong finisher) => new(
        "quest.overlay.m2.slay-earth-elementals",
        "quest.overlay.m2.slay-earth-elementals.title",
        "quest.overlay.m2.slay-earth-elementals.description",
        state,
        [],
        starter,
        finisher);
}
