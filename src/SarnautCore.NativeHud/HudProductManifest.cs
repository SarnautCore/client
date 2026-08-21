using System.Text.Json.Serialization;

namespace SarnautCore.NativeHud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudProductManifest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("runtime_scene")] string RuntimeScene,
    [property: JsonPropertyName("topology")] HudSceneTopology Topology,
    [property: JsonPropertyName("catalog_encoding")] HudCatalogEncoding CatalogEncoding,
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("timeline_resource")] string TimelineResource,
    [property: JsonPropertyName("chat_commands")] HudChatCommandCatalogReference ChatCommands,
    [property: JsonPropertyName("chat_antispam")] HudChatAntiSpamCatalogReference ChatAntiSpam,
    [property: JsonPropertyName("options_product")] HudExternalProductReference OptionsProduct,
    [property: JsonPropertyName("item_catalog")] HudItemCatalogReference ItemCatalog,
    [property: JsonPropertyName("external_actions")] HudExternalActions ExternalActions,
    [property: JsonPropertyName("window_policy")] HudWindowPolicy WindowPolicy,
    [property: JsonPropertyName("roots")] HudRootBinding[] Roots,
    [property: JsonPropertyName("systems")] HudSystemManifest Systems,
    [property: JsonPropertyName("cursors")] HudCursorResource[] Cursors,
    [property: JsonPropertyName("sounds")] HudCatalogResource[] Sounds,
    [property: JsonPropertyName("masks")] HudMaskResource[] Masks,
    [property: JsonPropertyName("timelines")] HudTimelineDefinition[] Timelines,
    [property: JsonPropertyName("cursor_bindings")] HudCursorBinding[] CursorBindings,
    [property: JsonPropertyName("cursor_aliases")] HudCursorAliases CursorAliases,
    [property: JsonPropertyName("mask_bindings")] HudMaskBinding[] MaskBindings,
    [property: JsonPropertyName("input_roles")] HudInputRoleBinding[] InputRoles,
    [property: JsonPropertyName("input_bindings")] HudSemanticInputBinding[] InputBindings,
    [property: JsonPropertyName("sound_bindings")] HudSoundBinding[] SoundBindings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudSceneTopology(
    [property: JsonPropertyName("product_root")] string ProductRoot,
    [property: JsonPropertyName("ui_root")] string UiRoot,
    [property: JsonPropertyName("target_selection_root")] string TargetSelectionRoot,
    [property: JsonPropertyName("target_selection_decal")] string TargetSelectionDecal);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCatalogEncoding(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("cursor_resource_type")] string CursorResourceType,
    [property: JsonPropertyName("mask_resource_type")] string MaskResourceType,
    [property: JsonPropertyName("sound_resource_type")] string SoundResourceType,
    [property: JsonPropertyName("timeline_resource_type")] string TimelineResourceType,
    [property: JsonPropertyName("theme_resource_type")] string ThemeResourceType);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudChatCommandCatalogReference(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("autocomplete_capacity")] int AutocompleteCapacity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudChatAntiSpamCatalogReference(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("product_key")] string ProductKey,
    [property: JsonPropertyName("resource")] string Resource);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudExternalProductReference(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("product_key")] string ProductKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudItemCatalogReference(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("product_key")] string ProductKey,
    [property: JsonPropertyName("key")] string Key);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudExternalActions(
    [property: JsonPropertyName("open_options")] string OpenOptions);

