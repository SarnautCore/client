using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using ProtocolChannel = Sarnaut.Protocol.V1.ChatChannel;
using ProtocolRejectionReason = Sarnaut.Protocol.V1.ChatRejectionReason;

namespace SarnautCore.Network;

public static class ChatProtocolMapper
{
    public static ChatSendRequest ToRequest(ulong requestId, HudChatSubmission submission)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(submission);
        submission.Validate();
        var request = new ChatSendRequest
        {
            RequestId = requestId,
            Channel = ToProtocol(submission.Channel),
            Text = submission.Text,
        };
        switch (submission.Target)
        {
            case HudChatTarget.NoTarget:
                break;
            case HudChatTarget.WhisperCharacterName target:
                request.WhisperCharacterName = target.Value;
                break;
            default:
                throw new ArgumentException("The chat submission target cannot be represented on the supported wire.", nameof(submission));
        }

        return request;
    }

    public static HudChatMessage ProjectLocal(
        ulong requestId,
        HudChatSubmission submission,
        long sentAtUnixMilliseconds,
        ulong senderEntityId,
        string senderName,
        bool senderAlive,
        HudChatAntiSpamCatalog antiSpam,
        IEnumerable<string> friendNames)
    {
        ArgumentNullException.ThrowIfNull(antiSpam);
        ChatSendRequest request = ToRequest(requestId, submission);
        int spamWeight = antiSpam.Score(submission.Channel, submission.Text, senderName, friendNames);
        HudChatContext context = submission.Target switch
        {
            HudChatTarget.NoTarget => HudChatContext.None,
            HudChatTarget.WhisperCharacterName target => new HudChatContext.WhisperPeerName(target.Value),
            _ => throw new ArgumentException("The local chat target is unsupported.", nameof(submission)),
        };
        return new HudChatMessage(
            new HudId($"chat-local-{request.RequestId}"),
            request.RequestId,
            submission.Channel,
            sentAtUnixMilliseconds,
            senderEntityId,
            senderName,
            senderAlive,
            new HudChatBody.UserText(submission.Text),
            context,
            spamWeight,
            true,
            true).Validate();
    }

    public static HudChatMessage FromDelivery(
        ChatDelivery delivery,
        bool senderIsPlayer,
        HudChatAntiSpamCatalog antiSpam,
        IEnumerable<string> friendNames)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(antiSpam);
        if (delivery.MessageId == 0 || delivery.RequestId != 0 || delivery.SenderEntityId == 0 ||
            string.IsNullOrEmpty(delivery.SenderName) || delivery.SentAtUnixMilliseconds < 0 || delivery.Body is null)
        {
            throw new ArgumentException("Remote chat delivery violates the closed delivery contract.", nameof(delivery));
        }

        HudChatChannel channel = FromProtocol(delivery.Channel);
        HudChatBody body = FromBody(delivery.Body);
        HudChatContext context = delivery.ContextCase switch
        {
            ChatDelivery.ContextOneofCase.None => HudChatContext.None,
            ChatDelivery.ContextOneofCase.WhisperPeerName =>
                new HudChatContext.WhisperPeerName(delivery.WhisperPeerName),
            ChatDelivery.ContextOneofCase.NamedChannel => new HudChatContext.NamedChannel(delivery.NamedChannel),
            _ => throw new ArgumentException("Remote chat context is unsupported.", nameof(delivery)),
        };
        int spamWeight = body is HudChatBody.UserText text
            ? antiSpam.Score(channel, text.Value, delivery.SenderName, friendNames)
            : 0;
        return new HudChatMessage(
            new HudId($"chat-remote-{delivery.MessageId}"),
            0,
            channel,
            delivery.SentAtUnixMilliseconds,
            delivery.SenderEntityId,
            delivery.SenderName,
            delivery.SenderAlive,
            body,
            context,
            spamWeight,
            senderIsPlayer,
            false).Validate();
    }

    public static HudChatRejection FromRejection(ChatRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        HudChatBody.Localized? detail = rejection.Detail is null
            ? null
            : new HudChatBody.Localized(
                new HudId(rejection.Detail.ProductLocalizationId),
                rejection.Detail.Arguments);
        return new HudChatRejection(
            rejection.RequestId,
            FromProtocol(rejection.Channel),
            rejection.Reason switch
            {
                ProtocolRejectionReason.Mute => HudChatRejectionReason.Mute,
                ProtocolRejectionReason.InternalError => HudChatRejectionReason.InternalError,
                ProtocolRejectionReason.Silence => HudChatRejectionReason.Silence,
                ProtocolRejectionReason.NoPoints => HudChatRejectionReason.NoPoints,
                ProtocolRejectionReason.EnemyFaction => HudChatRejectionReason.EnemyFaction,
                ProtocolRejectionReason.Ignored => HudChatRejectionReason.Ignored,
                ProtocolRejectionReason.Dead => HudChatRejectionReason.Dead,
                ProtocolRejectionReason.NotPsionic => HudChatRejectionReason.NotPsionic,
                ProtocolRejectionReason.TargetNotFound => HudChatRejectionReason.TargetNotFound,
                ProtocolRejectionReason.TargetOffline => HudChatRejectionReason.TargetOffline,
                ProtocolRejectionReason.RateLimited => HudChatRejectionReason.RateLimited,
                ProtocolRejectionReason.TooLong => HudChatRejectionReason.TooLong,
                ProtocolRejectionReason.NotMember => HudChatRejectionReason.NotMember,
                ProtocolRejectionReason.NotAuthorized => HudChatRejectionReason.NotAuthorized,
                ProtocolRejectionReason.UnsupportedChannel => HudChatRejectionReason.UnsupportedChannel,
                ProtocolRejectionReason.Empty => HudChatRejectionReason.Empty,
                _ => throw new ArgumentException("Chat rejection reason is unsupported.", nameof(rejection)),
            },
            detail,
            checked((int)rejection.RetryAfterMilliseconds)).Validate();
    }

    public static ProtocolChannel ToProtocol(HudChatChannel channel) => channel switch
    {
        HudChatChannel.Whisper => ProtocolChannel.Whisper,
        HudChatChannel.Party => ProtocolChannel.Group,
        HudChatChannel.Say => ProtocolChannel.Say,
        HudChatChannel.Zone => ProtocolChannel.Zone,
        HudChatChannel.ZoneSpecial => ProtocolChannel.ZoneSpecial,
        HudChatChannel.World => ProtocolChannel.World,
        HudChatChannel.Guild => ProtocolChannel.Guild,
        HudChatChannel.GuildOfficer => ProtocolChannel.GuildOfficer,
        HudChatChannel.Raid => ProtocolChannel.Raid,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public static HudChatChannel FromProtocol(ProtocolChannel channel) => channel switch
    {
        ProtocolChannel.Whisper => HudChatChannel.Whisper,
        ProtocolChannel.Group => HudChatChannel.Party,
        ProtocolChannel.Say => HudChatChannel.Say,
        ProtocolChannel.Zone => HudChatChannel.Zone,
        ProtocolChannel.ZoneSpecial => HudChatChannel.ZoneSpecial,
        ProtocolChannel.World => HudChatChannel.World,
        ProtocolChannel.Guild => HudChatChannel.Guild,
        ProtocolChannel.GuildOfficer => HudChatChannel.GuildOfficer,
        ProtocolChannel.Raid => HudChatChannel.Raid,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    private static HudChatBody FromBody(ChatBody body) => body.ValueCase switch
    {
        ChatBody.ValueOneofCase.UserText => new HudChatBody.UserText(body.UserText),
        ChatBody.ValueOneofCase.Localized => new HudChatBody.Localized(
            new HudId(body.Localized.ProductLocalizationId),
            body.Localized.Arguments),
        ChatBody.ValueOneofCase.UnreadableFaction => new HudChatBody.UnreadableFaction(
            new HudId(body.UnreadableFaction.FactionNameLocalizationId)),
        _ => throw new ArgumentException("Chat delivery body is missing or unsupported.", nameof(body)),
    };
}
