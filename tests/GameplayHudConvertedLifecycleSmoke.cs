using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore.Gameplay;

namespace SarnautCore;

/// <summary>Checks that the converted HUD subtrees die with their owning HUD.</summary>
public partial class GameplayHudConvertedLifecycleSmoke : Node
{
    // Pinned from a measured run (2026-08-21): the composed target plate draws
    // exactly these visible TextureRects; the seven conditional branches below
    // stay hidden until gameplay toggles them.
    private const int ExpectedDrawnTextureCount = 11;

    public override async void _Ready()
    {
        var focus = new GameplayFocusOwner();
        var model = new GameplayHudViewModel(
            ownEntityId: 1,
            abilities: [new AbilityDefinition("ability.test", "ability.test.name", string.Empty)],
            inventoryCapacity: 24,
            stackLimit: _ => 20,
            focus: focus);
        var network = new ZoneNetworkLoop();
        var hud = new GameplayHudControl();
        hud.Initialize(model, network);
        AddChild(hud);
        model.SelectTarget(new EntityHudSnapshot(
            2,
            "creature.test.name",
            "creature.test",
            3,
            75,
            100,
            true));
        focus.Open(GameplayWindow.Inventory);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        bool screenshotPassed = true;
        string? screenshotPath = System.Environment.GetEnvironmentVariable(
            "SARNAUT_HUD_LIFECYCLE_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            Error error = GetViewport().GetTexture().GetImage().SavePng(screenshotPath);
            screenshotPassed = error == Error.Ok;
            GD.Print($"GAMEPLAY_HUD_CONVERTED_LIFECYCLE screenshot={screenshotPath} error={error}");
        }

        List<Control> chromeRoots =
            [.. hud.FindChildren("ConvertedChrome", "Control", true, false).Cast<Control>()];
        // IsVisibleInTree is the real question (a chrome whose own Visible flag
        // is true inside a collapsed subtree draws nothing), but closed windows
        // legitimately hide their chrome. The four always-on subtrees (target
        // plate, ability bar, damage layer, quest tracker) must actually draw;
        // the four window subtrees (loot, inventory, quest log, quest info)
        // stay parked behind their closed windows with their own flag true.
        int drawnRoots = chromeRoots.Count(chrome => chrome.IsVisibleInTree());
        bool rootsVisible = drawnRoots == 4
            && chromeRoots.All(chrome => chrome.IsVisibleInTree() || chrome.Visible);
        TargetFrameControl? targetFrame = Find<TargetFrameControl>(hud);
        Control? targetChrome = targetFrame?.GetNodeOrNull<Control>("ConvertedChrome");
        Control? targetBars = targetChrome?.GetNodeOrNull<Control>("Bars");
        Control? targetFrameArt = targetChrome?.GetNodeOrNull<Control>("Frame");
        List<TextureRect> targetTextures = targetChrome is null
            ? []
            : [.. targetChrome.FindChildren("*", "TextureRect", true, false).Cast<TextureRect>()];
        // Authored visible=true elements (PvP flag, crown, halo, quality ornament, combat
        // status, wound ticks, mana gauge) are hidden by our code until gameplay toggles
        // them; the drawn contract covers only the visible branches.
        List<TextureRect> drawnTextures =
            [.. targetTextures.Where(texture => texture.IsVisibleInTree())];
        float targetAspect = targetChrome is null || targetChrome.Size.Y <= 0
            ? 0
            : targetChrome.Size.X / targetChrome.Size.Y;
        bool targetArtworkDrawn = targetBars is not null
            && targetFrameArt is not null
            && Mathf.Abs(targetAspect - 348f / 110f) <= 0.02f
            && (ExpectedDrawnTextureCount < 0 || drawnTextures.Count == ExpectedDrawnTextureCount)
            && drawnTextures.All(texture =>
                texture.Texture is not null
                && !texture.Texture.GetImage().IsEmpty()
                && texture.GetGlobalRect().HasArea());

        // Check visible=false on conditional paths.
        if (targetChrome is not null)
        {
            foreach (string conditionalPath in new[] { "PvPFlag", "Crown", "HaloSign", "Bars/Mana", "Bars/Health/Wounds", "Frame/UnitQuality", "Frame/Portrait/Combat" })
            {
                if (targetChrome.GetNodeOrNull<CanvasItem>(conditionalPath) is { } node)
                {
                    if (node.Visible)
                    {
                        GD.PushError($"Conditional path {conditionalPath} should be Visible=false but was Visible=true");
                        targetArtworkDrawn = false;
                    }
                }
            }
        }

        List<Node> chromeNodes = [];
        foreach (Control chrome in chromeRoots)
        {
            chromeNodes.Add(chrome);
            chromeNodes.AddRange(chrome.FindChildren("*", string.Empty, true, false).Cast<Node>());
        }

        hud.Free();
        network.Free();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        int alive = chromeNodes.Count(GodotObject.IsInstanceValid);
        bool passed = screenshotPassed
            && chromeRoots.Count == 8
            && rootsVisible
            && targetArtworkDrawn
            && alive == 0;
        GD.Print(
            $"GAMEPLAY_HUD_CONVERTED_LIFECYCLE roots={chromeRoots.Count} drawn_roots={drawnRoots} "
            + $"roots_visible={rootsVisible} target_composed={targetArtworkDrawn} "
            + $"target_aspect={targetAspect:0.000} target_textures={targetTextures.Count} "
            + $"target_drawn={drawnTextures.Count} "
            + $"nodes={chromeNodes.Count} alive={alive} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static T? Find<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? descendant = Find<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
