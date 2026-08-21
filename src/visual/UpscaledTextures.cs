using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>
/// The one place the client decides whether a texture load gets the Real-ESRGAN
/// x4 variant or the converted original.
///
/// The upscale batch mirrors the converted tree under a per-asset-class root
/// rather than reproducing its shape, so the mapping is not a prefix swap:
///
///   res://converted/assets/classic-1.1/assets/Characters/Elf_female/Face.png
///     -> res://upscaled/assets/characters/classic-1.1/Elf_female/Face.png
///
/// Interface art additionally drops the converter's ".(UITexture)" class suffix,
/// and Interface/Icons was batched as its own class. <see cref="ResolvePath"/>
/// reproduces every completed entry of all eight batch manifests exactly, so the
/// manifests do not have to ship with the client: presence on disk is the
/// authority, which is also what makes the batch's 3 skipped and 36 excluded
/// files fall back to their originals for free. (The 132 creature entries logged
/// "invalidated" are not among them: the manifests are append-only and every one
/// was re-run, so the file on disk is good art.)
///
/// The upscaled tree is mounted at res://upscaled/assets but carries a .gdignore,
/// so Godot never imports it. Variants are decoded through FileAccess/Image at
/// runtime — the same route <see cref="ConvertedSkinnedMesh"/> already uses for
/// an unimported converted tree. See docs/upscaled-texture-preference.md.
/// </summary>
public static class UpscaledTextures
{
    /// <summary>Master switch. Set false in project.godot to render originals only.</summary>
    public const string EnabledSetting = "sarnaut/visual/prefer_upscaled_textures";

    /// <summary>Where the upscale batch is mounted.</summary>
    public const string RootSetting = "sarnaut/visual/upscaled_root";

    /// <summary>
    /// Generate mipmaps for upscaled variants. Off, matching the converted
    /// originals' mipmaps/generate=false, and measured rather than assumed: a
    /// 4x texture minified to the same screen size selects roughly mip 2, which
    /// is the original resolution again. Turning this on in the League zone
    /// cost 100 MB and *lowered* frame detail (mean |Laplacian| on the near
    /// floor 8.09 -> 3.45, below the originals' own 4.02). Revisit together
    /// with a negative texture_mipmap_bias or anisotropic filtering, which is
    /// what would let a mip chain keep the detail it is meant to protect.
    /// </summary>
    public const string MipmapSetting = "sarnaut/visual/upscaled_mipmaps";

    /// <summary>
    /// Compress upscaled variants to a VRAM format after decode. 4x linear
    /// scale is 16x the texels; block compression buys most of that back —
    /// measured 1934 MB -> 429 MB of texture memory across the League zone.
    /// </summary>
    public const string VramCompressionSetting = "sarnaut/visual/upscaled_vram_compression";

    /// <summary>
    /// Set to "0" to force originals for one run without editing project.godot.
    /// Debug/evidence escape hatch; anything else leaves the setting in charge.
    /// </summary>
    public const string DisableEnvironmentVariable = "SARNAUT_UPSCALED_TEXTURES";

    private const string DefaultRoot = "res://upscaled/assets";
    private const string AssetsMarker = "/assets/";
    private const string IconsPrefix = "Icons/";

    private static readonly Dictionary<string, string> PathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D?> TextureCache = new(StringComparer.OrdinalIgnoreCase);

    // Substituted textures are runtime ImageTextures with no resource path, so
    // identity is the only way to tell them from the converted originals and
    // from the other runtime textures the codebase builds (Kania's atlas, the
    // unimported-tree fallback). Probes use this to measure real coverage.
    private static readonly HashSet<ulong> Substituted = new();

    private static bool? _enabled;
    private static string _root = string.Empty;
    private static bool? _mipmaps;
    private static bool? _vramCompression;

    /// <summary>Upscaled variants that were found and substituted.</summary>
    public static int Hits { get; private set; }

    /// <summary>Texture loads that fell back to the converted original.</summary>
    public static int Misses { get; private set; }

    /// <summary>Approximate GPU bytes of the upscaled variants held in cache.</summary>
    public static long ResidentBytes { get; private set; }

