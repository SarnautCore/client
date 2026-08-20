namespace SarnautCore.Gameplay;

public enum QuestClientState
{
    Unavailable,
    Offered,
    Accepted,
    InProgress,
    Completable,
    TurnedIn,
    Abandoned,
}

public sealed record QuestObjectiveUpdate(
    string TextKey,
    int Current,
    int Limit,
    bool ShowCount,
    bool Internal);

public sealed record QuestUpdate(
    string QuestId,
    string TitleKey,
    string DescriptionKey,
    QuestClientState State,
    IReadOnlyList<QuestObjectiveUpdate> Objectives,
    ulong StarterEntityId = 0,
    ulong FinisherEntityId = 0,
    bool CanAbandon = false);

public sealed record QuestTrackerEntry(
    string QuestId,
    string TitleKey,
    IReadOnlyList<QuestObjectiveUpdate> Objectives,
    bool Complete);

/// <summary>The compact list of active quest objectives shown during play.</summary>
public sealed class QuestTrackerViewModel
{
    private readonly List<QuestTrackerEntry> _quests = [];

    public IReadOnlyList<QuestTrackerEntry> Quests => _quests;

    public event Action? Changed;

    public event Action<string>? QuestCompleted;

    public event Action<string>? QuestTurnedIn;

    public void Apply(QuestUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        int index = _quests.FindIndex(quest => quest.QuestId == update.QuestId);
        if (update.State is QuestClientState.TurnedIn or QuestClientState.Abandoned
            or QuestClientState.Unavailable or QuestClientState.Offered)
        {
            if (index >= 0)
            {
                _quests.RemoveAt(index);
                Changed?.Invoke();
            }

            if (update.State == QuestClientState.TurnedIn)
            {
                QuestTurnedIn?.Invoke(update.QuestId);
            }

            return;
        }

        bool wasComplete = index >= 0 && _quests[index].Complete;
        var entry = new QuestTrackerEntry(
            update.QuestId,
            update.TitleKey,
            update.Objectives.Where(objective => !objective.Internal).ToArray(),
            update.State == QuestClientState.Completable);
        if (index >= 0)
        {
            _quests[index] = entry;
        }
        else
        {
            _quests.Add(entry);
        }

        Changed?.Invoke();
        if (!wasComplete && entry.Complete)
        {
            QuestCompleted?.Invoke(entry.QuestId);
        }
    }
}