public enum HudWindowFocusOrder { LastOpened }
public enum HudEscapePolicy { CloseLastOpened }
public enum HudWindowPlacement { AuthoredScene }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudWindowPolicy(
    [property: JsonPropertyName("focus_order")] HudWindowFocusOrder FocusOrder,
    [property: JsonPropertyName("escape")] HudEscapePolicy Escape,
    [property: JsonPropertyName("placement")] HudWindowPlacement Placement);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudRootBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("native_role")] string NativeRole,
    [property: JsonPropertyName("decal_only")] bool DecalOnly);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudSystemManifest(
    [property: JsonPropertyName("world_input")] HudRoleSystem WorldInput,
    [property: JsonPropertyName("action_bar")] HudActionBarSystem ActionBar,
    [property: JsonPropertyName("unit_plates")] HudUnitPlatesSystem UnitPlates,
    [property: JsonPropertyName("overtips")] HudOvertipsSystem Overtips,
    [property: JsonPropertyName("combat_feedback")] HudCombatFeedbackSystem CombatFeedback,
    [property: JsonPropertyName("compass")] HudRoleSystem Compass,
    [property: JsonPropertyName("chat")] HudChatSystem Chat,
    [property: JsonPropertyName("chat_input")] HudChatInputSystem ChatInput,
    [property: JsonPropertyName("world_chat_bubbles")] HudRoleSystem WorldChatBubbles,
    [property: JsonPropertyName("quest_tracker")] HudQuestTrackerSystem QuestTracker,
    [property: JsonPropertyName("target_selection")] HudTargetSelectionSystem TargetSelection,
    [property: JsonPropertyName("character")] HudCharacterSystem Character,
    [property: JsonPropertyName("multibag")] HudMultiBagSystem MultiBag,
    [property: JsonPropertyName("loot_bag")] HudLootBagSystem LootBag,
    [property: JsonPropertyName("quest_log")] HudQuestLogSystem QuestLog,
    [property: JsonPropertyName("quest_info")] HudQuestInfoSystem QuestInfo,
    [property: JsonPropertyName("npc_talk")] HudNpcTalkSystem NpcTalk);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudRoleSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudChatSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudChatInputSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("autocomplete_capacity")] int AutocompleteCapacity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudSemanticRole(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudActionBarSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("slot_count")] int SlotCount,
    [property: JsonPropertyName("slots")] HudActionSlotBinding[] Slots);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudActionSlotBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("presentation")] HudSemanticRole Presentation,
    [property: JsonPropertyName("action")] HudSemanticRole Action);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudUnitPlatesSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("plates")] HudSemanticRole[] Plates);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudOvertipsSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("prototype")] HudSemanticRole Prototype,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCombatFeedbackSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("pools")] HudFeedbackPools Pools);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudFeedbackPools(
    [property: JsonPropertyName("avatar")] HudFeedbackPool Avatar,
    [property: JsonPropertyName("enemy")] HudFeedbackPool Enemy,
    [property: JsonPropertyName("experience")] HudFeedbackPool Experience);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudFeedbackPool(
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("lanes")] HudSemanticRole[] Lanes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestTrackerSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("rows")] HudQuestRowBinding[] Rows);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestRowBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("task")] HudSemanticRole Task,
    [property: JsonPropertyName("toggle")] HudSemanticRole Toggle);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudTargetSelectionSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("decal")] HudSemanticRole Decal,
    [property: JsonPropertyName("state")] HudTargetSelectionState State,
    [property: JsonPropertyName("sizing")] HudTargetSelectionSizing Sizing);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudTargetSelectionState(
    [property: JsonPropertyName("visible")] string Visible,
    [property: JsonPropertyName("hidden")] string Hidden,
    [property: JsonPropertyName("identity")] string Identity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudTargetSelectionSizing(
    [property: JsonPropertyName("extent_source")] HudTargetExtentSource ExtentSource,
    [property: JsonPropertyName("object_cut_area_scale")] HudFraction ObjectCutAreaScale,
    [property: JsonPropertyName("extra_radius")] HudFraction ExtraRadius,
    [property: JsonPropertyName("diameter_scale")] HudFraction DiameterScale,
    [property: JsonPropertyName("projection_axis")] HudProjectionAxis ProjectionAxis,
    [property: JsonPropertyName("projection_depth")] HudProjectionDepthPolicy ProjectionDepth,
    [property: JsonPropertyName("vertical_offset")] HudVerticalOffsetPolicy VerticalOffset,
    [property: JsonPropertyName("cull_mask")] uint CullMask,
    [property: JsonPropertyName("albedo_format")] string AlbedoFormat,
    [property: JsonPropertyName("upscale_factor")] uint UpscaleFactor,
    [property: JsonPropertyName("hidden_until_applied")] bool HiddenUntilApplied);

public enum HudTargetExtentSource { SelectedObjectCutTerrainAreaAndRadius }
public enum HudProjectionAxis { PositiveYToNegativeY }
public enum HudProjectionDepthPolicy { SelectedEntityVisualHeight }
public enum HudVerticalOffsetPolicy { EntityGroundAnchor }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudFraction(
    [property: JsonPropertyName("numerator")] uint Numerator,
    [property: JsonPropertyName("denominator")] uint Denominator);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCharacterSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("equipment")] HudEquipmentSlotBinding[] Equipment,
    [property: JsonPropertyName("stat_rows")] HudSemanticRole[] StatRows,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudEquipmentSlotBinding(
    [property: JsonPropertyName("identity")] HudEquipmentIdentity Identity,
    [property: JsonPropertyName("retail_code")] byte RetailCode,
    [property: JsonPropertyName("role")] HudSemanticRole Role);

