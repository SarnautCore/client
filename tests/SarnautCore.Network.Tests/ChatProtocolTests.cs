using Google.Protobuf;
using Sarnaut.Protocol.V1;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class ChatProtocolTests
{
    [Fact]
    public void ChannelNumbersPreserveTheRetailChatTypeOrdinals()
    {
        Assert.Equal(0, (int)ChatChannel.Whisper);
        Assert.Equal(1, (int)ChatChannel.Group);
        Assert.Equal(2, (int)ChatChannel.Say);
        Assert.Equal(4, (int)ChatChannel.Zone);
        Assert.Equal(5, (int)ChatChannel.ZoneSpecial);
        Assert.Equal(6, (int)ChatChannel.World);
        Assert.Equal(9, (int)ChatChannel.Guild);
        Assert.Equal(10, (int)ChatChannel.GuildOfficer);
        Assert.Equal(11, (int)ChatChannel.Raid);
        Assert.False(Enum.IsDefined(typeof(ChatChannel), 3));
        Assert.False(Enum.IsDefined(typeof(ChatChannel), 7));
        Assert.False(Enum.IsDefined(typeof(ChatChannel), 8));
        Assert.False(Enum.IsDefined(typeof(ChatChannel), 12));
    }

    [Fact]
    public void RejectionAndEnvelopeNumbersRemainWireStable()
    {
        Assert.Equal(18, (int)ClientMessage.PayloadOneofCase.ChatSendRequest);
        Assert.Equal(20, (int)ServerMessage.PayloadOneofCase.ChatDelivery);
        Assert.Equal(21, (int)ServerMessage.PayloadOneofCase.ChatRejection);

        Assert.Equal(0, (int)ChatRejectionReason.Mute);
        Assert.Equal(1, (int)ChatRejectionReason.InternalError);
        Assert.Equal(2, (int)ChatRejectionReason.Silence);
        Assert.Equal(3, (int)ChatRejectionReason.NoPoints);
        Assert.Equal(4, (int)ChatRejectionReason.EnemyFaction);
        Assert.Equal(5, (int)ChatRejectionReason.Ignored);
        Assert.Equal(6, (int)ChatRejectionReason.Dead);
        Assert.Equal(7, (int)ChatRejectionReason.NotPsionic);
        Assert.Equal(8, (int)ChatRejectionReason.TargetNotFound);
        Assert.Equal(9, (int)ChatRejectionReason.TargetOffline);
        Assert.Equal(10, (int)ChatRejectionReason.RateLimited);
        Assert.Equal(11, (int)ChatRejectionReason.TooLong);
        Assert.Equal(12, (int)ChatRejectionReason.NotMember);
        Assert.Equal(13, (int)ChatRejectionReason.NotAuthorized);
        Assert.Equal(14, (int)ChatRejectionReason.UnsupportedChannel);
        Assert.Equal(15, (int)ChatRejectionReason.Empty);
    }

    [Fact]
    public void TypedRequestSurvivesTheClientEnvelopeRoundTrip()
    {
        var envelope = new ClientMessage
        {
            ClientSeq = 42,
            ChatSendRequest = new ChatSendRequest
            {
                RequestId = 7,
                Channel = ChatChannel.Whisper,
                Text = "  exact text  ",
                WhisperCharacterName = "Alice",
            },
        };

        ClientMessage decoded = ClientMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.Equal(ClientMessage.PayloadOneofCase.ChatSendRequest, decoded.PayloadCase);
        Assert.Equal((ulong)7, decoded.ChatSendRequest.RequestId);
        Assert.Equal(ChatSendRequest.TargetOneofCase.WhisperCharacterName, decoded.ChatSendRequest.TargetCase);
        Assert.Equal("Alice", decoded.ChatSendRequest.WhisperCharacterName);
        Assert.Equal("  exact text  ", decoded.ChatSendRequest.Text);
    }

    [Fact]
    public void DeliveryPreservesEveryClosedBodyAndContextCase()
    {
        Assert.Null(ChatDelivery.Descriptor.FindFieldByNumber(9));

        ChatBody[] bodies =
        [
            new() { UserText = "hello" },
            new()
            {
                Localized = new LocalizedChatBody
                {
                    ProductLocalizationId = "chat.system.notice",
                    Arguments = { "one", "two" },
                },
            },
            new()
            {
                UnreadableFaction = new UnreadableFactionChatBody
                {
                    FactionNameLocalizationId = "faction.enemy",
                },
            },
        ];

        foreach (ChatBody body in bodies)
        {
            var envelope = new ServerMessage
            {
                ServerTick = 9,
                ChatDelivery = new ChatDelivery
                {
                    MessageId = 11,
                    RequestId = 0,
                    Channel = ChatChannel.Whisper,
                    SentAtUnixMilliseconds = 1234,
                    SenderEntityId = 55,
                    SenderName = "Sender",
                    SenderAlive = true,
                    Body = body,
                    WhisperPeerName = "Peer",
                },
            };

            ServerMessage decoded = ServerMessage.Parser.ParseFrom(envelope.ToByteArray());

            Assert.Equal(ServerMessage.PayloadOneofCase.ChatDelivery, decoded.PayloadCase);
            Assert.Equal((ulong)0, decoded.ChatDelivery.RequestId);
            Assert.Equal(body.ValueCase, decoded.ChatDelivery.Body.ValueCase);
            Assert.Equal(ChatDelivery.ContextOneofCase.WhisperPeerName, decoded.ChatDelivery.ContextCase);
            Assert.Equal("Peer", decoded.ChatDelivery.WhisperPeerName);
        }
    }

    [Fact]
    public void RejectionIsAnOrdinaryTypedServerPayload()
    {
        var envelope = new ServerMessage
        {
            ServerTick = 10,
            ChatRejection = new ChatRejection
            {
                RequestId = 9,
                Channel = ChatChannel.World,
                Reason = ChatRejectionReason.RateLimited,
                RetryAfterMilliseconds = 750,
                Detail = new LocalizedChatBody
                {
                    ProductLocalizationId = "chat.rejected.rate-limited",
                    Arguments = { "750" },
                },
            },
        };

        ServerMessage decoded = ServerMessage.Parser.ParseFrom(envelope.ToByteArray());

        Assert.Equal(ServerMessage.PayloadOneofCase.ChatRejection, decoded.PayloadCase);
        Assert.Equal(ChatRejectionReason.RateLimited, decoded.ChatRejection.Reason);
        Assert.Equal((uint)750, decoded.ChatRejection.RetryAfterMilliseconds);
        Assert.Equal("chat.rejected.rate-limited", decoded.ChatRejection.Detail.ProductLocalizationId);
    }
}
