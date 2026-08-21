using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.Content;

/// <summary>One native character scene selected by the content manifest.</summary>
public sealed record NativeCharacterModel(
    string CharacterKey,
    string Kind,
    string IdentityId,
    string ScenePath,
    IReadOnlyList<string> Clips,
    IReadOnlyList<string> CombatEventClips)
{
    /// <summary>The bake applies authored scale to the scene root.</summary>
    public float Scale => 1.0f;
}

/// <summary>
/// Maps server content ids and player appearance keys to native Godot scenes.
/// </summary>
public sealed class NativeCharacterManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string ExpectedManifestType = "sarnaut.characters";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyDictionary<string, NativeCharacterModel> _characters;
    private readonly IReadOnlyDictionary<string, string> _contentIds;

    private NativeCharacterManifest(
        IReadOnlyDictionary<string, NativeCharacterModel> characters,
        IReadOnlyDictionary<string, string> contentIds,
        int identityCount)
    {
        _characters = characters;
        _contentIds = contentIds;
        IdentityCount = identityCount;
    }

    public int CharacterCount => _characters.Count;

    public int ContentIdCount => _contentIds.Count;

    public int IdentityCount { get; }

    /// <summary>
    /// Resolves a canonical server content id, a decimal alias, or a player key.
    /// </summary>
    public bool TryResolve(string key, out NativeCharacterModel model)
    {
        model = null!;
        string candidate = (key ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (_characters.TryGetValue(candidate, out NativeCharacterModel? direct))
        {
            model = direct;
            return true;
        }

        return _contentIds.TryGetValue(candidate, out string? characterKey)
            && _characters.TryGetValue(characterKey, out model!);
    }

    public bool TryResolvePlayer(string playerKey, out NativeCharacterModel model)
    {
        if (!TryResolve(playerKey, out model))
        {
            return false;
        }

        return !string.Equals(model.Kind, "mob", StringComparison.Ordinal);
    }

    public static NativeCharacterManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Character manifest is empty.");
        }

        ManifestDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Character manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Character manifest is invalid JSON: {exception.Message}", exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Character manifest schema {document.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
        }

        if (!string.Equals(document.ManifestType, ExpectedManifestType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Character manifest type '{document.ManifestType}' is unsupported; expected '{ExpectedManifestType}'.");
        }

        if (document.Counts is null || document.Identities is null
            || document.Characters is null || document.ContentIds is null)
        {
            throw new InvalidDataException(
                "Character manifest must contain counts, identities, characters, and content_ids.");
        }

        var identities = CopyUnique(document.Identities, "identity");
        var characterEntries = CopyUnique(document.Characters, "character");
        var contentIds = CopyUnique(document.ContentIds, "content id");
        var characters = new Dictionary<string, NativeCharacterModel>(StringComparer.OrdinalIgnoreCase);
        int mobCount = 0;
        int playerCount = 0;
        int chargenCount = 0;

        foreach ((string characterKey, CharacterEntry entry) in characterEntries)
        {
            string kind = entry.Kind.Trim();
            bool isPlayer = string.Equals(kind, "player", StringComparison.Ordinal);
            bool isMob = string.Equals(kind, "mob", StringComparison.Ordinal);
            bool isChargen = string.Equals(kind, "chargen", StringComparison.Ordinal);
            if (!isPlayer && !isMob && !isChargen)
            {
                throw new InvalidDataException(
                    $"Character '{characterKey}' has kind '{entry.Kind}'; expected 'mob', 'player', or 'chargen'.");
            }

            if ((isPlayer && !characterKey.StartsWith("player.", StringComparison.OrdinalIgnoreCase))
                || (isChargen && !characterKey.StartsWith("chargen.", StringComparison.OrdinalIgnoreCase))
                || (isMob && (characterKey.StartsWith("player.", StringComparison.OrdinalIgnoreCase)
                    || characterKey.StartsWith("chargen.", StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException(
                    $"Character '{characterKey}' does not match its '{kind}' kind.");
            }

            string identityId = entry.IdentityId.Trim();
            if (!identities.TryGetValue(identityId, out IdentityEntry? identity))
            {
                throw new InvalidDataException(
                    $"Character '{characterKey}' names missing identity '{entry.IdentityId}'.");
            }

            string scene = ValidateScenePath(identityId, identity.Scene);
            IReadOnlyList<string> clips = ValidateClips(identityId, "clips", identity.Clips);
            IReadOnlyList<string> combatClips = ValidateClips(
                identityId,
                "combat_event_clips",
                identity.CombatEventClips);
            characters.Add(
                characterKey,
                new NativeCharacterModel(characterKey, kind, identityId, scene, clips, combatClips));

            if (isMob)
            {
                mobCount++;
                string contentId = entry.ContentId.Trim();
                if (contentId.Length == 0
                    || !contentIds.TryGetValue(contentId, out string? mappedKey)
                    || !string.Equals(mappedKey, characterKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Mob '{characterKey}' has no matching content_ids entry for '{entry.ContentId}'.");
                }
            }
            else if (isPlayer)
            {
                playerCount++;
                if (entry.ContentId.Trim().Length > 0)
                {
                    throw new InvalidDataException(
                        $"Player '{characterKey}' must not carry a decimal content_id.");
                }
            }
            else
            {
                chargenCount++;
                if (entry.ContentId.Trim().Length > 0)
                {
                    throw new InvalidDataException(
                        $"Chargen binding '{characterKey}' must not carry a decimal content_id.");
                }
            }
        }

        foreach ((string contentId, string characterKey) in contentIds)
        {
            if (!ulong.TryParse(contentId, out _))
            {
                throw new InvalidDataException($"Content id alias '{contentId}' is not decimal.");
            }

            if (!characters.TryGetValue(characterKey, out NativeCharacterModel? model)
                || !string.Equals(model.Kind, "mob", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Content id alias '{contentId}' names missing mob '{characterKey}'.");
            }
        }

        ValidateCount("mob_keys", document.Counts.MobKeys, mobCount);
        ValidateCount("player_keys", document.Counts.PlayerKeys, playerCount);
        ValidateCount("chargen_keys", document.Counts.ChargenKeys, chargenCount);
        ValidateCount("identity_scenes", document.Counts.IdentityScenes, identities.Count);
        if (contentIds.Count != mobCount)
        {
            throw new InvalidDataException(
                $"Character manifest has {contentIds.Count} content id aliases for {mobCount} mobs.");
        }

        return new NativeCharacterManifest(
            new ReadOnlyDictionary<string, NativeCharacterModel>(characters),
            new ReadOnlyDictionary<string, string>(contentIds),
            identities.Count);
    }

    private static Dictionary<string, TValue> CopyUnique<TValue>(
        Dictionary<string, TValue> source,
        string label)
        where TValue : class
    {
        var copy = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawKey, TValue? value) in source)
        {
            string key = rawKey.Trim();
            if (key.Length == 0 || value is null || !copy.TryAdd(key, value))
            {
                throw new InvalidDataException($"Character manifest has an empty or duplicate {label} key.");
            }
        }

        return copy;
    }

    private static string ValidateScenePath(string identityId, string value)
    {
        string scene = value.Trim();
        string[] parts = scene.Split('/');
        if (scene.Length == 0
            || scene.Contains('\\')
            || scene.StartsWith('/')
            || scene.Contains("://", StringComparison.Ordinal)
            || !scene.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            || parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException(
                $"Identity '{identityId}' has invalid manifest-relative scene '{value}'.");
        }

        return scene;
    }

    private static IReadOnlyList<string> ValidateClips(
        string identityId,
        string label,
        List<string>? source)
    {
        if (source is null)
        {
            throw new InvalidDataException($"Identity '{identityId}' has no {label} array.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<string>(source.Count);
        foreach (string rawClip in source)
        {
            string clip = (rawClip ?? string.Empty).Trim();
            if (clip.Length == 0 || !seen.Add(clip))
            {
                throw new InvalidDataException(
                    $"Identity '{identityId}' has an empty or duplicate clip in {label}.");
            }

            clips.Add(clip);
        }

        return clips.AsReadOnly();
    }

    private static void ValidateCount(string label, int expected, int actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"Character manifest count {label} is {expected}; parsed {actual}.");
        }
    }

    private sealed class ManifestDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("manifest_type")] public string ManifestType { get; set; } = string.Empty;
        [JsonPropertyName("counts")] public ManifestCounts? Counts { get; set; }
        [JsonPropertyName("identities")] public Dictionary<string, IdentityEntry>? Identities { get; set; }
        [JsonPropertyName("characters")] public Dictionary<string, CharacterEntry>? Characters { get; set; }
        [JsonPropertyName("content_ids")] public Dictionary<string, string>? ContentIds { get; set; }
    }

    private sealed class ManifestCounts
    {
        [JsonPropertyName("mob_keys")] public int MobKeys { get; set; }
        [JsonPropertyName("player_keys")] public int PlayerKeys { get; set; }
        [JsonPropertyName("chargen_keys")] public int ChargenKeys { get; set; }
        [JsonPropertyName("identity_scenes")] public int IdentityScenes { get; set; }
    }

    private sealed class IdentityEntry
    {
        [JsonPropertyName("scene")] public string Scene { get; set; } = string.Empty;
        [JsonPropertyName("clips")] public List<string>? Clips { get; set; }
        [JsonPropertyName("combat_event_clips")] public List<string>? CombatEventClips { get; set; }
    }

    private sealed class CharacterEntry
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("content_id")] public string ContentId { get; set; } = string.Empty;
        [JsonPropertyName("identity_id")] public string IdentityId { get; set; } = string.Empty;
    }
}
