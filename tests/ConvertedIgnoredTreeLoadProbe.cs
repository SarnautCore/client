using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using SarnautCore;

/// <summary>
/// Proves the converted tree stays loadable at runtime with its .gdignore in
/// place. The editor's filesystem database never sees res://converted, so every
/// claim here has to hold without it: imported types resolve through their
/// .import sidecars (ResourceFormatImporter reads them with FileAccess), text
/// resources parse directly, and a TakeOverPath'd texture satisfies a scene's
/// ext_resource from the resource cache alone. That last check is the fallback
/// mechanism ConvertedSceneLoader would need if sidecar resolution ever
/// regressed, pinned here so the option stays proven.
///
/// Not part of visual-gate.ps1 — that gate stays at its 16 rendering probes.
/// Run directly: godot_console --path . res://tests/converted_ignored_tree_load_probe.tscn
/// </summary>
public partial class ConvertedIgnoredTreeLoadProbe : Node
{
    private const string ConvertedRoot = "res://converted/assets/classic-1.1";
    private const string ModelRoot =
        ConvertedRoot + "/assets/World/Astral/Astral/Buildings/Models/Astral_port";
    private const string TexturePath =
        ConvertedRoot + "/assets/World/Astral/Astral/Buildings/Textures/AstralPort_Wood_01.png";
    private const string MeshResourcePath = ModelRoot + "/Astral_Balk_01.(Geometry).mesh.tres";
    private const string StaticScenePath = ModelRoot + "/Astral_Balk_01.(StaticObject).scene.tscn";

    // Matches ConvertedSceneLoader's sidecar parsing, including the
    // format-suffixed keys VRAM-compressed imports write ("path.s3tc=").
    private static readonly Regex ImportedResourcePath = new(
        "^path(?:\\.[a-z0-9_]+)?=\"(?<path>res://\\.godot/imported/[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        CheckIgnoredTreeIsStillOnDisk();
        CheckSidecarTextureLoad();
        CheckTextResourceLoads();
        CheckConvertedSceneLoaderSeam();
        CheckExtResourceResolvesFromCache();

        foreach (string failure in _failures)
        {
            GD.PrintErr($"IGNORED_TREE_PROBE_FAIL {failure}");
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

    /// <summary>The probe only proves something when the tree is ignored.</summary>
    private void CheckIgnoredTreeIsStillOnDisk()
    {
        Expect(
            FileAccess.FileExists("res://converted/.gdignore"),
            "the .gdignore sentinel exists at the converted mount");
        Expect(FileAccess.FileExists(TexturePath), $"FileAccess still sees {TexturePath}");
        Expect(
            FileAccess.GetModifiedTime(TexturePath) > 0,
            "FileAccess.GetModifiedTime works through the junction (loader cache keys rely on it)");
    }

    /// <summary>
    /// A converted .png with a warm .import sidecar loads through ResourceLoader
    /// at runtime: ResourceFormatImporter follows the sidecar's path= remap into
    /// .godot/imported without any editor database.
    /// </summary>
    private void CheckSidecarTextureLoad()
    {
        Expect(
            ResourceLoader.Exists(TexturePath, "Texture2D"),
            $"ResourceLoader.Exists resolves {TexturePath} from its sidecar");

        var texture = ResourceLoader.Load<Texture2D>(TexturePath);
        Expect(texture != null, $"ResourceLoader.Load decodes {TexturePath}");
        if (texture != null)
        {
            Expect(
                texture is CompressedTexture2D && texture.GetWidth() > 0,
                $"the load produced a compiled artifact, got {texture.GetClass()} "
                + $"{texture.GetWidth()}x{texture.GetHeight()}");
            GD.Print(
                $"IGNORED_TREE_PROBE_TEXTURE class={texture.GetClass()} "
                + $"size={texture.GetWidth()}x{texture.GetHeight()}");
        }
    }

    /// <summary>
    /// Unimported text resources (.tres/.tscn) are parsed by the text loader
    /// straight off disk, so existence checks — IsLoadable's fallback branch —
    /// keep answering true for the ignored tree.
    /// </summary>
    private void CheckTextResourceLoads()
    {
        Expect(
            ResourceLoader.Exists(MeshResourcePath),
            $"ResourceLoader.Exists resolves the unimported {MeshResourcePath}");
        Expect(
            ResourceLoader.Exists(StaticScenePath, "PackedScene"),
            $"ResourceLoader.Exists resolves the unimported {StaticScenePath}");
        Expect(
            ConvertedSceneLoader.IsLoadable(TexturePath),
            "IsLoadable accepts the imported texture");
        Expect(
            ConvertedSceneLoader.IsLoadable(MeshResourcePath),
            "IsLoadable accepts the unimported mesh resource");
    }

    /// <summary>
    /// The full seam: FileAccess source read, res://assets/ relocation, patched
    /// user:// copy, and ext_resource textures loaded from the ignored tree.
    /// </summary>
    private void CheckConvertedSceneLoaderSeam()
    {
        ImporterMesh? mesh = ConvertedSceneLoader.LoadResource<ImporterMesh>(
            ConvertedRoot, MeshResourcePath, out string meshError);
        Expect(mesh != null, $"LoadResource<ImporterMesh> failed: {meshError}");
        if (mesh != null)
        {
            Expect(mesh.GetSurfaceCount() > 0, "the mesh kept its surfaces");
            int textured = 0;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.GetSurfaceMaterial(surface) is BaseMaterial3D { AlbedoTexture: not null })
                {
                    textured++;
                }
            }

            Expect(
                textured == mesh.GetSurfaceCount(),
                $"every surface resolved its ext_resource albedo: {textured}/{mesh.GetSurfaceCount()}");
            GD.Print(
                $"IGNORED_TREE_PROBE_MESH surfaces={mesh.GetSurfaceCount()} textured={textured}");
        }

        PackedScene? scene = ConvertedSceneLoader.Load(
            ConvertedRoot, StaticScenePath, out string sceneError);
        Expect(scene != null, $"ConvertedSceneLoader.Load failed: {sceneError}");
        if (scene != null)
        {
            Node instance = scene.Instantiate();
            Expect(instance != null, "the converted static object instantiates");
            instance?.QueueFree();
        }
    }

