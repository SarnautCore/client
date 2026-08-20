using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// Mounts a converted Allods main-menu form as the decorative layer behind a
/// screen's working controls, and says so when it cannot.
/// </summary>
/// <remarks>
/// The converted forms under <c>Interface/Wrap/MainMenu</c> are MY.GAMES-derived
/// and live only in the gitignored <c>converted/</c> tree, so no screen may
/// depend on one being there. Every caller pairs this with controls it built
/// itself; this only decorates.
///
/// The form is mounted with input disabled throughout. It is chrome: the widgets
/// in it are pictures of a login form, not this login form, and letting them
/// swallow clicks would make the working one look broken.
/// </remarks>
public static class ConvertedChrome
{
    public const string LoginForm = "Interface/Wrap/MainMenu/LoginAccount/MainForm.(WidgetForm).ui.tscn";
    public const string CharacterSelectForm =
        "Interface/Wrap/MainMenu/CharacterSelector/MainForm.(WidgetForm).ui.tscn";
    public const string CharacterCreateForm =
        "Interface/Wrap/MainMenu/CharacterGenerator2/MainForm.(WidgetForm).ui.tscn";

    /// <summary>
    /// Instantiates <paramref name="formPath"/> under <paramref name="host"/>.
    /// </summary>
    /// <returns>
    /// A one-line description of what was mounted, for the screen's status line:
    /// the reason on failure, so a missing conversion is visible rather than
    /// silent.
    /// </returns>
    public static string Mount(Control host, string formPath)
    {
        ArgumentNullException.ThrowIfNull(host);
        PackedScene? form = ConvertedSceneLoader.Load(
            ConvertedCharacter.DefaultConvertedRoot,
            formPath,
            out string error);
        if (form?.Instantiate() is not Control instance)
        {
            return string.IsNullOrEmpty(error)
                ? $"converted chrome unavailable: {formPath}"
                : $"converted chrome unavailable: {error}";
        }

        instance.Name = "ConvertedChrome";
        instance.Visible = true;
        instance.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        MakeDecorative(instance);
        // Appended, so it draws over the screen's own backdrop. The working
        // controls live on a CanvasLayer above it and are unaffected by where
        // this lands.
        host.AddChild(instance);
        return $"converted chrome: {formPath}";
    }

    private static void MakeDecorative(Node node)
    {
        if (node is Control control)
        {
            control.MouseFilter = Control.MouseFilterEnum.Ignore;
            control.FocusMode = Control.FocusModeEnum.None;
        }

        foreach (Node child in node.GetChildren())
        {
            MakeDecorative(child);
        }
    }
}