    /// <summary>Wall-clock milliseconds spent decoding upscaled PNGs.</summary>
    public static double DecodeMilliseconds { get; private set; }

    public static bool Enabled
    {
        get
        {
            _enabled ??= ReadEnabled();
            return _enabled.Value;
        }
    }

    private static bool ReadEnabled()
    {
        if (System.Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "0")
        {
            return false;
        }

        return ProjectSettings.GetSetting(EnabledSetting, true).AsBool();
    }

    private static string Root
    {
        get
        {
            if (_root.Length == 0)
            {
                _root = ProjectSettings.GetSetting(RootSetting, DefaultRoot).AsString().TrimEnd('/');
                if (_root.Length == 0)
                {
                    _root = DefaultRoot;
                }
            }

            return _root;
        }
    }

    /// <summary>
    /// Reads a boolean knob, letting an environment variable override the
    /// project setting for one run so the memory options can be measured
    /// without editing project.godot. "1"/"0" only, matching SARNAUT_LIGHT_DEBUG.
    /// </summary>
    private static bool ReadToggle(string setting, string environmentVariable, bool fallback)
    {
        string? overridden = System.Environment.GetEnvironmentVariable(environmentVariable);
        if (overridden == "1")
        {
            return true;
        }

        if (overridden == "0")
        {
            return false;
        }

        return ProjectSettings.GetSetting(setting, fallback).AsBool();
    }

    private static bool Mipmaps
    {
        get
        {
            _mipmaps ??= ReadToggle(MipmapSetting, "SARNAUT_UPSCALED_MIPMAPS", false);
            return _mipmaps.Value;
        }
    }

    private static bool VramCompression
    {
        get
        {
            _vramCompression ??= ReadToggle(
                VramCompressionSetting, "SARNAUT_UPSCALED_VRAM_COMPRESSION", false);
            return _vramCompression.Value;
        }
    }

    /// <summary>
    /// Maps a converted texture path to its upscaled variant, or returns an
    /// empty string when the preference is off or no variant exists on disk.
    /// Pure apart from the existence probe, which is cached.
    /// </summary>
    public static string ResolvePath(string convertedTexturePath)
    {
        if (!Enabled || string.IsNullOrEmpty(convertedTexturePath))
        {
            return string.Empty;
        }

        if (PathCache.TryGetValue(convertedTexturePath, out string? cached))
        {
            return cached;
        }

        string candidate = MapPath(convertedTexturePath, Root);
        string resolved = candidate.Length > 0 && FileAccess.FileExists(candidate)
            ? candidate
            : string.Empty;
        PathCache[convertedTexturePath] = resolved;
        return resolved;
    }

