using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Godot;

namespace SarnautCore;

public partial class ZoneLoader : Node3D
{
    public const string DefaultMapName = "Inst_LeagueStart";

    private const string DefaultConvertedRoot = "res://converted/assets/classic-1.1";
    private const string TerrainSuffix = ".terrain.obj";
    private const string MapRegionSuffix = "_MapRegion.xdb.placements.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly HashSet<string> _npcModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private AllodsResourceTree _tree = new(DefaultConvertedRoot);
    private Node3D _terrainRoot = null!;
    private Node3D _objectsRoot = null!;
    private Node3D _charactersRoot = null!;

    [Export] public string MapName { get; set; } = DefaultMapName;
    [Export(PropertyHint.Dir)] public string ConvertedRoot { get; set; } = DefaultConvertedRoot;
    [Export] public bool AutoLoad { get; set; } = true;
    [Export] public bool CreateTerrainCollision { get; set; } = true;

    /// <summary>
    /// Whether the map's authored mob placements are drawn as NPCs.
    /// </summary>
    /// <remarks>
    /// Off, because the shard is authoritative over what exists: a placement is
    /// where a mob may be spawned, not proof that one is there, and drawing both
    /// the placement and the replicated entity put two identical creatures on the
    /// same patch of ground with only one of them alive. The offline walkabout
    /// turns it back on, because there is no shard to ask.
    /// </remarks>
    [Export] public bool SpawnNpcVisuals { get; set; }

