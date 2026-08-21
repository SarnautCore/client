using System.Collections.ObjectModel;
using System.Text.Json;

namespace SarnautCore.Content;

/// <summary>One baked offline NPC placement in Godot world coordinates.</summary>
public sealed record NativeCharacterPlacement(
    string SpawnId,
    string CharacterKey,
    float PositionX,
    float PositionY,
    float PositionZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW);

/// <summary>A baked world-space transform owned by the map manifest.</summary>
public sealed record NativeCharacterWorldTransform(
    float PositionX,
    float PositionY,
    float PositionZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW);

/// <summary>A complete map-owned offline character inventory.</summary>
public sealed class NativeCharacterPlacements
{
    public const int CurrentSchemaVersion = 2;
    public const string ExpectedManifestType = "sarnaut.character-placements";
    public const string ExpectedFrameId = "godot-world-v1";

    private NativeCharacterPlacements(
        string mapId,
        int cellCount,
        NativeCharacterWorldTransform presentationSpawn,
        IReadOnlyList<NativeCharacterPlacement> placements)
    {
        MapId = mapId;
        CellCount = cellCount;
        PresentationSpawn = presentationSpawn;
        Placements = placements;
    }

    public string MapId { get; }
    public int CellCount { get; }
    public NativeCharacterWorldTransform PresentationSpawn { get; }
    public IReadOnlyList<NativeCharacterPlacement> Placements { get; }

    public static NativeCharacterPlacements Parse(string json, string expectedMapId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Character placement manifest is empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            RequireObjectProperties(
                root,
                "character placement manifest",
                "schema_version",
                "manifest_type",
                "map_id",
                "frame",
                "counts",
                "presentation_spawn",
                "placements");

            int schemaVersion = root.GetProperty("schema_version").GetInt32();
            string manifestType = RequiredString(root, "manifest_type");
            string mapId = RequiredString(root, "map_id");
            if (schemaVersion != CurrentSchemaVersion
                || manifestType != ExpectedManifestType
                || mapId != expectedMapId)
            {
                throw new InvalidDataException(
                    $"Character placement manifest contract mismatch: schema={schemaVersion}, type='{manifestType}', map='{mapId}'.");
            }

            JsonElement frame = root.GetProperty("frame");
            RequireObjectProperties(frame, "character placement frame", "id", "origin_applied");
            string frameId = RequiredString(frame, "id");
            bool originApplied = frame.GetProperty("origin_applied").GetBoolean();
            if (frameId != ExpectedFrameId || !originApplied)
            {
                throw new InvalidDataException(
                    $"Character placement frame mismatch: id='{frameId}', origin_applied={originApplied}.");
            }

            JsonElement counts = root.GetProperty("counts");
            RequireObjectProperties(
                counts,
                "character placement counts",
                "cells",
                "authored_rows",
                "resolved_rows",
                "unresolved_rows");
            int cells = counts.GetProperty("cells").GetInt32();
            int authored = counts.GetProperty("authored_rows").GetInt32();
            int resolved = counts.GetProperty("resolved_rows").GetInt32();
            int unresolved = counts.GetProperty("unresolved_rows").GetInt32();
            if (cells <= 0 || authored < 0 || resolved != authored || unresolved != 0)
            {
                throw new InvalidDataException(
                    $"Character placement manifest is incomplete: cells={cells}, authored={authored}, resolved={resolved}, unresolved={unresolved}.");
            }

            JsonElement rows = root.GetProperty("placements");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != resolved)
            {
                throw new InvalidDataException(
                    $"Character placement manifest declares {resolved} resolved rows but carries {rows.GetArrayLength()}.");
            }

            JsonElement presentation = root.GetProperty("presentation_spawn");
            RequireObjectProperties(presentation, "presentation spawn", "position", "rotation");
            float[] presentationPosition = ReadFiniteVector(
                presentation.GetProperty("position"),
                3,
                "position",
                "presentation_spawn");
            float[] presentationRotation = ReadUnitQuaternion(
                presentation.GetProperty("rotation"),
                "presentation_spawn");
            var presentationSpawn = new NativeCharacterWorldTransform(
                presentationPosition[0],
                presentationPosition[1],
                presentationPosition[2],
                presentationRotation[0],
                presentationRotation[1],
                presentationRotation[2],
                presentationRotation[3]);

            var seenSpawnIds = new HashSet<string>(StringComparer.Ordinal);
            var placements = new List<NativeCharacterPlacement>(resolved);
            foreach (JsonElement row in rows.EnumerateArray())
            {
                RequireObjectProperties(row, "character placement", "spawn_id", "character_key", "position", "rotation");
                string spawnId = RequiredString(row, "spawn_id");
                string characterKey = RequiredString(row, "character_key");
                if (!seenSpawnIds.Add(spawnId))
                {
                    throw new InvalidDataException($"Character placement spawn id '{spawnId}' is duplicated.");
                }

                float[] position = ReadFiniteVector(row.GetProperty("position"), 3, "position", spawnId);
                float[] rotation = ReadUnitQuaternion(row.GetProperty("rotation"), spawnId);

                placements.Add(new NativeCharacterPlacement(
                    spawnId,
                    characterKey,
                    position[0],
                    position[1],
                    position[2],
                    rotation[0],
                    rotation[1],
                    rotation[2],
                    rotation[3]));
            }

            return new NativeCharacterPlacements(
                mapId,
                cells,
                presentationSpawn,
                new ReadOnlyCollection<NativeCharacterPlacement>(placements));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Character placement manifest is invalid JSON: {exception.Message}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Character placement manifest has an invalid value: {exception.Message}",
                exception);
        }
    }

    private static string RequiredString(JsonElement owner, string name)
    {
        string value = owner.GetProperty(name).GetString()?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new InvalidDataException($"Character placement property '{name}' is empty.");
        }

        return value;
    }

    private static float[] ReadFiniteVector(JsonElement source, int length, string name, string spawnId)
    {
        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() != length)
        {
            throw new InvalidDataException(
                $"Character placement '{spawnId}' {name} must contain {length} numbers.");
        }

        float[] values = source.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (!values.All(float.IsFinite))
        {
            throw new InvalidDataException(
                $"Character placement '{spawnId}' {name} contains a non-finite number.");
        }

        return values;
    }

    private static float[] ReadUnitQuaternion(JsonElement source, string owner)
    {
        float[] rotation = ReadFiniteVector(source, 4, "rotation", owner);
        float lengthSquared = rotation.Sum(component => component * component);
        if (MathF.Abs(lengthSquared - 1.0f) > 0.0002f)
        {
            throw new InvalidDataException(
                $"Character placement '{owner}' rotation is not a unit quaternion.");
        }

        return rotation;
    }

    private static void RequireObjectProperties(JsonElement owner, string label, params string[] expected)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }

        var allowed = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!allowed.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"{label} contains unsupported property '{property.Name}'.");
            }
        }

        if (allowed.Count > 0)
        {
            throw new InvalidDataException(
                $"{label} is missing property '{allowed.Order(StringComparer.Ordinal).First()}'.");
        }
    }
}
