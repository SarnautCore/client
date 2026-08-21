namespace SarnautCore.NativeHud;

public enum HudChatCriticalHideStage
{
    None,
    FirstFlash,
    SecondFlash,
    FinalFade,
}

public enum HudChatBubbleEasing
{
    None,
    SymmetricFlash,
    MonotonicIncrease,
}

public readonly record struct HudChatBubbleFrame(bool Active, float Opacity);

public readonly record struct HudChatBubbleSettings(
    bool Show,
    int OpacityTenths,
    bool AntiSpamEnabled)
{
    public static HudChatBubbleSettings RetailDefault => new(true, HudChatBubblePolicy.DefaultOpacityTenths, true);

    public HudChatBubbleSettings Validate()
    {
        if (OpacityTenths is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(OpacityTenths));
        }

        return this;
    }
}

public readonly record struct HudChatCriticalHideFrame(
    bool Active,
    HudChatCriticalHideStage Stage,
    HudChatBubbleEasing Easing,
    int ElapsedInStageMilliseconds,
    int StageDurationMilliseconds,
    float FromOpacity,
    float ToOpacity);

/// <summary>Retail 1.1 ordinary chat-bubble eligibility, range, and deterministic lifetime policy.</summary>
public static class HudChatBubblePolicy
{
    public const int HoldMilliseconds = 7000;
    public const int FadeMilliseconds = 1000;
    public const int LifetimeMilliseconds = HoldMilliseconds + FadeMilliseconds;
    public const int CriticalHideStageMilliseconds = 250;
    public const int CriticalHideMilliseconds = CriticalHideStageMilliseconds * 3;
    public const float MinimumDisplayDistance = 1.5f;
    public const float MaximumDisplayDistance = 68f;
    public const float AttachmentCutDistance = 75f;
    public const int DefaultOpacityTenths = 7;
    public const int SpamWeightThreshold = 100;

    public static bool IsPublicChannel(HudChatChannel channel) => channel is
        HudChatChannel.Say or
        HudChatChannel.Zone or
        HudChatChannel.ZoneSpecial or
        HudChatChannel.World;

    public static bool AllowsOrdinaryBubble(
        HudChatChannel channel,
        bool senderIsPlayer,
        bool bubblesShown,
        int spamWeight,
        bool antiSpamEnabled)
    {
        if (spamWeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spamWeight));
        }

        return senderIsPlayer &&
            bubblesShown &&
            IsPublicChannel(channel) &&
            (!antiSpamEnabled || spamWeight < SpamWeightThreshold);
    }

    public static bool IsWithinDisplayDistance(float cameraSpaceDistance) =>
        float.IsFinite(cameraSpaceDistance) &&
        cameraSpaceDistance >= MinimumDisplayDistance &&
        cameraSpaceDistance <= MaximumDisplayDistance;

    public static bool IsWithinAttachmentDistance(float cameraSpaceDistance) =>
        float.IsFinite(cameraSpaceDistance) &&
        cameraSpaceDistance >= 0f &&
        cameraSpaceDistance <= AttachmentCutDistance;

    public static HudChatBubbleFrame EvaluateNormal(long elapsedMilliseconds, int opacityTenths)
    {
        if (elapsedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        }

        if (opacityTenths is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(opacityTenths));
        }

        if (elapsedMilliseconds >= LifetimeMilliseconds)
        {
            return default;
        }

        float configuredOpacity = opacityTenths / 10f;
        if (elapsedMilliseconds <= HoldMilliseconds)
        {
            return new HudChatBubbleFrame(true, configuredOpacity);
        }

        float remaining = (LifetimeMilliseconds - elapsedMilliseconds) / (float)FadeMilliseconds;
        return new HudChatBubbleFrame(true, configuredOpacity * remaining);
    }

    public static HudChatCriticalHideFrame EvaluateCriticalHide(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        }

        if (elapsedMilliseconds >= CriticalHideMilliseconds)
        {
            return default;
        }

        int stageIndex = (int)(elapsedMilliseconds / CriticalHideStageMilliseconds);
        int elapsedInStage = (int)(elapsedMilliseconds % CriticalHideStageMilliseconds);
        return stageIndex switch
        {
            0 => Stage(HudChatCriticalHideStage.FirstFlash, HudChatBubbleEasing.SymmetricFlash, elapsedInStage, 1f, 0.5f),
            1 => Stage(HudChatCriticalHideStage.SecondFlash, HudChatBubbleEasing.SymmetricFlash, elapsedInStage, 1f, 0.5f),
            2 => Stage(HudChatCriticalHideStage.FinalFade, HudChatBubbleEasing.MonotonicIncrease, elapsedInStage, 1f, 0f),
            _ => throw new InvalidOperationException("Critical hide stage is outside its validated lifetime."),
        };
    }

    private static HudChatCriticalHideFrame Stage(
        HudChatCriticalHideStage stage,
        HudChatBubbleEasing easing,
        int elapsedMilliseconds,
        float fromOpacity,
        float toOpacity) =>
        new(
            true,
            stage,
            easing,
            elapsedMilliseconds,
            CriticalHideStageMilliseconds,
            fromOpacity,
            toOpacity);
}
