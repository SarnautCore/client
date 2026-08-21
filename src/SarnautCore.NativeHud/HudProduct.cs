namespace SarnautCore.NativeHud;

public readonly record struct HudFeedbackPoolProduct(HudFeedbackKind Kind, HudId[] Elements);

public readonly record struct HudPlateAssignment(HudId SemanticId)
{
    public bool IsNone => SemanticId.IsEmpty;

    public static HudPlateAssignment None => default;
}

public readonly record struct HudUnitPlateProduct(HudPlateAssignment Assignment, HudId Element);

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
    int EntryFadeMilliseconds,
    int MessageMoveMilliseconds,
    int MessagePreemptFadeInMilliseconds,
    int MessageSolidMilliseconds,
    int MessageFadeOutMilliseconds,
    int GlowResizeMilliseconds,
    int GlowFadeInMilliseconds,
    int TextFadeInMilliseconds,
    int DamageTextScaleMilliseconds,
    int DamageVerticalShiftMilliseconds,
    int DamageHorizontalShiftMilliseconds,
    int DamageDropShiftMilliseconds,
    int DamageFadeOutMilliseconds,
    int CriticalGlowMilliseconds)
{
    public static HudTimelineCatalog Retail => new(
        10,
        350,
        350,
        1200,
        900,
        560,
        560,
        350,
        300,
        150,
        150,
        300,
        200,
        1680);

    public int AvatarVisibleMilliseconds => EntryFadeMilliseconds + MessageSolidMilliseconds + DamageFadeOutMilliseconds;

    public int AvatarMovementMilliseconds => EntryFadeMilliseconds + MessageSolidMilliseconds + DamageDropShiftMilliseconds;

    public int EnemyVisibleMilliseconds => EntryFadeMilliseconds + MessageSolidMilliseconds + MessageFadeOutMilliseconds;

    public int ExperienceVisibleMilliseconds => EntryFadeMilliseconds + MessageSolidMilliseconds + MessageFadeOutMilliseconds;

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
        if (EntryFadeMilliseconds != 10 || MessageMoveMilliseconds != 350 ||
            MessagePreemptFadeInMilliseconds != 350 || MessageSolidMilliseconds != 1200 ||
            MessageFadeOutMilliseconds != 900 || GlowResizeMilliseconds != 560 ||
            GlowFadeInMilliseconds != 560 || TextFadeInMilliseconds != 350 ||
            DamageTextScaleMilliseconds != 300 || DamageVerticalShiftMilliseconds != 150 ||
            DamageHorizontalShiftMilliseconds != 150 || DamageDropShiftMilliseconds != 300 ||
            DamageFadeOutMilliseconds != 200 ||
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
    public const int UnitPlateCount = 10;
    public const int InventoryLayoutCount = 10;
    public const int InventorySlotCount = 60;
    public const int InventoryPartitionCount = 5;
    public const int LootPageSize = 4;
    public const int LootEntryCount = 20;
    public const int CharacterEquipmentSlotCount = 21;
    public const int CharacterBagSlot = 19;
    public const int CharacterDeathInsuranceSlot = 20;
    public const int CharacterStatCount = 14;
    public const int QuestLogEntryCount = 20;
    public const int QuestLogBookmarkCount = 3;
    public const int QuestLogObjectiveCount = 5;
    public const int QuestInfoObjectiveCount = 6;
    public const int QuestInfoRewardItemCount = 5;
    public const int QuestInfoReputationCount = 5;
    public const int QuestInfoCurrencyCount = 5;
    public const int QuestLogSecretComponentCount = 15;
    public const int QuestTalkOptionCount = 20;

    public HudProduct(
        HudId[] actionSlots,
        HudFeedbackPoolProduct[] feedbackPools,
        HudId[] questTrackerRows,
        HudUnitPlateProduct[] unitPlates,
        HudId overtipPrototype,
        HudCursorCatalog cursors,
        HudTimelineCatalog timelines,
        HudContextProduct contexts,
        HudId[]? pixelMaskedElements = null,
        float pixelMaskThreshold = 0.5f,
        int maxEntities = 128,
        int maxOvertips = 128,
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
        ArgumentNullException.ThrowIfNull(unitPlates);
        UnitPlates = (HudUnitPlateProduct[])unitPlates.Clone();
        OvertipPrototype = overtipPrototype;
        Cursors = cursors;
        Timelines = timelines;
        ArgumentNullException.ThrowIfNull(contexts);
        Contexts = contexts;
        PixelMaskedElements = pixelMaskedElements is null ? [] : (HudId[])pixelMaskedElements.Clone();
        PixelMaskThreshold = pixelMaskThreshold;
        MaxEntities = maxEntities;
        MaxOvertips = maxOvertips;
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

    public HudUnitPlateProduct[] UnitPlates { get; }

    /// <summary>The one hidden retail factory prototype compiled by the offline bake.</summary>
    public HudId OvertipPrototype { get; }

    public HudCursorCatalog Cursors { get; }

    public HudTimelineCatalog Timelines { get; }

    public HudContextProduct Contexts { get; }

    public HudId[] PixelMaskedElements { get; }

    public float PixelMaskThreshold { get; }

    public int MaxEntities { get; }

    /// <summary>
    /// SarnautCore runtime policy bound. This is not an authored retail count. The engine adapter
    /// pre-materializes exactly this many prototype clones during Open.
    /// </summary>
    public int MaxOvertips { get; }

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

    internal int FindUnitPlate(HudPlateAssignment assignment)
    {
        for (int index = 0; index < UnitPlates.Length; index++)
        {
            if (UnitPlates[index].Assignment == assignment)
            {
                return index;
            }
        }

        return -1;
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
        if (UnitPlates.Length != UnitPlateCount)
        {
            throw new ArgumentException($"HUD must author exactly {UnitPlateCount} fixed unit plates.", nameof(UnitPlates));
        }

        for (int index = 0; index < UnitPlates.Length; index++)
        {
            HudUnitPlateProduct plate = UnitPlates[index];
            ValidateId(plate.Assignment.SemanticId, nameof(UnitPlates));
            ValidateId(plate.Element, nameof(UnitPlates));
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (UnitPlates[earlier].Assignment == plate.Assignment || UnitPlates[earlier].Element == plate.Element)
                {
                    throw new ArgumentException("Unit plate semantic assignments and elements must be unique.", nameof(UnitPlates));
                }
            }
        }

        ValidateId(OvertipPrototype, nameof(OvertipPrototype));
        ValidateId(Cursors.Default, nameof(Cursors));
        ValidateId(Cursors.Hover, nameof(Cursors));
        ValidateId(Cursors.Text, nameof(Cursors));
        ValidateId(Cursors.Drag, nameof(Cursors));
        Timelines.Validate();
        if (!float.IsFinite(PixelMaskThreshold) || PixelMaskThreshold < 0 || PixelMaskThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelMaskThreshold));
        }

        if (MaxEntities <= 0 || MaxOvertips <= 0 || MaxOvertips > MaxEntities ||
            MaxPendingInputs <= 0 || MaxSessionEventsPerFrame <= 0 || MaxChatEntries <= 0 ||
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
