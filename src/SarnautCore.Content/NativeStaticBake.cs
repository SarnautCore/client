using System.Collections.ObjectModel;
using System.Text.Json;

namespace SarnautCore.Content;

public sealed record NativeStaticCellKey(long SectorX, long SectorY, long TileX, long TileY)
{
    public override string ToString() => $"{SectorX:D3}_{SectorY:D3}/{TileX}_{TileY}";
}

public sealed record NativeStaticPlacement(
    string Name,
    string? ScenePath,
    float PositionX,
    float PositionY,
    float PositionZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    float Scale,
    bool Collision,
    bool Visual,
    string Classification,
    string? NonVisualReason);

public sealed record NativeStaticCell(
    NativeStaticCellKey Key,
    string ManifestPath,
    IReadOnlyList<NativeStaticPlacement> Placements);

/// <summary>A complete map-owned native static inventory.</summary>
public sealed class NativeStaticBake
{
    public const int CurrentSchemaVersion = 2;
    public const string ExpectedBakeFormat = "sarnaut-native-statics-v2";
    public const string ExpectedCellFormat = "sarnaut-native-statics-v1";
    public const string ExpectedFrameId = "godot-world-v1";
    public const string ExpectedCoordinateScope = "world";
    public const string ExpectedCellPolicy = "nonempty_placements_only";

    private NativeStaticBake(
        string map,
        string zone,
        IReadOnlyList<NativeStaticCell> cells,
        int placementCount,
        int visualCount,
        int nonVisualCount)
    {
        Map = map;
        Zone = zone;
        Cells = cells;
        PlacementCount = placementCount;
        VisualCount = visualCount;
        NonVisualCount = nonVisualCount;
    }

    public string Map { get; }
    public string Zone { get; }
    public IReadOnlyList<NativeStaticCell> Cells { get; }
    public int PlacementCount { get; }
    public int VisualCount { get; }
    public int NonVisualCount { get; }

