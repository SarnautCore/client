using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class ChatProtocolMapperTests
{
    [Fact]
    public void TypedWhisperRequestPreservesTargetAndBody()
    {
        var submission = new HudChatSubmission(
            HudChatChannel.Whisper,
            "  hello  ",
            new HudChatTarget.WhisperCharacterName("Alice"));

        ChatSendRequest request = ChatProtocolMapper.ToRequest(7, submission);

        Assert.Equal((ulong)7, request.RequestId);
        Assert.Equal(ChatChannel.Whisper, request.Channel);
        Assert.Equal("  hello  ", request.Text);
        Assert.Equal(ChatSendRequest.TargetOneofCase.WhisperCharacterName, request.TargetCase);
        Assert.Equal("Alice", request.WhisperCharacterName);
    }

    [Fact]
    public void LocalProjectionCarriesRequestAndComputesSpamWithoutRewrite()
    {
        HudChatAntiSpamCatalog antiSpam = AntiSpam();
        var submission = new HudChatSubmission(HudChatChannel.Say, "SELL");

        HudChatMessage message = ChatProtocolMapper.ProjectLocal(
            9, submission, 1234, 55, "Avatar", true, antiSpam, []);

        Assert.True(message.Local);
        Assert.Equal((ulong)9, message.RequestId);
        Assert.Equal(250, message.SpamWeight);
        Assert.Equal("SELL", Assert.IsType<HudChatBody.UserText>(message.Body).Value);
    }

    [Fact]
    public void RemoteDeliveryIsRequestlessAndPreservesClosedBodyAndContext()
    {
        var delivery = new ChatDelivery
        {
            MessageId = 11,
            RequestId = 0,
            Channel = ChatChannel.Whisper,
            SentAtUnixMilliseconds = 1234,
            SenderEntityId = 55,
            SenderName = "Sender",
            SenderAlive = true,
            Body = new ChatBody
            {
                Localized = new LocalizedChatBody
                {
                    ProductLocalizationId = "chat.notice",
                    Arguments = { "one", "two" },
                },
            },
            WhisperPeerName = "Peer",
        };

        HudChatMessage message = ChatProtocolMapper.FromDelivery(delivery, true, AntiSpam(), []);

        Assert.False(message.Local);
        Assert.Equal((ulong)0, message.RequestId);
        var body = Assert.IsType<HudChatBody.Localized>(message.Body);
        Assert.Equal(new[] { "one", "two" }, body.Arguments.ToArray());
        Assert.Equal("Peer", Assert.IsType<HudChatContext.WhisperPeerName>(message.Context).Value);
    }

    [Fact]
    public void DeliveryRejectsTheRemovedServerEchoShape()
    {
        var delivery = new ChatDelivery
        {
            MessageId = 1,
            RequestId = 9,
            Channel = ChatChannel.Say,
            SentAtUnixMilliseconds = 1,
            SenderEntityId = 2,
            SenderName = "Sender",
            Body = new ChatBody { UserText = "hello" },
        };

        Assert.Throws<ArgumentException>(() => ChatProtocolMapper.FromDelivery(delivery, true, AntiSpam(), []));
    }

    [Fact]
    public void RejectionMapsEveryTypedField()
    {
        var rejection = new ChatRejection
        {
            RequestId = 5,
            Channel = ChatChannel.World,
            Reason = ChatRejectionReason.RateLimited,
            RetryAfterMilliseconds = 750,
            Detail = new LocalizedChatBody
            {
                ProductLocalizationId = "chat.rate-limited",
                Arguments = { "750" },
            },
        };

        HudChatRejection mapped = ChatProtocolMapper.FromRejection(rejection);

        Assert.Equal(HudChatRejectionReason.RateLimited, mapped.Reason);
        Assert.Equal(750, mapped.RetryAfterMilliseconds);
        Assert.Equal("chat.rate-limited", mapped.Detail?.ProductLocalizationId.Value);
    }

    private static HudChatAntiSpamCatalog AntiSpam() =>
        new(
            "en-US",
            [
                new HudChatAntiSpamCategory(
                    "test",
                    100,
                    [new HudChatAntiSpamFilter.CapsLock(250)]),
            ]);
}
