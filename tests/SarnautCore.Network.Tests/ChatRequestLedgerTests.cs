using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class ChatRequestLedgerTests
{
    [Fact]
    public void OutboundIdsAreMonotonicAndEverySendProjectsLocally()
    {
        var ledger = new ChatRequestLedger(7, "Avatar");

        ChatOutbound first = ledger.CreateOutbound(
            new HudChatSubmission(HudChatChannel.Say, "one"), 1, true, AntiSpam(), []);
        ChatOutbound second = ledger.CreateOutbound(
            new HudChatSubmission(HudChatChannel.World, "two"), 2, true, AntiSpam(), []);

        Assert.Equal((ulong)1, first.Request.RequestId);
        Assert.Equal((ulong)2, second.Request.RequestId);
        Assert.Equal(first.Request.RequestId, first.LocalProjection.RequestId);
        Assert.Equal(second.Request.RequestId, second.LocalProjection.RequestId);
        Assert.True(first.LocalProjection.Local);
        Assert.True(second.LocalProjection.Local);
    }

    [Fact]
    public void RejectionMustMatchAnOutstandingRequestAndChannel()
    {
        var ledger = new ChatRequestLedger(7, "Avatar");
        ChatOutbound outbound = ledger.CreateOutbound(
            new HudChatSubmission(HudChatChannel.World, "hello"), 1, true, AntiSpam(), []);
        var wrong = new ChatRejection
        {
            RequestId = outbound.Request.RequestId,
            Channel = ChatChannel.Say,
            Reason = ChatRejectionReason.InternalError,
        };
        Assert.False(ledger.TryCorrelateRejection(wrong, out _));

        var exact = new ChatRejection
        {
            RequestId = outbound.Request.RequestId,
            Channel = ChatChannel.World,
            Reason = ChatRejectionReason.RateLimited,
            RetryAfterMilliseconds = 1000,
        };
        Assert.True(ledger.TryCorrelateRejection(exact, out HudChatRejection mapped));
        Assert.Equal(HudChatRejectionReason.RateLimited, mapped.Reason);
        Assert.False(ledger.TryCorrelateRejection(exact, out _));
    }

    [Fact]
    public void OwnSenderDeliveriesAreDroppedExceptOneAuthoredSelfWhisper()
    {
        var ledger = new ChatRequestLedger(7, "Avatar");
        Assert.False(ledger.AcceptRemoteDelivery(Delivery(ChatChannel.Say, "hello")));
        Assert.False(ledger.AcceptRemoteDelivery(Delivery(ChatChannel.Whisper, "hello", "Avatar")));

        ledger.CreateOutbound(
            new HudChatSubmission(
                HudChatChannel.Whisper,
                "hello",
                new HudChatTarget.WhisperCharacterName("Avatar")),
            1,
            true,
            AntiSpam(),
            []);

        Assert.True(ledger.AcceptRemoteDelivery(Delivery(ChatChannel.Whisper, "hello", "Avatar")));
        Assert.False(ledger.AcceptRemoteDelivery(Delivery(ChatChannel.Whisper, "hello", "Avatar")));
    }

    [Fact]
    public void OtherSendersAreNeverConsumedByTheLedger()
    {
        var ledger = new ChatRequestLedger(7, "Avatar");
        ChatDelivery delivery = Delivery(ChatChannel.Say, "hello");
        delivery.SenderEntityId = 8;
        delivery.SenderName = "Other";

        Assert.True(ledger.AcceptRemoteDelivery(delivery));
    }

    private static ChatDelivery Delivery(ChatChannel channel, string text, string? whisperPeer = null)
    {
        var delivery = new ChatDelivery
        {
            MessageId = 1,
            Channel = channel,
            SentAtUnixMilliseconds = 1,
            SenderEntityId = 7,
            SenderName = "Avatar",
            SenderAlive = true,
            Body = new ChatBody { UserText = text },
        };
        if (whisperPeer is not null)
        {
            delivery.WhisperPeerName = whisperPeer;
        }

        return delivery;
    }

    private static HudChatAntiSpamCatalog AntiSpam() =>
        new(
            "en-US",
            [new HudChatAntiSpamCategory("test", 100, [new HudChatAntiSpamFilter.CapsLock(250)])]);
}
