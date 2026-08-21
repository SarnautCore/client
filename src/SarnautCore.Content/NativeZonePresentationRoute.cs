namespace SarnautCore.Content;

/// <summary>
/// Confines one zone-presentation manifest and its scene to a canonical map and zone directory.
/// </summary>
public sealed class NativeZonePresentationRoute
{
    public const string ManifestFileName = "zone-presentation.json";

    private NativeZonePresentationRoute(
        string nativeRoot,
        string mapId,
        string zoneId)
    {
        NativeRoot = nativeRoot;
        MapId = mapId;
        ZoneId = zoneId;
        DirectoryPath = $"{nativeRoot}/maps/{mapId}/zones/{zoneId}";
        ManifestPath = $"{DirectoryPath}/{ManifestFileName}";
    }

    public string NativeRoot { get; }
    public string MapId { get; }
    public string ZoneId { get; }
    public string DirectoryPath { get; }
    public string ManifestPath { get; }

    public static bool TryCreate(
        string? nativeRoot,
        string? mapId,
        string? zoneId,
        out NativeZonePresentationRoute route,
        out string error)
    {
        route = null!;
        if (!NativeAssetReference.TryValidateRoot(nativeRoot, out string root, out error))
        {
            return false;
        }

        if (!IsCanonicalContentId(mapId))
        {
            error = $"The map id '{mapId}' is not lowercase kebab-case.";
            return false;
        }

        if (!IsCanonicalContentId(zoneId))
        {
            error = $"The zone id '{zoneId}' is not lowercase kebab-case.";
            return false;
        }

        route = new NativeZonePresentationRoute(root, mapId!, zoneId!);
        error = string.Empty;
        return true;
    }

    public bool TryResolveScenePath(
        string? relativeScene,
        out string scenePath,
        out string error)
    {
        scenePath = string.Empty;

        string selected;
        try
        {
            selected = NativeSceneReference.Select(relativeScene, runtimeScene: null);
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return false;
        }

        string candidate = $"{DirectoryPath}/{selected}";
        if (!NativeAssetReference.TryCreate(NativeRoot, candidate, out _, out error))
        {
            return false;
        }

        string directoryPrefix = DirectoryPath + "/";
        if (!candidate.StartsWith(directoryPrefix, StringComparison.Ordinal))
        {
            error = $"The presentation scene is outside '{DirectoryPath}'.";
            return false;
        }

        scenePath = candidate;
        error = string.Empty;
        return true;
    }

    private static bool IsCanonicalContentId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value[0] == '-'
            || value[^1] == '-'
            || value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(character =>
            character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-');
    }
}