    /// <summary>
    /// Parses the aggregate and every declared cell before the runtime loads a scene.
    /// The reader receives a path relative to the statics directory and must return
    /// null when that file does not exist.
    /// </summary>
    public static NativeStaticBake Parse(
        string aggregateJson,
        string expectedMap,
        Func<string, string?> readCellManifest)
    {
        ArgumentNullException.ThrowIfNull(readCellManifest);
        if (string.IsNullOrWhiteSpace(aggregateJson))
        {
            throw new InvalidDataException("Native static bake manifest is empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(aggregateJson);
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                "native static bake manifest",
                ["format", "schema_version", "map", "zone", "frame", "cell_policy", "report", "cells"]);

            string format = RequiredString(root, "format", "native static bake manifest");
            int schemaVersion = RequiredInt(root, "schema_version", "native static bake manifest");
            string map = RequiredString(root, "map", "native static bake manifest");
            string zone = RequiredString(root, "zone", "native static bake manifest");
            string cellPolicy = RequiredString(root, "cell_policy", "native static bake manifest");
            if (format != ExpectedBakeFormat
                || schemaVersion != CurrentSchemaVersion
                || map != expectedMap
                || cellPolicy != ExpectedCellPolicy)
            {
                throw new InvalidDataException(
                    $"Native static bake contract mismatch: format='{format}', schema={schemaVersion}, map='{map}', policy='{cellPolicy}'.");
            }

            ValidateBakeFrame(root.GetProperty("frame"));
            StaticReport aggregateReport = ReadReport(
                root.GetProperty("report"),
                "native static bake report",
                includeCells: true);
            RequireResolvedReport(aggregateReport, "Native static bake");

            JsonElement cellRows = root.GetProperty("cells");
            if (cellRows.ValueKind != JsonValueKind.Array
                || cellRows.GetArrayLength() != aggregateReport.Cells)
            {
                throw new InvalidDataException(
                    $"Native static bake declares {aggregateReport.Cells} cells but carries "
                    + $"{ArrayLength(cellRows, "cells")}.");
            }

            var cells = new List<NativeStaticCell>(aggregateReport.Cells);
            var seenCells = new HashSet<NativeStaticCellKey>();
            int placementCount = 0;
            int visualCount = 0;
            int nonVisualCount = 0;
            int cellIndex = 0;
            foreach (JsonElement cellRow in cellRows.EnumerateArray())
            {
                RequireProperties(
                    cellRow,
                    $"native static bake cell {cellIndex}",
                    ["order", "cell", "placements", "report"],
                    ["authored_lights"]);
                int order = RequiredInt(cellRow, "order", $"native static bake cell {cellIndex}");
                if (order != cellIndex)
                {
                    throw new InvalidDataException(
                        $"Native static bake cell order is not contiguous at index {cellIndex}.");
                }

                if (cellRow.TryGetProperty("authored_lights", out JsonElement authoredLights)
                    && authoredLights.ValueKind != JsonValueKind.Null
                    && !string.IsNullOrWhiteSpace(authoredLights.GetString()))
                {
                    throw new InvalidDataException(
                        $"Native static bake cell {cellIndex} still references an authored-light companion.");
                }

                NativeStaticCellKey declaredKey = ReadCellKey(
                    cellRow.GetProperty("cell"),
                    $"native static bake cell {cellIndex}");
                if (!seenCells.Add(declaredKey))
                {
                    throw new InvalidDataException($"Native static bake duplicates cell {declaredKey}.");
                }

                string manifestPath = NormalizeRelativePath(
                    string.Empty,
                    RequiredString(cellRow, "placements", $"native static bake cell {cellIndex}"),
                    ".json");
                string manifestJson = readCellManifest(manifestPath)
                    ?? throw new InvalidDataException(
                        $"Native static cell manifest is missing: {manifestPath}");
                StaticReport declaredReport = ReadReport(
                    cellRow.GetProperty("report"),
                    $"native static bake cell {cellIndex} report",
                    includeCells: false);
                RequireResolvedReport(declaredReport, $"Native static bake cell {cellIndex}");
                if (declaredReport.Placements == 0)
                {
                    throw new InvalidDataException(
                        $"Native static bake cell {cellIndex} is empty under the nonempty cell policy.");
                }

                NativeStaticCell parsedCell = ParseCell(
                    manifestJson,
                    manifestPath,
                    expectedMap,
                    zone,
                    declaredKey,
                    declaredReport);
                cells.Add(parsedCell);
                placementCount += parsedCell.Placements.Count;
                visualCount += parsedCell.Placements.Count(placement => placement.Visual);
                nonVisualCount += parsedCell.Placements.Count(placement => !placement.Visual);
                cellIndex++;
            }

            if (placementCount != aggregateReport.Placements
                || visualCount != aggregateReport.Visual
                || nonVisualCount != aggregateReport.NonVisual
                || aggregateReport.Visual + aggregateReport.NonVisual != aggregateReport.Placements)
            {
                throw new InvalidDataException(
                    "Native static bake report does not match its cell manifests.");
            }

            return new NativeStaticBake(
                map,
                zone,
                new ReadOnlyCollection<NativeStaticCell>(cells),
                placementCount,
                visualCount,
                nonVisualCount);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Native static bake contains invalid JSON: {exception.Message}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Native static bake contains an invalid value: {exception.Message}",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Native static bake contains an invalid value: {exception.Message}",
                exception);
        }
    }

    private static NativeStaticCell ParseCell(
        string json,
        string manifestPath,
        string expectedMap,
        string expectedZone,
        NativeStaticCellKey expectedKey,
        StaticReport expectedReport)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        RequireProperties(
            root,
            $"native static cell manifest '{manifestPath}'",
            ["format", "map", "zone", "cell", "frame", "placements"]);

        string format = RequiredString(root, "format", manifestPath);
        string map = RequiredString(root, "map", manifestPath);
        string zone = RequiredString(root, "zone", manifestPath);
        NativeStaticCellKey key = ReadCellKey(root.GetProperty("cell"), manifestPath);
        if (format != ExpectedCellFormat
            || map != expectedMap
            || zone != expectedZone
            || key != expectedKey)
        {
            throw new InvalidDataException(
                $"Native static cell contract mismatch in '{manifestPath}'.");
        }

        ValidateCellFrame(root.GetProperty("frame"), manifestPath);
        JsonElement rows = root.GetProperty("placements");
        if (rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() != expectedReport.Placements)
        {
            throw new InvalidDataException(
                $"Native static cell '{manifestPath}' declares {expectedReport.Placements} placements but carries "
                + $"{ArrayLength(rows, "placements")}.");
        }

        var placements = new List<NativeStaticPlacement>(expectedReport.Placements);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int placementIndex = 0;
        foreach (JsonElement row in rows.EnumerateArray())
        {
            RequireProperties(
                row,
                $"native static placement {placementIndex} in '{manifestPath}'",
                ["order", "name", "classification", "position", "rotation", "scale", "collision", "visual"],
                ["scene", "runtime_scene", "nonvisual_reason"]);
            int order = RequiredInt(row, "order", manifestPath);
            string name = RequiredString(row, "name", manifestPath);
            if (order != placementIndex || !names.Add(name))
            {
                throw new InvalidDataException(
                    $"Native static placement order or name is invalid at index {placementIndex} in '{manifestPath}'.");
            }

            float[] position = ReadFiniteVector(row.GetProperty("position"), 3, "position", name);
            float[] rotation = ReadFiniteVector(row.GetProperty("rotation"), 4, "rotation", name);
            float rotationLengthSquared = rotation.Sum(component => component * component);
            if (MathF.Abs(rotationLengthSquared - 1.0f) > 0.0002f)
            {
                throw new InvalidDataException(
                    $"Native static placement '{name}' rotation is not a unit quaternion.");
            }

            float scale = row.GetProperty("scale").GetSingle();
            if (!float.IsFinite(scale) || scale <= 0.0f)
            {
                throw new InvalidDataException(
                    $"Native static placement '{name}' scale must be finite and positive.");
            }

            bool visual = row.GetProperty("visual").GetBoolean();
            bool collision = row.GetProperty("collision").GetBoolean();
            string classification = RequiredString(row, "classification", manifestPath);
            string? scenePath = null;
            string? nonVisualReason = null;
            if (visual)
            {
                if (classification != "visual"
                    || row.TryGetProperty("nonvisual_reason", out _))
                {
                    throw new InvalidDataException(
                        $"Native visual placement '{name}' has an invalid classification or scene contract.");
                }

                scenePath = SelectScenePath(row, manifestPath, name);
            }
            else
            {
                if (!row.TryGetProperty("nonvisual_reason", out JsonElement reason))
                {
                    throw new InvalidDataException(
                        $"Native nonvisual placement '{name}' has no nonvisual reason.");
                }

                nonVisualReason = reason.GetString()?.Trim();
                if (nonVisualReason is not "collision_only" and not "invisible_portal"
                    || classification != nonVisualReason)
                {
                    throw new InvalidDataException(
                        $"Native nonvisual placement '{name}' has an invalid classification.");
                }

                bool hasScene = row.TryGetProperty("scene", out _);
                bool hasRuntimeScene = row.TryGetProperty("runtime_scene", out _);
                bool hasSceneReference = hasScene || hasRuntimeScene;
                if (collision != hasSceneReference || nonVisualReason == "collision_only" && !collision)
                {
                    throw new InvalidDataException(
                        $"Native nonvisual placement '{name}' must carry a native collision scene exactly when collision is true.");
                }

                if (hasSceneReference)
                {
                    scenePath = SelectScenePath(row, manifestPath, name);
                }
            }

            placements.Add(new NativeStaticPlacement(
                name,
                scenePath,
                position[0],
                position[1],
                position[2],
                rotation[0],
                rotation[1],
                rotation[2],
                rotation[3],
                scale,
                collision,
                visual,
                classification,
                nonVisualReason));
            placementIndex++;
        }

        int visualCount = placements.Count(placement => placement.Visual);
        int nonVisualCount = placements.Count - visualCount;
        if (visualCount != expectedReport.Visual
            || nonVisualCount != expectedReport.NonVisual
            || visualCount + nonVisualCount != expectedReport.Placements)
        {
            throw new InvalidDataException(
                $"Native static cell report does not match '{manifestPath}'.");
        }

        return new NativeStaticCell(
            key,
            manifestPath,
            new ReadOnlyCollection<NativeStaticPlacement>(placements));
    }

    private static void ValidateBakeFrame(JsonElement frame)
    {
        RequireProperties(frame, "native static bake frame", ["id", "coordinate_scope", "origin_applied"]);
        string id = RequiredString(frame, "id", "native static bake frame");
        string scope = RequiredString(frame, "coordinate_scope", "native static bake frame");
        bool originApplied = frame.GetProperty("origin_applied").GetBoolean();
        if (id != ExpectedFrameId || scope != ExpectedCoordinateScope || !originApplied)
        {
            throw new InvalidDataException(
                $"Native static bake frame mismatch: id='{id}', scope='{scope}', origin_applied={originApplied}.");
        }
    }

    private static void ValidateCellFrame(JsonElement frame, string manifestPath)
    {
        RequireProperties(frame, $"native static cell frame in '{manifestPath}'", ["id", "origin_applied"]);
        string id = RequiredString(frame, "id", manifestPath);
        bool originApplied = frame.GetProperty("origin_applied").GetBoolean();
        if (id != ExpectedFrameId || !originApplied)
        {
            throw new InvalidDataException(
                $"Native static cell frame mismatch in '{manifestPath}'.");
        }
    }

    private static NativeStaticCellKey ReadCellKey(JsonElement cell, string label)
    {
        RequireProperties(cell, $"native static cell coordinates in '{label}'", ["sector", "tile"]);
        long[] sector = ReadIntegerVector(cell.GetProperty("sector"), "sector", label);
        long[] tile = ReadIntegerVector(cell.GetProperty("tile"), "tile", label);
        return new NativeStaticCellKey(sector[0], sector[1], tile[0], tile[1]);
    }

    private static StaticReport ReadReport(JsonElement report, string label, bool includeCells)
    {
        string[] required = includeCells
            ? ["cells", "placements", "visual", "non_visual", "unresolved", "point_lights", "anti_lights"]
            : ["placements", "visual", "non_visual", "unresolved", "point_lights", "anti_lights"];
        RequireProperties(report, label, required);
        return new StaticReport(
            includeCells ? RequiredInt(report, "cells", label) : 0,
            RequiredInt(report, "placements", label),
            RequiredInt(report, "visual", label),
            RequiredInt(report, "non_visual", label),
            RequiredInt(report, "unresolved", label),
            RequiredInt(report, "point_lights", label),
            RequiredInt(report, "anti_lights", label));
    }

    private static void RequireResolvedReport(StaticReport report, string label)
    {
        if (report.Cells < 0
            || report.Placements < 0
            || report.Visual < 0
            || report.NonVisual < 0
            || report.Unresolved != 0
            || report.PointLights != 0
            || report.AntiLights != 0
            || report.Visual + report.NonVisual != report.Placements)
        {
            throw new InvalidDataException($"{label} report is partial or unresolved.");
        }
    }

    private static float[] ReadFiniteVector(JsonElement value, int length, string name, string owner)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
        {
            throw new InvalidDataException(
                $"Native static placement '{owner}' {name} must contain {length} numbers.");
        }

        float[] values = value.EnumerateArray().Select(component => component.GetSingle()).ToArray();
        if (!values.All(float.IsFinite))
        {
            throw new InvalidDataException(
                $"Native static placement '{owner}' {name} contains a non-finite number.");
        }

        return values;
    }

    private static long[] ReadIntegerVector(JsonElement value, string name, string owner)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"Native static {name} in '{owner}' must contain two integers.");
        }

        return value.EnumerateArray().Select(component => component.GetInt64()).ToArray();
    }

    private static string SelectScenePath(
        JsonElement placement,
        string manifestPath,
        string placementName)
    {
        string? normalizedScene = null;
        if (placement.TryGetProperty("scene", out JsonElement sceneElement))
        {
            string scene = NativeSceneReference.Select(
                RequiredStringValue(sceneElement, "scene", placementName),
                null,
                allowParentSegments: true);
            normalizedScene = NormalizeRelativePath(
                manifestPath,
                scene,
                NativeSceneReference.Extension(scene));
        }

        string? normalizedRuntimeScene = null;
        if (placement.TryGetProperty("runtime_scene", out JsonElement runtimeSceneElement))
        {
            string runtimeScene = NativeSceneReference.Select(
                null,
                RequiredStringValue(runtimeSceneElement, "runtime_scene", placementName),
                allowParentSegments: true);
            normalizedRuntimeScene = NormalizeRelativePath(
                manifestPath,
                runtimeScene,
                NativeSceneReference.Extension(runtimeScene));
        }

        return normalizedRuntimeScene
            ?? normalizedScene
            ?? throw new InvalidDataException(
                $"Native static placement '{placementName}' has no scene or runtime_scene.");
    }

    private static string NormalizeRelativePath(string ownerPath, string relativePath, string extension)
    {
        string relative = relativePath.Replace('\\', '/').Trim();
        if (relative.Length == 0
            || relative.StartsWith('/')
            || relative.Contains("://", StringComparison.Ordinal)
            || !relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Native static path '{relativePath}' is not a relative '{extension}' path.");
        }

        var parts = new List<string>();
        int slash = ownerPath.LastIndexOf('/');
        if (slash >= 0)
        {
            parts.AddRange(ownerPath[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Native static path '{relativePath}' escapes the statics directory.");
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (part.IndexOfAny([':', '*', '?']) >= 0)
            {
                throw new InvalidDataException(
                    $"Native static path '{relativePath}' contains an unsafe segment.");
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException($"Native static path '{relativePath}' is empty after normalization.");
        }

        return string.Join('/', parts);
    }

    private static string RequiredString(JsonElement owner, string name, string label)
    {
        string value = owner.GetProperty(name).GetString()?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new InvalidDataException($"{label} property '{name}' is empty.");
        }

        return value;
    }

    private static string RequiredStringValue(JsonElement value, string name, string label)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{label} property '{name}' must be a string.");
        }

        string result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length == 0)
        {
            throw new InvalidDataException($"{label} property '{name}' is empty.");
        }

        return result;
    }

    private static int RequiredInt(JsonElement owner, string name, string label)
    {
        int value = owner.GetProperty(name).GetInt32();
        if (value < 0)
        {
            throw new InvalidDataException($"{label} property '{name}' is negative.");
        }

        return value;
    }

    private static int ArrayLength(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Native static property '{name}' must be an array.");
        }

        return value.GetArrayLength();
    }

    private static void RequireProperties(
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (optional != null)
        {
            allowed.UnionWith(optional);
        }

        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"{label} contains unsupported or duplicate property '{property.Name}'.");
            }

            missing.Remove(property.Name);
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"{label} is missing property '{missing.Order(StringComparer.Ordinal).First()}'.");
        }
    }

    private sealed record StaticReport(
        int Cells,
        int Placements,
        int Visual,
        int NonVisual,
        int Unresolved,
        int PointLights,
        int AntiLights);
}