    /// <summary>
    /// The path arithmetic on its own, with the root passed in so it can be
    /// exercised without a Godot filesystem. Returns an empty string when the
    /// path is not a converted asset path or names an asset class the batch
    /// skipped (Maps terrain, SFX, System, Editor, Mechanics, Mods, fonts).
    /// </summary>
    public static string MapPath(string convertedTexturePath, string root)
    {
        if (string.IsNullOrEmpty(convertedTexturePath) || !convertedTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string normalized = convertedTexturePath.Replace('\\', '/');
        int marker = normalized.LastIndexOf(AssetsMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return string.Empty;
        }

        // The segment before the trailing "/assets/" is the source variant
        // ("classic-1.1"), which the upscale tree repeats under each class.
        string head = normalized[..marker];
        int variantStart = head.LastIndexOf('/');
        if (variantStart < 0)
        {
            return string.Empty;
        }

        string variant = head[(variantStart + 1)..];
        if (variant.Length == 0)
        {
            return string.Empty;
        }

        string rest = normalized[(marker + AssetsMarker.Length)..];
        int split = rest.IndexOf('/');
        if (split <= 0)
        {
            return string.Empty;
        }

        string assetClass = rest[..split].ToLowerInvariant();
        string tail = rest[(split + 1)..];
        if (tail.Length == 0 || !IsBatchedClass(assetClass))
        {
            return string.Empty;
        }

        if (assetClass == "interface")
        {
            if (tail.StartsWith(IconsPrefix, StringComparison.Ordinal))
            {
                assetClass = "icons";
                tail = tail[IconsPrefix.Length..];
            }

            // The converter stamps a class suffix onto interface art
            // ("Close.(UITexture).png"); the batch wrote plain names.
            tail = StripClassSuffix(tail);
        }

        return $"{root}/{assetClass}/{variant}/{tail}";
    }

    private static bool IsBatchedClass(string assetClass) => assetClass switch
    {
        "characters" or "client" or "creatures" or "interface" or "items"
            or "ships" or "spells" or "world" => true,
        _ => false,
    };

    private static string StripClassSuffix(string tail)
    {
        // "Foo.(UITexture).png" -> "Foo.png"; leaves "Foo.png" alone.
        int extension = tail.Length - ".png".Length;
        if (extension <= 0 || tail[extension - 1] != ')')
        {
            return tail;
        }

        int open = tail.LastIndexOf('(', extension - 1);
        if (open <= 0 || tail[open - 1] != '.')
        {
            return tail;
        }

        return string.Concat(tail.AsSpan(0, open - 1), tail.AsSpan(extension));
    }

    /// <summary>
    /// Returns the upscaled variant of a converted texture path, or null when
    /// the caller should keep whatever it already had. Decoded results are
    /// cached for the process lifetime, matching ConvertedSkinnedMesh.
    /// </summary>
    public static Texture2D? Load(string convertedTexturePath)
    {
        string upscaledPath = ResolvePath(convertedTexturePath);
        if (upscaledPath.Length == 0)
        {
            Misses++;
            return null;
        }

        if (TextureCache.TryGetValue(upscaledPath, out Texture2D? cached))
        {
            if (cached == null)
            {
                Misses++;
            }
            else
            {
                Hits++;
            }

            return cached;
        }

        ulong start = Time.GetTicksUsec();
        Texture2D? texture = Decode(upscaledPath, out long bytes);
        DecodeMilliseconds += (Time.GetTicksUsec() - start) / 1000.0;
        TextureCache[upscaledPath] = texture;

        if (texture == null)
        {
            Misses++;
            return null;
        }

        Hits++;
        Substituted.Add(texture.GetInstanceId());
        ResidentBytes += bytes;
        return texture;
    }

    /// <summary>True when this exact texture came out of the upscale batch.</summary>
    public static bool IsUpscaled(Texture2D? texture) =>
        texture != null && Substituted.Contains(texture.GetInstanceId());

    private static Texture2D? Decode(string upscaledPath, out long bytes)
    {
        bytes = 0;

        // Image is RefCounted too, and one is built per variant. Disposing it
        // here only drops this method's reference — CreateFromImage below has
        // already taken its own — and keeps its finalizer off the shutdown path.
        using Image? image = Image.LoadFromFile(ProjectSettings.GlobalizePath(upscaledPath));
        if (image == null || image.IsEmpty())
        {
            GD.PushWarning($"UpscaledTextures: cannot decode {upscaledPath}");
            return null;
        }

        if (Mipmaps && !image.HasMipmaps())
        {
            image.GenerateMipmaps();
        }

        if (VramCompression && !image.IsCompressed())
        {
            // Betsy/squish want mipmaps present to compress the whole chain, and
            // block compression needs dimensions it can tile; failures are not
            // fatal, the uncompressed image still renders.
            if (image.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Srgb) != Error.Ok)
            {
                GD.PushWarning($"UpscaledTextures: VRAM compression failed for {upscaledPath}");
            }
        }

        // Measured here rather than by reading the texture back, which would
        // copy every decoded image a second time just to count it.
        bytes = image.GetData().Length;
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Swaps every Texture2D reachable from <paramref name="root"/> that has an
    /// upscaled variant. Used after ConvertedSceneLoader hands back a scene or
    /// material, where the texture reference lives inside an already-imported
    /// resource rather than in a path the caller can intercept.
    /// Returns the number of substitutions.
    /// </summary>
    public static int Retexture(GodotObject? root)
    {
        if (!Enabled || root == null)
        {
            return 0;
        }

        var visited = new HashSet<ulong>();
        return RetextureObject(root, visited);
    }

    private static int RetextureObject(GodotObject target, HashSet<ulong> visited)
    {
        if (!GodotObject.IsInstanceValid(target) || !visited.Add(target.GetInstanceId()))
        {
            return 0;
        }

        int swapped = 0;

        // Each Dictionary in the property list wraps native storage; a full scene
        // walk makes thousands of them, so they are released as they are read
        // rather than left for the GC.
        foreach (Godot.Collections.Dictionary property in target.GetPropertyList())
        {
            string name;
            using (property)
            {
                if ((Variant.Type)(int)property["type"] != Variant.Type.Object)
                {
                    continue;
                }

                // Storage-only properties are the authored ones; skipping the
                // rest avoids waking lazily-built editor state.
                var usage = (PropertyUsageFlags)(int)property["usage"];
                if ((usage & PropertyUsageFlags.Storage) == 0)
                {
                    continue;
                }

                name = (string)property["name"];
            }

            Variant value;
            try
            {
                value = target.Get(name);
            }
            catch (Exception)
            {
                continue;
            }

            if (value.VariantType != Variant.Type.Object || value.AsGodotObject() is not GodotObject child)
            {
                continue;
            }

            if (child is Texture2D texture)
            {
                Texture2D? replacement = Load(texture.ResourcePath);
                if (replacement != null && !ReferenceEquals(replacement, texture))
                {
                    target.Set(name, replacement);
                    swapped++;
                }

                continue;
            }

            if (child is Resource)
            {
                swapped += RetextureObject(child, visited);
            }
        }

        // ImporterMesh is a plain Resource, not a Mesh, and exposes its surface
        // materials only through its own accessors. This is the branch the
        // converted zone props come through, so it carries most of the coverage.
        if (target is ImporterMesh importerMesh)
        {
            for (int surface = 0; surface < importerMesh.GetSurfaceCount(); surface++)
            {
                if (importerMesh.GetSurfaceMaterial(surface) is Material surfaceMaterial)
                {
                    swapped += RetextureObject(surfaceMaterial, visited);
                }
            }
        }

        // Mesh surface materials are not plain properties on every mesh type.
        if (target is Mesh mesh)
        {
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.SurfaceGetMaterial(surface) is Material material)
                {
                    swapped += RetextureObject(material, visited);
                }
            }
        }

