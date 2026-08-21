using System;
using System.Linq;
using Godot;
using SarnautCore.Content;
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
        ZoneWalkabout walkabout = (ZoneWalkabout)zone;
        WalkaboutController walker = zone.GetNode<WalkaboutController>("Walker");
        CharacterRig character = zone.GetNode<CharacterRig>("Walker/Character");
        Node3D? presentationRoot = zone.GetNodeOrNull<Node3D>("ZonePresentation");
        WorldEnvironment? worldEnvironment = presentationRoot?.GetNodeOrNull<WorldEnvironment>("Environment");
        DirectionalLight3D? sun = presentationRoot?.GetNodeOrNull<DirectionalLight3D>("Sun");
        CameraCenteredSky? centeredSky = presentationRoot?.GetNodeOrNull<CameraCenteredSky>("CameraCenteredSky");
        Node3D? sky = centeredSky?.GetNodeOrNull<Node3D>("Sky");
        Camera3D? camera = GetViewport().GetCamera3D();
        NativeZonePresentation? presentation = ReadPresentation(walkabout.NativePresentationManifestPath);
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
        bool nativeRoute = walkabout.NativePresentationManifestPath
                == "res://content/league-slice/maps/inst-league-start/zones/inst-league1/zone-presentation.json"
            && walkabout.NativePresentationScenePath.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
            && presentationRoot?.GetMeta("native_scene", string.Empty).AsString()
                == walkabout.NativePresentationScenePath;
        bool exactManifest = presentation is not null
            && presentation.MapId == "inst-league-start"
            && presentation.ZoneId == "inst-league1"
            && presentation.Sky.PartCount == 3
            && presentation.Sky.AnimatedPartCount == 1
            && presentation.Sky.ProjectionScaling == "xy"
            && presentation.Sky.Parts.Select(part => part.Node)
                .SequenceEqual(["Backdrop", "Stars", "Clouds"])
            && presentation.Sky.Parts.Select(part => part.FovFactor)
                .SequenceEqual([0.8f, 0.4f, 1.0f])
            && presentation.Sky.Parts.Select(part => part.Animated)
                .SequenceEqual([false, false, true]);
        bool exactTopology = worldEnvironment?.Environment is { } environment
            && sun is not null
            && centeredSky is not null
            && sky?.GetChildCount() == 3
            && sky.GetNodeOrNull<Node3D>("Backdrop") != null
            && sky.GetNodeOrNull<Node3D>("Stars") != null
            && sky.GetNodeOrNull<Node3D>("Clouds") != null
            && HasAuthoredProjection(sky.GetNode<Node3D>("Backdrop"), 0.8f, "blend_add")
            && HasAuthoredProjection(sky.GetNode<Node3D>("Stars"), 0.4f, "blend_add")
            && HasAuthoredProjection(sky.GetNode<Node3D>("Clouds"), 1.0f, "blend_mix")
            && camera is not null
            && centeredSky.GlobalPosition.DistanceTo(camera.GlobalPosition) <= 0.001f
            && environment.BackgroundMode == Godot.Environment.BGMode.Color
            && Near(environment.BackgroundColor, new Color(18.0f / 255.0f, 6.0f / 255.0f, 38.0f / 255.0f))
            && environment.AmbientLightSource == Godot.Environment.AmbientSource.Color
            && Near(environment.AmbientLightColor, new Color(45.0f / 255.0f, 58.0f / 255.0f, 179.0f / 255.0f))
            && Near(environment.AmbientLightEnergy, 0.5f)
            && environment.TonemapMode == Godot.Environment.ToneMapper.Linear
            && Near(environment.TonemapExposure, 1.11765f)
            && environment.FogEnabled
            && environment.FogMode == Godot.Environment.FogModeEnum.Depth
            && Near(environment.FogLightColor, new Color(18.0f / 255.0f, 6.0f / 255.0f, 38.0f / 255.0f))
            && Near(environment.FogDepthBegin, 20.0f)
            && Near(environment.FogDepthEnd, 150.0f)
            && Near(sun.RotationDegrees, new Vector3(0.0f, -45.0f, 0.0f))
            && Near(sun.LightColor, new Color(6.0f / 255.0f, 57.0f / 255.0f, 119.0f / 255.0f))
            && Near(sun.LightEnergy, 1.0f)
            && sun.ShadowEnabled
            && Near(sun.ShadowOpacity, 1.0f)
            && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits
            && sun.DirectionalShadowBlendSplits
            && Near(sun.DirectionalShadowMaxDistance, 600.0f);
        bool exactProbeColors = BakedLightProbe.Active is { } lightProbe
            && Near(lightProbe.AmbientColor, new Color(45.0f / 510.0f, 58.0f / 510.0f, 179.0f / 510.0f))
            && Near(lightProbe.DirectColor, new Color(70.0f / 255.0f, 30.0f / 255.0f, 0.0f));
        bool passed = loader.TerrainTileCount == 4
            && loader.VisualObjectCount == 36
            && atAuthoredFloor
            && selectedAppearance
            && nativeRoute
            && exactManifest
            && exactTopology
            && exactProbeColors
            && darkFraction <= MaximumDarkFraction
            && saveError == Error.Ok;

        GD.Print(
            $"ZONE_PRESENTATION_PIXEL_PROBE spawn={walker.Position} expected={ExpectedFloor6PlayerPosition} "
            + $"appearance=\"{character.ScenePath}\" selected_gear={selectedAppearance} "
            + $"native_manifest=\"{walkabout.NativePresentationManifestPath}\" "
            + $"native_scene=\"{walkabout.NativePresentationScenePath}\" native_route={nativeRoute} "
            + $"manifest_exact={exactManifest} topology_exact={exactTopology} probe_colors_exact={exactProbeColors} "
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

    private static NativeZonePresentation? ReadPresentation(string manifestPath)
    {
        try
        {
            return NativeZonePresentation.Parse(
                FileAccess.GetFileAsString(manifestPath),
                "inst-league-start",
                "inst-league1");
        }
        catch (Exception exception)
        {
            GD.PushError($"ZONE_PRESENTATION_PIXEL_PROBE manifest parse failed: {exception.Message}");
            return null;
        }
    }

    private static bool Near(float actual, float expected) =>
        Mathf.Abs(actual - expected) <= 0.0001f;

    private static bool Near(Color actual, Color expected) =>
        Near(actual.R, expected.R) && Near(actual.G, expected.G) && Near(actual.B, expected.B);

    private static bool Near(Vector3 actual, Vector3 expected) =>
        actual.DistanceTo(expected) <= 0.0001f;

    private static bool HasAuthoredProjection(
        Node root,
        float expectedFovFactor,
        string expectedBlendMode)
    {
        foreach (MeshInstance3D mesh in DescendantsAndSelf<MeshInstance3D>(root))
        {
            int surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (int surface = 0; surface < surfaceCount; surface++)
            {
                if (mesh.GetActiveMaterial(surface) is ShaderMaterial material
                    && material.Shader?.Code is { } code
                    && code.Contains("clip.xy *= fov_factor", StringComparison.Ordinal)
                    && code.Contains("* COLOR.rgb", StringComparison.Ordinal)
                    && code.Contains(expectedBlendMode, StringComparison.Ordinal)
                    && Near((float)material.GetShaderParameter("fov_factor").AsDouble(), expectedFovFactor))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static System.Collections.Generic.IEnumerable<T> DescendantsAndSelf<T>(Node root) where T : Node
    {
        if (root is T match)
        {
            yield return match;
        }

        foreach (Node child in root.GetChildren())
        {
            foreach (T descendant in DescendantsAndSelf<T>(child))
            {
                yield return descendant;
            }
        }
    }

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
