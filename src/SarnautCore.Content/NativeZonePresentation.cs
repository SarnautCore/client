using System.Collections.ObjectModel;
using System.Text.Json;

namespace SarnautCore.Content;

/// <summary>A normalized native RGB color used by the baked-light probe.</summary>
public sealed record NativeZoneColor(float Red, float Green, float Blue);

/// <summary>Native colors used to shade dynamic objects from baked visibility samples.</summary>
public sealed record NativeZoneProbeColors(
    NativeZoneColor Ambient,
    NativeZoneColor Direct);

/// <summary>Required node addresses in the native zone-presentation scene.</summary>
public sealed record NativeZonePresentationTopology(
    bool CameraCentered,
    string EnvironmentNode,
    string SunNode,
    string SkyRootNode);

/// <summary>One native child of the camera-centered sky root.</summary>
public sealed record NativeZoneSkyPart(
    string Node,
    float FovFactor,
    bool Animated);

/// <summary>The complete sky inventory declared by the native presentation scene.</summary>
public sealed class NativeZoneSky
{
    internal NativeZoneSky(
        int partCount,
        int animatedPartCount,
        string projectionScaling,
        IReadOnlyList<NativeZoneSkyPart> parts)
    {
        PartCount = partCount;
        AnimatedPartCount = animatedPartCount;
        ProjectionScaling = projectionScaling;
        Parts = parts;
    }

    public int PartCount { get; }
    public int AnimatedPartCount { get; }
    public string ProjectionScaling { get; }
    public IReadOnlyList<NativeZoneSkyPart> Parts { get; }
}

/// <summary>Strict routing and topology contract for one baked zone-presentation scene.</summary>
public sealed class NativeZonePresentation
{
    public const int CurrentSchemaVersion = 1;
    public const string ExpectedManifestType = "sarnaut.zone-presentation";
    public const int ExpectedSkyPartCount = 3;
    public const int ExpectedAnimatedSkyPartCount = 1;
    public const string ExpectedProjectionScaling = "xy";
    public const string ExpectedEnvironmentNode = "Environment";
    public const string ExpectedSunNode = "Sun";
    public const string ExpectedSkyRootNode = "Sky";

    private NativeZonePresentation(
        string mapId,
        string zoneId,
        string scene,
        NativeZonePresentationTopology topology,
        NativeZoneSky sky,
        NativeZoneProbeColors probeColors)
    {
        MapId = mapId;
        ZoneId = zoneId;
        Scene = scene;
        Topology = topology;
        Sky = sky;
        ProbeColors = probeColors;
    }

    public string MapId { get; }
    public string ZoneId { get; }
    public string Scene { get; }
    public NativeZonePresentationTopology Topology { get; }
    public NativeZoneSky Sky { get; }
    public NativeZoneProbeColors ProbeColors { get; }

    public static NativeZonePresentation Parse(
        string json,
        string expectedMapId,
        string expectedZoneId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Zone presentation manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(expectedMapId))
        {
            throw new ArgumentException("Expected map id is empty.", nameof(expectedMapId));
        }

