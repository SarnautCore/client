using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

/// <summary>Maps the owning client's authoritative quest update into HUD values.</summary>
public sealed class QuestStateUpdateAdapter : IQuestStateUpdateAdapter
{
    public bool TryMap(QuestStateUpdate message, out QuestUpdate update)
    {
        ArgumentNullException.ThrowIfNull(message);
        update = null!;
        if (string.IsNullOrWhiteSpace(message.QuestId)
            || !TryMapState(message.State, out QuestClientState state)
            || !TryMapRefusal(message.Refusal, out QuestClientRefusal refusal))
        {
            return false;
        }

        QuestObjectiveUpdate[] objectives = message.Objectives
            .Select(objective => new QuestObjectiveUpdate(
                string.IsNullOrWhiteSpace(objective.CounterKey)
                    ? $"{message.QuestId}.objective.{objective.Index}"
                    : objective.CounterKey,
                objective.Counter,
                objective.Limit,
                objective.ShowCount,
                Internal: false,
                objective.Index))
            .ToArray();
        QuestRewardItemUpdate[] items = message.Items
            .Select(item => new QuestRewardItemUpdate(item.ItemId, item.Count))
            .ToArray();

        update = new QuestUpdate(
            message.QuestId,
            $"{message.QuestId}.title",
            $"{message.QuestId}.description",
            state,
            objectives,
            Refusal: refusal,
            Reward: new QuestRewardUpdate(message.Experience, message.Money, message.Honor, items));
        return true;
    }

    private static bool TryMapState(QuestState state, out QuestClientState mapped)
    {
        mapped = state switch
        {
            QuestState.Unavailable => QuestClientState.Unavailable,
            QuestState.Offered => QuestClientState.Offered,
            QuestState.Accepted => QuestClientState.Accepted,
            QuestState.InProgress => QuestClientState.InProgress,
            QuestState.Completable => QuestClientState.Completable,
            QuestState.TurnedIn => QuestClientState.TurnedIn,
            QuestState.Abandoned => QuestClientState.Abandoned,
            _ => default,
        };
        return state is QuestState.Unavailable
            or QuestState.Offered
            or QuestState.Accepted
            or QuestState.InProgress
            or QuestState.Completable
            or QuestState.TurnedIn
            or QuestState.Abandoned;
    }

    private static bool TryMapRefusal(QuestRefusal refusal, out QuestClientRefusal mapped)
    {
        mapped = refusal switch
        {
            QuestRefusal.None => QuestClientRefusal.None,
            QuestRefusal.UnknownQuest => QuestClientRefusal.UnknownQuest,
            QuestRefusal.Unavailable => QuestClientRefusal.Unavailable,
            QuestRefusal.LogFull => QuestClientRefusal.LogFull,
            QuestRefusal.OutOfRange => QuestClientRefusal.OutOfRange,
            QuestRefusal.WrongNpc => QuestClientRefusal.WrongNpc,
            QuestRefusal.NotComplete => QuestClientRefusal.NotComplete,
            QuestRefusal.AlreadyComplete => QuestClientRefusal.AlreadyComplete,
            QuestRefusal.BagFull => QuestClientRefusal.BagFull,
            QuestRefusal.CannotCancel => QuestClientRefusal.CannotCancel,
            QuestRefusal.Internal => QuestClientRefusal.Internal,
            _ => default,
        };
        return refusal is QuestRefusal.None
            or QuestRefusal.UnknownQuest
            or QuestRefusal.Unavailable
            or QuestRefusal.LogFull
            or QuestRefusal.OutOfRange
            or QuestRefusal.WrongNpc
            or QuestRefusal.NotComplete
            or QuestRefusal.AlreadyComplete
            or QuestRefusal.BagFull
            or QuestRefusal.CannotCancel
            or QuestRefusal.Internal;
    }
}
