using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private static readonly Regex ConvertedSceneDependency = new(
        "ext_resource type=\"PackedScene\" path=\"res://assets/(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConvertedResourceDependency = new(
        "ext_resource type=\"[^\"]+\" path=\"res://assets/(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, PackedScene?> _sceneCache = new(StringComparer.OrdinalIgnoreCase);
    private Node3D _terrainRoot = null!;
    private Node3D _objectsRoot = null!;

    [Export] public string MapName { get; set; } = DefaultMapName;
    [Export(PropertyHint.Dir)] public string ConvertedRoot { get; set; } = DefaultConvertedRoot;
    [Export] public bool AutoLoad { get; set; } = true;
    [Export] public bool CreateTerrainCollision { get; set; } = true;

    public int TerrainTileCount { get; private set; }
    public int TerrainVertexCount { get; private set; }
    public int PlacedObjectCount { get; private set; }
    public int VisualObjectCount { get; private set; }
    public int UnresolvedObjectCount { get; private set; }
    public int ServerObjectCount { get; private set; }
    public int PlacementFileCount { get; private set; }
    public int ServerPlacementFileCount { get; private set; }
    public int SplatMapCount { get; private set; }
    public int LightmapCount { get; private set; }
    public bool UsedFlatTerrainFallback { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public Aabb TerrainBounds { get; private set; }
    public bool HasTerrainBounds { get; private set; }

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
        AddChild(_terrainRoot);
        AddChild(_objectsRoot);

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
            MapPlacementDocument? document = ReadPlacementDocument(serverPath);
            ServerObjectCount += document?.Objects?.Length ?? 0;
        }

        GD.Print(
            $"ZoneLoader: {MapName} | terrain={TerrainTileCount} | placements={PlacedObjectCount} " +
            $"| visual={VisualObjectCount} | unresolved={UnresolvedObjectCount} | server={ServerObjectCount}");
        return TerrainTileCount > 0 && PlacedObjectCount > 0;
    }

    private void ResetZone()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        _sceneCache.Clear();
        TerrainTileCount = 0;
        TerrainVertexCount = 0;
        PlacedObjectCount = 0;
        VisualObjectCount = 0;
        UnresolvedObjectCount = 0;
        ServerObjectCount = 0;
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
        PackedScene? scene = LoadRelocatedScene(scenePath);
        if (scene?.Instantiate() is Node3D instance)
        {
            return instance;
        }

        return InstantiateGeometryFallback(visualSource);
    }

    private PackedScene? LoadRelocatedScene(string scenePath)
    {
        if (_sceneCache.TryGetValue(scenePath, out PackedScene? cached))
        {
            return cached;
        }

        _sceneCache[scenePath] = null;
        string source = FileAccess.GetFileAsString(scenePath);
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        string assetsRoot = $"{ConvertedRoot.TrimEnd('/')}/assets/";
        foreach (Match match in ConvertedResourceDependency.Matches(source))
        {
            string dependencyPath = assetsRoot + match.Groups["path"].Value;
            if (!FileAccess.FileExists(dependencyPath))
            {
                GD.PushWarning($"ZoneLoader skipped {scenePath}: missing converted dependency {dependencyPath}");
                return null;
            }
        }

        foreach (Match match in ConvertedSceneDependency.Matches(source))
        {
            string childPath = assetsRoot + match.Groups["path"].Value;
            PackedScene? child = LoadRelocatedScene(childPath);
            if (child == null)
            {
                return null;
            }

            child?.TakeOverPath(childPath);
        }

        string relocated = source.Replace("res://assets/", assetsRoot, StringComparison.Ordinal);
        string cacheDirectory = "user://zone_walkabout_scene_cache";
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(cacheDirectory));
        if (directoryError != Error.Ok && directoryError != Error.AlreadyExists)
        {
            GD.PushWarning($"ZoneLoader could not create its scene cache: {directoryError}");
            return null;
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scenePath))).ToLowerInvariant();
        string cachePath = $"{cacheDirectory}/{hash}.tscn";
        using (FileAccess? file = FileAccess.Open(cachePath, FileAccess.ModeFlags.Write))
        {
            if (file == null)
            {
                return null;
            }

            file.StoreString(relocated);
        }

        PackedScene? scene = ResourceLoader.Load<PackedScene>(cachePath, string.Empty, ResourceLoader.CacheMode.Replace);
        scene?.TakeOverPath(scenePath);
        _sceneCache[scenePath] = scene;
        return scene;
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
        if (!FileAccess.FileExists(meshPath))
        {
            return null;
        }

        Mesh? mesh = ResourceLoader.Load<Mesh>(meshPath);
        return mesh == null ? null : new MeshInstance3D { Name = "ConvertedMesh", Mesh = mesh };
    }

    private AllodsResource? LoadAllodsResource(string sourcePath)
    {
        string path = $"{ConvertedRoot.TrimEnd('/')}/resources/{sourcePath}.tres";
        return ResourceLoader.Load<AllodsResource>(path);
    }

    private static string ReadHref(string xml, string elementName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            XElement? element = XDocument.Parse(xml).Descendants()
                .FirstOrDefault(candidate => candidate.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
            return element?.Attribute("href")?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

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

    private static string NormalizeHref(string ownerSource, string href)
    {
        string path = href.Split('#', 2)[0].Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        bool absolute = path.StartsWith('/');
        path = path.TrimStart('/');
        if (!absolute && !string.IsNullOrEmpty(ownerSource))
        {
            int slash = ownerSource.LastIndexOf('/');
            path = slash < 0 ? path : $"{ownerSource[..slash]}/{path}";
        }

        var segments = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static string StripXdbSuffix(string path)
    {
        return path.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

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
        [JsonPropertyName("position")] public float[]? Position { get; set; }
        [JsonPropertyName("rotation_yaw_pitch_roll")] public float[]? RotationYawPitchRoll { get; set; }
        [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
        [JsonPropertyName("template_href")] public string TemplateHref { get; set; } = string.Empty;
        [JsonPropertyName("ai_collision")] public bool AiCollision { get; set; } = true;
    }
}
