namespace SarnautCore.Gameplay;

public sealed record QuestLogEntry(
    string QuestId,
    string TitleKey,
    string DescriptionKey,
    QuestClientState State,
    IReadOnlyList<QuestObjectiveUpdate> Objectives,
    bool CanAbandon);

/// <summary>The active quest log, selection, and abandon command.</summary>
public sealed class QuestLogViewModel
{
    private readonly List<QuestLogEntry> _quests = [];

    public QuestLogViewModel(int capacity = 25)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public IReadOnlyList<QuestLogEntry> Quests => _quests;

    public QuestLogEntry? SelectedQuest { get; private set; }

    public event Action? Changed;

    public event Action<string>? AbandonRequested;

    public void Apply(QuestUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        int index = _quests.FindIndex(quest => quest.QuestId == update.QuestId);
        if (update.State is QuestClientState.TurnedIn or QuestClientState.Abandoned
            or QuestClientState.Unavailable or QuestClientState.Offered)
        {
            if (index < 0)
            {
                return;
            }

            bool selected = SelectedQuest?.QuestId == update.QuestId;
            _quests.RemoveAt(index);
            if (selected)
            {
                SelectedQuest = null;
            }

            Changed?.Invoke();
            return;
        }

        var entry = new QuestLogEntry(
            update.QuestId,
            update.TitleKey,
            update.DescriptionKey,
            update.State,
            update.Objectives.Where(objective => !objective.Internal).ToArray(),
            update.CanAbandon);
        if (index >= 0)
        {
            _quests[index] = entry;
        }
        else if (_quests.Count < Capacity)
        {
            _quests.Add(entry);
        }
        else
        {
            return;
        }

        if (SelectedQuest?.QuestId == update.QuestId)
        {
            SelectedQuest = entry;
        }

        Changed?.Invoke();
    }

    public bool Select(string questId)
    {
        QuestLogEntry? entry = _quests.FirstOrDefault(quest => quest.QuestId == questId);
        if (entry is null)
        {
            return false;
        }

        SelectedQuest = entry;
        Changed?.Invoke();
        return true;
    }

    public bool RequestAbandonSelected()
    {
        if (SelectedQuest is not { CanAbandon: true } selected)
        {
            return false;
        }

        AbandonRequested?.Invoke(selected.QuestId);
        return true;
    }
}