public enum HudEquipmentIdentity
{
    MainHand, OffHand, Ranged, Helm, Mantle, Cloak, Armor, Gloves, Belt, Pants, Boots,
    Earrings, Necklace, Tabard, Shirt, Bracers, Ring1, Ring2, Trinket, DeathInsurance, Bag,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudOpenCloseState(
    [property: JsonPropertyName("open")] string Open,
    [property: JsonPropertyName("closed")] string Closed);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudMultiBagSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("max_slots")] int MaxSlots,
    [property: JsonPropertyName("max_visual_partitions")] int MaxVisualPartitions,
    [property: JsonPropertyName("layouts")] HudInventoryLayoutBinding[] Layouts,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudInventoryLayoutBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("partitions")] HudInventoryPartitionBinding[] Partitions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudInventoryPartitionBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("slots")] HudInventorySlotBinding[] Slots);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudInventorySlotBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("icon")] HudSemanticRole Icon,
    [property: JsonPropertyName("cooldown")] HudSemanticRole Cooldown,
    [property: JsonPropertyName("count")] HudSemanticRole Count,
    [property: JsonPropertyName("prepared")] HudSemanticRole Prepared);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudLootBagSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("page_capacity")] int PageCapacity,
    [property: JsonPropertyName("total_capacity")] int TotalCapacity,
    [property: JsonPropertyName("max_pages")] int MaxPages,
    [property: JsonPropertyName("prototype")] HudSemanticRole Prototype,
    [property: JsonPropertyName("items")] HudLootItemBinding[] Items,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("actions")] HudLootActions Actions,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudLootItemBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("slot")] HudSemanticRole Slot,
    [property: JsonPropertyName("name")] HudSemanticRole Name,
    [property: JsonPropertyName("icon")] HudSemanticRole Icon,
    [property: JsonPropertyName("count")] HudSemanticRole Count);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudLootActions(
    [property: JsonPropertyName("open")] string Open,
    [property: JsonPropertyName("close")] string Close,
    [property: JsonPropertyName("take_item")] string TakeItem,
    [property: JsonPropertyName("take_money")] string TakeMoney,
    [property: JsonPropertyName("take_all")] string TakeAll,
    [property: JsonPropertyName("drag_and_drop_item")] string DragAndDropItem,
    [property: JsonPropertyName("item_index_argument")] string ItemIndexArgument,
    [property: JsonPropertyName("item_id_state")] string ItemIdState,
    [property: JsonPropertyName("wire_command")] string WireCommand,
    [property: JsonPropertyName("money_wire_index")] int MoneyWireIndex,
    [property: JsonPropertyName("take_all_wire_index")] int TakeAllWireIndex);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestLogSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("list")] HudSemanticRole List,
    [property: JsonPropertyName("entry_prototype")] HudSemanticRole EntryPrototype,
    [property: JsonPropertyName("rows")] HudQuestLogRowBinding[] Rows,
    [property: JsonPropertyName("bookmarks")] HudSemanticRole[] Bookmarks,
    [property: JsonPropertyName("detail")] HudQuestDetailPools Detail,
    [property: JsonPropertyName("sharing")] HudQuestSharingPolicy Sharing,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestLogRowBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestDetailPools(
    [property: JsonPropertyName("objectives")] HudRolePool Objectives,
    [property: JsonPropertyName("objective_no_numbers")] HudRolePool ObjectiveNoNumbers,
    [property: JsonPropertyName("reputation")] HudRolePool Reputation,
    [property: JsonPropertyName("currencies")] HudRolePool Currencies,
    [property: JsonPropertyName("alternative_texts")] HudRolePool AlternativeTexts,
    [property: JsonPropertyName("mandatory_texts")] HudRolePool MandatoryTexts,
    [property: JsonPropertyName("alternative_icons")] HudRolePool AlternativeIcons,
    [property: JsonPropertyName("mandatory_icons")] HudRolePool MandatoryIcons,
    [property: JsonPropertyName("secrets")] HudRolePool Secrets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudRolePool(
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("source_prototype")] string SourcePrototype,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestSharingPolicy(
    [property: JsonPropertyName("share")] string Share,
    [property: JsonPropertyName("accept")] string Accept,
    [property: JsonPropertyName("decline")] string Decline,
    [property: JsonPropertyName("abandon")] string Abandon,
    [property: JsonPropertyName("abandon_confirmation_ms")] uint AbandonConfirmationMilliseconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestInfoSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("prototype")] HudSemanticRole Prototype,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("runtime_mapping")] HudQuestInfoRuntimeMapping RuntimeMapping,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudQuestInfoRuntimeMapping(
    [property: JsonPropertyName("detail_system")] string DetailSystem,
    [property: JsonPropertyName("offer_turn_in_system")] string OfferTurnInSystem);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudNpcTalkSystem(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("options")] HudRolePool Options,
    [property: JsonPropertyName("option_prototype")] HudSemanticRole OptionPrototype,
    [property: JsonPropertyName("objectives")] HudRolePool Objectives,
    [property: JsonPropertyName("reputation")] HudRolePool Reputation,
    [property: JsonPropertyName("currencies")] HudRolePool Currencies,
    [property: JsonPropertyName("alternative_texts")] HudRolePool AlternativeTexts,
    [property: JsonPropertyName("mandatory_texts")] HudRolePool MandatoryTexts,
    [property: JsonPropertyName("alternative_icons")] HudRolePool AlternativeIcons,
    [property: JsonPropertyName("mandatory_icons")] HudRolePool MandatoryIcons,
    [property: JsonPropertyName("alternative_buttons")] HudRolePool AlternativeButtons,
    [property: JsonPropertyName("mandatory_buttons")] HudRolePool MandatoryButtons,
    [property: JsonPropertyName("reward_groups")] HudRolePool RewardGroups,
    [property: JsonPropertyName("roles")] HudSemanticRole[] Roles,
    [property: JsonPropertyName("return_quest")] HudReturnQuestAction ReturnQuest,
    [property: JsonPropertyName("state")] HudOpenCloseState State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudReturnQuestAction(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("quest_id_argument")] string QuestIdArgument,
    [property: JsonPropertyName("reward_index_argument")] string RewardIndexArgument);