    /// <summary>
    /// The fallback mechanism, proven end to end: load a compiled artifact
    /// directly, TakeOverPath it to a path that exists nowhere on disk, and a
    /// scene whose ext_resource names that path still instantiates with the
    /// seeded instance. This is what "the text loader consults the resource
    /// cache for ext_resources" means in practice — a plain CacheMode.Replace
    /// on the outer load leaves dependency loads on the cache-reusing path.
    /// </summary>
    private void CheckExtResourceResolvesFromCache()
    {
        string artifactPath = string.Empty;
        foreach (Match match in ImportedResourcePath.Matches(
            FileAccess.GetFileAsString(TexturePath + ".import")))
        {
            if (FileAccess.FileExists(match.Groups["path"].Value))
            {
                artifactPath = match.Groups["path"].Value;
                break;
            }
        }

        Expect(artifactPath.Length > 0, "the texture sidecar names an existing compiled artifact");
        if (artifactPath.Length == 0)
        {
            return;
        }

        var seeded = new CompressedTexture2D();
        Expect(
            seeded.Load(artifactPath) == Error.Ok && seeded.GetWidth() > 0,
            $"CompressedTexture2D.Load reads {artifactPath}");

        const string phantomPath = "res://converted/__cache_probe__/phantom.png";
        Expect(!FileAccess.FileExists(phantomPath), "the phantom path has no file behind it");
        seeded.TakeOverPath(phantomPath);

        const string cachePath = "user://ignored_tree_cache_probe.tscn";
        using (FileAccess? file = FileAccess.Open(cachePath, FileAccess.ModeFlags.Write))
        {
            if (file == null)
            {
                Expect(false, $"could not write {cachePath}");
                return;
            }

            file.StoreString(
                "[gd_scene load_steps=2 format=3]\n\n"
                + $"[ext_resource type=\"Texture2D\" path=\"{phantomPath}\" id=\"1_tex\"]\n\n"
                + "[node name=\"CacheProbe\" type=\"Sprite2D\"]\n"
                + "texture = ExtResource(\"1_tex\")\n");
        }

        var packed = ResourceLoader.Load<PackedScene>(
            cachePath, string.Empty, ResourceLoader.CacheMode.Replace);
        Expect(packed != null, "the phantom-referencing scene loads");
        if (packed == null)
        {
            return;
        }

        var sprite = packed.Instantiate<Sprite2D>();
        Expect(
            ReferenceEquals(sprite.Texture, seeded),
            "the ext_resource resolved to the exact TakeOverPath'd instance");
        GD.Print(
            $"IGNORED_TREE_PROBE_CACHE seeded_instance_hit={ReferenceEquals(sprite.Texture, seeded)}");
        sprite.QueueFree();
    }
}
