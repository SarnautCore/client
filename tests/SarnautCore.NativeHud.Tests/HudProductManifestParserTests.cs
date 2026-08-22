using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudProductManifestParserTests
{
    private static readonly JsonSerializerOptions WriteOptions = CreateWriteOptions();

    [Fact]
    public void ParsesV2AndBuildsEveryContextFromSemanticBindings()
    {
        HudProductManifest manifest = HudProductManifestParser.Parse(Json());
        HudProduct product = HudProductManifestParser.BuildProduct(manifest);

        Assert.Equal(HudProductManifestParser.Schema, manifest.Schema);
        Assert.Equal(HudSemanticEvent.OpenOptions,
            Assert.Single(manifest.InputBindings, binding => binding.Input == "open-options").Event);
        Assert.Equal((uint)1, manifest.Systems.TargetSelection.Sizing.CullMask);
        Assert.Equal("DXT5 RGBA8", manifest.Systems.TargetSelection.Sizing.AlbedoFormat);
        Assert.Equal(HudProduct.ActionSlotCount, product.ActionSlots.Length);
        Assert.Equal("action-01", product.ActionSlots[0].Value);
        Assert.Equal(HudProduct.InventoryLayoutCount, product.Contexts.Inventory.Layouts.Length);
        Assert.Equal(60, product.Contexts.Inventory.Layouts[^1].Capacity);
        Assert.Equal(HudProduct.LootPageSize, product.Contexts.Loot.PageSlots.Length);
        Assert.Equal(HudProduct.QuestLogEntryCount, product.Contexts.QuestLog.Entries.Length);
        Assert.Equal(HudProduct.QuestTalkOptionCount, product.Contexts.QuestInfo.TalkOptions.Length);
        Assert.Equal("quest-info", product.Contexts.QuestInfo.DetailRoot.Value);
        Assert.Equal("npc-talk", product.Contexts.QuestInfo.InteractionRoot.Value);
        Assert.Equal("character-equipment-bag", product.Contexts.Character.BagSlot.Value);
        Assert.Equal("character-equipment-death-insurance", product.Contexts.Character.DeathInsuranceSlot.Value);
    }

    [Fact]
    public void RejectsLooseJsonAndUnknownSchemaFields()
    {
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(""));
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(Json() + " trailing"));

        JsonObject duplicate = Object();
        string duplicateJson = duplicate.ToJsonString().Replace(
            "\"schema\":\"sarnaut.hud-product/v2\"",
            "\"schema\":\"sarnaut.hud-product/v2\",\"schema\":\"sarnaut.hud-product/v2\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(duplicateJson));

        JsonObject unknown = Object();
        unknown["legacy_converter"] = true;
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(unknown.ToJsonString()));

        string nonIntegral = Json().Replace("\"slot_count\":36", "\"slot_count\":36.0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(nonIntegral));
    }

    [Fact]
    public void RejectsWrongCensusesAndDuplicateSemanticIds()
    {
        JsonObject actions = Object();
        actions["systems"]!["action_bar"]!["slots"]!.AsArray().RemoveAt(35);
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(actions.ToJsonString()));

        JsonObject layouts = Object();
        layouts["systems"]!["multibag"]!["layouts"]![0]!["capacity"] = 13;
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(layouts.ToJsonString()));

        JsonObject duplicateRole = Object();
        duplicateRole["systems"]!["compass"]!["roles"]![0]!["id"] = "world-input-role-01";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(duplicateRole.ToJsonString()));

        JsonObject inputCensus = Object();
        inputCensus["input_roles"]!.AsArray().RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(inputCensus.ToJsonString()));
    }

    [Fact]
    public void RejectsBrokenExternalReferencesAndResourceEscapes()
    {
        JsonObject options = Object();
        options["options_product"]!["product_key"] = "embedded-options";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(options.ToJsonString()));

        JsonObject items = Object();
        items["item_catalog"]!["version"] = 2;
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(items.ToJsonString()));

        JsonObject traversal = Object();
        traversal["theme"] = "../theme.res";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(traversal.ToJsonString()));

        JsonObject originalFormat = Object();
        originalFormat["sounds"]![0]!["resource"] = "catalogs/button.xdb";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(originalFormat.ToJsonString()));
    }

    [Fact]
    public void RejectsChangedTargetSizingAndMissingOpenOptionsBinding()
    {
        JsonObject sizing = Object();
        sizing["systems"]!["target_selection"]!["sizing"]!["cull_mask"] = 2;
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(sizing.ToJsonString()));

        JsonObject format = Object();
        format["systems"]!["target_selection"]!["sizing"]!["albedo_format"] = "DXT1 RGB8";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(format.ToJsonString()));

        JsonObject action = Object();
        JsonNode openOptions = action["input_bindings"]!.AsArray()
            .Single(node => node!["input"]!.GetValue<string>() == "open-options")!;
        openOptions["event"] = "cancel";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(action.ToJsonString()));
    }

    [Fact]
    public void RejectsUnknownCatalogAndInputReferences()
    {
        JsonObject mask = Object();
        mask["mask_bindings"]![0]!["mask"] = "missing";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(mask.ToJsonString()));

        JsonObject sound = Object();
        sound["sound_bindings"]![0]!["role"] = "missing";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(sound.ToJsonString()));

        JsonObject target = Object();
        target["input_bindings"]![1]!["target"] = "missing";
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(target.ToJsonString()));

        JsonObject duplicatePriority = Object();
        duplicatePriority["input_roles"]![1]!["priority"] = duplicatePriority["input_roles"]![0]!["priority"]!.DeepClone();
        Assert.Throws<InvalidDataException>(() => HudProductManifestParser.Parse(duplicatePriority.ToJsonString()));
    }

    private static JsonObject Object() => JsonNode.Parse(Json())!.AsObject();
    private static string Json() => JsonSerializer.Serialize(Product(), WriteOptions);

    private static HudProductManifest Product()
    {
        HudSystemManifest systems = Systems();
        string[] semanticIds = SemanticIds(systems).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(semanticIds.Length >= 497);
        HudInputRoleBinding[] inputRoles = semanticIds.Take(497)
            .Select((id, index) => new HudInputRoleBinding(
                id,
                index + 1,
                [new HudInputRoute(HudPhysicalInput.PointerEntered, HudSemanticEvent.PointerEntered)]))
            .ToArray();
        HudSemanticInputBinding[] inputs =
        [
            new("cancel", HudSemanticEvent.Cancel, null),
            new("select-world-entity", HudSemanticEvent.SelectWorldEntity, "world-input-role-01"),
            new("interact-world-entity", HudSemanticEvent.InteractWorldEntity, "world-input-role-01"),
            new("focus-chat", HudSemanticEvent.RequestFocus, "chat-input-role-01"),
            new("submit-chat", HudSemanticEvent.SubmitChat, "chat-input-role-01"),
            .. Enumerable.Range(1, 36).Select(index => new HudSemanticInputBinding(
                $"action-{index:00}", HudSemanticEvent.ActivateAction, $"action-{index:00}")),
            new("open-options", HudSemanticEvent.OpenOptions, null),
        ];
        string[] cursorIds =
        [
            "default", "default-disabled", "attack", "attack-disabled", "cast", "cast-disabled",
            "drag", "drag-disabled", "inspect", "inspect-disabled", "loot", "loot-disabled",
            "repair", "repair-disabled", "talk", "talk-disabled", "trade", "trade-disabled",
            "train", "train-disabled", "use", "use-disabled",
        ];
        string[] soundIds =
        [
            "button-press", "character-open", "character-close", "bag-open", "bag-close",
            "loot-bag-open", "loot-bag-close", "quest-log-open", "quest-log-close",
            "npc-talk-open", "npc-talk-close",
        ];
        return new HudProductManifest(
            HudProductManifestParser.Schema,
            "hud.scn",
            new HudSceneTopology("HudProduct", "Main", "TargetSelection", "TargetSelection/TargetSelect"),
            new HudCatalogEncoding(HudProductManifestParser.CatalogSchema, "Texture2D", "Texture2D", "AudioStream", "Resource", "Theme"),
            "catalogs/theme.res",
            "catalogs/timelines.res",
            new HudChatCommandCatalogReference("sarnaut.chat-commands/v1", "catalogs/chat-commands-eng.json", "eng", 22),
            new HudExternalProductReference("sarnaut.options-product/v1", "options"),
            new HudItemCatalogReference("sarnaut.item-presentation-catalog", 1, "hud.items.inst-league1", "item-id"),
            new HudExternalActions("open-options"),
            new HudWindowPolicy(HudWindowFocusOrder.LastOpened, HudEscapePolicy.CloseLastOpened, HudWindowPlacement.AuthoredScene),
            RootBindings(),
            systems,
            cursorIds.Select(id => new HudCursorResource(id, $"catalogs/cursors/{id}.res", new(128, 128), new(0, 0))).ToArray(),
            soundIds.Select(id => new HudCatalogResource(id, $"catalogs/sounds/{id}.res")).ToArray(),
            [
                new("portrait-clip", "catalogs/masks/portrait-clip.res", new(256, 256)),
                new("portrait-pick", "catalogs/masks/portrait-pick.res", new(128, 128)),
                new("compass-pick", "catalogs/masks/compass-pick.res", new(256, 256)),
            ],
            TimelineDefinitions(),
            cursorIds.Select(id => new HudCursorBinding(id, id)).ToArray(),
            new HudCursorAliases("default", "default", "default", "drag"),
            semanticIds.Take(19).Select((id, index) => new HudMaskBinding(
                id, index % 2 == 0 ? HudMaskKind.Clip : HudMaskKind.Pick,
                index % 3 == 0 ? "portrait-clip" : index % 3 == 1 ? "portrait-pick" : "compass-pick", new(1))).ToArray(),
            inputRoles,
            inputs,
            semanticIds.Take(41).Select((id, index) => new HudSoundBinding(
                id, index % 2 == 0 ? HudSoundEvent.Open : HudSoundEvent.Close,
                soundIds[index % soundIds.Length])).ToArray())
        {
            ChatAntispam = new(
                "sarnaut.chat-antispam/v1",
                "chat-antispam",
                "catalogs/chat-antispam.json"),
        };
    }

    private static HudSystemManifest Systems()
    {
        HudRoleSystem Basic(string root, int count) => new(root, Roles(root, count));
        return new HudSystemManifest(
            Basic("world-input", 2),
            new HudActionBarSystem("action-bar", 36, Enumerable.Range(1, 36).Select(index => new HudActionSlotBinding(
                $"action-slot-{index:00}", Role($"action-{index:00}-presentation"), Role($"action-{index:00}"))).ToArray()),
            new HudUnitPlatesSystem("unit-plates", Roles("unit-portrait", 9),
                new[] { "avatar", "target", "target-target", "pet", "mount", "party-01", "party-02", "party-03", "party-04", "party-05" }
                    .Select(id => Role($"unit-plate-{id}")).ToArray()),
            new HudOvertipsSystem("overtips", Role("overtip-prototype"), Roles("overtip", 4)),
            new HudCombatFeedbackSystem("combat-feedback", new(
                Feedback("avatar"), Feedback("enemy"), Feedback("experience"))),
            Basic("compass", 3),
            new HudChatSystem("chat", Roles("chat", 4)),
            new HudChatInputSystem("chat-input", Roles("chat-input", 8), 22),
            Basic("world-chat-bubbles", 5),
            new HudQuestTrackerSystem("quest-tracker", 20, Roles("quest-tracker", 4),
                Enumerable.Range(1, 20).Select(index => new HudQuestRowBinding(
                    $"quest-row-{index:00}", $"Node/quest-row-{index:00}",
                    Role($"quest-row-{index:00}-task"), Role($"quest-row-{index:00}-toggle"))).ToArray()),
            new HudTargetSelectionSystem("target-selection", Role("target-selection-decal"),
                new("target-selection-visible", "target-selection-hidden", "target-selection-target-id"),
                new(HudTargetExtentSource.SelectedObjectCutTerrainAreaAndRadius, new(1, 2), new(1, 2), new(2, 1),
                    HudProjectionAxis.PositiveYToNegativeY, HudProjectionDepthPolicy.SelectedEntityVisualHeight,
                    HudVerticalOffsetPolicy.EntityGroundAnchor, 1, "DXT5 RGBA8", 4, true)),
            Character(), Inventory(), Loot(), QuestLog(), QuestInfo(), NpcTalk())
        {
            MessageBox = MessageBox(),
        };
    }

    private static HudCharacterSystem Character()
    {
        (HudEquipmentIdentity Identity, byte Code, string Id)[] equipment =
        [
            (HudEquipmentIdentity.MainHand,14,"main-hand"),(HudEquipmentIdentity.OffHand,15,"off-hand"),
            (HudEquipmentIdentity.Ranged,16,"ranged"),(HudEquipmentIdentity.Helm,0,"helm"),
            (HudEquipmentIdentity.Mantle,4,"mantle"),(HudEquipmentIdentity.Cloak,12,"cloak"),
            (HudEquipmentIdentity.Armor,1,"armor"),(HudEquipmentIdentity.Gloves,5,"gloves"),
            (HudEquipmentIdentity.Belt,7,"belt"),(HudEquipmentIdentity.Pants,2,"pants"),
            (HudEquipmentIdentity.Boots,3,"boots"),(HudEquipmentIdentity.Earrings,10,"earrings"),
            (HudEquipmentIdentity.Necklace,11,"necklace"),(HudEquipmentIdentity.Tabard,18,"tabard"),
            (HudEquipmentIdentity.Shirt,13,"shirt"),(HudEquipmentIdentity.Bracers,6,"bracers"),
            (HudEquipmentIdentity.Ring1,8,"ring-1"),(HudEquipmentIdentity.Ring2,9,"ring-2"),
            (HudEquipmentIdentity.Trinket,19,"trinket"),(HudEquipmentIdentity.Bag,20,"bag"),
            (HudEquipmentIdentity.DeathInsurance,21,"death-insurance"),
        ];
        return new("character", equipment.Select(item => new HudEquipmentSlotBinding(
            item.Identity, item.Code, Role($"character-equipment-{item.Id}"))).ToArray(),
            Roles("character-stat", 14), Roles("character-role", 6), State("character"));
    }

    private static HudMultiBagSystem Inventory()
    {
        int[] capacities = [12, 16, 18, 24, 30, 36, 42, 48, 54, 60];
        int[][] partitions = [[12], [16], [12, 6], [16, 8], [30], [8, 8, 8, 6, 6], [30, 12], [12, 12, 12, 12], [30, 12, 12], [30, 30]];
        var layouts = new HudInventoryLayoutBinding[capacities.Length];
        for (int layoutIndex = 0; layoutIndex < capacities.Length; layoutIndex++)
        {
            string layoutId = $"multibag-layout-{capacities[layoutIndex]}";
            int slot = 1;
            layouts[layoutIndex] = new(layoutId, $"Node/{layoutId}", capacities[layoutIndex],
                partitions[layoutIndex].Select((capacity, partitionIndex) =>
                {
                    string partitionId = $"{layoutId}-partition-{partitionIndex + 1:00}";
                    return new HudInventoryPartitionBinding(partitionId, $"Node/{partitionId}", capacity,
                        Enumerable.Range(slot, capacity).Select(index => InventorySlot(layoutId, index)).ToArray());
                }).Select(partition => { slot += partition.Capacity; return partition; }).ToArray());
        }
        return new("multibag", 60, 5, layouts, Roles("multibag-role", 7), State("multibag"));
    }

    private static HudInventorySlotBinding InventorySlot(string layout, int index)
    {
        string id = $"{layout}-slot-{index:00}";
        return new(id, $"Node/{id}", Role($"{id}-icon"), Role($"{id}-cooldown"), Role($"{id}-count"), Role($"{id}-prepared"));
    }

    private static HudLootBagSystem Loot() => new(
        "loot-bag", 4, 20, 5, Role("loot-prototype"),
        Enumerable.Range(1, 4).Select(index =>
        {
            string id = $"loot-item-{index:00}";
            return new HudLootItemBinding(id, $"Node/{id}", Role($"{id}-slot"), Role($"{id}-name"), Role($"{id}-icon"), Role($"{id}-count"));
        }).ToArray(), Roles("loot-role", 7),
        new("loot-open", "loot-close", "loot-take-item", "loot-take-money", "loot-take-all", "loot-drag-and-drop-item", "item-index", "item-id", "TakeLoot", -1, -1), State("loot-bag"));

    private static HudQuestLogSystem QuestLog() => new(
        "quest-log", Role("quest-log-list"), Role("quest-log-prototype"),
        Enumerable.Range(1, 20).Select(index =>
        {
            string id = $"quest-log-row-{index:00}";
            return new HudQuestLogRowBinding(id, $"Node/{id}", Roles($"{id}-role", 13));
        }).ToArray(),
        [
            new(Role("quest-log-bookmark-zones"), "quest-log-show-zones"),
            new(Role("quest-log-bookmark-completed"), "quest-log-show-completed"),
            new(Role("quest-log-bookmark-world-secrets"), "quest-log-show-world-secrets"),
        ],
        new(Role("quest-log-folder-toggle"), "quest-log-toggle-folder"),
        new(Pool("quest-log-objective", 5), Pool("quest-log-objective-no-number", 5),
            Pool("quest-log-reputation", 5), Pool("quest-log-currency", 5),
            Pool("quest-log-alternative-text", 5), Pool("quest-log-mandatory-text", 5),
            Pool("quest-log-alternative-icon", 5), Pool("quest-log-mandatory-icon", 5),
            Pool("quest-log-secret", 15)),
        new("quest-share", "quest-share-accept", "quest-share-decline", "message-box", "quest-abandon", 30_000),
        Roles("quest-log-role", 5), State("quest-log"));

    private static HudQuestInfoSystem QuestInfo() => new(
        "quest-info", Role("quest-info-prototype"), Roles("quest-info-role", 8),
        new("quest-info", "npc-talk"), State("quest-info"));

    private static HudNpcTalkSystem NpcTalk() => new(
        "npc-talk", Pool("npc-talk-option", 20), Role("npc-talk-option-prototype"),
        Pool("npc-talk-objective", 6), Pool("npc-talk-reputation", 5), Pool("npc-talk-currency", 5),
        Pool("npc-talk-alternative-text", 5), Pool("npc-talk-mandatory-text", 5),
        Pool("npc-talk-alternative-icon", 5), Pool("npc-talk-mandatory-icon", 5),
        Pool("npc-talk-alternative-button", 5), Pool("npc-talk-mandatory-button", 5),
        Pool("npc-talk-reward-group", 5), Roles("npc-talk-role", 24),
        new("return-quest", "quest-id", "reward-index"), State("npc-talk"));

    private static HudMessageBoxSystem MessageBox()
    {
        HudMessageBoxButton Button(string id, string action) => new(Role(id), action);
        HudMessageBoxInstance Instance(int ordinal)
        {
            string id = $"message-box-{ordinal:00}";
            return new(
                id,
                $"Node/{id}",
                Role($"{id}-title"),
                Role($"{id}-body"),
                Role($"{id}-icon"),
                Role($"{id}-progress"),
                Role($"{id}-timer-label"),
                Role($"{id}-button-tab"),
                Role($"{id}-button-container"),
                Button($"{id}-accept", "message-box-answer-accept"),
                Button($"{id}-decline", "message-box-answer-decline"),
                Button($"{id}-confirm", "message-box-answer-confirm"));
        }

        return new(
            "message-box",
            2,
            new(
                Role("message-box-prototype"),
                Role("message-box-header-prototype"),
                Role("message-box-text-prototype"),
                Role("message-box-progress-prototype"),
                Role("message-box-button-tab-prototype"),
                Role("message-box-button-container-prototype"),
                Role("message-box-accept-prototype"),
                Role("message-box-decline-prototype"),
                Role("message-box-confirm-prototype")),
            [Instance(1), Instance(2)],
            new("queue", "request-order", "request-priority"),
            new("second-timer", "request-default-button"),
            new(
                "message-box-answer-none",
                "message-box-answer-accept",
                "message-box-answer-decline",
                "message-box-answer-confirm"));
    }

    private static HudRolePool Pool(string id, int count) => new(count, $"Node/{id}-prototype", Roles(id, count));
    private static HudFeedbackPool Feedback(string id) => new(5, Roles($"feedback-{id}", 5));
    private static HudOpenCloseState State(string id) => new($"{id}-open", $"{id}-closed");
    private static HudSemanticRole Role(string id) => new(id, $"Node/{id}");
    private static HudSemanticRole[] Roles(string prefix, int count) =>
        Enumerable.Range(1, count).Select(index => Role($"{prefix}-role-{index:00}")).ToArray();

    private static IEnumerable<string> SemanticIds(object? value)
    {
        if (value is null || value is string || value.GetType().IsEnum) yield break;
        if (value is HudSemanticRole role) { yield return role.Id; yield break; }
        if (value is HudActionSlotBinding action) yield return action.Id;
        if (value is HudQuestRowBinding tracker) yield return tracker.Id;
        if (value is HudInventoryLayoutBinding layout) yield return layout.Id;
        if (value is HudInventoryPartitionBinding partition) yield return partition.Id;
        if (value is HudInventorySlotBinding slot) yield return slot.Id;
        if (value is HudLootItemBinding loot) yield return loot.Id;
        if (value is HudQuestLogRowBinding quest) yield return quest.Id;
        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
                foreach (string id in SemanticIds(item)) yield return id;
            yield break;
        }
        if (!value.GetType().Namespace!.StartsWith("SarnautCore.NativeHud", StringComparison.Ordinal)) yield break;
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            foreach (string id in SemanticIds(property.GetValue(value))) yield return id;
    }

    private static HudRootBinding[] RootBindings() =>
        new[] { "world-input", "action-bar", "unit-plates", "overtips", "combat-feedback", "compass", "chat", "chat-input", "world-chat-bubbles", "quest-tracker", "target-selection", "character", "multibag", "loot-bag", "quest-log", "quest-info", "npc-talk", "message-box" }
            .Select(id => new HudRootBinding(id, $"Root/{id}", id == "target-selection")).ToArray();

    private static HudTimelineDefinition[] TimelineDefinitions() =>
    [
        new("entry-fade",10), new("message-move",350), new("message-fade-in",350),
        new("message-fade-solid",1200), new("message-fade-out",900), new("glow-resize",560),
        new("glow-fade-in",560), new("text-fade-in",350), new("damage-text-scale",300),
        new("damage-vertical-shift",150), new("damage-horizontal-shift",150),
        new("damage-drop-shift",300), new("damage-fade-out",200), new("critical-glow-fade",1680),
    ];

    private static JsonSerializerOptions CreateWriteOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
