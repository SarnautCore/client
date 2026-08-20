using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// Mounts the interface theme: the converted Allods widget theme when the
/// converted tree is present, and a theme built in code when it is not.
/// </summary>
/// <remarks>
/// Everything visual under <c>converted/</c> is MY.GAMES-derived and gitignored,
/// and the converted theme embeds absolute source paths in its metadata, so it
/// can never be committed. A fresh clone and CI therefore have no theme at all.
/// The fallback is not a nicety: without it every screen renders with Godot's
/// default grey and no font, which is what "unusable client" means in practice.
///
/// The converted theme's font references are written against the converter's own
/// <c>res://assets/</c> root, so it is loaded through
/// <see cref="ConvertedSceneLoader.LoadResource{T}"/> rather than
/// <see cref="ResourceLoader"/>. Godot still tries the raw file itself at
/// startup, before any autoload runs, and logs that it could not load it; that
/// message is expected and is the reason this class exists rather than the
/// project setting being enough on its own.
/// </remarks>
public static class UiTheme
{
    /// <summary>
    /// The project setting that names the theme. Reading the path from here
    /// rather than from a constant keeps <c>project.godot</c> the single place
    /// the theme is chosen.
    /// </summary>
    public const string ProjectThemeSetting = "gui/theme/custom";

    private static readonly Color Ink = new("e6edf6");
    private static readonly Color Muted = new("9aa8b8");
    private static readonly Color Accent = new("55c8e8");
    private static readonly Color Danger = new("ef9a9a");
    private static readonly Color Surface = new("161d28");
    private static readonly Color SurfaceRaised = new("1e2836");
    private static readonly Color Edge = new("2c394b");

    private static Theme? _mounted;

    /// <summary>Where the mounted theme came from, for a status line.</summary>
    public static string Source { get; private set; } = "not mounted";

    /// <summary>True when the converted Allods theme is the one in use.</summary>
    public static bool UsesConvertedTheme { get; private set; }

    /// <summary>The colour a screen paints behind everything else.</summary>
    public static Color Backdrop => new("10151d");

    /// <summary>The colour a screen uses for a refusal.</summary>
    public static Color ErrorInk => Danger;

    /// <summary>The colour a screen uses for secondary text.</summary>
    public static Color MutedInk => Muted;

    /// <summary>
    /// Resolves the theme once and hands it to the root window, so every Control
    /// in every scene inherits it without each one loading anything.
    /// </summary>
    public static Theme Mount(Window root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Theme theme = Resolve();
        root.Theme = theme;
        return theme;
    }

    /// <summary>The theme in use, loading it on first call.</summary>
    public static Theme Resolve()
    {
        if (_mounted != null)
        {
            return _mounted;
        }

        string configured = ProjectSettings.GetSetting(ProjectThemeSetting, string.Empty).AsString();
        if (!string.IsNullOrEmpty(configured))
        {
            Theme? converted = ConvertedSceneLoader.LoadResource<Theme>(
                ConvertedRootOf(configured),
                configured,
                out string error);
            if (converted != null)
            {
                _mounted = converted;
                UsesConvertedTheme = true;
                Source = configured;
                return converted;
            }

            GD.Print($"UiTheme: falling back to the built-in theme ({error})");
        }

        _mounted = BuildFallback();
        UsesConvertedTheme = false;
        Source = "built-in fallback";
        return _mounted;
    }

    /// <summary>
    /// Builds the theme used when no converted tree is present.
    /// </summary>
    /// <remarks>
    /// Deliberately plain: dark surfaces, one accent, Godot's own default font.
    /// It carries nothing derived from the retail client, which is the point —
    /// this is the theme a public checkout renders with.
    /// </remarks>
    public static Theme BuildFallback()
    {
        var theme = new Theme { DefaultFontSize = 16 };

        theme.SetStylebox("panel", "PanelContainer", Box(SurfaceRaised, Edge));
        theme.SetStylebox("panel", "Panel", Box(Surface, Edge));

        theme.SetStylebox("normal", "Button", Box(SurfaceRaised, Edge, 10));
        theme.SetStylebox("hover", "Button", Box(Edge, Accent, 10));
        theme.SetStylebox("pressed", "Button", Box(Edge, Accent, 10));
        theme.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), Accent, 10));
        theme.SetStylebox("disabled", "Button", Box(Surface, Edge, 10));
        theme.SetColor("font_color", "Button", Ink);
        theme.SetColor("font_hover_color", "Button", Accent);
        theme.SetColor("font_pressed_color", "Button", Accent);
        theme.SetColor("font_disabled_color", "Button", Muted);
        theme.SetFontSize("font_size", "Button", 16);

        theme.SetStylebox("normal", "LineEdit", Box(Surface, Edge, 8));
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), Accent, 8));
        theme.SetColor("font_color", "LineEdit", Ink);
        theme.SetColor("font_placeholder_color", "LineEdit", Muted);
        theme.SetColor("caret_color", "LineEdit", Accent);

        theme.SetColor("font_color", "Label", Ink);
        theme.SetFontSize("font_size", "Label", 16);

        theme.SetStylebox("panel", "ItemList", Box(Surface, Edge, 8));
        theme.SetColor("font_color", "ItemList", Ink);
        theme.SetColor("font_selected_color", "ItemList", Accent);

        theme.SetConstant("separation", "VBoxContainer", 12);
        theme.SetConstant("separation", "HBoxContainer", 12);

        return theme;
    }

    /// <summary>
    /// Derives the converted tree's root from a path inside it. The converter
    /// writes dependencies relative to the last <c>assets/</c> segment.
    /// </summary>
    private static string ConvertedRootOf(string resourcePath)
    {
        int marker = resourcePath.LastIndexOf("/assets/", StringComparison.Ordinal);
        return marker < 0 ? ConvertedCharacter.DefaultConvertedRoot : resourcePath[..marker];
    }

    private static StyleBoxFlat Box(Color fill, Color border, int radius = 6)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        box.SetBorderWidthAll(1);
        return box;
    }
}