        if (target is MeshInstance3D instance)
        {
            for (int surface = 0; surface < instance.GetSurfaceOverrideMaterialCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is Material active)
                {
                    swapped += RetextureObject(active, visited);
                }
            }
        }

        if (target is Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                swapped += RetextureObject(child, visited);
            }
        }

        return swapped;
    }

    /// <summary>One machine-readable line for probes and the visual gate.</summary>
    public static string StatsLine() =>
        $"UPSCALED_TEXTURES enabled={Enabled} hits={Hits} misses={Misses} " +
        $"cached={TextureCache.Count} resident_mb={ResidentBytes / (1024.0 * 1024.0):F1} " +
        $"decode_ms={DecodeMilliseconds:F0} mipmaps={Mipmaps} vram_compression={VramCompression}";

    /// <summary>
    /// Drains the managed wrappers a retexture walk left behind, while the
    /// engine is still up. Every <c>Variant.AsGodotObject()</c> during the walk
    /// creates a wrapper whose finalizer calls back into Godot; any that the GC
    /// runs after the engine has torn down its native side faults the process
    /// with 0xC000001D. Called by the SessionHost autoload from _ExitTree.
    ///
    /// The cached textures are deliberately left alone. Disposing them here
    /// looked safe — a material holding one keeps the native object alive — but
    /// it releases the C# binding's GCHandle while Godot still expects it, and
    /// the later native destructor then trips
    /// <c>FATAL: Condition "gchandle.is_released()" is true</c>. Letting the
    /// normal shutdown path release them is both simpler and correct.
    /// </summary>
    public static void Release()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Test seam: drops every cache and re-reads the settings.</summary>
    public static void Reset()
    {
        TextureCache.Clear();
        Substituted.Clear();
        PathCache.Clear();
        _enabled = null;
        _root = string.Empty;
        _mipmaps = null;
        _vramCompression = null;
        Hits = 0;
        Misses = 0;
        ResidentBytes = 0;
        DecodeMilliseconds = 0;
    }
}
