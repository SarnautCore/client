using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// Mounts the public engine fallback theme.
/// </summary>
/// <remarks>
/// The compiled native UI product mounts its own self-contained Theme resource
/// on <see cref="NativeUiProductHost"/>. This fallback remains for public engine
/// screens that run without the private content pack.
/// </remarks>
public static class UiTheme
{
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

        _mounted = BuildFallback();
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
