namespace SarnautCore.NativeHud;

public readonly record struct HudFeedbackPoolProduct(HudFeedbackKind Kind, HudId[] Elements);

public readonly record struct HudCursorCatalog(HudId Default, HudId Hover, HudId Text, HudId Drag)
{
    internal HudId Resolve(HudCursor cursor) => cursor switch
    {
        HudCursor.Default => Default,
        HudCursor.Hover => Hover,
        HudCursor.Text => Text,
        HudCursor.Drag => Drag,
        _ => throw new ArgumentOutOfRangeException(nameof(cursor)),
    };
}

public readonly record struct HudTimelineCatalog(
    int AvatarVisibleMilliseconds,
    int AvatarMovementMilliseconds,
    int EnemyVisibleMilliseconds,
    int ExperienceVisibleMilliseconds,
    int PreemptFadeMilliseconds,
    int MoveMilliseconds,
    int GlowMilliseconds,
    int TextFadeMilliseconds,
    int TextScaleMilliseconds,
    int VerticalMilliseconds,
    int HorizontalMilliseconds,
    int DropMilliseconds,
    int DamageOutMilliseconds,
    int CriticalGlowMilliseconds)
{
    public static HudTimelineCatalog Retail => new(
        1410,
        1510,
        2110,
        2110,
        350,
        350,
        560,
        350,
        300,
        150,
        150,
        300,
        200,
        1680);

    internal int VisibleFor(HudFeedbackKind kind) => kind switch
    {
        HudFeedbackKind.Avatar => AvatarVisibleMilliseconds,
        HudFeedbackKind.Enemy => EnemyVisibleMilliseconds,
        HudFeedbackKind.Experience => ExperienceVisibleMilliseconds,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal int ActiveFor(HudFeedbackKind kind) => kind == HudFeedbackKind.Avatar
        ? AvatarMovementMilliseconds
        : VisibleFor(kind);

    internal void Validate()
    {
        if (AvatarVisibleMilliseconds != 1410 || AvatarMovementMilliseconds != 1510 ||
            EnemyVisibleMilliseconds != 2110 || ExperienceVisibleMilliseconds != 2110 ||
            PreemptFadeMilliseconds != 350 || MoveMilliseconds != 350 || GlowMilliseconds != 560 ||
            TextFadeMilliseconds != 350 || TextScaleMilliseconds != 300 || VerticalMilliseconds != 150 ||
            HorizontalMilliseconds != 150 || DropMilliseconds != 300 || DamageOutMilliseconds != 200 ||
            CriticalGlowMilliseconds != 1680)
        {
            throw new ArgumentException("HUD feedback timings must match the authored retail constants.");
        }
    }
}

/// <summary>Validated, engine-neutral output of the offline HUD product bake.</summary>
public sealed class HudProduct
{
    public const int ActionSlotCount = 36;
    public const int FeedbackPoolCount = 5;
    public const int QuestTrackerRowCount = 20;

    public HudProduct(
        HudId[] actionSlots,
        HudFeedbackPoolProduct[] feedbackPools,
        HudId[] questTrackerRows,
        HudCursorCatalog cursors,
        HudTimelineCatalog timelines,
        HudId[]? pixelMaskedElements = null,
        float pixelMaskThreshold = 0.5f,
        int maxEntities = 128,
        int maxPendingInputs = 64,
        int maxSessionEventsPerFrame = 128,
        int maxChatEntries = 128,
        int maxChangesPerFrame = 256,
        int maxErrorsPerFrame = 32)
    {
        ArgumentNullException.ThrowIfNull(actionSlots);
        ArgumentNullException.ThrowIfNull(feedbackPools);
        ActionSlots = (HudId[])actionSlots.Clone();
        FeedbackPools = Clone(feedbackPools);
        ArgumentNullException.ThrowIfNull(questTrackerRows);
        QuestTrackerRows = (HudId[])questTrackerRows.Clone();
        Cursors = cursors;
        Timelines = timelines;
        PixelMaskedElements = pixelMaskedElements is null ? [] : (HudId[])pixelMaskedElements.Clone();
        PixelMaskThreshold = pixelMaskThreshold;
        MaxEntities = maxEntities;
        MaxPendingInputs = maxPendingInputs;
        MaxSessionEventsPerFrame = maxSessionEventsPerFrame;
        MaxChatEntries = maxChatEntries;
        MaxChangesPerFrame = maxChangesPerFrame;
        MaxErrorsPerFrame = maxErrorsPerFrame;
        Validate();
    }

    public HudId[] ActionSlots { get; }

    public HudFeedbackPoolProduct[] FeedbackPools { get; }

    public HudId[] QuestTrackerRows { get; }

    public HudCursorCatalog Cursors { get; }

    public HudTimelineCatalog Timelines { get; }

    public HudId[] PixelMaskedElements { get; }

    public float PixelMaskThreshold { get; }

    public int MaxEntities { get; }

    public int MaxPendingInputs { get; }

    public int MaxSessionEventsPerFrame { get; }

    public int MaxChatEntries { get; }

    public int MaxChangesPerFrame { get; }

    public int MaxErrorsPerFrame { get; }

    internal int FindActionSlot(HudId element)
    {
        for (int index = 0; index < ActionSlots.Length; index++)
        {
            if (ActionSlots[index] == element)
            {
                return index;
            }
        }

        return -1;
    }

    internal bool RequiresPixelMask(HudId element)
    {
        for (int index = 0; index < PixelMaskedElements.Length; index++)
        {
            if (PixelMaskedElements[index] == element)
            {
                return true;
            }
        }

        return false;
    }

    internal HudId[] GetFeedbackElements(HudFeedbackKind kind)
    {
        for (int index = 0; index < FeedbackPools.Length; index++)
        {
            if (FeedbackPools[index].Kind == kind)
            {
                return FeedbackPools[index].Elements;
            }
        }

        throw new InvalidOperationException($"Missing {kind} feedback pool.");
    }

    private static HudFeedbackPoolProduct[] Clone(HudFeedbackPoolProduct[] source)
    {
        var result = new HudFeedbackPoolProduct[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            HudFeedbackPoolProduct pool = source[index];
            ArgumentNullException.ThrowIfNull(pool.Elements);
            result[index] = pool with { Elements = (HudId[])pool.Elements.Clone() };
        }

        return result;
    }

    private void Validate()
    {
        if (ActionSlots.Length != ActionSlotCount)
        {
            throw new ArgumentException($"HUD action bar must have exactly {ActionSlotCount} authored slots.", nameof(ActionSlots));
        }

        ValidateUnique(ActionSlots, nameof(ActionSlots));
        if (FeedbackPools.Length != Enum.GetValues<HudFeedbackKind>().Length)
        {
            throw new ArgumentException("HUD must author avatar, enemy, and experience feedback pools.", nameof(FeedbackPools));
        }

        Span<bool> kinds = stackalloc bool[3];
        for (int index = 0; index < FeedbackPools.Length; index++)
        {
            HudFeedbackPoolProduct pool = FeedbackPools[index];
            int kind = (int)pool.Kind;
            if ((uint)kind >= (uint)kinds.Length || kinds[kind])
            {
                throw new ArgumentException("Feedback pool kinds must be unique and known.", nameof(FeedbackPools));
            }

            kinds[kind] = true;
            if (pool.Elements.Length != FeedbackPoolCount)
            {
                throw new ArgumentException($"Each feedback pool must have exactly {FeedbackPoolCount} authored elements.", nameof(FeedbackPools));
            }

            ValidateUnique(pool.Elements, nameof(FeedbackPools));
        }

        ValidateUniqueAcrossProduct();
        if (QuestTrackerRows.Length != QuestTrackerRowCount)
        {
            throw new ArgumentException($"Quest tracker must have exactly {QuestTrackerRowCount} authored rows.", nameof(QuestTrackerRows));
        }

        ValidateUnique(QuestTrackerRows, nameof(QuestTrackerRows));
        ValidateId(Cursors.Default, nameof(Cursors));
        ValidateId(Cursors.Hover, nameof(Cursors));
        ValidateId(Cursors.Text, nameof(Cursors));
        ValidateId(Cursors.Drag, nameof(Cursors));
        Timelines.Validate();
        if (!float.IsFinite(PixelMaskThreshold) || PixelMaskThreshold < 0 || PixelMaskThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelMaskThreshold));
        }

        if (MaxEntities <= 0 || MaxPendingInputs <= 0 || MaxSessionEventsPerFrame <= 0 || MaxChatEntries <= 0 ||
            MaxChangesPerFrame < ActionSlotCount + (3 * FeedbackPoolCount) || MaxErrorsPerFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntities), "HUD capacities must be positive and the change buffer must fit the authored stable pools.");
        }
    }

    private void ValidateUniqueAcrossProduct()
    {
        for (int poolIndex = 0; poolIndex < FeedbackPools.Length; poolIndex++)
        {
            HudId[] elements = FeedbackPools[poolIndex].Elements;
            for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
            {
                HudId element = elements[elementIndex];
                if (FindActionSlot(element) >= 0)
                {
                    throw new ArgumentException($"HUD element '{element}' is authored more than once.");
                }

                for (int earlierPool = 0; earlierPool <= poolIndex; earlierPool++)
                {
                    int limit = earlierPool == poolIndex ? elementIndex : FeedbackPools[earlierPool].Elements.Length;
                    for (int earlierElement = 0; earlierElement < limit; earlierElement++)
                    {
                        if (FeedbackPools[earlierPool].Elements[earlierElement] == element)
                        {
                            throw new ArgumentException($"HUD element '{element}' is authored more than once.");
                        }
                    }
                }
            }
        }
    }

    private static void ValidateUnique(HudId[] values, string parameterName)
    {
        for (int index = 0; index < values.Length; index++)
        {
            ValidateId(values[index], parameterName);
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (values[earlier] == values[index])
                {
                    throw new ArgumentException($"HUD identifier '{values[index]}' is duplicated.", parameterName);
                }
            }
        }
    }

    private static void ValidateId(HudId id, string parameterName)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("HUD identifiers cannot be empty.", parameterName);
        }
    }
}
