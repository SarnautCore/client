using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.NativeHud;

/// <summary>Strict reader for the source-free output of the offline HUD bake.</summary>
public static class HudProductManifestParser
{
    public const string Schema = "sarnaut.hud-product/v2";
    public const string CatalogSchema = "sarnaut.hud-catalog/v2";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly string[] RootIds =
    [
        "world-input", "action-bar", "unit-plates", "overtips", "combat-feedback", "compass",
        "chat", "chat-input", "world-chat-bubbles", "quest-tracker", "target-selection", "character",
        "multibag", "loot-bag", "quest-log", "quest-info", "npc-talk",
    ];
    private static readonly int[] InventoryCapacities = [12, 16, 18, 24, 30, 36, 42, 48, 54, 60];
    private static readonly int[][] InventoryPartitions =
    [
        [12], [16], [12, 6], [16, 8], [30], [8, 8, 8, 6, 6], [30, 12],
        [12, 12, 12, 12], [30, 12, 12], [30, 30],
    ];
    private static readonly (HudEquipmentIdentity Identity, byte RetailCode)[] Equipment =
    [
        (HudEquipmentIdentity.MainHand, 14), (HudEquipmentIdentity.OffHand, 15),
        (HudEquipmentIdentity.Ranged, 16), (HudEquipmentIdentity.Helm, 0),
        (HudEquipmentIdentity.Mantle, 4), (HudEquipmentIdentity.Cloak, 12),
        (HudEquipmentIdentity.Armor, 1), (HudEquipmentIdentity.Gloves, 5),
        (HudEquipmentIdentity.Belt, 7), (HudEquipmentIdentity.Pants, 2),
        (HudEquipmentIdentity.Boots, 3), (HudEquipmentIdentity.Earrings, 10),
        (HudEquipmentIdentity.Necklace, 11), (HudEquipmentIdentity.Tabard, 18),
        (HudEquipmentIdentity.Shirt, 13), (HudEquipmentIdentity.Bracers, 6),
        (HudEquipmentIdentity.Ring1, 8), (HudEquipmentIdentity.Ring2, 9),
        (HudEquipmentIdentity.Trinket, 19), (HudEquipmentIdentity.Bag, 20),
        (HudEquipmentIdentity.DeathInsurance, 21),
    ];
    private static readonly (string Id, int Duration)[] Timelines =
    [
        ("entry-fade", 10), ("message-move", 350), ("message-fade-in", 350),
        ("message-fade-solid", 1200), ("message-fade-out", 900), ("glow-resize", 560),
        ("glow-fade-in", 560), ("text-fade-in", 350), ("damage-text-scale", 300),
        ("damage-vertical-shift", 150), ("damage-horizontal-shift", 150),
        ("damage-drop-shift", 300), ("damage-fade-out", 200), ("critical-glow-fade", 1680),
    ];

