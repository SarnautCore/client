using System;
using System.Collections.Generic;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

/// <summary>A native character binding resolved from the content manifest.</summary>
public readonly record struct EntityModel(
    string CharacterKey,
    string IdentityId,
    string ScenePath,
    float Scale);

/// <summary>Maps canonical server and player keys to native character scenes.</summary>
public sealed class EntityModelCatalog
{
    private readonly NativeCharacterManifestReader _reader;
    private readonly Dictionary<string, EntityModel?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

    public EntityModelCatalog()
        : this(NativeContentSettings.NativeRoot)
    {
    }

    public EntityModelCatalog(string nativeRoot)
    {
        _reader = new NativeCharacterManifestReader(nativeRoot);
    }

    public string ManifestPath => _reader.ManifestPath;
    public int EntryCount => _reader.Manifest?.CharacterCount ?? 0;
    public bool IsAvailable => _reader.Manifest is not null;
    public string LastError => _reader.LastError;
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    public bool TryResolve(string contentId, out EntityModel model) =>
        TryResolve(contentId, playerOnly: false, out model);

    public bool TryResolvePlayer(string playerKey, out EntityModel model) =>
        TryResolve(playerKey, playerOnly: true, out model);

    private bool TryResolve(string key, bool playerOnly, out EntityModel model)
    {
        model = default;
        string candidate = (key ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        string cacheKey = playerOnly ? $"player:{candidate}" : candidate;
        if (_resolved.TryGetValue(cacheKey, out EntityModel? cached))
        {
            if (cached is null)
            {
                return false;
            }

            model = cached.Value;
            return true;
        }

        NativeCharacterModel nativeModel;
        bool found = playerOnly
            ? _reader.TryResolvePlayer(candidate, out nativeModel)
            : _reader.TryResolve(candidate, out nativeModel);
        if (!found)
        {
            _resolved[cacheKey] = null;
            return false;
        }

        string scenePath = _reader.ResolveScenePath(nativeModel);
        // The native mount is hidden from the editor database. Runtime scene
        // loading still works, so test the mounted file rather than the editor cache.
        if (!Godot.FileAccess.FileExists(scenePath))
        {
            _resolved[cacheKey] = null;
            _unresolved.Add(candidate);
            return false;
        }

        model = new EntityModel(
            nativeModel.CharacterKey,
            nativeModel.IdentityId,
            scenePath,
            nativeModel.Scale);
        _resolved[cacheKey] = model;
        return true;
    }
}
