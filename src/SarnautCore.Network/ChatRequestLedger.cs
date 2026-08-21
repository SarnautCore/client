using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;

namespace SarnautCore.Network;

public readonly record struct ChatOutbound(
    ChatSendRequest Request,
    HudChatMessage LocalProjection);

/// <summary>Bounded authored-send correlation and retail no-bounce policy for one admitted avatar.</summary>
public sealed class ChatRequestLedger
{
    private readonly Entry[] _entries;
    private readonly ulong _ownEntityId;
    private readonly string _ownName;
    private int _cursor;
    private ulong _nextRequestId = 1;

    public ChatRequestLedger(ulong ownEntityId, string ownName, int capacity = 64)
    {
        if (ownEntityId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownEntityId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ownName);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ownEntityId = ownEntityId;
        _ownName = ownName;
        _entries = new Entry[capacity];
    }

    public ChatOutbound CreateOutbound(
        HudChatSubmission submission,
        long sentAtUnixMilliseconds,
        bool senderAlive,
        HudChatAntiSpamCatalog antiSpam,
        IEnumerable<string> friendNames)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ulong requestId = AllocateRequestId();
        ChatSendRequest request = ChatProtocolMapper.ToRequest(requestId, submission);
        HudChatMessage local = ChatProtocolMapper.ProjectLocal(
            requestId,
            submission,
            sentAtUnixMilliseconds,
            _ownEntityId,
            _ownName,
            senderAlive,
            antiSpam,
            friendNames);
        _entries[_cursor] = new Entry(
            true,
            requestId,
            submission.Channel,
            submission.Text,
            submission.Target is HudChatTarget.WhisperCharacterName target ? target.Value : null);
        _cursor = (_cursor + 1) % _entries.Length;
        return new ChatOutbound(request, local);
    }

    public bool TryCorrelateRejection(ChatRejection rejection, out HudChatRejection mapped)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        int index = FindRequest(rejection.RequestId);
        if (index < 0)
        {
            mapped = null!;
            return false;
        }

        mapped = ChatProtocolMapper.FromRejection(rejection);
        if (_entries[index].Channel != mapped.Channel)
        {
            mapped = null!;
            return false;
        }

        _entries[index] = default;
        return true;
    }

    /// <summary>True only for a remote delivery that is not a prohibited sender bounce.</summary>
    public bool AcceptRemoteDelivery(ChatDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.SenderEntityId != _ownEntityId)
        {
            return true;
        }

        if (delivery.Channel != ChatChannel.Whisper ||
            delivery.ContextCase != ChatDelivery.ContextOneofCase.WhisperPeerName ||
            !string.Equals(delivery.SenderName, _ownName, StringComparison.Ordinal) ||
            !string.Equals(delivery.WhisperPeerName, _ownName, StringComparison.Ordinal) ||
            delivery.Body?.ValueCase != ChatBody.ValueOneofCase.UserText)
        {
            return false;
        }

        for (int index = 0; index < _entries.Length; index++)
        {
            ref Entry entry = ref _entries[index];
            if (!entry.Occupied || entry.Channel != HudChatChannel.Whisper ||
                !string.Equals(entry.Target, _ownName, StringComparison.Ordinal) ||
                !string.Equals(entry.Text, delivery.Body.UserText, StringComparison.Ordinal))
            {
                continue;
            }

            entry = default;
            return true;
        }

        return false;
    }

    private ulong AllocateRequestId()
    {
        ulong allocated = _nextRequestId++;
        if (_nextRequestId == 0)
        {
            _nextRequestId = 1;
        }

        return allocated;
    }

    private int FindRequest(ulong requestId)
    {
        if (requestId == 0)
        {
            return -1;
        }

        for (int index = 0; index < _entries.Length; index++)
        {
            if (_entries[index].Occupied && _entries[index].RequestId == requestId)
            {
                return index;
            }
        }

        return -1;
    }

    private record struct Entry(
        bool Occupied,
        ulong RequestId,
        HudChatChannel Channel,
        string Text,
        string? Target);
}
