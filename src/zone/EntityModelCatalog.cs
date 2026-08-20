using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SarnautCore;

/// <summary>The converted model one content id renders as.</summary>
public readonly record struct EntityModel(string ScenePath, float Scale);

/// <summary>
/// Answers "what does <c>mob.inst-league1.rat.rat1-1</c> look like".
/// </summary>
/// <remarks>
/// <para>
/// A snapshot names an entity by content id and nothing else: the wire carries
/// content references, never display data (ADR 0007, replication.proto). The
/// shard resolves those ids against the runtime pack; the client has to resolve
/// the same ids against the converted asset tree, and the bridge between the two
/// is a manifest that ships with the converted assets rather than with this
/// repository — it is derived from extracted content and has no business in a
/// public tree.
/// </para>
/// <para>
/// The manifest is therefore optional by design. A checkout with no
/// <c>converted/</c> resolves nothing, and every entity falls back to a labelled
/// capsule instead of throwing: that is the state CI builds in.
/// </para>
/// </remarks>
public sealed class EntityModelCatalog
{
    public const string ManifestFileName = "entity_models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AllodsResourceTree _tree;
    private readonly Dictionary<string, ManifestEntry> _manifest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolution results, hits and misses alike. A miss is worth caching: a
    /// zone full of one unresolvable mob would otherwise reparse the same XML
    /// once per spawn.
    /// </summary>
    private readonly Dictionary<string, EntityModel?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public EntityModelCatalog(string convertedRoot)
    {
        _tree = new AllodsResourceTree(convertedRoot);
        ManifestPath = $"{_tree.ConvertedRoot}/{ManifestFileName}";
        LoadManifest();
    }

    public string ManifestPath { get; }

    /// <summary>How many content ids the manifest names.</summary>
    public int EntryCount => _manifest.Count;

    /// <summary>
    /// True when there is a converted tree and a manifest to read; false is the
    /// ordinary state of a source checkout and not an error.
    /// </summary>
    public bool IsAvailable => _manifest.Count > 0;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>Content ids that named a model the converted tree could not produce.</summary>
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string contentId, out EntityModel model)
    {
        model = default;
        if (string.IsNullOrEmpty(contentId))
        {
            return false;
        }

        if (_resolved.TryGetValue(contentId, out EntityModel? cached))
        {
            if (cached is null)
            {
                return false;
            }

            model = cached.Value;
            return true;
        }

        EntityModel? resolved = Resolve(contentId);
        _resolved[contentId] = resolved;
        if (resolved is null)
        {
            if (_manifest.ContainsKey(contentId))
            {
                _unresolved.Add(contentId);
            }

            return false;
        }

        model = resolved.Value;
        return true;
    }

    private EntityModel? Resolve(string contentId)
    {
        if (!_manifest.TryGetValue(contentId, out ManifestEntry? entry))
        {
            return null;
        }

        float scale = entry.Scale > 0 ? entry.Scale : 1.0f;
        if (!string.IsNullOrWhiteSpace(entry.Scene))
        {
            string scenePath = entry.Scene.StartsWith("res://", StringComparison.Ordinal)
                ? entry.Scene
                : $"{_tree.ConvertedRoot}/assets/{entry.Scene.TrimStart('/')}";
            return ConvertedSceneLoader.IsLoadable(scenePath, "PackedScene")
                ? new EntityModel(scenePath, scale)
                : null;
        }

        if (string.IsNullOrWhiteSpace(entry.VisualRef))
        {
            return null;
        }

        string visualMobSource = AllodsResourceTree.NormalizeHref(string.Empty, entry.VisualRef);
        if (!_tree.TryResolveVisualMob(visualMobSource, out string resolvedScene, out float authoredScale))
        {
            return null;
        }

        if (!ConvertedSceneLoader.IsLoadable(resolvedScene, "PackedScene"))
        {
            return null;
        }

        // An explicit scale in the manifest wins; otherwise the visual's own.
        return new EntityModel(resolvedScene, entry.Scale > 0 ? entry.Scale : authoredScale);
    }

    private void LoadManifest()
    {
        if (!FileAccess.FileExists(ManifestPath))
        {
            LastError = $"No entity model manifest at {ManifestPath}; entities render as labelled capsules.";
            return;
        }

        try
        {
            string json = FileAccess.GetFileAsString(ManifestPath);
            ManifestDocument? document = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions);
            if (document?.Models == null)
            {
                LastError = $"Entity model manifest {ManifestPath} has no models.";
                return;
            }

            foreach (KeyValuePair<string, ManifestEntry> pair in document.Models)
            {
                if (pair.Value != null)
                {
                    _manifest[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception exception)
        {
            LastError = $"Entity model manifest {ManifestPath} could not be read: {exception.Message}";
            GD.PushWarning(LastError);
        }
    }

    private sealed class ManifestDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("ruleset")] public string Ruleset { get; set; } = string.Empty;
        [JsonPropertyName("models")] public Dictionary<string, ManifestEntry>? Models { get; set; }
    }

    private sealed class ManifestEntry
    {
        /// <summary>The mob's <c>VisualMob</c> href, as authored in the source tree.</summary>
        [JsonPropertyName("visual_ref")] public string VisualRef { get; set; } = string.Empty;

        /// <summary>An already-resolved scene, for content the walk cannot reach.</summary>
        [JsonPropertyName("scene")] public string Scene { get; set; } = string.Empty;

        [JsonPropertyName("scale")] public float Scale { get; set; }
    }
}
