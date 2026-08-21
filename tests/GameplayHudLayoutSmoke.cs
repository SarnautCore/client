using System.Collections.Generic;
using Godot;
using SarnautCore.Gameplay;

namespace SarnautCore;

/// <summary>Checks the live HUD against the scene-owned help overlay at representative viewport sizes.</summary>
public partial class GameplayHudLayoutSmoke : Node
{
    private static readonly Vector2I[] ViewportSizes =
    [
        new(960, 540),
        new(1280, 720),
        new(1600, 900),
    ];

    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        foreach (Vector2I viewportSize in ViewportSizes)
        {
            await CheckViewport(viewportSize);
        }

        bool passed = _failures.Count == 0;
        GD.Print($"GAMEPLAY_HUD_LAYOUT viewports={ViewportSizes.Length} result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"GAMEPLAY_HUD_LAYOUT {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private async System.Threading.Tasks.Task CheckViewport(Vector2I viewportSize)
    {
        var viewport = new SubViewport
        {
            Name = $"Viewport{viewportSize.X}x{viewportSize.Y}",
            Size = viewportSize,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(viewport);

        PackedScene scene = GD.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node zone = scene.Instantiate();
        CanvasLayer interfaceLayer = zone.GetNode<CanvasLayer>("Interface");
        zone.RemoveChild(interfaceLayer);
        zone.Free();
        viewport.AddChild(interfaceLayer);

        PanelContainer helpPanel = interfaceLayer.GetNode<PanelContainer>("Margin/Column/HelpPanel");
        var model = new GameplayHudViewModel(
            ownEntityId: 1,
            abilities: [new AbilityDefinition("ability.test", "ability.test.name", string.Empty)],
            inventoryCapacity: 4,
            stackLimit: _ => 20,
            focus: new GameplayFocusOwner());
        var network = new ZoneNetworkLoop();
        var hud = new GameplayHudControl();
        hud.Initialize(model, network, helpPanel);
        interfaceLayer.AddChild(hud);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        AbilityBarControl abilityBar = Find<AbilityBarControl>(hud)
            ?? throw new System.InvalidOperationException("Ability bar was not created.");
        Rect2 abilityRect = abilityBar.GetGlobalRect();
        Rect2 helpRect = helpPanel.GetGlobalRect();
        float gap = helpRect.Position.Y - abilityRect.End.Y;
        Expect(Mathf.Abs(gap - 8f) <= 0.5f, viewportSize, $"ability/help gap was {gap:0.0}px");
        Expect(abilityRect.Position.X >= 0 && abilityRect.End.X <= viewportSize.X, viewportSize, "ability bar left the viewport");
        Expect(helpRect.Position.X >= 0 && helpRect.End.X <= viewportSize.X, viewportSize, "help panel left the viewport");

        Label help = helpPanel.GetNode<Label>("Help");
        Expect(help.Size.Y + 0.5f >= help.GetMinimumSize().Y, viewportSize, "help text was vertically clipped");

        string? screenshotPath = System.Environment.GetEnvironmentVariable("SARNAUT_HUD_LAYOUT_SCREENSHOT");
        if (viewportSize == new Vector2I(1280, 720) && !string.IsNullOrWhiteSpace(screenshotPath))
        {
            Error screenshotError = viewport.GetTexture().GetImage().SavePng(screenshotPath);
            Expect(screenshotError == Error.Ok, viewportSize, $"screenshot failed with {screenshotError}");
            GD.Print($"GAMEPLAY_HUD_LAYOUT screenshot={screenshotPath}");
        }

        GD.Print(
            $"GAMEPLAY_HUD_LAYOUT viewport={viewportSize.X}x{viewportSize.Y} "
            + $"ability={abilityRect} help={helpRect} gap={gap:0.0}px");
        interfaceLayer.Free();
        viewport.Free();
        network.Free();
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

    private void Expect(bool condition, Vector2I viewportSize, string what)
    {
        if (!condition)
        {
            _failures.Add($"{viewportSize.X}x{viewportSize.Y}: {what}");
        }
    }
}