        if (string.IsNullOrWhiteSpace(expectedZoneId))
        {
            throw new ArgumentException("Expected zone id is empty.", nameof(expectedZoneId));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            RequireObjectProperties(
                root,
                "zone presentation manifest",
                [
                    "schema_version",
                    "manifest_type",
                    "map_id",
                    "zone_id",
                    "topology",
                    "sky",
                    "probe_colors",
                ],
                ["scene", "runtime_scene"]);

            int schemaVersion = root.GetProperty("schema_version").GetInt32();
            string manifestType = RequiredString(root, "manifest_type", "zone presentation manifest");
            string mapId = RequiredString(root, "map_id", "zone presentation manifest");
            string zoneId = RequiredString(root, "zone_id", "zone presentation manifest");
            if (schemaVersion != CurrentSchemaVersion
                || manifestType != ExpectedManifestType
                || mapId != expectedMapId
                || zoneId != expectedZoneId)
            {
                throw new InvalidDataException(
                    $"Zone presentation manifest contract mismatch: schema={schemaVersion}, type='{manifestType}', map='{mapId}', zone='{zoneId}'.");
            }

            string? plainScene = OptionalString(root, "scene");
            string? runtimeScene = OptionalString(root, "runtime_scene");
            string scene = NativeSceneReference.Select(plainScene, runtimeScene);

            NativeZonePresentationTopology topology = ReadTopology(root.GetProperty("topology"));
            NativeZoneSky sky = ReadSky(root.GetProperty("sky"));
            NativeZoneProbeColors probeColors = ReadProbeColors(root.GetProperty("probe_colors"));

            return new NativeZonePresentation(mapId, zoneId, scene, topology, sky, probeColors);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Zone presentation manifest is invalid JSON: {exception.Message}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Zone presentation manifest has an invalid value: {exception.Message}",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Zone presentation manifest has an invalid number: {exception.Message}",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"Zone presentation manifest has an out-of-range number: {exception.Message}",
                exception);
        }
    }

    private static NativeZonePresentationTopology ReadTopology(JsonElement source)
    {
        RequireObjectProperties(
            source,
            "zone presentation topology",
            ["camera_centered", "environment_node", "sun_node", "sky_root_node"]);

        bool cameraCentered = source.GetProperty("camera_centered").GetBoolean();
        string environment = RequiredNodePath(source, "environment_node", "zone presentation topology");
        string sun = RequiredNodePath(source, "sun_node", "zone presentation topology");
        string sky = RequiredNodePath(source, "sky_root_node", "zone presentation topology");
        if (!cameraCentered
            || environment != ExpectedEnvironmentNode
            || sun != ExpectedSunNode
            || sky != ExpectedSkyRootNode)
        {
            throw new InvalidDataException(
                $"Zone presentation topology mismatch: camera_centered={cameraCentered}, environment='{environment}', sun='{sun}', sky='{sky}'.");
        }

        return new NativeZonePresentationTopology(cameraCentered, environment, sun, sky);
    }

    private static NativeZoneSky ReadSky(JsonElement source)
    {
        RequireObjectProperties(
            source,
            "zone presentation sky",
            ["part_count", "animated_part_count", "projection_scaling", "parts"]);

        int partCount = source.GetProperty("part_count").GetInt32();
        int animatedPartCount = source.GetProperty("animated_part_count").GetInt32();
        if (partCount < 0 || animatedPartCount < 0 || animatedPartCount > partCount)
        {
            throw new InvalidDataException(
                $"Zone presentation sky counts are invalid: parts={partCount}, animated={animatedPartCount}.");
        }

        string projectionScaling = RequiredString(source, "projection_scaling", "zone presentation sky");
        JsonElement partRows = source.GetProperty("parts");
        if (partRows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Zone presentation sky parts must be an array.");
        }

        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<NativeZoneSkyPart>(partRows.GetArrayLength());
        foreach (JsonElement row in partRows.EnumerateArray())
        {
            RequireObjectProperties(row, "zone presentation sky part", ["node", "fov_factor", "animated"]);
            string node = RequiredNodePath(row, "node", "zone presentation sky part");
            if (!seenNodes.Add(node))
            {
                throw new InvalidDataException($"Zone presentation sky node '{node}' is duplicated.");
            }

            float fovFactor = row.GetProperty("fov_factor").GetSingle();
            if (!float.IsFinite(fovFactor) || fovFactor <= 0.0f)
            {
                throw new InvalidDataException(
                    $"Zone presentation sky node '{node}' has invalid fov_factor {fovFactor}.");
            }

            parts.Add(new NativeZoneSkyPart(
                node,
                fovFactor,
                row.GetProperty("animated").GetBoolean()));
        }

        int actualAnimatedCount = parts.Count(part => part.Animated);
        if (partCount != parts.Count || animatedPartCount != actualAnimatedCount)
        {
            throw new InvalidDataException(
                $"Zone presentation sky inventory mismatch: declared={partCount}/{animatedPartCount}, actual={parts.Count}/{actualAnimatedCount}.");
        }

        if (partCount != ExpectedSkyPartCount
            || animatedPartCount != ExpectedAnimatedSkyPartCount
            || projectionScaling != ExpectedProjectionScaling)
        {
            throw new InvalidDataException(
                $"Zone presentation sky contract mismatch: parts={partCount}, animated={animatedPartCount}, projection='{projectionScaling}'.");
        }

        return new NativeZoneSky(
            partCount,
            animatedPartCount,
            projectionScaling,
            new ReadOnlyCollection<NativeZoneSkyPart>(parts));
    }

    private static NativeZoneProbeColors ReadProbeColors(JsonElement source)
    {
        RequireObjectProperties(source, "zone presentation probe colors", ["ambient", "direct"]);
        return new NativeZoneProbeColors(
            ReadNormalizedColor(source.GetProperty("ambient"), "ambient"),
            ReadNormalizedColor(source.GetProperty("direct"), "direct"));
    }

    private static NativeZoneColor ReadNormalizedColor(JsonElement source, string name)
    {
        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() != 3)
        {
            throw new InvalidDataException(
                $"Zone presentation probe color '{name}' must contain three numbers.");
        }

        double[] channels = source.EnumerateArray().Select(channel => channel.GetDouble()).ToArray();
        if (channels.Any(channel => !double.IsFinite(channel) || channel < 0.0 || channel > 1.0))
        {
            throw new InvalidDataException(
                $"Zone presentation probe color '{name}' must be finite and normalized.");
        }

        return new NativeZoneColor((float)channels[0], (float)channels[1], (float)channels[2]);
    }

    private static string RequiredNodePath(JsonElement owner, string name, string label)
    {
        string path = RequiredString(owner, name, label);
        if (path.Contains('\\') || path.StartsWith('/') || path.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} property '{name}' is not a confined node path.");
        }

        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0
            || segment is "." or ".."
            || segment.IndexOfAny([':', '*', '?', '%']) >= 0))
        {
            throw new InvalidDataException($"{label} property '{name}' has an unsafe node segment.");
        }

        return path;
    }

    private static string RequiredString(JsonElement owner, string name, string label)
    {
        string value = owner.GetProperty(name).GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label} property '{name}' is empty.");
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} property '{name}' has surrounding whitespace.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Zone presentation manifest property '{name}' must be a string.");
        }

        return value.GetString();
    }

    private static void RequireObjectProperties(
        JsonElement owner,
        string label,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }

        var missing = new HashSet<string>(required, StringComparer.Ordinal);
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        if (optional is not null)
        {
            allowed.UnionWith(optional);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"{label} contains unsupported property '{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"{label} contains duplicate property '{property.Name}'.");
            }

            missing.Remove(property.Name);
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"{label} is missing property '{missing.Order(StringComparer.Ordinal).First()}'.");
        }
    }
}
