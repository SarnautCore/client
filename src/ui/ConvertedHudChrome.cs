using System;
using Godot;

namespace SarnautCore;

/// <summary>Converted Ingame scene references used as HUD chrome.</summary>
public static class ConvertedHudChrome
{
    public const string TargetSelection = "Interface/Ingame/TargetSelection/MainForm.(WidgetForm).ui.tscn";
    public const string Character = "Interface/Ingame/ContextCharacter/MainForm.(WidgetForm).ui.tscn";
    public const string DamageVisualization =
        "Interface/Ingame/ContextDamageVisualization/MainForm.(WidgetForm).ui.tscn";
    public const string LootBag = "Interface/Ingame/ContextLootBag/MainForm.(WidgetForm).ui.tscn";
    public const string Multibag = "Interface/Ingame/ContextMultibag/MainForm.(WidgetForm).ui.tscn";
    public const string QuestLog = "Interface/Ingame/ContextQuestLog/MainForm.(WidgetForm).ui.tscn";
    public const string QuestTracker =
        "Interface/Ingame/ContextQuestTrackerNew/MainForm.(WidgetForm).ui.tscn";
    public const string QuestInfo = "Interface/Ingame/ContextQuestInfo/QuestInfo.(WidgetPanel).ui.tscn";

    /// <summary>Mounts converted decoration behind a widget's working fallback controls.</summary>
    public static bool Mount(Control host, string scenePath)
    {
        ArgumentNullException.ThrowIfNull(host);
        PackedScene? scene = ConvertedSceneLoader.Load(
            ConvertedCharacter.DefaultConvertedRoot,
            scenePath,
            out _);
        if (scene?.Instantiate() is not Control chrome)
        {
            return false;
        }

        chrome.Name = "ConvertedChrome";
        chrome.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        MakeDecorative(chrome);
        host.AddChild(chrome);
        host.MoveChild(chrome, 0);
        return true;
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
