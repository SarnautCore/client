using System;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// Renders the real offline walkabout and rejects the dark void-heavy view that
/// results when the camera starts below the authored tutorial floor.
/// </summary>
public partial class ZonePresentationPixelProbe : Node
{
    // Pinned against the authored AstralCoast_Tubes lighting (2026-08-21): the
    // healthy spawn frame measures 0.21 under the near-void thresholds below,
    // while a camera under the floor sees mostly fog/background and reads far
    // above the ceiling. The looser pre-authored thresholds counted legitimate
    // dim blue floor as void.
    private const float MaximumDarkFraction = 0.45f;
    private static readonly Vector3 ExpectedFloor6PlayerPosition = new(321.8298f, 156.142f, -5793.858f);

    public override async void _Ready()
    {
        ChargenOption option = CuratedOption();
        SessionHost.Of(this).Player.SelectCharacter(
            new CharacterSummary(Guid.NewGuid(), "Visual Proof", option.Id, DateTimeOffset.UtcNow),
            option);

        PackedScene packed = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node3D zone = packed.Instantiate<Node3D>();
        AddChild(zone);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);

        ZoneLoader loader = zone.GetNode<ZoneLoader>("ZoneLoader");
        WalkaboutController walker = zone.GetNode<WalkaboutController>("Walker");
        CharacterRig character = zone.GetNode<CharacterRig>("Walker/Character");
        Image image = GetViewport().GetTexture().GetImage();
        string proofPath = ProjectSettings.GlobalizePath("user://zone-presentation-proof.png");
        Error saveError = image.SavePng(proofPath);
        float darkFraction = DarkFraction(image);
        bool atAuthoredFloor = walker.Position.DistanceTo(ExpectedFloor6PlayerPosition) <= 1.0f;
        bool selectedAppearance = character.ScenePath.Contains(option.Id, StringComparison.OrdinalIgnoreCase)
            && NativeCharacterLodContract.HasAttachment(
                character.Model,
                "Attach_Mace_1H_Club_A_01",
                "Slot_Hand_R")
            && NativeCharacterLodContract.HasAttachment(
                character.Model,
                "Attach_Shield_1H_Simple_A_01",
                "Slot_Shield_Hand");
        bool passed = loader.TerrainTileCount == 4
            && loader.VisualObjectCount == 36
            && atAuthoredFloor
            && selectedAppearance
            && darkFraction <= MaximumDarkFraction
            && saveError == Error.Ok;

        GD.Print(
            $"ZONE_PRESENTATION_PIXEL_PROBE spawn={walker.Position} expected={ExpectedFloor6PlayerPosition} "
            + $"appearance=\"{character.ScenePath}\" selected_gear={selectedAppearance} "
            + $"dark_fraction={darkFraction:F4} max_dark_fraction={MaximumDarkFraction:F2} "
            + $"proof={proofPath} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static ChargenOption CuratedOption() => new(
        "chargen.league.warrior",
        "race.kania",
        "class.warrior",
        "female",
        "faction.league",
        "M2.Chargen.LeagueWarrior.Name",
        "M2.Chargen.LeagueWarrior.Description",
        "unused-by-native-runtime",
        "zone.inst-league1",
        1,
        110.0f,
        170.5f,
        156.293f);

    private static float DarkFraction(Image image)
    {
        int dark = 0;
        int samples = 0;
        int top = 64;
        int bottom = image.GetHeight() - 64;
        for (int y = top; y < bottom; y += 2)
        {
            for (int x = 0; x < image.GetWidth(); x += 2)
            {
                Color color = image.GetPixel(x, y);
                samples++;
                if (color.R < (24.0f / 255.0f)
                    && color.G < (24.0f / 255.0f)
                    && color.B < (48.0f / 255.0f))
                {
                    dark++;
                }
            }
        }

        return samples == 0 ? 1.0f : (float)dark / samples;
    }
}
