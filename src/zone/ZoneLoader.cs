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
    private const string NativeTerrainSuffix = ".terrain.tscn";
    private const string LegacyTerrainSuffix = ".terrain.obj";
    private const string MapRegionSuffix = "_MapRegion.xdb.placements.json";
    private const string ServerObjectsSuffix = "_ServerObjects.xdb.placements.json";
    private const string ClientTerrainShaderPath = "res://src/zone/terrain_splat.gdshader";
    private const string TileCoordinateFrameId = "allods-tile-local-v1";
    private const string TileCoordinateScope = "tile-local";
    private const string TerrainManifestMetadata = "allods_coordinate_manifest_json";
    private const string ObjManifestPrefix = "# allods_coordinate_manifest ";
    private const float TerrainTilePitch = 256.0f;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private const string BakedLightSuffix = "_MapRegion.xdb.lightvrt.json";
    private const string AuthoredLightsSuffix = "_MapRegion.xdb.lights.json";
    private readonly HashSet<string> _npcModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staticModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingBakedLight> _pendingBakedLight = [];
    private readonly List<PendingAuthoredLights> _pendingAuthoredLights = [];
    private BakedLightProbe _lightProbe = new();
    private readonly List<ShaderMaterial> _terrainSplatMaterials = [];
    private readonly List<Aabb> _terrainSpawnBounds = [];
    private readonly List<Vector3> _spawnHints = [];
    private readonly List<Vector3> _presentationSpawnHints = [];
    private static Shader? _clientTerrainShader;
    private AllodsResourceTree _tree = new(DefaultConvertedRoot);
    private Node3D? _terrainRoot;
    private Node3D? _objectsRoot;
    private Node3D? _charactersRoot;
    private bool _terrainFatal;
    private int _nativeTerrainTileCount;

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
    public int NonVisualObjectCount { get; private set; }
    public int UnresolvedObjectCount { get; private set; }
    public int ServerObjectCount { get; private set; }
    public int NpcPlacementCount { get; private set; }
    public int NpcVisualCount { get; private set; }
    public int NpcPlaceholderCount { get; private set; }
    public int PlacementFileCount { get; private set; }
    public int ServerPlacementFileCount { get; private set; }
    public int SplatMapCount { get; private set; }
    public int LightmapCount { get; private set; }
    public int BakedLightFileCount { get; private set; }
    public int BakedLitObjectCount { get; private set; }
    public int BakedLitSurfaceCount { get; private set; }
    public int AuthoredLightFileCount { get; private set; }
    public int AuthoredLightCount { get; private set; }
    public int AuthoredAntiLightCount { get; private set; }
    public int LightProbeTileCount => _lightProbe.TileCount;
    public int NativeTerrainTileCount => _nativeTerrainTileCount;
    public bool UsedFlatTerrainFallback { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// True when every renderable placement resolved and no loader error was
    /// recorded. Probes pin this; the zone itself no longer aborts on
    /// unresolved props, only on terrain failure.
    /// </summary>
    public bool IsFullyResolved => UnresolvedObjectCount == 0 && string.IsNullOrWhiteSpace(LastError);
    public Aabb TerrainBounds { get; private set; }
    public bool HasTerrainBounds { get; private set; }
    public IReadOnlyCollection<string> NpcModelFailures => _npcModelFailures;
    public IReadOnlyCollection<string> StaticModelFailures => _staticModelFailures;

    public Vector3 SuggestedSpawnPosition => _presentationSpawnHints.FirstOrDefault(
        ZoneSpawnFrame.Suggest(_terrainSpawnBounds, _spawnHints));

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
        var nativeTerrainFiles = files.Where(path => path.EndsWith(NativeTerrainSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var legacyTerrainFiles = files.Where(path => path.EndsWith(LegacyTerrainSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var placementFiles = files.Where(path => path.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var serverFiles = files.Where(path => path.EndsWith(ServerObjectsSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();

        PlacementFileCount = placementFiles.Length;
        ServerPlacementFileCount = serverFiles.Length;
        SplatMapCount = files.Count(path => System.IO.Path.GetFileName(path).Contains("_SplatMap_", StringComparison.OrdinalIgnoreCase));
        LightmapCount = files.Count(path => path.EndsWith("_lightmap.png", StringComparison.OrdinalIgnoreCase));

        var terrainFailures = new List<string>();
        IReadOnlyList<string>? terrainLayerTextures = null;
        var terrainSources = nativeTerrainFiles
            .Concat(legacyTerrainFiles)
            .GroupBy(TerrainSourceStem, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, string> sources in terrainSources)
        {
            string? nativePath = sources.FirstOrDefault(path =>
                path.EndsWith(NativeTerrainSuffix, StringComparison.OrdinalIgnoreCase));
            string? legacyPath = sources.FirstOrDefault(path =>
                path.EndsWith(LegacyTerrainSuffix, StringComparison.OrdinalIgnoreCase));
            string nativeError = string.Empty;
            bool loaded = nativePath != null
                && TryAddNativeTerrainTile(nativePath, out nativeError);
            string failure = loaded || nativePath == null ? string.Empty : nativeError;

            if (!loaded && legacyPath != null)
            {
                terrainLayerTextures ??= LoadTerrainLayerTextures();
                loaded = TryAddLegacyTerrainTile(legacyPath, terrainLayerTextures, out string legacyError);
                if (!loaded)
                {
                    failure = string.IsNullOrWhiteSpace(failure)
                        ? legacyError
                        : $"{failure}; legacy fallback failed: {legacyError}";
                }
            }

            if (!loaded)
            {
                terrainFailures.Add($"{sources.Key}: {failure}");
            }
        }

        if (terrainFailures.Count > 0)
        {
            // Terrain failure is the one fatal path: a zone without its floor
            // is not walkable, while an unresolved prop is a visual gap.
            _terrainFatal = true;
            Fail($"{terrainFailures.Count} terrain tile(s) could not load. {string.Join(" | ", terrainFailures)}");
        }
        else if (TerrainTileCount == 0)
        {
            AddFlatTerrainFallback(placementFiles);
        }

        if (_nativeTerrainTileCount > 0)
        {
            GD.Print(
                $"ZoneLoader: native terrain | map={MapName} "
                + $"tiles={_nativeTerrainTileCount}/{TerrainTileCount} "
                + $"root={NativeContentSettings.NativeRoot}");
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
            $"| visual={VisualObjectCount} | non_visual={NonVisualObjectCount} " +
            $"| unresolved={UnresolvedObjectCount} | server={ServerObjectCount} " +
            $"| npc={NpcVisualCount}/{NpcPlacementCount} | npc_placeholders={NpcPlaceholderCount}");
        if (UnresolvedObjectCount > 0)
        {
            string firstFailure = _staticModelFailures.FirstOrDefault() ?? "No resource detail was recorded.";
            // Recorded through Fail so an earlier terrain diagnostic is not
            // clobbered: first error wins, later ones append.
            Fail($"{UnresolvedObjectCount} renderable static placements could not load. " +
                "If the converted source files exist, import them before starting the zone. " +
                $"First failure: {firstFailure}");
        }

        // Unresolved props no longer abort the zone: the walkabout with a
        // missing barrel beats a refusal to load. Probes assert zero through
        // UnresolvedObjectCount/IsFullyResolved; terrain failure stays fatal.
        return TerrainTileCount > 0
            && PlacedObjectCount > 0
            && !_terrainFatal;
    }

    private void ResetZone()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        // Nulled so an early-failure path cannot dangle freed roots into the
        // next load attempt.
        _terrainRoot = null;
        _objectsRoot = null;
        _charactersRoot = null;
        _terrainFatal = false;
        _npcModelFailures.Clear();
        _staticModelFailures.Clear();
        _pendingBakedLight.Clear();
        _pendingAuthoredLights.Clear();
        _terrainSplatMaterials.Clear();
        if (BakedLightProbe.Active == _lightProbe)
        {
            BakedLightProbe.Activate(null);
        }

        _lightProbe = new BakedLightProbe();
        BakedLightFileCount = 0;
        BakedLitObjectCount = 0;
        BakedLitSurfaceCount = 0;
        AuthoredLightFileCount = 0;
        AuthoredLightCount = 0;
        AuthoredAntiLightCount = 0;
        _terrainSpawnBounds.Clear();
        _spawnHints.Clear();
        _presentationSpawnHints.Clear();
        TerrainTileCount = 0;
        _nativeTerrainTileCount = 0;
        TerrainVertexCount = 0;
        PlacedObjectCount = 0;
        VisualObjectCount = 0;
        NonVisualObjectCount = 0;
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

    protected virtual bool TryAddNativeTerrainTile(string terrainPath, out string error)
    {
        // Try native content first, then fall back to converted route.
        if (TryAddNativeTerrainTileImpl(terrainPath, out error))
        {
            return true;
        }

        // Fallback to converted terrain (kept for backward compatibility).
        return TryAddConvertedTerrainTile(terrainPath, out error);
    }

    /// <summary>
    /// Loads native terrain from the native content root.
    /// Overridable for fault-injection loaders (ADR 0038 gate item 5).
    /// </summary>
    protected virtual bool TryAddNativeTerrainTileImpl(string convertedPath, out string error)
    {
        error = string.Empty;

        // Extract tile coordinates from the converted path (e.g., "0_2_terrain").
        string fileName = System.IO.Path.GetFileNameWithoutExtension(convertedPath);

        // Build native path: <native_root>/maps/<kebab-map-name>/<coords>/<coords>_terrain.tscn
        string kebabMapName = MapNameTransform.ToKebabCase(MapName);
        string nativePath = $"{NativeContentSettings.NativeRoot}/maps/{kebabMapName}/{fileName}/{fileName}_terrain.tscn";

        // Check if native file exists without relying on ResourceLoader (which requires import).
        if (!FileAccess.FileExists(nativePath))
        {
            return false;
        }

        // Load native scene directly.
        PackedScene? scene = ResourceLoader.Load<PackedScene>(nativePath);
        Node? instance = scene?.Instantiate();
        if (instance is not Node3D tile)
        {
            instance?.Free();
            error = $"Native terrain scene failed to instantiate: {nativePath}";
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        // Native scenes have position already baked; don't read manifest or set Position.
        tile.SetMeta("source_path", nativePath);
        tile.SetMeta("layered_terrain", true);
        tile.SetMeta("native_terrain", true);  // Mark as native for ApplyZoneLighting to skip.
        _terrainRoot!.AddChild(tile);

        MeshInstance3D? up = tile.GetNodeOrNull<MeshInstance3D>("Up");
        MeshInstance3D? down = tile.GetNodeOrNull<MeshInstance3D>("Down");
        MeshInstance3D?[] candidates = [up, down];
        MeshInstance3D[] sides = candidates.Where(side => side?.Mesh != null).Cast<MeshInstance3D>().ToArray();
        if (sides.Length == 0)
        {
            tile.QueueFree();
            error = $"Native terrain scene has no mesh sides: {nativePath}";
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        // Native materials already have baked uniforms; do NOT override them.
        // Just register for lightmap probe.
        MeshInstance3D? upSide = up != null && sides.Contains(up) ? up : sides.FirstOrDefault();
        if (upSide != null)
        {
            _lightProbe.TryAddTile(upSide, tile.Position + upSide.Position);
        }

        // Collect bounds and collision, same as converted route.
        foreach (MeshInstance3D side in sides)
        {
            Mesh mesh = side.Mesh!;
            Aabb localBounds = side.GetAabb();
            var worldBounds = new Aabb(localBounds.Position + tile.Position + side.Position, localBounds.Size);
            _terrainSpawnBounds.Add(worldBounds);
            TerrainBounds = HasTerrainBounds ? TerrainBounds.Merge(worldBounds) : worldBounds;
            HasTerrainBounds = true;
            TerrainVertexCount += CountMeshVertices(mesh);

            if (CreateTerrainCollision)
            {
                var body = new StaticBody3D { Name = $"{side.Name}Collision" };
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                side.AddChild(body);
            }
        }

        TerrainTileCount++;
        _nativeTerrainTileCount++;
        return true;
    }

    /// <summary>
    /// Loads converted terrain using the existing converted route.
    /// Fallback when native is unavailable. Overridable for fault-injection.
    /// </summary>
    protected virtual bool TryAddConvertedTerrainTile(string terrainPath, out string error)
    {
        PackedScene? scene = ConvertedSceneLoader.Load(ConvertedRoot, terrainPath, out string loadError);
        Node? instance = scene?.Instantiate();
        if (instance is not Node3D tile)
        {
            instance?.Free();
            error = $"Native terrain scene is not loadable: {terrainPath}. {loadError}".Trim();
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        string manifestJson = ReadTileManifestJson(tile, terrainPath);
        if (!TryReadTileOrigin(manifestJson, terrainPath, out Vector3 tileOrigin, out string manifestError))
        {
            tile.QueueFree();
            error = manifestError;
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        MeshInstance3D? up = tile.GetNodeOrNull<MeshInstance3D>("Up");
        MeshInstance3D? down = tile.GetNodeOrNull<MeshInstance3D>("Down");
        MeshInstance3D?[] candidates = [up, down];
        MeshInstance3D[] sides = candidates.Where(side => side?.Mesh != null).Cast<MeshInstance3D>().ToArray();
        if (sides.Length == 0)
        {
            tile.QueueFree();
            error = $"Native terrain scene has no mesh sides: {terrainPath}";
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        tile.Name = SafeNodeName(System.IO.Path.GetFileNameWithoutExtension(terrainPath));
        tile.Position = tileOrigin;
        tile.SetMeta("source_path", terrainPath);
        tile.SetMeta("layered_terrain", true);
        _terrainRoot!.AddChild(tile);

        foreach (MeshInstance3D side in sides)
        {
            // The converter's terrain shader collapses the baked lightmap to a
            // grey scalar and then the scene sun re-lights the result; the
            // client-owned copy applies the baked light as authored color.
            if (side.MaterialOverride is ShaderMaterial splat)
            {
                _clientTerrainShader ??= ResourceLoader.Load<Shader>(ClientTerrainShaderPath);
                if (_clientTerrainShader != null)
                {
                    splat.Shader = _clientTerrainShader;
                }

                _terrainSplatMaterials.Add(splat);
            }
        }

        MeshInstance3D? upSide = up != null && sides.Contains(up) ? up : sides.FirstOrDefault();
        if (upSide != null)
        {
            // The walkable side's lightmap doubles as the zone's light probe:
            // dynamic entities sample it to receive the baked direct term.
            _lightProbe.TryAddTile(upSide, tile.Position + upSide.Position);
        }

        foreach (MeshInstance3D side in sides)
        {
            Mesh mesh = side.Mesh!;
            Aabb localBounds = side.GetAabb();
            var worldBounds = new Aabb(localBounds.Position + tile.Position + side.Position, localBounds.Size);
            _terrainSpawnBounds.Add(worldBounds);
            TerrainBounds = HasTerrainBounds ? TerrainBounds.Merge(worldBounds) : worldBounds;
            HasTerrainBounds = true;
            TerrainVertexCount += CountMeshVertices(mesh);

            if (CreateTerrainCollision)
            {
                var body = new StaticBody3D { Name = $"{side.Name}Collision" };
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                side.AddChild(body);
            }
        }

        TerrainTileCount++;
        error = string.Empty;
        return true;
    }
    /// <summary>
    /// Reads one tile's coordinate manifest. Overridable so fault-injection
    /// loaders can serve a corrupted manifest (ADR 0038 gate item 5).
    /// </summary>
    protected virtual string ReadTileManifestJson(Node3D tile, string terrainPath) =>
        tile.GetMeta(TerrainManifestMetadata, string.Empty).AsString();

    protected virtual bool TryAddLegacyTerrainTile(
        string terrainPath,
        IReadOnlyList<string> layerTextures,
        out string error)
    {
        if (!TryReadObjTileOrigin(terrainPath, out Vector3 tileOrigin, out string manifestError))
        {
            error = manifestError;
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        if (!ConvertedSceneLoader.IsLoadable(terrainPath, "Mesh"))
        {
            error = $"Legacy terrain mesh is not imported or loadable: {terrainPath}";
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        Mesh? mesh = ResourceLoader.Load<Mesh>(terrainPath);
        if (mesh == null || mesh.GetSurfaceCount() == 0)
        {
            error = $"Legacy terrain mesh has no surfaces: {terrainPath}";
            GD.PushWarning($"ZoneLoader: {error}");
            return false;
        }

        var tile = new MeshInstance3D
        {
            Name = SafeNodeName(System.IO.Path.GetFileNameWithoutExtension(terrainPath)),
            Mesh = mesh,
            Position = tileOrigin,
        };

        int dominantLayer = FindDominantTerrainLayer(terrainPath);
        Material? material = CreateTerrainMaterial(dominantLayer, layerTextures);
        if (material != null)
        {
            tile.MaterialOverride = material;
        }

        tile.SetMeta("source_path", terrainPath);
        tile.SetMeta("dominant_layer", dominantLayer);
        _terrainRoot!.AddChild(tile);

        Aabb localBounds = tile.GetAabb();
        var bounds = new Aabb(localBounds.Position + tile.Position, localBounds.Size);
        _terrainSpawnBounds.Add(bounds);
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

        error = string.Empty;
        return true;
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
        string placementPath = terrainPath[..^LegacyTerrainSuffix.Length] + MapRegionSuffix;
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

        if (!TryReadTileOrigin(document.CoordinateManifest, placementPath, out Vector3 tileOrigin, out string manifestError))
        {
            Fail(manifestError);
            return;
        }

        // The baked-light companion indexes objects by their position in this
        // same placement list (the source MapRegion object order).
        BakedStaticLighting? bakedLight = placementPath.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase)
            ? BakedStaticLighting.TryLoad(placementPath[..^MapRegionSuffix.Length] + BakedLightSuffix)
            : null;
        if (bakedLight != null)
        {
            BakedLightFileCount++;
        }

        LoadAuthoredLights(placementPath, tileOrigin);

        for (int objectIndex = 0; objectIndex < document.Objects.Length; objectIndex++)
        {
            MapObjectPlacement placement = document.Objects[objectIndex];
            StaticObjectResolution resolution = ResolveStaticObject(placement.TemplateHref);
            Node3D instance = resolution.Instance ?? new Node3D
            {
                Name = resolution.Kind == StaticObjectKind.NonVisual
                    ? "NonVisualStaticObject"
                    : "UnresolvedStaticObject",
            };
            instance.Position = tileOrigin + ConvertPosition(placement.Position);
            _spawnHints.Add(instance.Position);
            instance.Quaternion = ConvertRotation(placement.RotationYawPitchRoll);
            float scale = placement.Scale <= 0 ? 1.0f : placement.Scale;
            instance.Scale = Vector3.One * scale;
            instance.SetMeta("allods_template", placement.TemplateHref);
            instance.SetMeta("allods_ai_collision", placement.AiCollision);
            _objectsRoot!.AddChild(instance);

            // Map statics are authored support geometry as well as scenery.
            // The converted scenes contain render meshes but no physics bodies,
            // so without this the online controller can appear to stand on a
            // floor while an offline controller falls straight through it.
            if (resolution.Kind == StaticObjectKind.Visual && placement.AiCollision)
            {
                AddStaticCollision(instance);
            }

            PlacedObjectCount++;
            if (resolution.Kind == StaticObjectKind.Visual)
            {
                VisualObjectCount++;
                if (bakedLight != null && bakedLight.HasObject(objectIndex))
                {
                    _pendingBakedLight.Add(new PendingBakedLight(bakedLight, instance, objectIndex));
                }
            }
            else if (resolution.Kind == StaticObjectKind.NonVisual)
            {
                NonVisualObjectCount++;
            }
            else
            {
                UnresolvedObjectCount++;
                _staticModelFailures.Add($"{placement.TemplateHref}: {resolution.Error}");
            }
        }
    }

    /// <summary>
    /// Reads a cell's authored point-light companion. The lights are held
    /// until <see cref="ApplyZoneLighting"/> because their color is the zone's
    /// authored PointLightColor, which only the environment settings know.
    /// </summary>
    private void LoadAuthoredLights(string placementPath, Vector3 tileOrigin)
    {
        if (!placementPath.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string lightsPath = placementPath[..^MapRegionSuffix.Length] + AuthoredLightsSuffix;
        if (!FileAccess.FileExists(lightsPath))
        {
            return;
        }

        IReadOnlyList<AuthoredZoneLight>? lights = AuthoredZoneLights.Parse(FileAccess.GetFileAsString(lightsPath));
        if (lights == null)
        {
            GD.PushWarning($"ZoneLoader: authored lights companion is invalid: {lightsPath}");
            return;
        }

        AuthoredLightFileCount++;
        _pendingAuthoredLights.Add(new PendingAuthoredLights(tileOrigin, lights));
    }

    /// <summary>
    /// Places the zone's authored point lights. They cull to the runtime-lit
    /// receiver layer only (dynamic entities and unbaked props): baked statics
    /// and terrain already carry these same sources inside their bake, so
    /// letting the runtime lights reach them would double-light the world.
    /// </summary>
    private void PlaceAuthoredLights(Color directColor)
    {
        if (_pendingAuthoredLights.Count == 0)
        {
            return;
        }

        var lightsRoot = new Node3D { Name = "AuthoredLights" };
        AddChild(lightsRoot);
        foreach (PendingAuthoredLights pending in _pendingAuthoredLights)
        {
            foreach (AuthoredZoneLight light in pending.Lights)
            {
                if (light.Intensity <= 0)
                {
                    // Authored anti-lights (darkeners) have no additive Godot
                    // analogue; counted so the report shows what was skipped.
                    AuthoredAntiLightCount++;
                    continue;
                }

                lightsRoot.AddChild(new OmniLight3D
                {
                    Name = $"AuthoredLight_{AuthoredLightCount}",
                    Position = pending.TileOrigin + light.Position,
                    LightColor = directColor,
                    // Modulate-2x: authored colors sit at half intensity, the
                    // same factor the baked combine applies to statics.
                    LightEnergy = light.Intensity * 2.0f,
                    OmniRange = Mathf.Max(light.Radius, 0.5f),
                    OmniAttenuation = Mathf.Clamp(light.AttenuationPower, 0.5f, 3.0f),
                    LightCullMask = DynamicEntityLighting.ReceiverLayerMask,
                    ShadowEnabled = false,
                });
                AuthoredLightCount++;
            }
        }
    }

    /// <summary>
    /// Colors the zone with its authored light data: baked per-vertex static
    /// lighting on placed objects and the two-term lightmap combine on terrain.
    /// Called once the authored environment is known (the loader itself has no
    /// zone id); without it the zone keeps the neutral placeholder shading.
    /// </summary>
    public void ApplyZoneLighting(ZoneEnvironmentSettings settings)
    {
        Color ambient = settings.BakedAmbientLight;
        Color direct = settings.DirectLightColor;
        var ambientVector = new Vector3(ambient.R, ambient.G, ambient.B);
        var directVector = new Vector3(direct.R, direct.G, direct.B);
        // Only converted-route materials are collected here. Native-baked tiles
        // never register their splat materials: the bake pre-sets ambient_light
        // and direct_light from the same authored ZoneLights values, and the
        // baked uniforms are authoritative — a mismatch is a bake bug, not
        // something to patch at runtime.
        foreach (ShaderMaterial splat in _terrainSplatMaterials)
        {
            splat.SetShaderParameter("ambient_light", ambientVector);
            splat.SetShaderParameter("direct_light", directVector);
        }

        int litObjects = 0;
        int litSurfaces = 0;
        foreach (PendingBakedLight pending in _pendingBakedLight)
        {
            if (!IsInstanceValid(pending.Instance))
            {
                continue;
            }

            int applied = pending.Baked.Apply(pending.Instance, pending.ObjectIndex, ambient, direct);
            litSurfaces += applied;
            if (applied > 0)
            {
                litObjects++;
            }
        }

        BakedLitObjectCount = litObjects;
        BakedLitSurfaceCount = litSurfaces;
        PlaceAuthoredLights(direct);
        // With the environment known, dynamic entities may sample the bake:
        // the probe colors its samples with the same authored terms.
        _lightProbe.SetZoneColors(ambient, direct);
        BakedLightProbe.Activate(_lightProbe);
        GD.Print(
            $"ZoneLoader: baked lighting | files={BakedLightFileCount} lit_objects={litObjects}/"
            + $"{_pendingBakedLight.Count} lit_surfaces={litSurfaces} ambient={ambient} direct={direct} "
            + $"terrain_materials={_terrainSplatMaterials.Count} probe_tiles={_lightProbe.TileCount} "
            + $"authored_lights={AuthoredLightCount}/{AuthoredLightFileCount} anti_lights={AuthoredAntiLightCount}");
    }

    public override void _ExitTree()
    {
        // The zone owns the process-wide probe slot only while it lives.
        if (BakedLightProbe.Active == _lightProbe)
        {
            BakedLightProbe.Activate(null);
        }
    }

    private static void AddStaticCollision(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } meshInstance
            && meshInstance.GetNodeOrNull<StaticBody3D>("AuthoredCollision") == null)
        {
            Shape3D shape = meshInstance.Mesh.CreateTrimeshShape();
            var body = new StaticBody3D { Name = "AuthoredCollision" };
            body.AddChild(new CollisionShape3D { Shape = shape });
            meshInstance.AddChild(body);
        }

        foreach (Node child in node.GetChildren())
        {
            // Do not recurse into the body we just created.
            if (child is not StaticBody3D)
            {
                AddStaticCollision(child);
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

        if (!TryReadTileOrigin(document.CoordinateManifest, placementPath, out Vector3 tileOrigin, out string manifestError))
        {
            Fail(manifestError);
            return;
        }

        ServerObjectCount += document.Objects.Length;
        foreach (MapObjectPlacement placement in document.Objects)
        {
            if (IsPresentationSpawnLocator(placement))
            {
                _presentationSpawnHints.Add(tileOrigin + ConvertPosition(placement.Position));
            }

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
                AddNpcPlaceholder(placement, modelSource, tileOrigin);
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
                Position = tileOrigin + ConvertPosition(placement.Position),
                Quaternion = ConvertServerRotation(placement),
                Scale = Vector3.One * definition.Scale,
            };
            character.SetMeta("allods_mob", definition.MobSource);
            character.SetMeta("allods_visual_mob", definition.VisualMobSource);
            _charactersRoot!.AddChild(character);

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

    private void AddNpcPlaceholder(MapObjectPlacement placement, string modelSource, Vector3 tileOrigin)
    {
        var placeholder = new MeshInstance3D
        {
            Name = $"NpcPlaceholder_{NpcPlacementCount}",
            Position = tileOrigin + ConvertPosition(placement.Position) + Vector3.Up * 0.9f,
            Quaternion = ConvertServerRotation(placement),
            Mesh = new CapsuleMesh { Radius = 0.42f, Height = 1.8f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("d06a55"),
                Roughness = 0.85f,
            },
        };
        placeholder.SetMeta("allods_mob", modelSource);
        DynamicEntityLighting.MarkReceiver(placeholder);
        _charactersRoot!.AddChild(placeholder);
        NpcPlaceholderCount++;
    }

    private StaticObjectResolution ResolveStaticObject(string templateHref)
    {
        string staticSource = NormalizeHref(string.Empty, templateHref);
        if (string.IsNullOrEmpty(staticSource))
        {
            return StaticObjectResolution.Unresolved("The placement has no static-object template.");
        }

        AllodsResource? staticResource = LoadAllodsResource(staticSource);
        if (staticResource == null)
        {
            return StaticObjectResolution.Unresolved($"Static-object resource is missing: {staticSource}");
        }

        string visualHref = ReadHref(staticResource.raw_xml, "ObjectTemplate");
        string visualSource = NormalizeHref(staticSource, visualHref);
        if (string.IsNullOrEmpty(visualSource))
        {
            return IsCollisionOnlyStatic(staticResource.raw_xml)
                ? StaticObjectResolution.NonVisual()
                : StaticObjectResolution.Unresolved($"Static-object resource has no ObjectTemplate: {staticSource}");
        }

        AllodsResource? visualResource = LoadAllodsResource(visualSource);
        if (visualResource != null && IsInvisiblePortalGeometry(visualSource, visualResource.raw_xml))
        {
            return StaticObjectResolution.NonVisual();
        }

        string scenePath = $"{ConvertedRoot.TrimEnd('/')}/assets/{StripXdbSuffix(visualSource)}.scene.tscn";
        PackedScene? scene = ConvertedSceneLoader.Load(ConvertedRoot, scenePath, out string sceneError);
        Node? sceneInstance = scene?.Instantiate();
        if (sceneInstance is Node3D instance)
        {
            // Catches textures authored straight onto a scene's materials; the
            // ImporterMesh route is already covered inside ConvertedSceneLoader.
            UpscaledTextures.Retexture(instance);
            instance.SetMeta("allods_resolution", "scene");
            return StaticObjectResolution.Visual(instance);
        }

        // A non-Node3D root would otherwise leak as an orphan node.
        sceneInstance?.Free();

        Node3D? fallback = InstantiateGeometryFallback(visualSource);
        if (fallback != null)
        {
            fallback.SetMeta("allods_resolution", "geometry_fallback");
            return StaticObjectResolution.Visual(fallback);
        }

        string detail = string.IsNullOrWhiteSpace(sceneError)
            ? $"Scene and geometry are not loadable: {scenePath}"
            : sceneError;
        return StaticObjectResolution.Unresolved(detail);
    }

    private static bool IsCollisionOnlyStatic(string xml)
    {
        try
        {
            XDocument document = XDocument.Parse(xml);
            return new[] { "aiMesh", "Collision", "aiCollision", "LosData" }
                .Select(name => document.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("href")?.Value)
                .Any(href => !string.IsNullOrWhiteSpace(href));
        }
        catch
        {
            return false;
        }
    }

    private bool IsInvisiblePortalGeometry(string visualSource, string visualXml)
    {
        string geometrySource = NormalizeHref(visualSource, ReadHref(visualXml, "geometry"));
        AllodsResource? geometry = LoadAllodsResource(geometrySource);
        if (geometry == null)
        {
            return false;
        }

        try
        {
            XDocument document = XDocument.Parse(geometry.raw_xml);
            bool portalModel = bool.TryParse(
                document.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "portalModel")
                    ?.Value,
                out bool parsedPortal)
                && parsedPortal;
            bool[] visibility = document.Descendants()
                .Where(element => element.Name.LocalName == "material")
                .Select(material => material.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "visible")
                    ?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => bool.TryParse(value, out bool visible) && visible)
                .ToArray();
            return portalModel && visibility.Length > 0 && visibility.All(visible => !visible);
        }
        catch
        {
            return false;
        }
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
        if (mesh == null)
        {
            return null;
        }

        var fallback = new MeshInstance3D { Name = "ConvertedMesh", Mesh = mesh };
        // Geometry fallbacks join the receiver layer like every converted
        // mesh; the baked pass demotes them when the bake covers them.
        DynamicEntityLighting.MarkReceiver(fallback);
        return fallback;
    }

    private AllodsResource? LoadAllodsResource(string sourcePath) => _tree.Load(sourcePath);

    private static string ReadHref(string xml, string elementName) =>
        AllodsResourceTree.ReadHref(xml, elementName);

    private static IReadOnlyList<string> ReadHrefs(string xml) => AllodsResourceTree.ReadHrefs(xml);

    private void AddFlatTerrainFallback(IEnumerable<string> placementFiles)
    {
        var positions = placementFiles
            .SelectMany(path =>
            {
                MapPlacementDocument? document = ReadPlacementDocument(path);
                Vector3 origin = Vector3.Zero;
                string error = string.Empty;
                if (document == null
                    || !TryReadTileOrigin(document.CoordinateManifest, path, out origin, out error))
                {
                    Fail(document == null ? $"Converted placement cache could not be read: {path}" : error);
                    return Enumerable.Empty<Vector3>();
                }

                return document?.Objects?.Select(placement => origin + ConvertPosition(placement.Position))
                    ?? Enumerable.Empty<Vector3>();
            })
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
        _terrainRoot!.AddChild(tile);

        if (CreateTerrainCollision)
        {
            var body = new StaticBody3D { Name = "Collision" };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, 0.2f, size) } });
            tile.AddChild(body);
        }

        TerrainBounds = new Aabb(tile.Position - new Vector3(size * 0.5f, 0.1f, size * 0.5f), new Vector3(size, 0.2f, size));
        _terrainSpawnBounds.Add(TerrainBounds);
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

    /// <summary>Reads the declared Godot origin from one tile-local placement cache.</summary>
    internal static Vector3 TileOrigin(string resourcePath)
    {
        MapPlacementDocument? document = ReadPlacementDocument(resourcePath);
        Vector3 origin = Vector3.Zero;
        string error = string.Empty;
        if (document == null
            || !TryReadTileOrigin(document.CoordinateManifest, resourcePath, out origin, out error))
        {
            throw new InvalidOperationException(
                document == null ? $"Converted placement cache could not be read: {resourcePath}" : error);
        }

        return origin;
    }

    private static bool TryReadObjTileOrigin(
        string resourcePath,
        out Vector3 origin,
        out string error)
    {
        string text = FileAccess.GetFileAsString(resourcePath);
        string? manifestJson = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(8)
            .FirstOrDefault(line => line.StartsWith(ObjManifestPrefix, StringComparison.Ordinal))
            ?[ObjManifestPrefix.Length..]
            .Trim();
        return TryReadTileOrigin(manifestJson ?? string.Empty, resourcePath, out origin, out error);
    }

    private static bool TryReadTileOrigin(
        string manifestJson,
        string resourcePath,
        out Vector3 origin,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            origin = Vector3.Zero;
            error = $"Tile-local cache is missing {TileCoordinateFrameId} metadata: {resourcePath}";
            return false;
        }

        try
        {
            TileCoordinateManifest? manifest = JsonSerializer.Deserialize<TileCoordinateManifest>(manifestJson, JsonOptions);
            return TryReadTileOrigin(manifest, resourcePath, out origin, out error);
        }
        catch (Exception exception)
        {
            origin = Vector3.Zero;
            error = $"Tile-local coordinate metadata is invalid for {resourcePath}: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadTileOrigin(
        TileCoordinateManifest? manifest,
        string resourcePath,
        out Vector3 origin,
        out string error)
    {
        if (manifest == null)
        {
            origin = Vector3.Zero;
            error = $"Tile-local cache is missing {TileCoordinateFrameId} metadata: {resourcePath}";
            return false;
        }

        long expectedX = ((long)manifest.SectorIndices.X + manifest.TileIndices.X) * manifest.TilePitch;
        long expectedY = ((long)manifest.SectorIndices.Y + manifest.TileIndices.Y) * manifest.TilePitch;
        bool valid = manifest.FrameId == TileCoordinateFrameId
            && manifest.CoordinateScope == TileCoordinateScope
            && !manifest.OriginApplied
            && manifest.TilePitch == (int)TerrainTilePitch
            && manifest.CanonicalServerOrigin.X == expectedX
            && manifest.CanonicalServerOrigin.Y == expectedY
            && manifest.CanonicalServerOrigin.Z == 0
            && manifest.GodotOrigin.X == expectedX
            && manifest.GodotOrigin.Y == manifest.CanonicalServerOrigin.Z
            && manifest.GodotOrigin.Z == -expectedY;
        if (!valid)
        {
            origin = Vector3.Zero;
            error = $"Tile-local coordinate contract is incompatible or already shifted: {resourcePath}";
            return false;
        }

        origin = new Vector3(
            (float)manifest.GodotOrigin.X,
            (float)manifest.GodotOrigin.Y,
            (float)manifest.GodotOrigin.Z);
        error = string.Empty;
        return true;
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

    private static bool IsPresentationSpawnLocator(MapObjectPlacement placement)
    {
        return placement.ObjectType.EndsWith(".Locator", StringComparison.OrdinalIgnoreCase)
            && placement.Properties.Any(property =>
                property.Key.EndsWith(".scriptID", StringComparison.OrdinalIgnoreCase)
                && property.Value.EndsWith("_PlayerPos", StringComparison.OrdinalIgnoreCase));
    }

    private static string TerrainSourceStem(string path)
    {
        if (path.EndsWith(NativeTerrainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return path[..^NativeTerrainSuffix.Length];
        }

        return path.EndsWith(LegacyTerrainSuffix, StringComparison.OrdinalIgnoreCase)
            ? path[..^LegacyTerrainSuffix.Length]
            : path;
    }

    private bool Fail(string message)
    {
        // First error wins; later failures append rather than clobber, so the
        // terrain diagnostic survives a following static-placement message.
        LastError = string.IsNullOrWhiteSpace(LastError)
            ? message
            : $"{LastError} | {message}";
        GD.PushError($"ZoneLoader: {message}");
        return false;
    }

    private sealed class MapPlacementDocument
    {
        [JsonPropertyName("coordinate_manifest")] public TileCoordinateManifest? CoordinateManifest { get; set; }
        [JsonPropertyName("objects")] public MapObjectPlacement[]? Objects { get; set; }
        [JsonPropertyName("used_layers")] public int[]? UsedLayers { get; set; }
    }

    private sealed class TileCoordinateManifest
    {
        [JsonPropertyName("frame_id")] public string FrameId { get; set; } = string.Empty;
        [JsonPropertyName("coordinate_scope")] public string CoordinateScope { get; set; } = string.Empty;
        [JsonPropertyName("origin_applied")] public bool OriginApplied { get; set; }
        [JsonPropertyName("sector_indices")] public TileIndices SectorIndices { get; set; } = new();
        [JsonPropertyName("tile_indices")] public TileIndices TileIndices { get; set; } = new();
        [JsonPropertyName("tile_pitch")] public int TilePitch { get; set; }
        [JsonPropertyName("canonical_server_origin")] public IntegerPosition CanonicalServerOrigin { get; set; } = new();
        [JsonPropertyName("godot_origin")] public IntegerPosition GodotOrigin { get; set; } = new();
    }

    private sealed class TileIndices
    {
        [JsonPropertyName("x")] public long X { get; set; }
        [JsonPropertyName("y")] public long Y { get; set; }
    }

    private sealed class IntegerPosition
    {
        [JsonPropertyName("x")] public long X { get; set; }
        [JsonPropertyName("y")] public long Y { get; set; }
        [JsonPropertyName("z")] public long Z { get; set; }
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

    private sealed record PendingBakedLight(BakedStaticLighting Baked, Node3D Instance, int ObjectIndex);

    private sealed record PendingAuthoredLights(Vector3 TileOrigin, IReadOnlyList<AuthoredZoneLight> Lights);

    private enum StaticObjectKind
    {
        Visual,
        NonVisual,
        Unresolved,
    }

    private sealed record StaticObjectResolution(StaticObjectKind Kind, Node3D? Instance, string Error)
    {
        public static StaticObjectResolution Visual(Node3D instance) =>
            new(StaticObjectKind.Visual, instance, string.Empty);

        public static StaticObjectResolution NonVisual() =>
            new(StaticObjectKind.NonVisual, null, string.Empty);

        public static StaticObjectResolution Unresolved(string error) =>
            new(StaticObjectKind.Unresolved, null, error);
    }
}
