using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace SarnautCore;

internal static class ConvertedSceneLoader
{
    private static readonly Regex ConvertedSceneDependency = new(
        "ext_resource type=\"PackedScene\" path=\"res://assets/(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConvertedResourceDependency = new(
        "ext_resource type=\"[^\"]+\" path=\"res://assets/(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SkinnedMeshObjFallback = new(
        "^\\[ext_resource type=\"Mesh\" path=\"res://assets/[^\"]+\" id=\"mesh\"\\]\\r?\\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    // VRAM-compressed imports write format-suffixed remap keys ("path.s3tc=",
    // "path.etc2=", ...) instead of a plain "path=" line, so the suffix is part
    // of the pattern.
    private static readonly Regex ImportedResourcePath = new(
        "^path(?:\\.[a-z0-9_]+)?=\"(?<path>res://\\.godot/imported/[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex AnimationResourceLine = new(
        "^\\[ext_resource type=\"Animation\" path=\"(?<path>[^\"]+)\" id=\"(?<id>[^\"]+)\"\\]\\r?\\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex LocomotionAnimationPath = new(
        "\\.(Idle[^.]*|Run[^.]*|Walk[^.]*|Attack[^.]*|Battle[^.]*|Hit[^.]*|Damage[^.]*|Death[^.]*|Dead[^.]*)" +
        "\\.\\(SkeletalAnimation\\)\\.animation\\.tres$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AnimationLibraryDataLine = new(
        "^_data = \\{(?<entries>.*)\\}\\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex AnimationLibraryEntry = new(
        "&\"(?<name>[^\"]+)\"\\s*:\\s*ExtResource\\(\"(?<id>[^\"]+)\"\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SceneNodeHeader = new(
        "^\\[node name=\"(?<name>[^\"]+)\"(?<rest>[^\\]]*)\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex SceneNodeParent = new(
        " parent=\"(?<parent>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, PackedScene?> SceneCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Resource?> ResourceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ErrorCache = new(StringComparer.OrdinalIgnoreCase);

    private const string CacheDirectory = "user://converted_scene_cache";

    public static PackedScene? Load(
        string convertedRoot,
        string scenePath,
        out string error,
        bool enableRuntimeSkinnedMesh = false,
        bool locomotionOnly = false)
    {
        string normalizedRoot = convertedRoot.TrimEnd('/');
        string normalizedScenePath = scenePath.StartsWith("res://", StringComparison.Ordinal)
            ? scenePath
            : $"{normalizedRoot}/assets/{scenePath.TrimStart('/')}";

        // The modification time is part of the key, not just the path and the
        // flags. Without it a reconversion writes a new scene and this cache
        // keeps serving the patched copy of the old one, which looks like the
        // converter silently doing nothing.
        string cacheKey =
            $"{normalizedScenePath}|skinned={enableRuntimeSkinnedMesh}|locomotion={locomotionOnly}" +
            $"|mtime={FileAccess.GetModifiedTime(normalizedScenePath)}";
        if (SceneCache.TryGetValue(cacheKey, out PackedScene? cached))
        {
            error = ErrorCache.GetValueOrDefault(cacheKey, string.Empty);
            return cached;
        }

        SceneCache[cacheKey] = null;
        string source = FileAccess.GetFileAsString(normalizedScenePath);
        if (string.IsNullOrWhiteSpace(source))
        {
            return Fail(cacheKey, $"Converted scene not found: {normalizedScenePath}", out error);
        }

        if (enableRuntimeSkinnedMesh && source.Contains("metadata/allods_skin_mesh", StringComparison.Ordinal))
        {
            source = SkinnedMeshObjFallback.Replace(source, string.Empty);
            source = source.Replace("mesh = ExtResource(\"mesh\")\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("mesh = ExtResource(\"mesh\")\n", string.Empty, StringComparison.Ordinal);
            // Regenerated scenes hardwire the ImporterMesh LOD script on the
            // root, which cannot re-dress a character at runtime. This flag's
            // contract is the runtime ArrayMesh route, whose class exposes
            // ApplyAppearance for per-slot geosets and body atlases.
            source = source.Replace(
                "res://src/GodotNative/ConvertedImporterMesh.cs",
                "res://src/GodotNative/ConvertedSkinnedMesh.cs",
                StringComparison.Ordinal);
        }

        if (locomotionOnly)
        {
            source = KeepLocomotionAnimations(source);
        }

        string assetsRoot = $"{normalizedRoot}/assets/";
        foreach (Match match in ConvertedResourceDependency.Matches(source))
        {
            string dependencyPath = assetsRoot + match.Groups["path"].Value;
            if (!IsLoadable(dependencyPath))
            {
                return Fail(
                    cacheKey,
                    $"Converted scene {normalizedScenePath} is missing dependency {dependencyPath}",
                    out error);
            }
        }

        foreach (Match match in ConvertedSceneDependency.Matches(source))
        {
            string childPath = assetsRoot + match.Groups["path"].Value;
            PackedScene? child = Load(
                normalizedRoot,
                childPath,
                out string childError,
                enableRuntimeSkinnedMesh,
                locomotionOnly);
            if (child == null)
            {
                return Fail(cacheKey, childError, out error);
            }

            child.TakeOverPath(childPath);
        }

        string relocated = DeduplicateSiblingNames(
            source.Replace("res://assets/", assetsRoot, StringComparison.Ordinal));
        if (!TryWritePatchedCopy(cacheKey, relocated, ".tscn", out string cachePath, out string writeError))
        {
            return Fail(cacheKey, writeError, out error);
        }

        PackedScene? scene = ResourceLoader.Load<PackedScene>(cachePath, string.Empty, ResourceLoader.CacheMode.Replace);
        if (scene == null)
        {
            return Fail(cacheKey, $"Godot could not load converted scene {normalizedScenePath}", out error);
        }

        scene.TakeOverPath(normalizedScenePath);
        SceneCache[cacheKey] = scene;
        ErrorCache[cacheKey] = string.Empty;
        error = string.Empty;
        return scene;
    }

    /// <summary>
    /// Loads one converted <c>.tres</c>, patching the converter's own
    /// <c>res://assets/</c> prefixes the same way <see cref="Load"/> does.
    /// </summary>
    /// <remarks>
    /// The converter writes every dependency relative to the tree it emitted, so
    /// a converted theme's font references resolve nowhere when the tree is
    /// mounted under <c>converted/</c>. Returns null with a reason rather than
    /// throwing: every caller of this has a code-built fallback, because
    /// <c>converted/</c> is gitignored and a fresh clone has none of it.
    /// </remarks>
    public static T? LoadResource<T>(string convertedRoot, string resourcePath, out string error)
        where T : Resource
    {
        string normalizedRoot = convertedRoot.TrimEnd('/');
        string normalizedPath = resourcePath.StartsWith("res://", StringComparison.Ordinal)
            ? resourcePath
            : $"{normalizedRoot}/assets/{resourcePath.TrimStart('/')}";

        string cacheKey = $"{normalizedPath}|as={typeof(T).Name}|mtime={FileAccess.GetModifiedTime(normalizedPath)}";
        if (ResourceCache.TryGetValue(cacheKey, out Resource? cached))
        {
            error = ErrorCache.GetValueOrDefault(cacheKey, string.Empty);
            return cached as T;
        }

        ResourceCache[cacheKey] = null;
        string source = FileAccess.GetFileAsString(normalizedPath);
        if (string.IsNullOrWhiteSpace(source))
        {
            return FailResource<T>(cacheKey, $"Converted resource not found: {normalizedPath}", out error);
        }

        string assetsRoot = $"{normalizedRoot}/assets/";
        foreach (Match match in ConvertedResourceDependency.Matches(source))
        {
            string dependencyPath = assetsRoot + match.Groups["path"].Value;
            if (!IsLoadable(dependencyPath))
            {
                return FailResource<T>(
                    cacheKey,
                    $"Converted resource {normalizedPath} is missing dependency {dependencyPath}",
                    out error);
            }
        }

        string relocated = source.Replace("res://assets/", assetsRoot, StringComparison.Ordinal);
        if (!TryWritePatchedCopy(cacheKey, relocated, ".tres", out string cachePath, out string writeError))
        {
            return FailResource<T>(cacheKey, writeError, out error);
        }

        Resource? loaded = ResourceLoader.Load(cachePath, typeof(T).Name, ResourceLoader.CacheMode.Replace);
        if (loaded is not T typed)
        {
            return FailResource<T>(
                cacheKey,
                $"Converted resource {normalizedPath} is not a {typeof(T).Name}",
                out error);
        }

        // Texture references inside a converted .tres (ImporterMesh surface
        // materials, theme icons) are ext_resource paths Godot has already
        // resolved by now, so the upscaled variant is substituted on the loaded
        // object rather than in the patched text. Done once per cache entry.
        UpscaledTextures.Retexture(typed);

        typed.TakeOverPath(normalizedPath);
        ResourceCache[cacheKey] = typed;
        ErrorCache[cacheKey] = string.Empty;
        error = string.Empty;
        return typed;
    }

    public static bool IsLoadable(string resourcePath, string typeHint = "")
    {
        if (!FileAccess.FileExists(resourcePath))
        {
            return false;
        }

        string importPath = resourcePath + ".import";
        if (FileAccess.FileExists(importPath))
        {
            // Any one existing compiled artifact makes the resource loadable:
            // the editor only compiles the formats this machine renders with.
            foreach (Match imported in ImportedResourcePath.Matches(FileAccess.GetFileAsString(importPath)))
            {
                if (FileAccess.FileExists(imported.Groups["path"].Value))
                {
                    return true;
                }
            }

            return false;
        }

        return ResourceLoader.Exists(resourcePath, typeHint);
    }

    private static string KeepLocomotionAnimations(string source)
    {
        var keptIds = new HashSet<string>(StringComparer.Ordinal);
        string trimmed = AnimationResourceLine.Replace(source, match =>
        {
            if (!LocomotionAnimationPath.IsMatch(match.Groups["path"].Value))
            {
                return string.Empty;
            }

            keptIds.Add(match.Groups["id"].Value);
            return match.Value;
        });

        if (keptIds.Count == 0)
        {
            return source;
        }

        return AnimationLibraryDataLine.Replace(trimmed, match =>
        {
            var entries = AnimationLibraryEntry.Matches(match.Groups["entries"].Value)
                .Where(entry => keptIds.Contains(entry.Groups["id"].Value))
                .Select(entry => entry.Value);
            return $"_data = {{{string.Join(", ", entries)}}}";
        });
    }

    /// <summary>
    /// Renames colliding sibling nodes ("Widget", "Widget2", ...). The converter
    /// names every widget-layer scene root "Widget", so a panel composing two
    /// layers declares two same-named siblings; Godot then silently drops the
    /// later one at instantiation — created and leaked at exit, never rendered.
    /// Only later duplicates are renamed, so paths to the first sibling keep
    /// resolving exactly as before.
    /// </summary>
    private static string DeduplicateSiblingNames(string source)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        return SceneNodeHeader.Replace(source, match =>
        {
            string rest = match.Groups["rest"].Value;
            Match parentMatch = SceneNodeParent.Match(rest);
            if (!parentMatch.Success)
            {
                return match.Value;
            }

            string name = match.Groups["name"].Value;
            string parent = parentMatch.Groups["parent"].Value;
            if (taken.Add($"{parent}\n{name}"))
            {
                return match.Value;
            }

            int suffix = 2;
            while (!taken.Add($"{parent}\n{name}{suffix}"))
            {
                suffix++;
            }

            return $"[node name=\"{name}{suffix}\"{rest}]";
        });
    }

    /// <summary>
    /// Writes the relocated copy under <c>user://</c> and hands back its path.
    /// The file name is the SHA-256 of the cache key, which now carries the
    /// source's modification time, so a reconversion writes a new file rather
    /// than reusing the old one.
    /// </summary>
    private static bool TryWritePatchedCopy(
        string cacheKey,
        string content,
        string extension,
        out string cachePath,
        out string error)
    {
        cachePath = string.Empty;
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(CacheDirectory));
        if (directoryError != Error.Ok && directoryError != Error.AlreadyExists)
        {
            error = $"Could not create converted-scene cache: {directoryError}";
            return false;
        }

        string hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))
            .ToLowerInvariant();
        cachePath = $"{CacheDirectory}/{hash}{extension}";
        using FileAccess? file = FileAccess.Open(cachePath, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            error = $"Could not write converted-scene cache file {cachePath}";
            return false;
        }

        file.StoreString(content);
        error = string.Empty;
        return true;
    }

    private static PackedScene? Fail(string scenePath, string message, out string error)
    {
        ErrorCache[scenePath] = message;
        error = message;
        return null;
    }

    private static T? FailResource<T>(string cacheKey, string message, out string error)
        where T : Resource
    {
        ErrorCache[cacheKey] = message;
        error = message;
        return null;
    }
}