    public int TerrainTileCount { get; private set; }
    public int TerrainVertexCount { get; private set; }
    public int PlacedObjectCount { get; private set; }
    public int VisualObjectCount { get; private set; }
    public int UnresolvedObjectCount { get; private set; }
    public int ServerObjectCount { get; private set; }
    public int NpcPlacementCount { get; private set; }
    public int NpcVisualCount { get; private set; }
    public int NpcPlaceholderCount { get; private set; }
    public int PlacementFileCount { get; private set; }
    public int ServerPlacementFileCount { get; private set; }
    public int SplatMapCount { get; private set; }
    public int LightmapCount { get; private set; }
    public bool UsedFlatTerrainFallback { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public Aabb TerrainBounds { get; private set; }
    public bool HasTerrainBounds { get; private set; }
    public IReadOnlyCollection<string> NpcModelFailures => _npcModelFailures;

    public Vector3 SuggestedSpawnPosition => HasTerrainBounds
        ? new Vector3(TerrainBounds.GetCenter().X, TerrainBounds.End.Y + 5.0f, TerrainBounds.GetCenter().Z)
        : new Vector3(0, 5, 0);

    public override void _Ready()
    {
        if (AutoLoad)
        {
            LoadZone(MapName);
        }
    }

    public bool LoadZone(string mapName)
    {
        ResetZone();
        _tree = new AllodsResourceTree(ConvertedRoot);
        MapName = mapName.Trim();
        if (!IsSafeMapName(MapName))
        {
            return Fail($"Invalid map name '{MapName}'. Use a directory name below the converted Maps folder.");
        }

        string mapRoot = $"{ConvertedRoot.TrimEnd('/')}/assets/Maps/{MapName}";
        if (DirAccess.Open(mapRoot) == null)
        {
            return Fail($"Converted map not found: {mapRoot}");
        }

        _terrainRoot = new Node3D { Name = "Terrain" };
        _objectsRoot = new Node3D { Name = "StaticObjects" };
        _charactersRoot = new Node3D { Name = "NpcCharacters" };
        AddChild(_terrainRoot);
        AddChild(_objectsRoot);
        AddChild(_charactersRoot);

        var files = EnumerateFiles(mapRoot);
        var terrainFiles = files.Where(path => path.EndsWith(TerrainSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var placementFiles = files.Where(path => path.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var serverFiles = files.Where(path => path.EndsWith("_ServerObjects.xdb.placements.json", StringComparison.OrdinalIgnoreCase)).ToArray();

        PlacementFileCount = placementFiles.Length;
        ServerPlacementFileCount = serverFiles.Length;
        SplatMapCount = files.Count(path => System.IO.Path.GetFileName(path).Contains("_SplatMap_", StringComparison.OrdinalIgnoreCase));
        LightmapCount = files.Count(path => path.EndsWith("_lightmap.png", StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<string> terrainLayerTextures = LoadTerrainLayerTextures();
        foreach (string terrainPath in terrainFiles)
        {
            AddTerrainTile(terrainPath, terrainLayerTextures);
        }

        if (TerrainTileCount == 0)
        {
            AddFlatTerrainFallback(placementFiles);
        }

        foreach (string placementPath in placementFiles)
        {
            LoadStaticPlacements(placementPath);
        }

        foreach (string serverPath in serverFiles)
        {
            LoadServerPlacements(serverPath);
        }

        GD.Print(
            $"ZoneLoader: {MapName} | terrain={TerrainTileCount} | placements={PlacedObjectCount} " +
            $"| visual={VisualObjectCount} | unresolved={UnresolvedObjectCount} | server={ServerObjectCount} " +
            $"| npc={NpcVisualCount}/{NpcPlacementCount} | npc_placeholders={NpcPlaceholderCount}");
        return TerrainTileCount > 0 && PlacedObjectCount > 0;
    }

    private void ResetZone()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        _npcModelFailures.Clear();
        TerrainTileCount = 0;
        TerrainVertexCount = 0;
        PlacedObjectCount = 0;
        VisualObjectCount = 0;
        UnresolvedObjectCount = 0;
        ServerObjectCount = 0;
        NpcPlacementCount = 0;
        NpcVisualCount = 0;
        NpcPlaceholderCount = 0;
        PlacementFileCount = 0;
        ServerPlacementFileCount = 0;
        SplatMapCount = 0;
        LightmapCount = 0;
        UsedFlatTerrainFallback = false;
        HasTerrainBounds = false;
        TerrainBounds = default;
        LastError = string.Empty;
    }

    private void AddTerrainTile(string terrainPath, IReadOnlyList<string> layerTextures)
    {
        if (!ConvertedSceneLoader.IsLoadable(terrainPath, "Mesh"))
        {
            return;
        }

        Mesh? mesh = ResourceLoader.Load<Mesh>(terrainPath);
        if (mesh == null || mesh.GetSurfaceCount() == 0)
        {
            GD.PushWarning($"ZoneLoader could not load terrain mesh {terrainPath}");
            return;
        }

        var tile = new MeshInstance3D
        {
            Name = SafeNodeName(System.IO.Path.GetFileNameWithoutExtension(terrainPath)),
            Mesh = mesh,
        };

        int dominantLayer = FindDominantTerrainLayer(terrainPath);
        Material? material = CreateTerrainMaterial(dominantLayer, layerTextures);
        if (material != null)
        {
            tile.MaterialOverride = material;
        }

        tile.SetMeta("source_path", terrainPath);
        tile.SetMeta("dominant_layer", dominantLayer);
        _terrainRoot.AddChild(tile);

        Aabb bounds = tile.GetAabb();
        TerrainBounds = HasTerrainBounds ? TerrainBounds.Merge(bounds) : bounds;
        HasTerrainBounds = true;
        TerrainTileCount++;
        TerrainVertexCount += CountMeshVertices(mesh);

        if (CreateTerrainCollision)
        {
            var body = new StaticBody3D { Name = "Collision" };
            body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
            tile.AddChild(body);
        }
    }

    private Material? CreateTerrainMaterial(int dominantLayer, IReadOnlyList<string> layerTextures)
    {
        // TODO: Replace this single dominant-layer material with the converter's layered terrain material.
        if (dominantLayer < 0 || dominantLayer >= layerTextures.Count)
        {
            return new StandardMaterial3D
            {
                AlbedoColor = new Color("566348"),
                Roughness = 0.95f,
            };
        }

        Texture2D? texture = ResourceLoader.Load<Texture2D>(layerTextures[dominantLayer]);
        if (texture == null)
        {
            return null;
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Roughness = 0.95f,
            Uv1Scale = new Vector3(24, 24, 24),
        };
    }

    private IReadOnlyList<string> LoadTerrainLayerTextures()
    {
        string resourcePath = $"{ConvertedRoot.TrimEnd('/')}/resources/Maps/{MapName}/layers.xdb.tres";
        AllodsResource? layers = ResourceLoader.Load<AllodsResource>(resourcePath);
        if (layers == null || string.IsNullOrWhiteSpace(layers.raw_xml))
        {
            return [];
        }

        try
        {
            XDocument document = XDocument.Parse(layers.raw_xml);
            XElement? layerList = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Layers");
            if (layerList == null)
            {
                return [];
            }

            return layerList.Elements()
                .Where(element => element.Name.LocalName == "Item")
                .Select(item => item.Descendants().FirstOrDefault(element => element.Name.LocalName == "DiffuseTexture"))
                .Select(element => element?.Attribute("href")?.Value ?? string.Empty)
                .Select(TextureHrefToAssetPath)
                .ToArray();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ZoneLoader could not parse terrain layers for {MapName}: {exception.Message}");
            return [];
        }
    }

    private string TextureHrefToAssetPath(string href)
    {
        string sourcePath = NormalizeHref(string.Empty, href);
        int classMarker = sourcePath.LastIndexOf(".(Texture).xdb", StringComparison.OrdinalIgnoreCase);
        if (classMarker < 0)
        {
            return string.Empty;
        }

        return $"{ConvertedRoot.TrimEnd('/')}/assets/{sourcePath[..classMarker]}.png";
    }

    private int FindDominantTerrainLayer(string terrainPath)
    {
        string placementPath = terrainPath[..^TerrainSuffix.Length] + MapRegionSuffix;
        MapPlacementDocument? document = ReadPlacementDocument(placementPath);
        if (document?.UsedLayers == null || document.UsedLayers.Length == 0)
        {
            return -1;
        }

        return document.UsedLayers
            .GroupBy(layer => layer)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;
    }

    private void LoadStaticPlacements(string placementPath)
    {
        MapPlacementDocument? document = ReadPlacementDocument(placementPath);
        if (document?.Objects == null)
        {
            return;
        }

        foreach (MapObjectPlacement placement in document.Objects)
        {
            Node3D? resolved = InstantiateStaticObject(placement.TemplateHref);
            bool visual = resolved != null;
            Node3D instance = resolved ?? new Node3D { Name = "UnresolvedStaticObject" };
            instance.Position = ConvertPosition(placement.Position);
            instance.Quaternion = ConvertRotation(placement.RotationYawPitchRoll);
            float scale = placement.Scale <= 0 ? 1.0f : placement.Scale;
            instance.Scale = Vector3.One * scale;
            instance.SetMeta("allods_template", placement.TemplateHref);
            instance.SetMeta("allods_ai_collision", placement.AiCollision);
            _objectsRoot.AddChild(instance);

            PlacedObjectCount++;
            if (visual)
            {
                VisualObjectCount++;
            }
            else
            {
                UnresolvedObjectCount++;
            }
        }
    }

    private void LoadServerPlacements(string placementPath)
    {
        MapPlacementDocument? document = ReadPlacementDocument(placementPath);
        if (document?.Objects == null)
        {
            return;
        }

        ServerObjectCount += document.Objects.Length;
        foreach (MapObjectPlacement placement in document.Objects)
        {
            IReadOnlyList<string> mobSources = FindMobSources(placement);
            bool explicitMob = placement.ObjectType.Contains("MobSingleSpawn", StringComparison.OrdinalIgnoreCase);
            if (mobSources.Count == 0 && !explicitMob)
            {
                continue;
            }

            NpcPlacementCount++;
            if (!SpawnNpcVisuals)
            {
                // Counted, not drawn: the placement count still says what the map
                // authored, and what stands there is the shard's business.
                continue;
            }

            string modelSource = mobSources.FirstOrDefault() ?? placement.Hrefs.FirstOrDefault() ?? placement.ObjectType;
            NpcDefinition? definition = mobSources
                .Select(ResolveNpcDefinition)
                .FirstOrDefault(candidate => candidate != null);

            if (definition == null)
            {
                AddNpcPlaceholder(placement, modelSource);
                _npcModelFailures.Add(modelSource);
                continue;
            }

            var character = new ConvertedCharacter
            {
                Name = $"Npc_{NpcPlacementCount}",
                AutoLoad = false,
                LocomotionOnly = true,
                ConvertedRoot = ConvertedRoot,
                CharacterScene = definition.ScenePath,
                Position = ConvertPosition(placement.Position),
                Quaternion = ConvertServerRotation(placement),
                Scale = Vector3.One * definition.Scale,
            };
            character.SetMeta("allods_mob", definition.MobSource);
            character.SetMeta("allods_visual_mob", definition.VisualMobSource);
            _charactersRoot.AddChild(character);

            if (character.LoadCharacter())
            {
                NpcVisualCount++;
            }
            else
            {
                NpcPlaceholderCount++;
                _npcModelFailures.Add($"{definition.MobSource}: {character.LastError}");
            }
        }
    }

    private IReadOnlyList<string> FindMobSources(MapObjectPlacement placement)
    {
        var mobSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string href in placement.Hrefs)
        {
            string source = NormalizeHref(string.Empty, href);
            if (source.Contains("(MobWorld).xdb", StringComparison.OrdinalIgnoreCase))
            {
                mobSources.Add(source);
                continue;
            }

            if (!source.Contains("(MobSpawnTable).xdb", StringComparison.OrdinalIgnoreCase)
                && !source.Contains("(SpawnTable).xdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AllodsResource? spawnTable = LoadAllodsResource(source);
            if (spawnTable == null)
            {
                continue;
            }

            foreach (string mobHref in ReadHrefs(spawnTable.raw_xml)
                         .Where(candidate => candidate.Contains("(MobWorld).xdb", StringComparison.OrdinalIgnoreCase)))
            {
                mobSources.Add(NormalizeHref(source, mobHref));
            }
        }

        return mobSources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private NpcDefinition? ResolveNpcDefinition(string mobSource)
    {
        AllodsResource? mob = LoadAllodsResource(mobSource);
        string visualMobSource = NormalizeHref(mobSource, ReadHref(mob?.raw_xml ?? string.Empty, "visMob"));
        if (!_tree.TryResolveVisualMob(visualMobSource, out string scenePath, out float visualScale))
        {
            return null;
        }

        return new NpcDefinition(mobSource, visualMobSource, scenePath, visualScale);
    }

    private void AddNpcPlaceholder(MapObjectPlacement placement, string modelSource)
    {
        var placeholder = new MeshInstance3D
        {
            Name = $"NpcPlaceholder_{NpcPlacementCount}",
            Position = ConvertPosition(placement.Position) + Vector3.Up * 0.9f,
            Quaternion = ConvertServerRotation(placement),
            Mesh = new CapsuleMesh { Radius = 0.42f, Height = 1.8f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("d06a55"),
                Roughness = 0.85f,
            },
        };
        placeholder.SetMeta("allods_mob", modelSource);
        _charactersRoot.AddChild(placeholder);
        NpcPlaceholderCount++;
    }

    private Node3D? InstantiateStaticObject(string templateHref)
    {
        string staticSource = NormalizeHref(string.Empty, templateHref);
        if (string.IsNullOrEmpty(staticSource))
        {
            return null;
        }

        AllodsResource? staticResource = LoadAllodsResource(staticSource);
        if (staticResource == null)
        {
            return null;
        }

        string visualHref = ReadHref(staticResource.raw_xml, "ObjectTemplate");
        string visualSource = NormalizeHref(staticSource, visualHref);
        if (string.IsNullOrEmpty(visualSource))
        {
            return null;
        }

        string scenePath = $"{ConvertedRoot.TrimEnd('/')}/assets/{StripXdbSuffix(visualSource)}.scene.tscn";
        PackedScene? scene = ConvertedSceneLoader.Load(ConvertedRoot, scenePath, out _);
        if (scene?.Instantiate() is Node3D instance)
        {
            return instance;
        }

        return InstantiateGeometryFallback(visualSource);
    }

    private Node3D? InstantiateGeometryFallback(string visualSource)
    {
        AllodsResource? visualResource = LoadAllodsResource(visualSource);
        if (visualResource == null)
        {
            return null;
        }

        string geometryHref = ReadHref(visualResource.raw_xml, "geometry");
        string geometrySource = NormalizeHref(visualSource, geometryHref);
        if (string.IsNullOrEmpty(geometrySource))
        {
            return null;
        }

        string meshPath = $"{ConvertedRoot.TrimEnd('/')}/assets/{StripXdbSuffix(geometrySource)}.obj";
        if (!ConvertedSceneLoader.IsLoadable(meshPath, "Mesh"))
        {
            return null;
        }

        Mesh? mesh = ResourceLoader.Load<Mesh>(meshPath);
        return mesh == null ? null : new MeshInstance3D { Name = "ConvertedMesh", Mesh = mesh };
    }

    private AllodsResource? LoadAllodsResource(string sourcePath) => _tree.Load(sourcePath);

    private static string ReadHref(string xml, string elementName) =>
        AllodsResourceTree.ReadHref(xml, elementName);

    private static IReadOnlyList<string> ReadHrefs(string xml) => AllodsResourceTree.ReadHrefs(xml);

    private void AddFlatTerrainFallback(IEnumerable<string> placementFiles)
    {
        var positions = placementFiles
            .Select(ReadPlacementDocument)
            .Where(document => document?.Objects != null)
            .SelectMany(document => document!.Objects!)
            .Select(placement => ConvertPosition(placement.Position))
            .ToArray();

        Vector3 center = positions.Length == 0 ? Vector3.Zero : positions.Aggregate(Vector3.Zero, (sum, value) => sum + value) / positions.Length;
        float size = positions.Length == 0
            ? 256.0f
            : Mathf.Max(64.0f, positions.Max(position => Mathf.Max(Mathf.Abs(position.X - center.X), Mathf.Abs(position.Z - center.Z))) * 2.2f);

        var mesh = new PlaneMesh { Size = new Vector2(size, size) };
        var tile = new MeshInstance3D
        {
            Name = "FlatTerrainFallback",
            Mesh = mesh,
            Position = new Vector3(center.X, positions.Length == 0 ? 0 : positions.Min(position => position.Y), center.Z),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("566348"), Roughness = 1.0f },
        };
        _terrainRoot.AddChild(tile);

        if (CreateTerrainCollision)
        {
            var body = new StaticBody3D { Name = "Collision" };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, 0.2f, size) } });
            tile.AddChild(body);
        }

        TerrainBounds = new Aabb(tile.Position - new Vector3(size * 0.5f, 0.1f, size * 0.5f), new Vector3(size, 0.2f, size));
        HasTerrainBounds = true;
        TerrainTileCount = 1;
        UsedFlatTerrainFallback = true;
    }

    private static MapPlacementDocument? ReadPlacementDocument(string path)
    {
        try
        {
            string json = FileAccess.GetFileAsString(path);
            return JsonSerializer.Deserialize<MapPlacementDocument>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ZoneLoader could not parse {path}: {exception.Message}");
            return null;
        }
    }

    private static List<string> EnumerateFiles(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directoryPath = pending.Pop();
            using DirAccess? directory = DirAccess.Open(directoryPath);
            if (directory == null)
            {
                continue;
            }

            directory.ListDirBegin();
            string name = directory.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (!name.StartsWith('.'))
                {
                    string path = $"{directoryPath}/{name}";
                    if (directory.CurrentIsDir())
                    {
                        pending.Push(path);
                    }
                    else
                    {
                        result.Add(path);
                    }
                }

                name = directory.GetNext();
            }

            directory.ListDirEnd();
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static int CountMeshVertices(Mesh mesh)
    {
        int count = 0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            if (arrays.Count > (int)Mesh.ArrayType.Vertex && arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.PackedVector3Array)
            {
                count += arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
            }
        }

        return count;
    }

    private static Vector3 ConvertPosition(float[]? source)
    {
        return source is { Length: >= 3 } ? new Vector3(source[0], source[2], -source[1]) : Vector3.Zero;
    }

    private static Quaternion ConvertRotation(float[]? source)
    {
        if (source is not { Length: >= 3 })
        {
            return Quaternion.Identity;
        }

        var yaw = new Quaternion(Vector3.Up, source[0]);
        var pitch = new Quaternion(Vector3.Right, source[1]);
        var roll = new Quaternion(new Vector3(0, 0, -1), source[2]);
        return yaw * pitch * roll;
    }

    private static Quaternion ConvertServerRotation(MapObjectPlacement placement)
    {
        if (placement.RotationYawPitchRoll is { Length: >= 3 }
            && placement.RotationYawPitchRoll.Any(value => MathF.Abs(value) > 0.0001f))
        {
            return ConvertRotation(placement.RotationYawPitchRoll);
        }

        string? yawText = placement.Properties
            .FirstOrDefault(property => property.Key.EndsWith(".yaw", StringComparison.OrdinalIgnoreCase))
            .Value;
        return float.TryParse(yawText, NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw)
            ? new Quaternion(Vector3.Up, yaw)
            : Quaternion.Identity;
    }

    private static string NormalizeHref(string ownerSource, string href) =>
        AllodsResourceTree.NormalizeHref(ownerSource, href);

    private static string StripXdbSuffix(string path) => AllodsResourceTree.StripXdbSuffix(path);

    private static bool IsSafeMapName(string mapName)
    {
        return !string.IsNullOrWhiteSpace(mapName)
            && mapName.IndexOfAny(['/', '\\', ':']) < 0
            && mapName != "."
            && mapName != "..";
    }

    private static string SafeNodeName(string name)
    {
        return name.Replace('.', '_').Replace(' ', '_');
    }

    private bool Fail(string message)
    {
        LastError = message;
        GD.PushError($"ZoneLoader: {message}");
        return false;
    }

    private sealed class MapPlacementDocument
    {
        [JsonPropertyName("objects")] public MapObjectPlacement[]? Objects { get; set; }
        [JsonPropertyName("used_layers")] public int[]? UsedLayers { get; set; }
    }

    private sealed class MapObjectPlacement
    {
        [JsonPropertyName("object_type")] public string ObjectType { get; set; } = string.Empty;
        [JsonPropertyName("position")] public float[]? Position { get; set; }
        [JsonPropertyName("rotation_yaw_pitch_roll")] public float[]? RotationYawPitchRoll { get; set; }
        [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
        [JsonPropertyName("template_href")] public string TemplateHref { get; set; } = string.Empty;
        [JsonPropertyName("ai_collision")] public bool AiCollision { get; set; } = true;
        [JsonPropertyName("hrefs")] public string[] Hrefs { get; set; } = [];
        [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; set; } = new();
    }

    private sealed record NpcDefinition(string MobSource, string VisualMobSource, string ScenePath, float Scale);
}
