using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudChatBubblePolicyTests
{
    [Fact]
    public void OnlyRetailPublicPlayerMessagesPassTheVisibilityAndSpamGates()
    {
        HudChatChannel[] publicChannels =
        [
            HudChatChannel.Say,
            HudChatChannel.Zone,
            HudChatChannel.ZoneSpecial,
            HudChatChannel.World,
        ];

        foreach (HudChatChannel channel in Enum.GetValues<HudChatChannel>())
        {
            Assert.Equal(publicChannels.Contains(channel), HudChatBubblePolicy.IsPublicChannel(channel));
        }

        Assert.True(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Say, true, true, 99, true));
        Assert.False(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Say, true, true, 100, true));
        Assert.True(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Say, true, true, 100, false));
        Assert.False(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Say, false, true, 0, false));
        Assert.False(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Say, true, false, 0, false));
        Assert.False(HudChatBubblePolicy.AllowsOrdinaryBubble(HudChatChannel.Whisper, true, true, 0, false));
    }

    [Theory]
    [InlineData(1.499f, false)]
    [InlineData(1.5f, true)]
    [InlineData(68f, true)]
    [InlineData(68.001f, false)]
    public void DisplayRangeUsesTheAuditedInclusiveBounds(float distance, bool expected) =>
        Assert.Equal(expected, HudChatBubblePolicy.IsWithinDisplayDistance(distance));

    [Theory]
    [InlineData(75f, true)]
    [InlineData(75.001f, false)]
    public void AttachmentRangeUsesTheAuditedCutDistance(float distance, bool expected) =>
        Assert.Equal(expected, HudChatBubblePolicy.IsWithinAttachmentDistance(distance));

    [Fact]
    public void NormalLifetimeHoldsForSevenSecondsThenFadesForOne()
    {
        Assert.Equal(new HudChatBubbleFrame(true, 0.7f), HudChatBubblePolicy.EvaluateNormal(0, 7));
        Assert.Equal(new HudChatBubbleFrame(true, 0.7f), HudChatBubblePolicy.EvaluateNormal(7000, 7));
        Assert.Equal(new HudChatBubbleFrame(true, 0.35f), HudChatBubblePolicy.EvaluateNormal(7500, 7));
        HudChatBubbleFrame lastFrame = HudChatBubblePolicy.EvaluateNormal(7999, 7);
        Assert.True(lastFrame.Active);
        Assert.Equal(0.0007f, lastFrame.Opacity, 6);
        Assert.Equal(default, HudChatBubblePolicy.EvaluateNormal(8000, 7));
    }

    [Fact]
    public void CriticalHideHasTwoFlashesAndOneFadeOfTwoHundredFiftyMillisecondsEach()
    {
        HudChatCriticalHideFrame first = HudChatBubblePolicy.EvaluateCriticalHide(0);
        Assert.Equal(HudChatCriticalHideStage.FirstFlash, first.Stage);
        Assert.Equal(HudChatBubbleEasing.SymmetricFlash, first.Easing);
        Assert.Equal((1f, 0.5f, 0), (first.FromOpacity, first.ToOpacity, first.ElapsedInStageMilliseconds));

        HudChatCriticalHideFrame second = HudChatBubblePolicy.EvaluateCriticalHide(250);
        Assert.Equal(HudChatCriticalHideStage.SecondFlash, second.Stage);
        Assert.Equal(HudChatBubbleEasing.SymmetricFlash, second.Easing);

        HudChatCriticalHideFrame final = HudChatBubblePolicy.EvaluateCriticalHide(500);
        Assert.Equal(HudChatCriticalHideStage.FinalFade, final.Stage);
        Assert.Equal(HudChatBubbleEasing.MonotonicIncrease, final.Easing);
        Assert.Equal((1f, 0f), (final.FromOpacity, final.ToOpacity));
        Assert.True(HudChatBubblePolicy.EvaluateCriticalHide(749).Active);
        Assert.Equal(default, HudChatBubblePolicy.EvaluateCriticalHide(750));
    }
}
