using System.Collections.Immutable;

namespace SarnautCore.UI;

public sealed record OptionsProduct(
    NativeContentPath Scene,
    OptionsLayout Layout,
    ImmutableArray<OptionsPageDefinition> Pages,
    ImmutableArray<OptionDefinition> Options,
    ImmutableArray<GraphicsPresetDefinition> GraphicsPresets,
    int BindingSlots,
    ImmutableArray<BindingSectionDefinition> BindingSections,
    OptionsContentInventory Content)
{
    public const string SchemaId = "sarnaut.options-product/v1";
    public const int RequiredWidth = 750;
    public const int RequiredHeight = 673;
    public const int RequiredPriority = 5400;
    public const int RequiredPageCount = 4;
    public const int RequiredGroupCount = 8;
    public const int RequiredBlockCount = 15;
    public const int RequiredOptionCount = 48;
    public const int RequiredPresetCount = 5;
    public const int RequiredPresetValueCount = 14;
    public const int RequiredBindingSectionCount = 6;
    public const int RequiredBindingCount = 90;
    public const int RequiredBindingSlots = 2;
    public const int RequiredSceneCount = 69;
    public const int RequiredResourceCount = 0;
    public const int RequiredTextureCount = 33;

    public static ImmutableArray<string> RequiredChildOrder { get; } =
    [
        "CheckboxPanel",
        "SliderPanel",
        "ListPanel",
        "ButtonPanel",
        "MainPanel",
        "GroupPanel",
        "BlockPanel",
        "HotkeyPanel",
    ];

    public static ImmutableArray<string> RequiredPageOrder { get; } =
        ["video_page", "advanced_video_page", "audio_page", "interface_page"];

    public static ImmutableArray<string> RequiredOptionOrder { get; } =
    [
        "gfxFullScreen", "gfx_gamma", "gfxResolution", "gfx_vsync", "gfxSystemSpecDefault",
        "gfxSystemSpec", "gfx_anisotropic_filter", "gfx_antialias", "gfx_sharpen",
        "gfx_simple_terrain", "gfx_simple_water", "gfx_texture_manager", "cpu_load_factor",
        "gfx_fade_factor", "gfx_fog_factor", "gfx_lod_factor", "use_area_effect",
        "use_post_effect", "resolution_boost", "show_grass", "gfx_grass_density",
        "gfx_simple_astral", "masterVolume", "muteAll", "ambientVolume", "interfaceVolume",
        "musicVolume", "sfxVolume", "voiceVolume", "localization", "use_move_by_click",
        "simplified_move", "input_swap_mouse_buttons", "opt_mission_max_click_delay_ms",
        "opt_mission_max_click_threshold", "camera_bind_to_avatar",
        "opt_mission_player_camera_invert_y_factor", "show_all_overtips", "show_all_titles",
        "show_all_healthbars", "show_all_status", "show_hostile_overtips", "show_hostile_titles",
        "show_hostile_healthbars", "show_hostile_status", "chat_bubbles_show", "emote_icons_show",
        "chat_bubbles_opacity",
    ];

    public static ImmutableArray<string> RequiredBindingSectionOrder { get; } =
    [
        "mission_common",
        "mission_movement",
        "mission_actions",
        "mission_class_actions",
        "mission_members",
        "mission_other",
    ];

    public static ImmutableArray<string> RequiredPresetOptionIds { get; } =
    [
        "gfx_anisotropic_filter",
        "gfx_antialias",
        "gfx_fade_factor",
        "gfx_fog_factor",
        "gfx_lod_factor",
        "gfx_sharpen",
        "gfx_simple_astral",
        "gfx_simple_terrain",
        "gfx_simple_water",
        "gfx_texture_manager",
        "resolution_boost",
        "show_grass",
        "use_area_effect",
        "use_post_effect",
    ];

    public static ImmutableArray<string> RequiredPresetOrder { get; } =
        ["quality_0", "quality_1", "quality_2", "quality_3", "quality_4"];
}

public sealed record OptionsLayout(
    int Width,
    int Height,
    OptionsAlignment HorizontalAlign,
    OptionsAlignment VerticalAlign,
    int Priority,
    ImmutableArray<string> ChildOrder);

public enum OptionsAlignment
{
    Center,
}

public sealed record OptionsPageDefinition(
    string Id,
    string Name,
    string Description,
    ImmutableArray<OptionsGroupDefinition> Groups);

public sealed record OptionsGroupDefinition(
    string Id,
    string Name,
    string Description,
    ImmutableArray<OptionsBlockDefinition> Blocks);

public sealed record OptionsBlockDefinition(
    string Id,
    string Name,
    string Description,
    ImmutableArray<string> Options);

public sealed record OptionDefinition(
    string Id,
    string Page,
    string Group,
    string Block,
    string Name,
    string Description,
    OptionStorage Storage,
    OptionDataKind DataKind,
    OptionViewKind ViewKind,
    int AuthoredDefaultIndex,
    int EffectiveDefaultIndex,
    int? DeclaredValueCount,
    ImmutableArray<OptionScalar> Values,
    ImmutableArray<string> ValueNames,
    ImmutableArray<string> ValueDescriptions,
    bool LivePreview,
    OptionHandler Handler);

public enum OptionStorage
{
    Global,
    User,
}

public enum OptionDataKind
{
    Boolean,
    Discrete,
    DiscreteFloat,
    Action,
}

public enum OptionViewKind
{
    Checkbox,
    Slider,
    List,
    Button,
}

public enum OptionHandler
{
    None,
    Audio,
    Resolution,
    Localization,
    QualityPreset,
    Autodetect,
}

public enum OptionScalarKind
{
    Boolean,
    Number,
    Text,
}

public readonly record struct OptionScalar
{
    private OptionScalar(OptionScalarKind kind, bool boolean, double number, string? text)
    {
        Kind = kind;
        Boolean = boolean;
        Number = number;
        Text = text;
    }

    public OptionScalarKind Kind { get; }
    public bool Boolean { get; }
    public double Number { get; }
    public string? Text { get; }

    public static OptionScalar FromBoolean(bool value) =>
        new(OptionScalarKind.Boolean, value, 0, null);

    public static OptionScalar FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return new OptionScalar(OptionScalarKind.Number, false, value, null);
    }

    public static OptionScalar FromText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new OptionScalar(OptionScalarKind.Text, false, 0, value);
    }

    public override string ToString() => Kind switch
    {
        OptionScalarKind.Boolean => Boolean ? "true" : "false",
        OptionScalarKind.Number => Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        OptionScalarKind.Text => Text ?? string.Empty,
        _ => string.Empty,
    };
}

public sealed record GraphicsPresetDefinition(
    string Id,
    ImmutableDictionary<string, double> Values);

public sealed record BindingSectionDefinition(
    string Id,
    string Name,
    int AssignmentSlots,
    ImmutableArray<BindingDefinition> Bindings);

public sealed record BindingDefinition(
    string Id,
    string Section,
    string Name,
    BindingActivation Activation,
    bool AnyModifiers,
    ImmutableArray<string> DefaultBindings);

public enum BindingActivation
{
    ActivateOnly,
    ActivateAndDeactivate,
}

public sealed record OptionsContentInventory(
    ImmutableArray<NativeContentPath> Scenes,
    ImmutableArray<NativeContentPath> Resources,
    ImmutableArray<NativeContentPath> Textures);
