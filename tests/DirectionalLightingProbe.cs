using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// Renders the production League walkabout with the sun enabled, with its
/// shadows disabled, and with the sun disabled. The frame deltas prove that
/// the authored light affects the converted scene and that its shadows survive
/// the Forward+ render path.
/// </summary>
public partial class DirectionalLightingProbe : Node
{
    private const float VisibleDelta = 0.025f;
    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        PackedScene packed = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node3D zone = packed.Instantiate<Node3D>();
        AddChild(zone);

        CanvasLayer? interfaceLayer = zone.GetNodeOrNull<CanvasLayer>("Interface");
        if (interfaceLayer != null)
        {
            interfaceLayer.Visible = false;
        }

        var walker = zone.GetNode<WalkaboutController>("Walker");
        walker.NetworkControlled = true;
        walker.InputEnabled = false;
        walker.LookInputEnabled = false;
        var sun = zone.GetNode<DirectionalLight3D>("Sun");
        var loader = zone.GetNode<ZoneLoader>("ZoneLoader");
        CharacterRig character = zone.GetNode<CharacterRig>("Walker/Character");

        for (int frame = 0; frame < 24; frame++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }

        MeshInstance3D[] terrain = Descendants<MeshInstance3D>(loader.GetNode<Node3D>("Terrain")).ToArray();
        MeshInstance3D[] statics = Descendants<MeshInstance3D>(loader.GetNode<Node3D>("StaticObjects")).ToArray();
        MeshInstance3D[] player = character.Model == null
            ? []
            : Descendants<MeshInstance3D>(character.Model).Where(mesh => mesh.IsVisibleInTree()).ToArray();

        Expect(terrain.Length > 0, "converted terrain meshes loaded");
        Expect(statics.Length > 0, "converted static meshes loaded");
        Expect(player.Length > 0, "converted player meshes loaded");
        Expect(terrain.All(CastsShadows), "every terrain mesh can cast shadows");
        Expect(statics.All(CastsShadows), "every static mesh can cast shadows");
        Expect(player.All(CastsShadows), "every player mesh can cast shadows");
        // Terrain and statics carry their authored baked lighting (the lightmap
        // two-term combine and the lightvrt vertex colors) and render unshaded
        // like retail; the runtime sun only lights dynamic entities.
        Expect(terrain.All(HasBakedTerrainSurface), "every terrain mesh uses the baked two-term combine");
        Expect(statics.All(HasBakedOrLitSurface), "every static mesh is baked-lit or falls back to scene lighting");
        Expect(player.All(HasLitSurface), "every visible player mesh has a lit surface");

        sun.Visible = true;
        sun.ShadowEnabled = true;
        Image shadowed = await CaptureSettledFrame();
        SaveFrame(shadowed, "shadowed");

        sun.ShadowEnabled = false;
        Image unshadowed = await CaptureSettledFrame();
        SaveFrame(unshadowed, "unshadowed");

        sun.Visible = false;
        Image ambientOnly = await CaptureSettledFrame();
        SaveFrame(ambientOnly, "ambient-only");

        sun.Visible = true;
        sun.ShadowEnabled = true;

        FrameDifference shadowDifference = Compare(shadowed, unshadowed, darkerInFirst: true);
        FrameDifference sunlightDifference = Compare(shadowed, ambientOnly, darkerInFirst: false);
        FrameStats frameStats = Measure(shadowed);
        int pixelCount = shadowed.GetWidth() * shadowed.GetHeight();
        float shadowCoverage = shadowDifference.ChangedPixels / (float)pixelCount;
        float sunlightCoverage = sunlightDifference.ChangedPixels / (float)pixelCount;
        string renderingMethod = RenderingServer.GetCurrentRenderingMethod();

        Expect(renderingMethod == "forward_plus", $"the probe uses Forward+, saw {renderingMethod}");
        Expect(sun.ShadowEnabled, "the production sun enables shadows");
        Expect(
            sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits,
            "the production sun uses four cascades for the third-person camera");
        Expect(sun.DirectionalShadowBlendSplits, "the production sun blends cascade boundaries");
        // Thresholds pinned against the authored zone lighting (ZoneLights
        // AstralCoast_Tubes) with baked static/terrain lighting applied, so the
        // sun and its shadows now register only on dynamic entities (player and
        // NPC meshes). Measured on 2026-08-21 after the lightvrt integration:
        // shadow_coverage 0.654%, sun_coverage 0.66%, mean_luma 0.223,
        // sigma 0.074, clipped 0.00% (previously sun_coverage 1.08%,
        // mean_luma 0.117, sigma 0.052 under the placeholder ambient rig).
        Expect(shadowCoverage >= 0.003f,
            $"visible shadow coverage is at least 0.3%, saw {shadowCoverage:P3}");
        Expect(sunlightCoverage >= 0.005f,
            $"the authored sun affects at least 0.5% of the frame, saw {sunlightCoverage:P2}");
        Expect(frameStats.ClippedBrightFraction < 0.02f,
            $"fewer than 2% of pixels clip near white, saw {frameStats.ClippedBrightFraction:P2}");
        Expect(frameStats.MeanLuma is >= 0.06 and <= 0.65,
            $"mean luma stays readable without washing out, saw {frameStats.MeanLuma:F4}");
        Expect(frameStats.StandardDeviation >= 0.03,
            $"frame contrast preserves texture detail, saw sigma {frameStats.StandardDeviation:F4}");

