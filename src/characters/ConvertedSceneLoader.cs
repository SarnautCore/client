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
    private static readonly Regex ImportedResourcePath = new(
        "^path=\"(?<path>res://\\.godot/imported/[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex AnimationResourceLine = new(
        "^\\[ext_resource type=\"Animation\" path=\"(?<path>[^\"]+)\" id=\"(?<id>[^\"]+)\"\\]\\r?\\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex LocomotionAnimationPath = new(
        "\\.(Idle|Run|Walk)\\.\\(SkeletalAnimation\\)\\.animation\\.tres$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AnimationLibraryDataLine = new(
        "^_data = \\{(?<entries>.*)\\}\\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex AnimationLibraryEntry = new(
        "&\"(?<name>[^\"]+)\"\\s*:\\s*ExtResource\\(\"(?<id>[^\"]+)\"\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, PackedScene?> SceneCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ErrorCache = new(StringComparer.OrdinalIgnoreCase);

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

        string cacheKey = $"{normalizedScenePath}|skinned={enableRuntimeSkinnedMesh}|locomotion={locomotionOnly}";
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

        string relocated = source.Replace("res://assets/", assetsRoot, StringComparison.Ordinal);
        string cacheDirectory = "user://converted_scene_cache";
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(cacheDirectory));
        if (directoryError != Error.Ok && directoryError != Error.AlreadyExists)
        {
            return Fail(
                cacheKey,
                $"Could not create converted-scene cache: {directoryError}",
                out error);
        }

        string hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))
            .ToLowerInvariant();
        string cachePath = $"{cacheDirectory}/{hash}.tscn";
        using (FileAccess? file = FileAccess.Open(cachePath, FileAccess.ModeFlags.Write))
        {
            if (file == null)
            {
                return Fail(cacheKey, $"Could not write converted-scene cache file {cachePath}", out error);
            }

            file.StoreString(relocated);
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

    public static bool IsLoadable(string resourcePath, string typeHint = "")
    {
        if (!FileAccess.FileExists(resourcePath))
        {
            return false;
        }

        string importPath = resourcePath + ".import";
        if (FileAccess.FileExists(importPath))
        {
            Match imported = ImportedResourcePath.Match(FileAccess.GetFileAsString(importPath));
            return imported.Success && FileAccess.FileExists(imported.Groups["path"].Value);
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

    private static PackedScene? Fail(string scenePath, string message, out string error)
    {
        ErrorCache[scenePath] = message;
        error = message;
        return null;
    }
}