    public static HudProductManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static HudProductManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new InvalidDataException("HUD product manifest is empty.");
        }

        try
        {
            byte[] payload = utf8Json.ToArray();
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
            RejectDuplicateProperties(document.RootElement, "$");
            RejectNonIntegralNumbers(document.RootElement, "$");
            HudProductManifest manifest = JsonSerializer.Deserialize<HudProductManifest>(payload, SerializerOptions)
                ?? throw new InvalidDataException("HUD product manifest is null.");
            Validate(manifest);
            return manifest;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("HUD product manifest is not valid hud-product/v2 JSON.", exception);
        }
    }

    public static HudProductManifest ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllBytes(path));
    }

    public static HudProduct BuildProduct(HudProductManifest manifest, int maxOvertips = 128)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        HudSystemManifest systems = manifest.Systems;
        HudFeedbackPools pools = systems.CombatFeedback.Pools;
        HudTimelineDefinition[] timelines = manifest.Timelines;

        var inventoryLayouts = new HudInventoryLayoutProduct[systems.MultiBag.Layouts.Length];
        for (int layoutIndex = 0; layoutIndex < inventoryLayouts.Length; layoutIndex++)
        {
            HudInventoryLayoutBinding layout = systems.MultiBag.Layouts[layoutIndex];
            var slots = new List<HudId>(layout.Capacity);
            var partitions = new HudInventoryPartitionProduct[layout.Partitions.Length];
            int firstSlot = 0;
            for (int partitionIndex = 0; partitionIndex < layout.Partitions.Length; partitionIndex++)
            {
                HudInventoryPartitionBinding partition = layout.Partitions[partitionIndex];
                partitions[partitionIndex] = new HudInventoryPartitionProduct(
                    new HudId(partition.Id), firstSlot, partition.Capacity);
                slots.AddRange(partition.Slots.Select(slot => new HudId(slot.Id)));
                firstSlot += partition.Capacity;
            }

            inventoryLayouts[layoutIndex] = new HudInventoryLayoutProduct(
                new HudId(layout.Id), layout.Capacity, slots.ToArray(), partitions);
        }

        HudQuestDetailPools detail = systems.QuestLog.Detail;
        HudNpcTalkSystem talk = systems.NpcTalk;
        var contexts = new HudContextProduct(
            new HudInventoryProduct(new HudId(systems.MultiBag.Root), inventoryLayouts),
            new HudLootProduct(
                new HudId(systems.LootBag.Root),
                systems.LootBag.Items.Select(item => new HudId(item.Id)).ToArray(),
                systems.LootBag.TotalCapacity),
            new HudQuestLogProduct(
                new HudId(systems.QuestLog.Root),
                systems.QuestLog.Rows.Select(row => new HudId(row.Id)).ToArray(),
                systems.QuestLog.Bookmarks.Select(RoleId).ToArray(),
                detail.Objectives.Roles.Select(RoleId).ToArray(),
                detail.AlternativeIcons.Roles.Select(RoleId).ToArray(),
                detail.MandatoryIcons.Roles.Select(RoleId).ToArray(),
                detail.Reputation.Roles.Select(RoleId).ToArray(),
                detail.Currencies.Roles.Select(RoleId).ToArray(),
                detail.Secrets.Roles.Select(RoleId).ToArray()),
            new HudQuestInfoProduct(
                new HudId(systems.QuestInfo.Root),
                new HudId(talk.Root),
                talk.Options.Roles.Select(RoleId).ToArray(),
                talk.Objectives.Roles.Select(RoleId).ToArray(),
                talk.AlternativeIcons.Roles.Select(RoleId).ToArray(),
                talk.MandatoryIcons.Roles.Select(RoleId).ToArray(),
                talk.Reputation.Roles.Select(RoleId).ToArray(),
                talk.Currencies.Roles.Select(RoleId).ToArray()),
            new HudCharacterProduct(
                new HudId(systems.Character.Root),
                systems.Character.Equipment.Select(binding => RoleId(binding.Role)).ToArray(),
                systems.Character.StatRows.Select(RoleId).ToArray()));

        return new HudProduct(
            systems.ActionBar.Slots.Select(slot => RoleId(slot.Action)).ToArray(),
            [
                new(HudFeedbackKind.Avatar, pools.Avatar.Lanes.Select(RoleId).ToArray()),
                new(HudFeedbackKind.Enemy, pools.Enemy.Lanes.Select(RoleId).ToArray()),
                new(HudFeedbackKind.Experience, pools.Experience.Lanes.Select(RoleId).ToArray()),
            ],
            systems.QuestTracker.Rows.Select(row => new HudId(row.Id)).ToArray(),
            systems.UnitPlates.Plates.Select(plate => new HudUnitPlateProduct(
                new HudPlateAssignment(new HudId(plate.Id["unit-plate-".Length..])),
                new HudId(plate.Id))).ToArray(),
            RoleId(systems.Overtips.Prototype),
            new HudCursorCatalog(
                new HudId(manifest.CursorAliases.Default),
                new HudId(manifest.CursorAliases.Hover),
                new HudId(manifest.CursorAliases.Text),
                new HudId(manifest.CursorAliases.Drag)),
            new HudTimelineCatalog(
                timelines[0].DurationMilliseconds, timelines[1].DurationMilliseconds,
                timelines[2].DurationMilliseconds, timelines[3].DurationMilliseconds,
                timelines[4].DurationMilliseconds, timelines[5].DurationMilliseconds,
                timelines[6].DurationMilliseconds, timelines[7].DurationMilliseconds,
                timelines[8].DurationMilliseconds, timelines[9].DurationMilliseconds,
                timelines[10].DurationMilliseconds, timelines[11].DurationMilliseconds,
                timelines[12].DurationMilliseconds, timelines[13].DurationMilliseconds),
            contexts,
            manifest.MaskBindings.Where(binding => binding.Kind == HudMaskKind.Pick)
                .Select(binding => new HudId(binding.Role)).ToArray(),
            1.0f / byte.MaxValue,
            maxOvertips: maxOvertips);
    }

    private static HudId RoleId(HudSemanticRole role) => new(role.Id);

    private static void Validate(HudProductManifest manifest)
    {
        Require(manifest.Schema == Schema, "HUD product schema is unsupported.");
        ValidateResourcePath(manifest.RuntimeScene, ".scn", "runtime_scene");
        ValidateMetadata(manifest);

        HudRootBinding[] roots = Required(manifest.Roots, "roots");
        Require(roots.Length == RootIds.Length, "HUD root census must have exactly 17 entries.");
        for (int index = 0; index < roots.Length; index++)
        {
            HudRootBinding root = Required(roots[index], $"roots[{index}]");
            Require(root.Id == RootIds[index], $"HUD root {index} has an unexpected semantic id.");
            ValidateNativeRole(root.NativeRole, $"roots[{index}].native_role");
            Require(root.DecalOnly == (root.Id == "target-selection"),
                $"HUD root '{root.Id}' has an invalid decal_only policy.");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        ValidateSystems(Required(manifest.Systems, "systems"), roles);
        ValidateCatalogs(manifest, roles);
    }

    private static void ValidateMetadata(HudProductManifest manifest)
    {
        HudSceneTopology topology = Required(manifest.Topology, "topology");
        ValidateNativeRole(topology.ProductRoot, "topology.product_root");
        ValidateNativeRole(topology.UiRoot, "topology.ui_root");
        ValidateNativeRole(topology.TargetSelectionRoot, "topology.target_selection_root");
        ValidateNativeRole(topology.TargetSelectionDecal, "topology.target_selection_decal");
        Require(topology.ProductRoot != topology.UiRoot &&
            topology.TargetSelectionDecal.StartsWith(topology.TargetSelectionRoot + "/", StringComparison.Ordinal),
            "HUD scene topology does not separate the UI and target decal roots.");

        HudCatalogEncoding encoding = Required(manifest.CatalogEncoding, "catalog_encoding");
        Require(encoding.Schema == CatalogSchema && encoding.CursorResourceType == "Texture2D" &&
            encoding.MaskResourceType == "Texture2D" && encoding.SoundResourceType == "AudioStream" &&
            encoding.TimelineResourceType == "Resource" && encoding.ThemeResourceType == "Theme",
            "HUD catalog encoding is unsupported.");
        ValidateResourcePath(manifest.Theme, ".res", "theme");
        ValidateResourcePath(manifest.TimelineResource, ".res", "timeline_resource");

        HudChatCommandCatalogReference chat = Required(manifest.ChatCommands, "chat_commands");
        Require(chat.Schema == "sarnaut.chat-commands/v1" && chat.Locale == "eng" &&
            chat.AutocompleteCapacity == 22, "HUD chat command catalog contract changed.");
        ValidateResourcePath(chat.Resource, ".json", "chat_commands.resource");
        HudExternalProductReference options = Required(manifest.OptionsProduct, "options_product");
        Require(options.Schema == "sarnaut.options-product/v1" && options.ProductKey == "options",
            "HUD options product reference changed.");
        HudItemCatalogReference items = Required(manifest.ItemCatalog, "item_catalog");
        Require(items.Schema == "sarnaut.item-presentation-catalog" && items.Version == 1 &&
            items.ProductKey == "hud.items.inst-league1" && items.Key == "item-id",
            "HUD item catalog reference changed.");
        HudExternalActions actions = Required(manifest.ExternalActions, "external_actions");
        Require(actions.OpenOptions == "open-options", "HUD open-options action changed.");
        HudWindowPolicy policy = Required(manifest.WindowPolicy, "window_policy");
        Require(policy.FocusOrder == HudWindowFocusOrder.LastOpened &&
            policy.Escape == HudEscapePolicy.CloseLastOpened && policy.Placement == HudWindowPlacement.AuthoredScene,
            "HUD window policy changed.");
    }

    private static void ValidateSystems(HudSystemManifest systems, HashSet<string> roles)
    {
        ValidateRoleSystem(systems.WorldInput, "world-input", 2, roles);
        ValidateActionBar(Required(systems.ActionBar, "systems.action_bar"), roles);
        ValidateRoleSystem(systems.UnitPlates, "unit-plates", 9, roles);
        HudUnitPlatesSystem plates = Required(systems.UnitPlates, "systems.unit_plates");
        Require(plates.Plates.Length == HudProduct.UnitPlateCount, "HUD unit plate census changed.");
        ValidateRoles(plates.Plates, roles, "systems.unit_plates.plates");
        for (int index = 0; index < plates.Plates.Length; index++)
        {
            Require(plates.Plates[index].Id.StartsWith("unit-plate-", StringComparison.Ordinal),
                "HUD unit plate semantic id is invalid.");
        }

        HudOvertipsSystem overtips = Required(systems.Overtips, "systems.overtips");
        Require(overtips.Root == "overtips", "HUD overtip root changed.");
        AddRole(overtips.Prototype, roles, "systems.overtips.prototype");
        Require(overtips.Roles.Length == 4, "HUD overtip role census changed.");
        ValidateRoles(overtips.Roles, roles, "systems.overtips.roles");

        HudCombatFeedbackSystem feedback = Required(systems.CombatFeedback, "systems.combat_feedback");
        Require(feedback.Root == "combat-feedback", "HUD feedback root changed.");
        ValidateFeedbackPool(feedback.Pools.Avatar, "avatar", roles);
        ValidateFeedbackPool(feedback.Pools.Enemy, "enemy", roles);
        ValidateFeedbackPool(feedback.Pools.Experience, "experience", roles);
        ValidateRoleSystem(systems.Compass, "compass", 3, roles);
        ValidateRoleSystem(systems.Chat, "chat", 4, roles);
        HudChatInputSystem chatInput = Required(systems.ChatInput, "systems.chat_input");
        Require(chatInput.Root == "chat-input" && chatInput.AutocompleteCapacity == 22,
            "HUD chat input contract changed.");
        Require(chatInput.Roles.Length == 8, "HUD chat input role census changed.");
        ValidateRoles(chatInput.Roles, roles, "systems.chat_input.roles");
        ValidateRoleSystem(systems.WorldChatBubbles, "world-chat-bubbles", 5, roles);
        ValidateQuestTracker(Required(systems.QuestTracker, "systems.quest_tracker"), roles);
        ValidateTarget(Required(systems.TargetSelection, "systems.target_selection"), roles);
        ValidateCharacter(Required(systems.Character, "systems.character"), roles);
        ValidateInventory(Required(systems.MultiBag, "systems.multibag"), roles);
        ValidateLoot(Required(systems.LootBag, "systems.loot_bag"), roles);
        ValidateQuestLog(Required(systems.QuestLog, "systems.quest_log"), roles);
        ValidateQuestInfo(Required(systems.QuestInfo, "systems.quest_info"), roles);
        ValidateNpcTalk(Required(systems.NpcTalk, "systems.npc_talk"), roles);
    }

    private static void ValidateRoleSystem(HudRoleSystem? system, string root, int count, HashSet<string> roles)
    {
        HudRoleSystem value = Required(system, $"systems.{root}");
        Require(value.Root == root && value.Roles.Length == count, $"HUD '{root}' contract changed.");
        ValidateRoles(value.Roles, roles, $"systems.{root}.roles");
    }

    private static void ValidateRoleSystem(HudUnitPlatesSystem? system, string root, int count, HashSet<string> roles)
    {
        HudUnitPlatesSystem value = Required(system, $"systems.{root}");
        Require(value.Root == root && value.Roles.Length == count, $"HUD '{root}' contract changed.");
        ValidateRoles(value.Roles, roles, $"systems.{root}.roles");
    }

    private static void ValidateRoleSystem(HudChatSystem? system, string root, int count, HashSet<string> roles)
    {
        HudChatSystem value = Required(system, $"systems.{root}");
        Require(value.Root == root && value.Roles.Length == count, $"HUD '{root}' contract changed.");
        ValidateRoles(value.Roles, roles, $"systems.{root}.roles");
    }

    private static void ValidateActionBar(HudActionBarSystem system, HashSet<string> roles)
    {
        Require(system.Root == "action-bar" && system.SlotCount == HudProduct.ActionSlotCount &&
            system.Slots.Length == HudProduct.ActionSlotCount, "HUD action bar census changed.");
        for (int index = 0; index < system.Slots.Length; index++)
        {
            HudActionSlotBinding slot = Required(system.Slots[index], $"systems.action_bar.slots[{index}]");
            int ordinal = index + 1;
            Require(slot.Id == $"action-slot-{ordinal:00}" && slot.Presentation.Id == $"action-{ordinal:00}-presentation" &&
                slot.Action.Id == $"action-{ordinal:00}", $"HUD action slot {ordinal} has invalid semantic ids.");
            AddSemanticId(slot.Id, roles, "action slot");
            AddRole(slot.Presentation, roles, "action presentation");
            AddRole(slot.Action, roles, "action input");
        }
    }

    private static void ValidateFeedbackPool(HudFeedbackPool pool, string label, HashSet<string> roles)
    {
        pool = Required(pool, $"feedback.{label}");
        Require(pool.Capacity == HudProduct.FeedbackPoolCount && pool.Lanes.Length == HudProduct.FeedbackPoolCount,
            $"HUD feedback pool '{label}' census changed.");
        ValidateRoles(pool.Lanes, roles, $"feedback.{label}.lanes");
    }

    private static void ValidateQuestTracker(HudQuestTrackerSystem system, HashSet<string> roles)
    {
        Require(system.Root == "quest-tracker" && system.Capacity == HudProduct.QuestTrackerRowCount &&
            system.Roles.Length == 4 && system.Rows.Length == HudProduct.QuestTrackerRowCount,
            "HUD quest tracker census changed.");
        ValidateRoles(system.Roles, roles, "systems.quest_tracker.roles");
        for (int index = 0; index < system.Rows.Length; index++)
        {
            HudQuestRowBinding row = Required(system.Rows[index], $"systems.quest_tracker.rows[{index}]");
            int ordinal = index + 1;
            Require(row.Id == $"quest-row-{ordinal:00}" && row.Task.Id == $"quest-row-{ordinal:00}-task" &&
                row.Toggle.Id == $"quest-row-{ordinal:00}-toggle", "HUD quest tracker semantic ids changed.");
            AddSemanticId(row.Id, roles, "quest tracker row");
            ValidateNativeRole(row.Role, "quest tracker row role");
            AddRole(row.Task, roles, "quest tracker task");
            AddRole(row.Toggle, roles, "quest tracker toggle");
        }
    }

    private static void ValidateTarget(HudTargetSelectionSystem system, HashSet<string> roles)
    {
        Require(system.Root == "target-selection", "HUD target-selection root changed.");
        AddRole(system.Decal, roles, "systems.target_selection.decal");
        HudTargetSelectionState state = Required(system.State, "systems.target_selection.state");
        Require(state.Visible == "target-selection-visible" && state.Hidden == "target-selection-hidden" &&
            state.Identity == "target-selection-target-id", "HUD target-selection state contract changed.");
        HudTargetSelectionSizing sizing = Required(system.Sizing, "systems.target_selection.sizing");
        Require(sizing.ExtentSource == HudTargetExtentSource.SelectedObjectCutTerrainAreaAndRadius &&
            IsFraction(sizing.ObjectCutAreaScale, 1, 2) && IsFraction(sizing.ExtraRadius, 1, 2) &&
            IsFraction(sizing.DiameterScale, 2, 1) && sizing.ProjectionAxis == HudProjectionAxis.PositiveYToNegativeY &&
            sizing.ProjectionDepth == HudProjectionDepthPolicy.SelectedEntityVisualHeight &&
            sizing.VerticalOffset == HudVerticalOffsetPolicy.EntityGroundAnchor && sizing.CullMask == 1 &&
            sizing.AlbedoFormat == "DXT5 RGBA8" && sizing.UpscaleFactor == 4 && sizing.HiddenUntilApplied,
            "HUD target-selection sizing contract changed.");
    }

    private static bool IsFraction(HudFraction? value, uint numerator, uint denominator) =>
        value is not null && value.Numerator == numerator && value.Denominator == denominator;

    private static void ValidateCharacter(HudCharacterSystem system, HashSet<string> roles)
    {
        Require(system.Root == "character" && system.Equipment.Length == Equipment.Length &&
            system.StatRows.Length == HudProduct.CharacterStatCount && system.Roles.Length == 6,
            "HUD character census changed.");
        for (int index = 0; index < system.Equipment.Length; index++)
        {
            HudEquipmentSlotBinding binding = Required(system.Equipment[index], $"character.equipment[{index}]");
            Require((binding.Identity, binding.RetailCode) == Equipment[index],
                $"HUD character equipment identity {index} changed.");
            AddRole(binding.Role, roles, "character equipment");
        }
        ValidateRoles(system.StatRows, roles, "character.stat_rows");
        ValidateRoles(system.Roles, roles, "character.roles");
        ValidateState(system.State, "character");
    }

    private static void ValidateInventory(HudMultiBagSystem system, HashSet<string> roles)
    {
        Require(system.Root == "multibag" && system.MaxSlots == HudProduct.InventorySlotCount &&
            system.MaxVisualPartitions == HudProduct.InventoryPartitionCount &&
            system.Layouts.Length == HudProduct.InventoryLayoutCount && system.Roles.Length == 7,
            "HUD multibag census changed.");
        ValidateRoles(system.Roles, roles, "multibag.roles");
        ValidateState(system.State, "multibag");
        for (int layoutIndex = 0; layoutIndex < system.Layouts.Length; layoutIndex++)
        {
            HudInventoryLayoutBinding layout = Required(system.Layouts[layoutIndex], $"multibag.layouts[{layoutIndex}]");
            Require(layout.Id == $"multibag-layout-{InventoryCapacities[layoutIndex]}" &&
                layout.Capacity == InventoryCapacities[layoutIndex] &&
                layout.Partitions.Length == InventoryPartitions[layoutIndex].Length,
                $"HUD multibag layout {layoutIndex} changed.");
            AddSemanticPath(layout.Id, layout.Role, roles, "multibag layout");
            int slotOrdinal = 1;
            for (int partitionIndex = 0; partitionIndex < layout.Partitions.Length; partitionIndex++)
            {
                HudInventoryPartitionBinding partition = layout.Partitions[partitionIndex];
                int expected = InventoryPartitions[layoutIndex][partitionIndex];
                Require(partition.Capacity == expected && partition.Slots.Length == expected,
                    "HUD multibag partition census changed.");
                AddSemanticPath(partition.Id, partition.Role, roles, "multibag partition");
                foreach (HudInventorySlotBinding slot in partition.Slots)
                {
                    Require(slot.Id == $"{layout.Id}-slot-{slotOrdinal:00}", "HUD multibag slot order changed.");
                    AddSemanticPath(slot.Id, slot.Role, roles, "multibag slot");
                    AddRole(slot.Icon, roles, "multibag slot icon");
                    AddRole(slot.Cooldown, roles, "multibag slot cooldown");
                    AddRole(slot.Count, roles, "multibag slot count");
                    AddRole(slot.Prepared, roles, "multibag slot prepared");
                    slotOrdinal++;
                }
            }
            Require(slotOrdinal == layout.Capacity + 1, "HUD multibag partitions do not cover their layout.");
        }
    }

    private static void ValidateLoot(HudLootBagSystem system, HashSet<string> roles)
    {
        Require(system.Root == "loot-bag" && system.PageCapacity == HudProduct.LootPageSize &&
            system.TotalCapacity == HudProduct.LootEntryCount && system.MaxPages == 5 &&
            system.Items.Length == HudProduct.LootPageSize && system.Roles.Length == 7,
            "HUD loot-bag census changed.");
        AddRole(system.Prototype, roles, "loot prototype");
        for (int index = 0; index < system.Items.Length; index++)
        {
            HudLootItemBinding item = system.Items[index];
            Require(item.Id == $"loot-item-{index + 1:00}", "HUD loot item order changed.");
            AddSemanticPath(item.Id, item.Role, roles, "loot item");
            AddRole(item.Slot, roles, "loot slot");
            AddRole(item.Name, roles, "loot name");
            AddRole(item.Icon, roles, "loot icon");
            AddRole(item.Count, roles, "loot count");
        }
        ValidateRoles(system.Roles, roles, "loot.roles");
        HudLootActions actions = Required(system.Actions, "loot.actions");
        Require(actions.Open == "loot-open" && actions.Close == "loot-close" &&
            actions.TakeItem == "loot-take-item" && actions.TakeMoney == "loot-take-money" &&
            actions.TakeAll == "loot-take-all" && actions.DragAndDropItem == "loot-drag-and-drop-item" &&
            actions.ItemIndexArgument == "item-index" && actions.ItemIdState == "item-id" &&
            actions.WireCommand == "TakeLoot" && actions.MoneyWireIndex == -1 && actions.TakeAllWireIndex == -1,
            "HUD loot action contract changed.");
        ValidateState(system.State, "loot-bag");
    }

    private static void ValidateQuestLog(HudQuestLogSystem system, HashSet<string> roles)
    {
        Require(system.Root == "quest-log" && system.Rows.Length == HudProduct.QuestLogEntryCount &&
            system.Bookmarks.Length == HudProduct.QuestLogBookmarkCount && system.Roles.Length == 5,
            "HUD quest-log census changed.");
        AddRole(system.List, roles, "quest-log list");
        AddRole(system.EntryPrototype, roles, "quest-log prototype");
        foreach (HudQuestLogRowBinding row in system.Rows)
        {
            AddSemanticPath(row.Id, row.Role, roles, "quest-log row");
            Require(row.Roles.Length == 13, "HUD quest-log row role census changed.");
            ValidateRoles(row.Roles, roles, "quest-log row roles");
        }
        ValidateRoles(system.Bookmarks, roles, "quest-log bookmarks");
        HudQuestDetailPools detail = Required(system.Detail, "quest-log.detail");
        ValidatePool(detail.Objectives, 5, roles, "quest-log objectives");
        ValidatePool(detail.ObjectiveNoNumbers, 5, roles, "quest-log objectives without numbers");
        ValidatePool(detail.Reputation, 5, roles, "quest-log reputation");
        ValidatePool(detail.Currencies, 5, roles, "quest-log currencies");
        ValidatePool(detail.AlternativeTexts, 5, roles, "quest-log alternative reward text");
        ValidatePool(detail.MandatoryTexts, 5, roles, "quest-log mandatory reward text");
        ValidatePool(detail.AlternativeIcons, 5, roles, "quest-log alternative reward icons");
        ValidatePool(detail.MandatoryIcons, 5, roles, "quest-log mandatory reward icons");
        ValidatePool(detail.Secrets, 15, roles, "quest-log secrets");
        HudQuestSharingPolicy sharing = Required(system.Sharing, "quest-log.sharing");
        Require(sharing.Share == "quest-share" && sharing.Accept == "quest-share-accept" &&
            sharing.Decline == "quest-share-decline" && sharing.Abandon == "quest-abandon" &&
            sharing.AbandonConfirmationMilliseconds == 30_000, "HUD quest sharing contract changed.");
        ValidateRoles(system.Roles, roles, "quest-log.roles");
        ValidateState(system.State, "quest-log");
    }

    private static void ValidateQuestInfo(HudQuestInfoSystem system, HashSet<string> roles)
    {
        Require(system.Root == "quest-info" && system.Roles.Length == 8, "HUD quest-info census changed.");
        AddRole(system.Prototype, roles, "quest-info prototype");
        ValidateRoles(system.Roles, roles, "quest-info.roles");
        HudQuestInfoRuntimeMapping mapping = Required(system.RuntimeMapping, "quest-info.runtime_mapping");
        Require(mapping.DetailSystem == "quest-info" && mapping.OfferTurnInSystem == "npc-talk",
            "HUD quest-info runtime mapping changed.");
        ValidateState(system.State, "quest-info");
    }

    private static void ValidateNpcTalk(HudNpcTalkSystem system, HashSet<string> roles)
    {
        Require(system.Root == "npc-talk" && system.Roles.Length == 24, "HUD NPC-talk census changed.");
        ValidatePool(system.Options, 20, roles, "npc-talk options");
        AddRole(system.OptionPrototype, roles, "npc-talk option prototype");
        ValidatePool(system.Objectives, 6, roles, "npc-talk objectives");
        ValidatePool(system.Reputation, 5, roles, "npc-talk reputation");
        ValidatePool(system.Currencies, 5, roles, "npc-talk currencies");
        ValidatePool(system.AlternativeTexts, 5, roles, "npc-talk alternative reward text");
        ValidatePool(system.MandatoryTexts, 5, roles, "npc-talk mandatory reward text");
        ValidatePool(system.AlternativeIcons, 5, roles, "npc-talk alternative reward icons");
        ValidatePool(system.MandatoryIcons, 5, roles, "npc-talk mandatory reward icons");
        ValidatePool(system.AlternativeButtons, 5, roles, "npc-talk alternative reward buttons");
        ValidatePool(system.MandatoryButtons, 5, roles, "npc-talk mandatory reward buttons");
        ValidatePool(system.RewardGroups, 5, roles, "npc-talk reward groups");
        ValidateRoles(system.Roles, roles, "npc-talk.roles");
        HudReturnQuestAction action = Required(system.ReturnQuest, "npc-talk.return_quest");
        Require(action.Event == "return-quest" && action.QuestIdArgument == "quest-id" &&
            action.RewardIndexArgument == "reward-index", "HUD NPC quest return contract changed.");
        ValidateState(system.State, "npc-talk");
    }

    private static void ValidatePool(HudRolePool pool, int capacity, HashSet<string> roles, string label)
    {
        pool = Required(pool, label);
        Require(pool.Capacity == capacity && pool.Roles.Length == capacity, $"HUD {label} census changed.");
        ValidateNativeRole(pool.SourcePrototype, $"{label}.source_prototype");
        ValidateRoles(pool.Roles, roles, $"{label}.roles");
    }

    private static void ValidateState(HudOpenCloseState state, string label)
    {
        state = Required(state, $"{label}.state");
        ValidateId(state.Open, $"{label}.state.open");
        ValidateId(state.Closed, $"{label}.state.closed");
        Require(state.Open != state.Closed, $"HUD {label} open and closed states must differ.");
    }

    private static void ValidateCatalogs(HudProductManifest manifest, HashSet<string> roles)
    {
        HudCursorResource[] cursors = Required(manifest.Cursors, "cursors");
        HudCatalogResource[] sounds = Required(manifest.Sounds, "sounds");
        HudMaskResource[] masks = Required(manifest.Masks, "masks");
        Require(cursors.Length == 22 && sounds.Length == 11 && masks.Length == 3,
            "HUD resource catalog census changed.");
        var cursorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (HudCursorResource cursor in cursors)
        {
            ValidateCatalogEntry(cursor.Id, cursor.Resource, cursorIds, "cursor");
            Require(cursor.Dimensions.Width > 0 && cursor.Dimensions.Height > 0 && cursor.Hotspot.X >= 0 &&
                cursor.Hotspot.Y >= 0 && cursor.Hotspot.X < cursor.Dimensions.Width &&
                cursor.Hotspot.Y < cursor.Dimensions.Height, "HUD cursor dimensions or hotspot are invalid.");
        }
        var soundIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (HudCatalogResource sound in sounds) ValidateCatalogEntry(sound.Id, sound.Resource, soundIds, "sound");
        var maskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (HudMaskResource mask in masks)
        {
            ValidateCatalogEntry(mask.Id, mask.Resource, maskIds, "mask");
            Require(mask.Dimensions.Width > 0 && mask.Dimensions.Height > 0, "HUD mask dimensions are invalid.");
        }

        HudCursorBinding[] cursorBindings = Required(manifest.CursorBindings, "cursor_bindings");
        Require(cursorBindings.Length == 22, "HUD cursor binding census changed.");
        var cursorSemantics = new HashSet<string>(StringComparer.Ordinal);
        foreach (HudCursorBinding binding in cursorBindings)
        {
            ValidateId(binding.Semantic, "cursor semantic");
            Require(cursorSemantics.Add(binding.Semantic) && cursorIds.Contains(binding.Cursor),
                "HUD cursor binding is duplicated or references an unknown cursor.");
        }
        HudCursorAliases aliases = Required(manifest.CursorAliases, "cursor_aliases");
        Require(cursorIds.Contains(aliases.Default) && cursorIds.Contains(aliases.Hover) &&
            cursorIds.Contains(aliases.Text) && cursorIds.Contains(aliases.Drag),
            "HUD cursor alias references an unknown cursor.");

        HudMaskBinding[] maskBindings = Required(manifest.MaskBindings, "mask_bindings");
        Require(maskBindings.Length == 19, "HUD mask binding census changed.");
        var maskKeys = new HashSet<(string, HudMaskKind)>();
        foreach (HudMaskBinding binding in maskBindings)
        {
            Require(roles.Contains(binding.Role) && maskIds.Contains(binding.Mask) &&
                maskKeys.Add((binding.Role, binding.Kind)) && binding.Threshold.AlphaByteGreaterThanOrEqual == 1,
                "HUD mask binding is invalid.");
        }

        ValidateInputRoles(Required(manifest.InputRoles, "input_roles"), roles);
        ValidateInputBindings(Required(manifest.InputBindings, "input_bindings"), roles);
        HudSoundBinding[] soundBindings = Required(manifest.SoundBindings, "sound_bindings");
        Require(soundBindings.Length == 41, "HUD sound binding census changed.");
        var soundKeys = new HashSet<(string, HudSoundEvent)>();
        foreach (HudSoundBinding binding in soundBindings)
        {
            Require(roles.Contains(binding.Role) && soundIds.Contains(binding.Sound) &&
                soundKeys.Add((binding.Role, binding.Event)), "HUD sound binding is invalid.");
        }

        HudTimelineDefinition[] timelines = Required(manifest.Timelines, "timelines");
        Require(timelines.Length == Timelines.Length, "HUD timeline census changed.");
        for (int index = 0; index < timelines.Length; index++)
        {
            Require((timelines[index].Id, timelines[index].DurationMilliseconds) == Timelines[index],
                $"HUD timeline {index} changed.");
        }
    }

    private static void ValidateInputRoles(HudInputRoleBinding[] bindings, HashSet<string> roles)
    {
        Require(bindings.Length == 490, "HUD input role census changed.");
        var boundRoles = new HashSet<string>(StringComparer.Ordinal);
        var priorities = new HashSet<int>();
        foreach (HudInputRoleBinding binding in bindings)
        {
            Require(roles.Contains(binding.Role) && boundRoles.Add(binding.Role) && priorities.Add(binding.Priority),
                "HUD input role is duplicated or references an unknown semantic role.");
            HudInputRoute[] routes = Required(binding.Routes, "input role routes");
            Require(routes.Length > 0, "HUD input role has no routes.");
            var inputs = new HashSet<HudPhysicalInput>();
            foreach (HudInputRoute route in routes)
            {
                Require(route is not null && inputs.Add(route.Input), "HUD input role repeats a physical route.");
            }
        }
    }

    private static void ValidateInputBindings(HudSemanticInputBinding[] bindings, HashSet<string> roles)
    {
        Require(bindings.Length == 42, "HUD global input binding census changed.");
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        bool foundOptions = false;
        foreach (HudSemanticInputBinding binding in bindings)
        {
            ValidateId(binding.Input, "global input");
            Require(inputs.Add(binding.Input), "HUD global input is duplicated.");
            Require(binding.Target is null || roles.Contains(binding.Target),
                "HUD global input targets an unknown semantic role.");
            if (binding.Event == HudSemanticEvent.OpenOptions)
            {
                Require(binding.Input == "open-options" && binding.Target is null && !foundOptions,
                    "HUD open-options input binding changed.");
                foundOptions = true;
            }
        }
        Require(foundOptions, "HUD open-options input binding is missing.");
    }

    private static void ValidateCatalogEntry(
        string id, string resource, HashSet<string> ids, string label)
    {
        ValidateId(id, $"{label} id");
        ValidateResourcePath(resource, ".res", $"{label} resource");
        Require(ids.Add(id), $"HUD {label} id '{id}' is duplicated.");
    }

    private static void ValidateRoles(HudSemanticRole[] values, HashSet<string> roles, string label)
    {
        values = Required(values, label);
        foreach (HudSemanticRole value in values) AddRole(value, roles, label);
    }

    private static void AddRole(HudSemanticRole role, HashSet<string> roles, string label)
    {
        role = Required(role, label);
        AddSemanticPath(role.Id, role.Role, roles, label);
    }

    private static void AddSemanticPath(string id, string role, HashSet<string> roles, string label)
    {
        AddSemanticId(id, roles, label);
        ValidateNativeRole(role, $"{label}.role");
    }

    private static void AddSemanticId(string id, HashSet<string> roles, string label)
    {
        ValidateId(id, $"{label}.id");
        Require(roles.Add(id), $"HUD semantic role id '{id}' is duplicated.");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), $"HUD product manifest repeats property '{property.Name}' at {path}.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static void RejectNonIntegralNumbers(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            string token = element.GetRawText();
            Require(!token.Contains('.', StringComparison.Ordinal) &&
                !token.Contains('e', StringComparison.OrdinalIgnoreCase),
                $"HUD product manifest requires an integer token at {path}.");
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                RejectNonIntegralNumbers(property.Value, $"{path}.{property.Name}");
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                RejectNonIntegralNumbers(item, $"{path}[{index++}]");
        }
    }

    private static void ValidateId(string value, string label)
    {
        Require(!string.IsNullOrEmpty(value) && value[0] is >= 'a' and <= 'z', $"HUD {label} is invalid.");
        foreach (char character in value)
            Require(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-', $"HUD {label} is invalid.");
    }

    private static void ValidateNativeRole(string value, string label)
    {
        Require(!string.IsNullOrEmpty(value) && !value.Contains('\\') && !value.Contains(':'), $"HUD {label} is invalid.");
        Require(value.Split('/').All(part => part.Length > 0 && part is not "." and not ".."), $"HUD {label} is invalid.");
    }

    private static void ValidateResourcePath(string value, string extension, string label)
    {
        Require(!string.IsNullOrEmpty(value) && value.EndsWith(extension, StringComparison.Ordinal) &&
            !value.Contains('\\') && !value.Contains(':') && !value.StartsWith('/'), $"HUD {label} path is invalid.");
        Require(value.Split('/').All(part => part.Length > 0 && part is not "." and not ".."),
            $"HUD {label} path is invalid.");
    }

    private static T Required<T>(T? value, string label) where T : class =>
        value ?? throw new InvalidDataException($"HUD product manifest is missing {label}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
