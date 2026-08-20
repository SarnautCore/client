namespace SarnautCore.Gameplay;

public enum QuestDialogueMode
{
    None,
    Offer,
    TurnIn,
}

public sealed record QuestCommandRequest(string QuestId, ulong NpcEntityId);

/// <summary>One NPC quest offer or turn-in conversation.</summary>
public sealed class QuestDialogueViewModel
{
    public QuestDialogueMode Mode { get; private set; }

    public QuestUpdate? Quest { get; private set; }

    public ulong NpcEntityId { get; private set; }

    public bool IsOpen => Mode != QuestDialogueMode.None;

    public event Action? Changed;

    public event Action<QuestCommandRequest>? AcceptRequested;

    public event Action<QuestCommandRequest>? TurnInRequested;

    public event Action? Closed;

    public bool ShowOffer(QuestUpdate quest, ulong starterEntityId)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (quest.State != QuestClientState.Offered || starterEntityId == 0)
        {
            return false;
        }

        Show(QuestDialogueMode.Offer, quest, starterEntityId);
        return true;
    }

    public bool ShowTurnIn(QuestUpdate quest, ulong finisherEntityId)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (quest.State != QuestClientState.Completable || finisherEntityId == 0)
        {
            return false;
        }

        Show(QuestDialogueMode.TurnIn, quest, finisherEntityId);
        return true;
    }

    public bool RequestAccept()
    {
        if (Mode != QuestDialogueMode.Offer || Quest is null)
        {
            return false;
        }

        AcceptRequested?.Invoke(new QuestCommandRequest(Quest.QuestId, NpcEntityId));
        return true;
    }

    public bool RequestTurnIn()
    {
        if (Mode != QuestDialogueMode.TurnIn || Quest is null)
        {
            return false;
        }

        TurnInRequested?.Invoke(new QuestCommandRequest(Quest.QuestId, NpcEntityId));
        return true;
    }

    public void Apply(QuestUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (Quest?.QuestId != update.QuestId)
        {
            return;
        }

        Quest = update;
        bool acceptedOffer = Mode == QuestDialogueMode.Offer
            && update.State is QuestClientState.Accepted or QuestClientState.InProgress or QuestClientState.Completable;
        bool finishedTurnIn = Mode == QuestDialogueMode.TurnIn && update.State == QuestClientState.TurnedIn;
        if (acceptedOffer || finishedTurnIn || update.State == QuestClientState.Abandoned)
        {
            Close();
            return;
        }

        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Mode = QuestDialogueMode.None;
        Quest = null;
        NpcEntityId = 0;
        Changed?.Invoke();
        Closed?.Invoke();
    }

    private void Show(QuestDialogueMode mode, QuestUpdate quest, ulong npcEntityId)
    {
        Mode = mode;
        Quest = quest;
        NpcEntityId = npcEntityId;
        Changed?.Invoke();
    }
}
