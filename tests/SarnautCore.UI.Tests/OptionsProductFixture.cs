using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SarnautCore.UI.Tests;

internal static class OptionsProductFixture
{
    public static string Json()
    {
        JsonArray options = BuildOptions();
        JsonArray pages = BuildPages(options);
        string[] presetKeys = [.. OptionsProduct.RequiredPresetOptionIds];

        var root = new JsonObject
        {
            ["schema_id"] = OptionsProduct.SchemaId,
            ["scene"] = OptionsProductLocation.PlainRootScene,
            ["layout"] = new JsonObject
            {
                ["width"] = 750,
                ["height"] = 673,
                ["horizontal_align"] = "center",
                ["vertical_align"] = "center",
                ["priority"] = 5400,
                ["child_order"] = new JsonArray(
                    OptionsProduct.RequiredChildOrder.Select(value => JsonValue.Create(value)).ToArray()),
            },
            ["pages"] = pages,
            ["options"] = options,
            ["graphics_presets"] = new JsonArray(
                Enumerable.Range(0, 5).Select(index =>
                {
                    var values = new JsonObject();
                    for (int keyIndex = 0; keyIndex < presetKeys.Length; keyIndex++)
                    {
                        values[presetKeys[keyIndex]] = index == 0 && keyIndex < 2
                            ? keyIndex + 2
                            : index % 2;
                    }

                    return (JsonNode)new JsonObject
                    {
                        ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["values"] = values,
                    };
                }).ToArray()),
            ["binding_slots"] = 2,
            ["binding_sections"] = BuildBindings(),
            ["content"] = new JsonObject
            {
                ["scenes"] = ContentPaths(
                    OptionsProductLocation.PlainRootScene,
                    "screens/widget_{0:00}.tscn",
                    68),
                ["resources"] = new JsonArray(),
                ["textures"] = ContentPaths(null, "textures/texture_{0:00}.png", 33),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray ContentPaths(string? first, string pattern, int count)
    {
        IEnumerable<string> values = Enumerable.Range(0, count)
            .Select(index => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                pattern,
                index));
        if (first is not null)
        {
            values = values.Prepend(first);
        }

        return new JsonArray(values
            .Order(StringComparer.Ordinal)
            .Select(value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
    }

    public static OptionsProduct Parse()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json()));
        return OptionsProductManifestParser.Parse(stream);
    }

    public static RecordingOptionsAdapters Adapters(OptionsProduct product)
    {
        var adapters = new RecordingOptionsAdapters();
        foreach (OptionDefinition option in product.Options)
        {
            if (option.DataKind == OptionDataKind.Boolean)
            {
                adapters.Settings.Defaults[option.Id] = OptionScalar.FromBoolean(false);
            }

            if (option.Handler is OptionHandler.Resolution or OptionHandler.Localization)
            {
                ImmutableArray<OptionScalar> choices = option.Handler == OptionHandler.Resolution
                    ? [OptionScalar.FromText("1280x720"), OptionScalar.FromText("1920x1080")]
                    : [OptionScalar.FromText("eng"), OptionScalar.FromText("rus")];
                adapters.Settings.DynamicChoices[option.Id] = choices;
                adapters.Settings.Defaults[option.Id] = choices[0];
                adapters.Settings.Current[option.Id] = choices[0];
            }
        }

        foreach (OptionDefinition option in product.Options.Where(option =>
                     option.DataKind != OptionDataKind.Action))
        {
            OptionScalar detected = option.DataKind == OptionDataKind.Boolean
                ? OptionScalar.FromBoolean(true)
                : option.Values.Length > 0
                    ? option.Values[^1]
                    : adapters.Settings.DynamicChoices[option.Id][^1];
            adapters.Settings.Detected[option.Id] = detected;
        }

        return adapters;
    }

    private static JsonArray BuildOptions()
    {
        string[] ids =
        [
            "gfxFullScreen", "gfx_gamma", "gfxResolution", "gfx_vsync",
            "gfxSystemSpecDefault", "gfxSystemSpec", "gfx_anisotropic_filter",
            "gfx_antialias", "gfx_sharpen", "gfx_simple_terrain", "gfx_simple_water",
            "gfx_texture_manager", "cpu_load_factor", "gfx_fade_factor", "gfx_fog_factor",
            "gfx_lod_factor", "use_area_effect", "use_post_effect", "resolution_boost",
            "show_grass", "gfx_grass_density", "gfx_simple_astral", "masterVolume", "muteAll",
            "ambientVolume", "interfaceVolume", "musicVolume", "sfxVolume", "voiceVolume",
            "localization", "use_move_by_click", "simplified_move", "input_swap_mouse_buttons",
            "opt_mission_max_click_delay_ms", "opt_mission_max_click_threshold",
            "camera_bind_to_avatar", "opt_mission_player_camera_invert_y_factor",
            "show_all_overtips", "show_all_titles", "show_all_healthbars", "show_all_status",
            "show_hostile_overtips", "show_hostile_titles", "show_hostile_healthbars",
            "show_hostile_status", "chat_bubbles_show", "emote_icons_show", "chat_bubbles_opacity",
        ];
        var booleans = new HashSet<string>(
        [
            "gfxFullScreen", "gfx_vsync", "gfx_simple_terrain", "gfx_simple_water",
            "use_area_effect", "use_post_effect", "resolution_boost", "show_grass", "muteAll",
            "use_move_by_click", "simplified_move", "input_swap_mouse_buttons",
            "camera_bind_to_avatar", "opt_mission_player_camera_invert_y_factor",
            "chat_bubbles_show", "emote_icons_show",
        ], StringComparer.Ordinal);
        var discrete = new HashSet<string>(
        [
            "gfxResolution", "gfxSystemSpec", "gfx_anisotropic_filter", "gfx_texture_manager",
            "cpu_load_factor", "gfx_simple_astral", "localization", "show_all_overtips",
            "show_all_titles", "show_all_healthbars", "show_all_status", "show_hostile_overtips",
            "show_hostile_titles", "show_hostile_healthbars", "show_hostile_status",
            "chat_bubbles_opacity",
        ], StringComparer.Ordinal);
        var listViews = new HashSet<string>(
        [
            "gfxResolution", "gfx_anisotropic_filter", "gfx_antialias", "gfx_texture_manager",
            "cpu_load_factor", "gfx_simple_astral", "localization", "show_all_overtips",
            "show_all_titles", "show_all_healthbars", "show_all_status", "show_hostile_overtips",
            "show_hostile_titles", "show_hostile_healthbars", "show_hostile_status",
        ], StringComparer.Ordinal);
        var user = new HashSet<string>(
        [
            "show_all_overtips", "show_all_titles", "show_all_healthbars", "show_all_status",
            "show_hostile_overtips", "show_hostile_titles", "show_hostile_healthbars",
            "show_hostile_status", "chat_bubbles_show", "emote_icons_show", "chat_bubbles_opacity",
        ], StringComparer.Ordinal);
        var audio = new HashSet<string>(
        [
            "masterVolume", "muteAll", "ambientVolume", "interfaceVolume", "musicVolume",
            "sfxVolume", "voiceVolume",
        ], StringComparer.Ordinal);
        var live = new HashSet<string>(audio, StringComparer.Ordinal);
        live.Remove("muteAll");
        var booleanTrue = new HashSet<string>(
        [
            "use_area_effect", "use_post_effect", "resolution_boost", "show_grass",
            "chat_bubbles_show", "emote_icons_show",
        ], StringComparer.Ordinal);

        var options = new JsonArray();
        foreach (string id in ids)
        {
            string dataKind = id == "gfxSystemSpecDefault"
                ? "action"
                : booleans.Contains(id)
                    ? "boolean"
                    : discrete.Contains(id) ? "discrete" : "discrete_float";
            string viewKind = dataKind switch
            {
                "action" => "button",
                "boolean" => "checkbox",
                _ => listViews.Contains(id) ? "list" : "slider",
            };
            string handler = id switch
            {
                "gfxResolution" => "resolution",
                "localization" => "localization",
                "gfxSystemSpec" => "quality_preset",
                "gfxSystemSpecDefault" => "autodetect",
                _ when audio.Contains(id) => "audio",
                _ => "none",
            };
            var values = new JsonArray();
            if (dataKind is "discrete" or "discrete_float"
                && id is not ("gfxResolution" or "localization"))
            {
                int valueCount = id switch
                {
                    "gfxSystemSpec" => 6,
                    "masterVolume" => 3,
                    "chat_bubbles_opacity" => 11,
                    _ => 2,
                };
                for (int value = 0; value < valueCount; value++)
                {
                    values.Add(id == "masterVolume" ? value * 0.5 : value);
                }
            }

            int authoredDefault = dataKind == "boolean"
                ? booleanTrue.Contains(id) ? 1 : 0
                : id == "gfxSystemSpec" ? 0
                : id == "chat_bubbles_opacity" ? 7
                : values.Count > 0 ? 5 : 0;
            int effectiveDefault = values.Count > 0
                ? Math.Min(authoredDefault, values.Count - 1)
                : authoredDefault;
            options.Add(new JsonObject
            {
                ["id"] = id,
                ["page"] = string.Empty,
                ["group"] = string.Empty,
                ["block"] = string.Empty,
                ["name"] = string.Empty,
                ["description"] = string.Empty,
                ["storage"] = user.Contains(id) ? "user" : "global",
                ["data_kind"] = dataKind,
                ["view_kind"] = viewKind,
                ["authored_default_index"] = authoredDefault,
                ["effective_default_index"] = effectiveDefault,
                ["declared_value_count"] = id == "chat_bubbles_opacity" ? 0 : values.Count,
                ["values"] = values,
                ["value_names"] = new JsonArray(),
                ["value_descriptions"] = new JsonArray(),
                ["live_preview"] = live.Contains(id),
                ["handler"] = handler,
            });
        }

        return options;
    }

    private static JsonArray BuildPages(JsonArray options)
    {
        (string Page, string Group, string Block, string[] Options)[] blocks =
        [
            ("video_page", "video_group_display", "video_display_block_common",
                ["gfxFullScreen", "gfx_gamma", "gfxResolution", "gfx_vsync"]),
            ("video_page", "video_group_quality_combined", "video_quality_combined_block_common",
                ["gfxSystemSpecDefault", "gfxSystemSpec"]),
            ("advanced_video_page", "advanced_video_group_quality", "advanced_video_quality_block_common",
                ["gfx_anisotropic_filter", "gfx_antialias", "gfx_sharpen", "gfx_simple_terrain",
                    "gfx_simple_water", "gfx_texture_manager", "cpu_load_factor"]),
            ("advanced_video_page", "advanced_video_group_quality", "advanced_video_quality_block_distance",
                ["gfx_fade_factor", "gfx_fog_factor", "gfx_lod_factor"]),
            ("advanced_video_page", "advanced_video_group_quality", "advanced_video_quality_block_effects",
                ["use_area_effect", "use_post_effect", "resolution_boost"]),
            ("advanced_video_page", "advanced_video_group_quality", "advanced_video_quality_block_grass",
                ["show_grass", "gfx_grass_density"]),
            ("advanced_video_page", "advanced_video_group_astral", "advanced_video_astral_block_common",
                ["gfx_simple_astral"]),
            ("audio_page", "audio_group_common", "audio_block_common", ["masterVolume", "muteAll"]),
            ("audio_page", "audio_group_common", "audio_block_advanced",
                ["ambientVolume", "interfaceVolume", "musicVolume", "sfxVolume", "voiceVolume"]),
            ("interface_page", "interface_group_common", "interface_block_common", ["localization"]),
            ("interface_page", "interface_group_common", "interface_block_mouse_movement",
                ["use_move_by_click", "simplified_move", "input_swap_mouse_buttons",
                    "opt_mission_max_click_delay_ms", "opt_mission_max_click_threshold"]),
            ("interface_page", "interface_group_common", "interface_block_camera",
                ["camera_bind_to_avatar", "opt_mission_player_camera_invert_y_factor"]),
            ("interface_page", "interface_group_overtips", "interface_block_all_creatures",
                ["show_all_overtips", "show_all_titles", "show_all_healthbars", "show_all_status"]),
            ("interface_page", "interface_group_overtips", "interface_block_hostile_creatures",
                ["show_hostile_overtips", "show_hostile_titles", "show_hostile_healthbars",
                    "show_hostile_status"]),
            ("interface_page", "interface_group_chat_bubbles", "intarface_chat_bubbles_show",
                ["chat_bubbles_show", "emote_icons_show", "chat_bubbles_opacity"]),
        ];
        string[] pageOrder = ["video_page", "advanced_video_page", "audio_page", "interface_page"];
        var byOption = options.Select(node => node!.AsObject())
            .ToDictionary(option => option["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var pages = new JsonArray();
        foreach (string pageId in pageOrder)
        {
            var groups = new JsonArray();
            foreach (IGrouping<string, (string Page, string Group, string Block, string[] Options)> group
                     in blocks.Where(block => block.Page == pageId).GroupBy(block => block.Group))
            {
                var groupBlocks = new JsonArray();
                foreach (var block in group)
                {
                    var optionIds = new JsonArray();
                    foreach (string optionId in block.Options)
                    {
                        JsonObject option = byOption[optionId];
                        option["page"] = pageId;
                        option["group"] = block.Group;
                        option["block"] = block.Block;
                        optionIds.Add(optionId);
                    }

                    groupBlocks.Add(new JsonObject
                    {
                        ["id"] = block.Block,
                        ["name"] = string.Empty,
                        ["description"] = string.Empty,
                        ["options"] = optionIds,
                    });
                }

                groups.Add(new JsonObject
                {
                    ["id"] = group.Key,
                    ["name"] = string.Empty,
                    ["description"] = string.Empty,
                    ["blocks"] = groupBlocks,
                });
            }

            pages.Add(new JsonObject
            {
                ["id"] = pageId,
                ["name"] = string.Empty,
                ["description"] = string.Empty,
                ["groups"] = groups,
            });
        }

        return pages;
    }

    private static JsonArray BuildBindings()
    {
        int[] counts = [13, 10, 44, 8, 8, 7];
        string[] sectionIds =
        [
            "mission_common",
            "mission_movement",
            "mission_actions",
            "mission_class_actions",
            "mission_members",
            "mission_other",
        ];
        var sections = new JsonArray();
        int bindingIndex = 0;
        for (int sectionIndex = 0; sectionIndex < counts.Length; sectionIndex++)
        {
            string sectionId = sectionIds[sectionIndex];
            var bindings = new JsonArray();
            for (int local = 0; local < counts[sectionIndex]; local++)
            {
                var defaults = new JsonArray($"KEY_{bindingIndex}_A");
                if (bindingIndex < 4)
                {
                    defaults.Add($"KEY_{bindingIndex}_B");
                }

                bindings.Add(new JsonObject
                {
                    ["id"] = $"binding_{bindingIndex:00}",
                    ["section"] = sectionId,
                    ["name"] = $"Binding {bindingIndex}",
                    ["activation"] = bindingIndex % 2 == 0
                        ? "activate_only"
                        : "activate_and_deactivate",
                    ["any_modifiers"] = bindingIndex % 3 == 0,
                    ["default_bindings"] = defaults,
                });
                bindingIndex++;
            }

            sections.Add(new JsonObject
            {
                ["id"] = sectionId,
                ["name"] = string.Empty,
                ["assignment_slots"] = 2,
                ["bindings"] = bindings,
            });
        }

        return sections;
    }
}