        bool passed = _failures.Count == 0;
        GD.Print(
            $"DIRECTIONAL_LIGHTING_PROBE renderer={renderingMethod} terrain={terrain.Length} "
            + $"statics={statics.Length} player={player.Length} "
            + $"shadow_pixels={shadowDifference.ChangedPixels}/{pixelCount} shadow_coverage={shadowCoverage:P3} "
            + $"shadow_mean_delta={shadowDifference.MeanDelta:F4} sun_pixels={sunlightDifference.ChangedPixels}/{pixelCount} "
            + $"sun_coverage={sunlightCoverage:P2} sun_mean_delta={sunlightDifference.MeanDelta:F4} "
            + $"mean_luma={frameStats.MeanLuma:F4} sigma={frameStats.StandardDeviation:F4} "
            + $"clipped={frameStats.ClippedBrightFraction:P2} "
            + $"shadow_mode={sun.DirectionalShadowMode} blend_splits={sun.DirectionalShadowBlendSplits} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"DIRECTIONAL_LIGHTING_PROBE {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private async System.Threading.Tasks.Task<Image> CaptureSettledFrame()
    {
        for (int frame = 0; frame < 4; frame++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }

        Image image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private static FrameDifference Compare(Image first, Image second, bool darkerInFirst)
    {
        byte[] firstBytes = first.GetData();
        byte[] secondBytes = second.GetData();
        int changed = 0;
        double deltaSum = 0;
        for (int index = 0; index < firstBytes.Length; index += 4)
        {
            float firstLuma = Luma(firstBytes, index);
            float secondLuma = Luma(secondBytes, index);
            float delta = darkerInFirst ? secondLuma - firstLuma : firstLuma - secondLuma;
            if (delta < VisibleDelta)
            {
                continue;
            }

            changed++;
            deltaSum += delta;
        }

        return new FrameDifference(changed, changed == 0 ? 0 : deltaSum / changed);
    }

    private static FrameStats Measure(Image image)
    {
        byte[] bytes = image.GetData();
        int pixels = bytes.Length / 4;
        int clipped = 0;
        double luma = 0;
        double squaredLuma = 0;
        for (int index = 0; index < bytes.Length; index += 4)
        {
            float value = Luma(bytes, index);
            luma += value;
            squaredLuma += value * value;
            if (value >= 0.98f)
            {
                clipped++;
            }
        }

        double mean = luma / pixels;
        double variance = Math.Max(0, (squaredLuma / pixels) - (mean * mean));
        return new FrameStats(mean, Math.Sqrt(variance), clipped / (float)pixels);
    }

    private static float Luma(IReadOnlyList<byte> bytes, int index) =>
        ((0.2126f * bytes[index]) + (0.7152f * bytes[index + 1]) + (0.0722f * bytes[index + 2])) / 255.0f;

    private static bool CastsShadows(MeshInstance3D mesh) =>
        mesh.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;

    private static bool HasLitSurface(MeshInstance3D mesh)
    {
        if (mesh.MaterialOverride != null)
        {
            return IsLit(mesh.MaterialOverride);
        }

        if (mesh.Mesh == null)
        {
            return false;
        }

        for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            Material? material = mesh.GetActiveMaterial(surface);
            if (material == null || IsLit(material))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLit(Material material) => material switch
    {
        ShaderMaterial shader =>
            shader.Shader?.Code.Contains("render_mode unshaded", StringComparison.OrdinalIgnoreCase) != true,
        BaseMaterial3D standard => standard.ShadingMode != BaseMaterial3D.ShadingModeEnum.Unshaded,
        _ => true,
    };

    /// <summary>The client terrain shader's baked two-term lightmap combine.</summary>
    private static bool HasBakedTerrainSurface(MeshInstance3D mesh) =>
        mesh.MaterialOverride is ShaderMaterial shader
        && shader.Shader?.Code.Contains("baked_light", StringComparison.Ordinal) == true;

    /// <summary>
    /// A converted static either carries baked lightvrt vertex colors (unshaded
    /// with the color modulating the albedo) on at least one surface, or keeps
    /// scene lighting where a cell shipped no bake.
    /// </summary>
    private static bool HasBakedOrLitSurface(MeshInstance3D mesh)
    {
        if (mesh.Mesh == null)
        {
            return HasLitSurface(mesh);
        }

        for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.GetActiveMaterial(surface) is BaseMaterial3D standard
                && standard.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded
                && standard.VertexColorUseAsAlbedo)
            {
                return true;
            }
        }

        return HasLitSurface(mesh);
    }

    private static IEnumerable<T> Descendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void SaveFrame(Image image, string suffix)
    {
        string prefix = System.Environment.GetEnvironmentVariable("SARNAUT_LIGHTING_PROBE_PREFIX") ?? string.Empty;
        if (prefix.Length == 0)
        {
            return;
        }

        Error result = image.SavePng($"{prefix}-{suffix}.png");
        Expect(result == Error.Ok, $"the {suffix} frame saves: {result}");
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }

    private readonly record struct FrameDifference(int ChangedPixels, double MeanDelta);
    private readonly record struct FrameStats(
        double MeanLuma,
        double StandardDeviation,
        float ClippedBrightFraction);
}
