using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore;

/// <summary>
/// Verifies the upscaled-variant seam: the path rule the batch manifests
/// describe, that variants actually decode, and how much of a real League zone
/// load ends up on upscaled art. Reports resident texture bytes so the 16x
/// texel cost of 4x art can be checked against a full zone.
///
/// Not part of visual-gate.ps1 — that gate stays at its 16 rendering probes.
/// Run directly: godot_console --path . res://tests/upscaled_texture_probe.tscn
/// </summary>
public partial class UpscaledTextureProbe : Node
{
    private const string ConvertedRoot = "res://converted/assets/classic-1.1";
    private const string UpscaledRoot = "res://upscaled/assets";

    private readonly List<string> _failures = new();

    public override async void _Ready()
    {
        CheckPathRule();
        CheckDecode();
        await CheckZoneCoverage();

        foreach (string failure in _failures)
        {
            GD.PrintErr($"UPSCALED_PROBE_FAIL {failure}");
        }

        GD.Print($"result={(_failures.Count == 0 ? "pass" : "fail")} failures={_failures.Count}");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    private void Expect(bool condition, string description)
    {
        if (!condition)
        {
            _failures.Add(description);
        }
    }

    private void ExpectMap(string converted, string expected)
    {
        string actual = UpscaledTextures.MapPath(converted, UpscaledRoot);
        Expect(actual == expected, $"map {converted}\n  want={expected}\n  got ={actual}");
    }

    /// <summary>
    /// The eight batch manifests agree with this rule on all 25,730 completed
    /// entries; these cases pin one representative of each shape.
    /// </summary>
    private void CheckPathRule()
    {
        ExpectMap(
            $"{ConvertedRoot}/assets/Characters/Elf_female/ElfFemaleFaceLower00.png",
            $"{UpscaledRoot}/characters/classic-1.1/Elf_female/ElfFemaleFaceLower00.png");

        ExpectMap(
            $"{ConvertedRoot}/assets/Client/Render/ParticleAtlas.png",
            $"{UpscaledRoot}/client/classic-1.1/Render/ParticleAtlas.png");

        ExpectMap(
            $"{ConvertedRoot}/assets/World/Astral/Astral/Textures/Astral_Bottom_Crystal_Mask.png",
            $"{UpscaledRoot}/world/classic-1.1/Astral/Astral/Textures/Astral_Bottom_Crystal_Mask.png");

        // Interface art drops the converter's class suffix.
        ExpectMap(
            $"{ConvertedRoot}/assets/Interface/Common/Buttons/Cross/CloseDisabled.(UITexture).png",
            $"{UpscaledRoot}/interface/classic-1.1/Common/Buttons/Cross/CloseDisabled.png");

        // Interface/Icons was batched as its own asset class.
        ExpectMap(
            $"{ConvertedRoot}/assets/Interface/Icons/Actions/AngarFlayButton.(UITexture).png",
            $"{UpscaledRoot}/icons/classic-1.1/Actions/AngarFlayButton.png");

        // Terrain (Maps) is a separate lane and keeps originals, as do the
        // classes the batch never covered.
        ExpectMap($"{ConvertedRoot}/assets/Maps/Inst_LeagueStart/000_020/C1_Grass.png", string.Empty);
        ExpectMap($"{ConvertedRoot}/assets/System/Whatever.png", string.Empty);
        ExpectMap($"{ConvertedRoot}/assets/Characters/Elf_female/Face.tres", string.Empty);
        ExpectMap("res://src/zone/terrain_splat.gdshader", string.Empty);
        ExpectMap(string.Empty, string.Empty);
    }

    private void CheckDecode()
    {
        string face = $"{ConvertedRoot}/assets/Characters/Elf_female/ElfFemaleFaceLower00.png";
        if (!UpscaledTextures.Enabled)
        {
            // The originals-only debug mode is a supported configuration, so
            // assert it substitutes nothing rather than failing the whole probe.
            Expect(
                UpscaledTextures.ResolvePath(face).Length == 0 && UpscaledTextures.Load(face) == null,
                "the disable toggle suppresses every substitution");
            GD.Print("UPSCALED_PROBE_DECODE disabled=true");
            return;
        }

        string resolved = UpscaledTextures.ResolvePath(face);
        Expect(resolved.Length > 0, $"a variant resolves on disk for {face}");

        Texture2D? original = ResourceLoader.Load<Texture2D>(face);
        Texture2D? upscaled = UpscaledTextures.Load(face);
        Expect(upscaled != null, "the variant decodes through FileAccess despite the .gdignore");

        if (original != null && upscaled != null)
        {
            Expect(
                upscaled.GetWidth() == original.GetWidth() * 4,
                $"the variant is 4x wide: original {original.GetWidth()} vs upscaled {upscaled.GetWidth()}");
            GD.Print(
                $"UPSCALED_PROBE_DECODE original={original.GetWidth()}x{original.GetHeight()} "
                + $"upscaled={upscaled.GetWidth()}x{upscaled.GetHeight()}");
        }

        // The 132 creature textures the batch logged as "invalidated" (blank
        // Real-ESRGAN output) were re-run and the retry succeeded, so the file
        // on disk is good art and must be preferred like any other.
        string retried =
            $"{ConvertedRoot}/assets/Creatures/DruidPets/Lynx/LynxPet01body_Green.png";
        Texture2D? retriedTexture = UpscaledTextures.Load(retried);
        Expect(retriedTexture != null, $"the retried variant of {retried} loads");
        Expect(
            retriedTexture == null || !IsBlank(retriedTexture),
            "the retried variant is not the blank output the batch first rejected");

        // Terrain is a separate lane; nothing under Maps/ may be substituted.
        Expect(
            UpscaledTextures.Load(
                $"{ConvertedRoot}/assets/Maps/Inst_LeagueStart/000_020/C1_Grass.png") == null,
            "terrain keeps its originals");
    }

    private static bool IsBlank(Texture2D texture)
    {
        Image? image = texture.GetImage();
        if (image == null || image.IsEmpty())
        {
            return true;
        }

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        Color first = image.GetPixel(0, 0);
        int stepX = System.Math.Max(1, image.GetWidth() / 24);
        int stepY = System.Math.Max(1, image.GetHeight() / 24);
        for (int y = 0; y < image.GetHeight(); y += stepY)
        {
            for (int x = 0; x < image.GetWidth(); x += stepX)
            {
                if (image.GetPixel(x, y) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async System.Threading.Tasks.Task CheckZoneCoverage()
    {
        PackedScene packed = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node3D zone = packed.Instantiate<Node3D>();
        AddChild(zone);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);

        int upscaledSurfaces = 0;
        int originalSurfaces = 0;
        var originalsByClass = new Dictionary<string, int>();
        foreach (MeshInstance3D mesh in Descendants(zone).OfType<MeshInstance3D>())
        {
            for (int surface = 0; surface < mesh.Mesh?.GetSurfaceCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not BaseMaterial3D { AlbedoTexture: { } albedo })
                {
                    continue;
                }

                if (UpscaledTextures.IsUpscaled(albedo))
                {
                    upscaledSurfaces++;
                }
                else
                {
                    originalSurfaces++;
                    string key = ClassifyOriginal(albedo.ResourcePath);
                    originalsByClass[key] = originalsByClass.GetValueOrDefault(key) + 1;
                }
            }
        }

        GD.Print(
            $"UPSCALED_PROBE_ZONE upscaled_surfaces={upscaledSurfaces} original_surfaces={originalSurfaces}");
        foreach (KeyValuePair<string, int> entry in originalsByClass.OrderByDescending(e => e.Value))
        {
            GD.Print($"UPSCALED_PROBE_ORIGINAL {entry.Key}={entry.Value}");
        }
        GD.Print(UpscaledTextures.StatsLine());
        GD.Print(
            $"UPSCALED_PROBE_VRAM texture_mem_mb="
            + $"{Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / (1024.0 * 1024.0):F1} "
            + $"video_mem_mb={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0):F1}");
    }

    /// <summary>
    /// Buckets a surface that kept its original by why: which asset class it
    /// came from, or that it was already a runtime-built texture.
    /// </summary>
    private static string ClassifyOriginal(string resourcePath)
    {
        if (resourcePath.Length == 0)
        {
            return "runtime-built(no-path)";
        }

        const string marker = "/assets/";
        int at = resourcePath.LastIndexOf(marker, System.StringComparison.Ordinal);
        if (at < 0)
        {
            return "non-converted";
        }

        string rest = resourcePath[(at + marker.Length)..];
        int slash = rest.IndexOf('/');
        string assetClass = slash < 0 ? rest : rest[..slash];
        return UpscaledTextures.MapPath(resourcePath, UpscaledRoot).Length > 0
            ? $"{assetClass}(variant-exists-but-not-substituted)"
            : $"{assetClass}(not-batched)";
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