public enum HudMaskKind { Clip, Pick }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudMaskBinding(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("kind")] HudMaskKind Kind,
    [property: JsonPropertyName("mask")] string Mask,
    [property: JsonPropertyName("threshold")] HudMaskThreshold Threshold);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudMaskThreshold(
    [property: JsonPropertyName("alpha_byte_gte")] int AlphaByteGreaterThanOrEqual);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudInputRoleBinding(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("routes")] HudInputRoute[] Routes);

public enum HudPhysicalInput
{
    PointerMoved, PointerEntered, PointerExited, PrimaryPressed, PrimaryReleased,
    PrimaryDoublePressed, SecondaryPressed, SecondaryReleased, SecondaryDoublePressed,
    DragStarted, DragEnded, TextSubmitted,
}

public enum HudSemanticEvent
{
    ActivateAction, SelectWorldEntity, InteractWorldEntity, RequestFocus, ReleaseFocus, Cancel,
    PointerMoved, PointerEntered, PointerExited, PointerPrimaryPressed, PointerPrimaryReleased,
    PointerPrimaryDoublePressed, PointerSecondaryPressed, PointerSecondaryReleased,
    PointerSecondaryDoublePressed, DragStarted, DragEnded, SubmitChat, OpenOptions,
}

public enum HudSoundEvent { ActivateAction, SelectWorldEntity, Open, Close }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudInputRoute(
    [property: JsonPropertyName("input")] HudPhysicalInput Input,
    [property: JsonPropertyName("event")] HudSemanticEvent Event);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudSemanticInputBinding(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("event")] HudSemanticEvent Event,
    [property: JsonPropertyName("target")] string? Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudSoundBinding(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("event")] HudSoundEvent Event,
    [property: JsonPropertyName("sound")] string Sound);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCatalogResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("resource")] string Resource);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudDimensions(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudPointInt(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCursorResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("dimensions")] HudDimensions Dimensions,
    [property: JsonPropertyName("hotspot")] HudPointInt Hotspot);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudMaskResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("dimensions")] HudDimensions Dimensions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCursorBinding(
    [property: JsonPropertyName("semantic")] string Semantic,
    [property: JsonPropertyName("cursor")] string Cursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudCursorAliases(
    [property: JsonPropertyName("default")] string Default,
    [property: JsonPropertyName("hover")] string Hover,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("drag")] string Drag);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HudTimelineDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("duration_ms")] int DurationMilliseconds);
