using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

public partial class ZoneLoader : Node3D
{
    public const string DefaultMapName = "Inst_LeagueStart";

    private const string DefaultConvertedRoot = "res://converted/assets/classic-1.1";
    private const string MapRegionSuffix = "_MapRegion.xdb.placements.json";
    private const string TileCoordinateFrameId = "allods-tile-local-v1";
    private const string TileCoordinateScope = "tile-local";
    private const float TerrainTilePitch = 256.0f;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private const string BakedLightSuffix = "_MapRegion.xdb.lightvrt.json";
    private const string AuthoredLightsSuffix = "_MapRegion.xdb.lights.json";
    private const string NativeStaticsBakeFormat = "sarnaut-native-statics-v2";
    private const string NativeStaticsCellFormat = "sarnaut-native-statics-v1";
    private const string NativeStaticsFrameId = "godot-world-v1";
    private const string NativeStaticsCoordinateScope = "world";
    private const string NativeStaticsCellPolicy = "nonempty_placements_only";
    private const string NativeTerrainFrameId = "godot-world-v1";
    private readonly HashSet<string> _npcModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staticModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingBakedLight> _pendingBakedLight = [];
    private readonly List<PendingAuthoredLights> _pendingAuthoredLights = [];
    private BakedLightProbe _lightProbe = new();
    private readonly List<Aabb> _terrainSpawnBounds = [];
    private readonly List<Vector3> _spawnHints = [];
    private readonly List<Vector3> _presentationSpawnHints = [];
    private AllodsResourceTree _tree = new(DefaultConvertedRoot);
    private Node3D? _terrainRoot;
    private Node3D? _objectsRoot;
    private Node3D? _charactersRoot;
    private bool _terrainFatal;
    private bool _characterFatal;
    private bool _authoredLightsPlaced;
    private int _nativeTerrainTileCount;
    private int _nativeStaticPlacementCount;
    private int _nativeStaticVisualCount;
    private int _nativeStaticNonVisualCount;
    private int _nativeStaticReceiverMeshCount;

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
    public int BakedLightFileCount { get; private set; }
    public int BakedLitObjectCount { get; private set; }
    public int BakedLitSurfaceCount { get; private set; }
    public int AuthoredLightFileCount { get; private set; }
    public int AuthoredLightCount { get; private set; }
    public int AuthoredAntiLightCount { get; private set; }
    public int LightProbeTileCount => _lightProbe.TileCount;
    public int NativeTerrainTileCount => _nativeTerrainTileCount;
    public int NativeStaticPlacementCount => _nativeStaticPlacementCount;
    public int NativeStaticVisualCount => _nativeStaticVisualCount;
    public int NativeStaticNonVisualCount => _nativeStaticNonVisualCount;
    public int NativeStaticReceiverMeshCount => _nativeStaticReceiverMeshCount;
    public int NativeCharacterPlacementCount { get; private set; }
    public int NativeCharacterVisualCount { get; private set; }
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
    public Quaternion SuggestedSpawnRotation { get; private set; } = Quaternion.Identity;

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
            return Fail($"Invalid map name '{MapName}'.");
        }

        string mapRoot = $"{ConvertedRoot.TrimEnd('/')}/assets/Maps/{MapName}";
        _terrainRoot = new Node3D { Name = "Terrain" };
        _objectsRoot = new Node3D { Name = "StaticObjects" };
        _charactersRoot = new Node3D { Name = "NpcCharacters" };
        AddChild(_terrainRoot);
        AddChild(_objectsRoot);
        AddChild(_charactersRoot);

        var files = DirAccess.Open(mapRoot) == null ? [] : EnumerateFiles(mapRoot);
        var placementFiles = files.Where(path => path.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();

        PlacementFileCount = placementFiles.Length;
        if (!TryLoadNativeTerrain(out string terrainError))
        {
            _terrainFatal = true;
            Fail(terrainError);
        }

        if (_nativeTerrainTileCount > 0)
        {
            GD.Print(
                $"ZoneLoader: native terrain | map={MapName} "
                + $"tiles={_nativeTerrainTileCount}/{TerrainTileCount} "
                + $"root={NativeContentSettings.NativeRoot}");
        }

        NativeStaticsIndex? nativeStatics = TryIndexNativeStatics();
        if (nativeStatics != null)
        {
            foreach (NativeStaticsRuntimeCell nativeCell in nativeStatics.Cells)
            {
                LoadNativeStaticPlacements(nativeCell, nativeStatics);
            }
        }
        else
        {
            foreach (string placementPath in placementFiles)
            {
                LoadStaticPlacements(placementPath);
            }
        }

        if (nativeStatics != null && _nativeStaticPlacementCount > 0)
        {
            GD.Print(
                $"ZoneLoader: native statics | map={MapName} cells={nativeStatics.Cells.Count} "
                + $"placements={_nativeStaticPlacementCount}/{PlacedObjectCount} "
                + $"visual={_nativeStaticVisualCount}/{VisualObjectCount} "
                + $"non_visual={_nativeStaticNonVisualCount}/{NonVisualObjectCount} "
                + $"receiver_meshes={_nativeStaticReceiverMeshCount} "
                + $"root={NativeContentSettings.NativeRoot}");
        }

        if (!TryLoadNativeCharacterPlacements(out string characterError))
        {
            _characterFatal = true;
            Fail(characterError);
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
            && !_terrainFatal
            && !_characterFatal;
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
        _characterFatal = false;
        _authoredLightsPlaced = false;
        _npcModelFailures.Clear();
        _staticModelFailures.Clear();
        _pendingBakedLight.Clear();
        _pendingAuthoredLights.Clear();
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
        _nativeStaticPlacementCount = 0;
        _nativeStaticVisualCount = 0;
        _nativeStaticNonVisualCount = 0;
        _nativeStaticReceiverMeshCount = 0;
        NativeCharacterPlacementCount = 0;
        NativeCharacterVisualCount = 0;
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
        UsedFlatTerrainFallback = false;
        HasTerrainBounds = false;
        TerrainBounds = default;
        SuggestedSpawnRotation = Quaternion.Identity;
        LastError = string.Empty;
    }

    /// <summary>
    /// Loads the complete native terrain inventory. The manifest is the only
    /// runtime authority for which tiles exist and which scenes represent them.
    /// </summary>
    protected virtual bool TryLoadNativeTerrain(out string error)
    {
        string terrainRoot = $"{NativeContentSettings.NativeRoot}/maps/"
            + MapNameTransform.ToKebabCase(MapName);
        string manifestPath = $"{terrainRoot}/terrain-manifest.json";
        if (!FileAccess.FileExists(manifestPath))
        {
            error = $"Native terrain manifest is missing: {manifestPath}";
            return false;
        }

        NativeTerrainManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<NativeTerrainManifest>(
                ReadNativeTerrainManifestText(manifestPath), JsonOptions);
        }
        catch (Exception exception)
        {
            error = $"Native terrain manifest is invalid: {manifestPath}. {exception.Message}";
            return false;
        }

        string expectedMapId = MapNameTransform.ToKebabCase(MapName);
        if (manifest == null
            || manifest.SchemaVersion != 1
            || !manifest.MapId.Equals(expectedMapId, StringComparison.Ordinal)
            || manifest.Frame?.Id != NativeTerrainFrameId
            || !manifest.Frame.OriginApplied
            || manifest.Tiles is not { Length: > 0 })
        {
            error = $"Native terrain manifest is incompatible: {manifestPath}";
            return false;
        }

        var seenTileIds = new HashSet<string>(StringComparer.Ordinal);
        var seenScenes = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<NativeTerrainRuntimeTile>(manifest.Tiles.Length);
        foreach (NativeTerrainTile entry in manifest.Tiles)
        {
            string pathError = string.Empty;
            if (string.IsNullOrWhiteSpace(entry.TileId)
                || entry.TileId.IndexOfAny(['/', '\\', ':']) >= 0
                || !seenTileIds.Add(entry.TileId)
                || entry.Origin == null
                || !entry.Origin.IsFinite
                || entry.ScenePath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal)
                || !TryResolveRelativeResourcePath(
                    manifestPath,
                    entry.ScenePath,
                    terrainRoot,
                    ".tscn",
                    out string scenePath,
                    out pathError))
            {
                error = $"Native terrain entry is incompatible: {entry.TileId}. {pathError}".Trim();
                return false;
            }

            if (!FileAccess.FileExists(scenePath))
            {
                error = $"Native terrain scene is missing: {scenePath}";
                return false;
            }

            if (!seenScenes.Add(scenePath))
            {
                error = $"Native terrain scene is listed more than once: {scenePath}";
                return false;
            }

            PackedScene? scene = ResourceLoader.Load<PackedScene>(scenePath);
            if (scene == null)
            {
                error = $"Native terrain scene is not loadable: {scenePath}";
                return false;
            }

            pending.Add(new NativeTerrainRuntimeTile(entry, scenePath, scene));
        }

        foreach (NativeTerrainRuntimeTile runtime in pending)
        {
            if (!TryAddNativeTerrainTile(runtime, out error))
            {
                ClearNativeTerrainAfterFailure();
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private void ClearNativeTerrainAfterFailure()
    {
        foreach (Node child in _terrainRoot!.GetChildren())
        {
            _terrainRoot.RemoveChild(child);
            child.Free();
        }

        TerrainTileCount = 0;
        _nativeTerrainTileCount = 0;
        TerrainVertexCount = 0;
        _terrainSpawnBounds.Clear();
        TerrainBounds = default;
        HasTerrainBounds = false;
        _lightProbe = new BakedLightProbe();
    }

    protected virtual string ReadNativeTerrainManifestText(string manifestPath) =>
        FileAccess.GetFileAsString(manifestPath);

    private bool TryAddNativeTerrainTile(NativeTerrainRuntimeTile runtime, out string error)
    {
        Node? instance = runtime.Scene.Instantiate();
        if (instance is not Node3D tile)
        {
            instance?.Free();
            error = $"Native terrain scene failed to instantiate: {runtime.ScenePath}";
            return false;
        }

        Vector3 expectedOrigin = runtime.Entry.Origin!.ToVector3();
        if (!tile.Position.IsEqualApprox(expectedOrigin))
        {
            tile.Free();
            error = $"Native terrain scene origin does not match its manifest: {runtime.ScenePath}";
            return false;
        }

        MeshInstance3D? up = tile.GetNodeOrNull<MeshInstance3D>("Up");
        MeshInstance3D? down = tile.GetNodeOrNull<MeshInstance3D>("Down");
        MeshInstance3D?[] candidates = [up, down];
        MeshInstance3D[] sides = candidates.Where(side => side?.Mesh != null).Cast<MeshInstance3D>().ToArray();
        if (sides.Length == 0)
        {
            tile.Free();
            error = $"Native terrain scene has no mesh sides: {runtime.ScenePath}";
            return false;
        }

        tile.Name = SafeNodeName(runtime.Entry.TileId);
        tile.SetMeta("native_scene", runtime.ScenePath);
        tile.SetMeta("layered_terrain", true);
        tile.SetMeta("native_terrain", true);
        _terrainRoot!.AddChild(tile);

        MeshInstance3D? upSide = up != null && sides.Contains(up) ? up : sides.FirstOrDefault();
        if (upSide != null)
        {
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
        _nativeTerrainTileCount++;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Builds one validated native-static index for the zone. Native statics
    /// are all-or-nothing: the aggregate owns ordered cell discovery, and
    /// every referenced cell and scene must load before any instance is added.
    /// A partial or malformed bake falls back to the complete converted route.
    /// </summary>
    private NativeStaticsIndex? TryIndexNativeStatics()
    {
        string staticsRoot = $"{NativeContentSettings.NativeRoot}/maps/"
            + $"{MapNameTransform.ToKebabCase(MapName)}/statics";
        string bakePath = $"{staticsRoot}/bake.json";
        if (!FileAccess.FileExists(bakePath))
        {
            return null;
        }

        try
        {
            NativeStaticsBakeManifest? bake = JsonSerializer.Deserialize<NativeStaticsBakeManifest>(
                FileAccess.GetFileAsString(bakePath), JsonOptions);
            if (bake == null
                || bake.Format != NativeStaticsBakeFormat
                || bake.SchemaVersion != 2
                || !bake.Map.Equals(MapName, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(bake.Zone)
                || bake.Frame?.Id != NativeStaticsFrameId
                || bake.Frame.CoordinateScope != NativeStaticsCoordinateScope
                || !bake.Frame.OriginApplied
                || bake.CellPolicy != NativeStaticsCellPolicy
                || bake.Report == null
                || bake.Report.Unresolved != 0
                || bake.Cells == null)
            {
                return DeclineNativeStatics(
                    $"Native static bake manifest is incompatible or unresolved: {bakePath}");
            }

            var seenCells = new HashSet<NativeStaticCellKey>();
            var runtimeCells = new List<NativeStaticsRuntimeCell>();
            var scenes = new Dictionary<string, PackedScene>(StringComparer.Ordinal);
            int manifestPlacements = 0;
            int manifestVisual = 0;
            int manifestNonVisual = 0;

            NativeStaticsBakeCell[] orderedCells = bake.Cells
                .OrderBy(cell => cell.Order)
                .ToArray();
            for (int cellIndex = 0; cellIndex < orderedCells.Length; cellIndex++)
            {
                NativeStaticsBakeCell bakeCell = orderedCells[cellIndex];
                string manifestPath = string.Empty;
                string manifestPathError = string.Empty;
                if (bakeCell.Order != cellIndex
                    || bakeCell.Cell?.Sector is not { Length: 2 }
                    || bakeCell.Cell.Tile is not { Length: 2 }
                    || bakeCell.Report == null
                    || bakeCell.Report.Unresolved != 0
                    || !TryResolveRelativeResourcePath(
                        bakePath,
                        bakeCell.Placements,
                        staticsRoot,
                        ".json",
                        out manifestPath,
                        out manifestPathError))
                {
                    return DeclineNativeStatics(
                        $"Native static bake cell {cellIndex} is incompatible: "
                        + (string.IsNullOrWhiteSpace(manifestPathError) ? bakePath : manifestPathError));
                }

                if (!FileAccess.FileExists(manifestPath))
                {
                    return DeclineNativeStatics($"Native static cell manifest is missing: {manifestPath}");
                }

                NativeStaticsManifest? manifest = JsonSerializer.Deserialize<NativeStaticsManifest>(
                    FileAccess.GetFileAsString(manifestPath), JsonOptions);
                if (!TryValidateNativeStaticsManifest(
                        manifest,
                        manifestPath,
                        staticsRoot,
                        MapName,
                        bake.Zone,
                        scenes,
                        out NativeStaticCellKey cellKey,
                        out string manifestError))
                {
                    return DeclineNativeStatics(manifestError);
                }

                NativeStaticCellKey declaredCellKey = NativeStaticCellKey.From(bakeCell.Cell);
                NativeStaticPlacement[] placements = manifest!.Placements!;
                int cellVisual = placements.Count(placement => placement.Visual);
                int cellNonVisual = placements.Length - cellVisual;
                if (cellKey != declaredCellKey
                    || bakeCell.Report.Placements != placements.Length
                    || bakeCell.Report.Visual != cellVisual
                    || bakeCell.Report.NonVisual != cellNonVisual
                    || bakeCell.Report.PointLights != 0
                    || bakeCell.Report.AntiLights != 0
                    || !string.IsNullOrWhiteSpace(bakeCell.AuthoredLights))
                {
                    return DeclineNativeStatics(
                        $"Native static bake cell report does not match {manifestPath}");
                }

                if (!seenCells.Add(declaredCellKey))
                {
                    return DeclineNativeStatics(
                        $"Native static bake has duplicate cell {declaredCellKey}: {manifestPath}");
                }

                manifestPlacements += placements.Length;
                manifestVisual += cellVisual;
                manifestNonVisual += cellNonVisual;
                runtimeCells.Add(new NativeStaticsRuntimeCell(manifest));
            }

            if (runtimeCells.Count != bake.Report.Cells
                || manifestPlacements != bake.Report.Placements
                || manifestVisual != bake.Report.Visual
                || manifestNonVisual != bake.Report.NonVisual
                || bake.Report.PointLights != 0
                || bake.Report.AntiLights != 0)
            {
                return DeclineNativeStatics(
                    $"Native static bake report does not match its cell manifests: {bakePath}");
            }

            return new NativeStaticsIndex(runtimeCells, scenes, bake.Report);
        }
        catch (Exception exception)
        {
            return DeclineNativeStatics(
                $"Native static bake could not be indexed: {bakePath}: {exception.Message}");
        }
    }

    private NativeStaticsIndex? DeclineNativeStatics(string error)
    {
        GD.PushWarning($"ZoneLoader: {error}; using the converted static route for the whole zone.");
        return null;
    }

    private static bool TryValidateNativeStaticsManifest(
        NativeStaticsManifest? manifest,
        string manifestPath,
        string staticsRoot,
        string expectedMap,
        string expectedZone,
        Dictionary<string, PackedScene> scenes,
        out NativeStaticCellKey cellKey,
        out string error)
    {
        cellKey = default;
        if (manifest == null
            || manifest.Format != NativeStaticsCellFormat
            || !manifest.Map.Equals(expectedMap, StringComparison.Ordinal)
            || !manifest.Zone.Equals(expectedZone, StringComparison.Ordinal)
            || manifest.Cell?.Sector is not { Length: 2 }
            || manifest.Cell.Tile is not { Length: 2 }
            || manifest.Frame?.Id != NativeStaticsFrameId
            || !manifest.Frame.OriginApplied
            || manifest.Placements == null)
        {
            error = $"Native static cell manifest is incompatible: {manifestPath}";
            return false;
        }

        cellKey = NativeStaticCellKey.From(manifest.Cell);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int placementIndex = 0; placementIndex < manifest.Placements.Length; placementIndex++)
        {
            NativeStaticPlacement placement = manifest.Placements[placementIndex];
            if (placement.Order != placementIndex)
            {
                error = $"Native static placement order is not contiguous in {manifestPath}";
                return false;
            }

            if (!TryReadNativeTransform(placement, out _, out _, out _, out string transformError))
            {
                error = $"Native static placement '{placement.Name}' is invalid in {manifestPath}: {transformError}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(placement.Name) || !names.Add(placement.Name))
            {
                error = $"Native static placement names are empty or duplicated in {manifestPath}";
                return false;
            }

            if (!placement.Visual)
            {
                if (!string.IsNullOrWhiteSpace(placement.Scene)
                    || (placement.NonVisualReason != "collision_only"
                        && placement.NonVisualReason != "invisible_portal")
                    || placement.Classification != placement.NonVisualReason)
                {
                    error = $"Native nonvisual placement '{placement.Name}' is invalid in {manifestPath}";
                    return false;
                }

                continue;
            }

            if (placement.Classification != "visual")
            {
                error = $"Native visual placement '{placement.Name}' has an invalid classification in {manifestPath}";
                return false;
            }

            if (!TryResolveRelativeResourcePath(
                    manifestPath,
                    placement.Scene,
                    staticsRoot,
                    ".tscn",
                    out string scenePath,
                    out string scenePathError))
            {
                error = $"Native static placement '{placement.Name}' has an invalid scene: {scenePathError}";
                return false;
            }

            if (!scenes.TryGetValue(scenePath, out PackedScene? scene))
            {
                if (!FileAccess.FileExists(scenePath))
                {
                    error = $"Native static scene is missing: {scenePath}";
                    return false;
                }

                scene = ResourceLoader.Load<PackedScene>(scenePath);
                Node? probe = scene?.Instantiate();
                bool validRoot = probe is Node3D;
                probe?.Free();
                if (scene == null || !validRoot)
                {
                    error = $"Native static scene does not instantiate as Node3D: {scenePath}";
                    return false;
                }

                scenes.Add(scenePath, scene);
            }

            placement.ResolvedScenePath = scenePath;
        }

        manifest.SourcePath = manifestPath;
        error = string.Empty;
        return true;
    }

    private static bool TryResolveRelativeResourcePath(
        string ownerPath,
        string? relativePath,
        string allowedRoot,
        string requiredExtension,
        out string resolvedPath,
        out string error)
    {
        resolvedPath = string.Empty;
        string relative = (relativePath ?? string.Empty).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative)
            || relative.StartsWith('/')
            || relative.Contains("://", StringComparison.Ordinal)
            || !relative.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"expected a relative '{requiredExtension}' resource path, got '{relativePath}'";
            return false;
        }

        string ownerDirectory = ownerPath[..ownerPath.LastIndexOf('/')];
        var parts = ownerDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count <= 1)
                {
                    error = $"relative path escapes the resource root: '{relativePath}'";
                    return false;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (part.IndexOfAny([':', '*', '?']) >= 0)
            {
                error = $"relative path contains an unsafe segment: '{relativePath}'";
                return false;
            }

            parts.Add(part);
        }

        resolvedPath = "res://" + string.Join('/', parts.Skip(1));
        string rootPrefix = allowedRoot.TrimEnd('/') + "/";
        if (!resolvedPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            error = $"relative path escapes the allowed native root: '{relativePath}'";
            resolvedPath = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadNativeTransform(
        NativeStaticPlacement placement,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale,
        out string error)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        scale = Vector3.One;
        if (placement.Position is not { Length: 3 }
            || placement.Rotation is not { Length: 4 }
            || !placement.Position.All(float.IsFinite)
            || !placement.Rotation.All(float.IsFinite)
            || !float.IsFinite(placement.Scale)
            || placement.Scale <= 0)
        {
            error = "position, rotation, or scale is missing or non-finite";
            return false;
        }

        position = new Vector3(placement.Position[0], placement.Position[1], placement.Position[2]);
        rotation = new Quaternion(
            placement.Rotation[0],
            placement.Rotation[1],
            placement.Rotation[2],
            placement.Rotation[3]);
        if (!Mathf.IsEqualApprox(rotation.LengthSquared(), 1.0f))
        {
            error = "rotation is not a unit quaternion";
            return false;
        }

        scale = Vector3.One * placement.Scale;
        error = string.Empty;
        return true;
    }

    private void LoadNativeStaticPlacements(
        NativeStaticsRuntimeCell cell,
        NativeStaticsIndex index)
    {
        foreach (NativeStaticPlacement placement in cell.Manifest.Placements!)
        {
            _ = TryReadNativeTransform(placement, out Vector3 position, out Quaternion rotation, out Vector3 scale, out _);
            Node3D instance;
            if (placement.Visual)
            {
                PackedScene scene = index.Scenes[placement.ResolvedScenePath];
                instance = (Node3D)scene.Instantiate();
                ConfigureNativeStaticLighting(instance);
                _nativeStaticVisualCount++;
                VisualObjectCount++;
            }
            else
            {
                instance = new Node3D();
                instance.SetMeta("native_nonvisual_reason", placement.NonVisualReason ?? string.Empty);
                _nativeStaticNonVisualCount++;
                NonVisualObjectCount++;
            }

            instance.Name = placement.Name;
            instance.Position = position;
            instance.Quaternion = rotation;
            instance.Scale = scale;
            instance.SetMeta("native_static", true);
            instance.SetMeta("native_visual", placement.Visual);
            instance.SetMeta("native_collision", placement.Collision);
            instance.SetMeta("native_classification", placement.Classification);
            if (placement.Visual)
            {
                instance.SetMeta("native_scene", placement.ResolvedScenePath);
            }

            _objectsRoot!.AddChild(instance);
            _spawnHints.Add(position);
            if (placement.Visual && placement.Collision)
            {
                AddStaticCollision(instance);
            }

            _nativeStaticPlacementCount++;
            PlacedObjectCount++;
        }
    }

    private void ConfigureNativeStaticLighting(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh)
        {
            bool hasShadedSurface = Enumerable.Range(0, mesh.Mesh.GetSurfaceCount())
                .Select(mesh.GetActiveMaterial)
                .Any(material => material is not BaseMaterial3D baseMaterial
                    || baseMaterial.ShadingMode != BaseMaterial3D.ShadingModeEnum.Unshaded);
            mesh.Layers = hasShadedSurface
                ? DynamicEntityLighting.ReceiverLayers
                : DynamicEntityLighting.BakedOnlyLayers;
            if (hasShadedSurface)
            {
                _nativeStaticReceiverMeshCount++;
            }
        }

        foreach (Node child in node.GetChildren())
        {
            ConfigureNativeStaticLighting(child);
        }
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
        if (_authoredLightsPlaced || _pendingAuthoredLights.Count == 0)
        {
            return;
        }

        _authoredLightsPlaced = true;
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
            + $"probe_tiles={_lightProbe.TileCount} "
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

    private bool TryLoadNativeCharacterPlacements(out string error)
    {
        string mapId = MapNameTransform.ToKebabCase(MapName);
        string placementPath = $"{NativeContentSettings.NativeRoot}/maps/{mapId}/character-placements.json";
        if (!FileAccess.FileExists(placementPath))
        {
            error = $"Native character placement manifest is missing: {placementPath}";
            return false;
        }

        NativeCharacterPlacements placements;
        try
        {
            placements = NativeCharacterPlacements.Parse(
                FileAccess.GetFileAsString(placementPath),
                mapId);
        }
        catch (Exception exception)
        {
            error = $"Native character placement manifest is invalid: {placementPath}. {exception.Message}";
            return false;
        }

        NativeCharacterWorldTransform presentation = placements.PresentationSpawn;
        _presentationSpawnHints.Add(new Vector3(
            presentation.PositionX,
            presentation.PositionY,
            presentation.PositionZ));
        SuggestedSpawnRotation = new Quaternion(
            presentation.RotationX,
            presentation.RotationY,
            presentation.RotationZ,
            presentation.RotationW);

        var catalog = new EntityModelCatalog();
        var resolved = new List<(NativeCharacterPlacement Placement, EntityModel Model)>(
            placements.Placements.Count);
        foreach (NativeCharacterPlacement placement in placements.Placements)
        {
            if (!catalog.TryResolve(placement.CharacterKey, out EntityModel model))
            {
                error = $"Native character placement '{placement.SpawnId}' has no loadable scene for key "
                    + $"'{placement.CharacterKey}'. {catalog.LastError}".Trim();
                return false;
            }

            resolved.Add((placement, model));
        }

        ServerObjectCount = resolved.Count;
        NpcPlacementCount = resolved.Count;
        NativeCharacterPlacementCount = resolved.Count;
        if (!SpawnNpcVisuals)
        {
            error = string.Empty;
            return true;
        }

        foreach ((NativeCharacterPlacement placement, EntityModel model) in resolved)
        {
            var character = new CharacterRig
            {
                Name = SafeNodeName(placement.SpawnId),
                AutoLoad = false,
                ShowPlaceholderOnFailure = false,
                ScenePath = model.ScenePath,
                Position = new Vector3(
                    placement.PositionX,
                    placement.PositionY,
                    placement.PositionZ),
                Quaternion = new Quaternion(
                    placement.RotationX,
                    placement.RotationY,
                    placement.RotationZ,
                    placement.RotationW),
            };
            character.SetMeta("native_spawn_id", placement.SpawnId);
            character.SetMeta("native_character_key", placement.CharacterKey);
            character.SetMeta("native_scene", model.ScenePath);
            _charactersRoot!.AddChild(character);
            if (!character.Load())
            {
                ClearNativeCharactersAfterFailure();
                error = $"Native character placement '{placement.SpawnId}' failed to load: {character.LastError}";
                return false;
            }

            NpcVisualCount++;
            NativeCharacterVisualCount++;
        }

        GD.Print(
            $"ZoneLoader: native characters | map={MapName} placements={NativeCharacterPlacementCount} "
            + $"visuals={NativeCharacterVisualCount} placeholders={NpcPlaceholderCount} "
            + $"root={NativeContentSettings.NativeRoot}");
        error = string.Empty;
        return true;
    }

    private void ClearNativeCharactersAfterFailure()
    {
        foreach (Node child in _charactersRoot!.GetChildren())
        {
            _charactersRoot.RemoveChild(child);
            child.Free();
        }

        NpcVisualCount = 0;
        NativeCharacterVisualCount = 0;
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
        // First error wins; later failures append rather than clobber, so the
        // terrain diagnostic survives a following static-placement message.
        LastError = string.IsNullOrWhiteSpace(LastError)
            ? message
            : $"{LastError} | {message}";
        GD.PushError($"ZoneLoader: {message}");
        return false;
    }

    private sealed class NativeTerrainManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("map_id")] public string MapId { get; set; } = string.Empty;
        [JsonPropertyName("frame")] public NativeTerrainFrame? Frame { get; set; }
        [JsonPropertyName("tiles")] public NativeTerrainTile[]? Tiles { get; set; }
    }

    private sealed class NativeTerrainFrame
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("origin_applied")] public bool OriginApplied { get; set; }
    }

    private sealed class NativeTerrainTile
    {
        [JsonPropertyName("tile_id")] public string TileId { get; set; } = string.Empty;
        [JsonPropertyName("origin")] public NativeTerrainPosition? Origin { get; set; }
        [JsonPropertyName("scene_path")] public string ScenePath { get; set; } = string.Empty;
    }

    private sealed class NativeTerrainPosition
    {
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }

        [JsonIgnore] public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

        public Vector3 ToVector3() => new(X, Y, Z);
    }

    private sealed record NativeTerrainRuntimeTile(
        NativeTerrainTile Entry,
        string ScenePath,
        PackedScene Scene);

    private sealed class NativeStaticsBakeManifest
    {
        [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;
        [JsonPropertyName("zone")] public string Zone { get; set; } = string.Empty;
        [JsonPropertyName("frame")] public NativeStaticsBakeFrame? Frame { get; set; }
        [JsonPropertyName("cell_policy")] public string CellPolicy { get; set; } = string.Empty;
        [JsonPropertyName("report")] public NativeStaticsReport? Report { get; set; }
        [JsonPropertyName("cells")] public NativeStaticsBakeCell[]? Cells { get; set; }
    }

    private sealed class NativeStaticsBakeFrame
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("coordinate_scope")] public string CoordinateScope { get; set; } = string.Empty;
        [JsonPropertyName("origin_applied")] public bool OriginApplied { get; set; }
    }

    private sealed class NativeStaticsBakeCell
    {
        [JsonPropertyName("order")] public int Order { get; set; } = -1;
        [JsonPropertyName("cell")] public NativeStaticCell? Cell { get; set; }
        [JsonPropertyName("placements")] public string Placements { get; set; } = string.Empty;
        [JsonPropertyName("authored_lights")] public string? AuthoredLights { get; set; }
        [JsonPropertyName("report")] public NativeStaticsReport? Report { get; set; }
    }

    private sealed class NativeStaticsReport
    {
        [JsonPropertyName("cells")] public int Cells { get; set; }
        [JsonPropertyName("placements")] public int Placements { get; set; }
        [JsonPropertyName("visual")] public int Visual { get; set; }
        [JsonPropertyName("non_visual")] public int NonVisual { get; set; }
        [JsonPropertyName("unresolved")] public int Unresolved { get; set; }
        [JsonPropertyName("point_lights")] public int PointLights { get; set; }
        [JsonPropertyName("anti_lights")] public int AntiLights { get; set; }
    }

    private sealed class NativeStaticsManifest
    {
        [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
        [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;
        [JsonPropertyName("zone")] public string Zone { get; set; } = string.Empty;
        [JsonPropertyName("cell")] public NativeStaticCell? Cell { get; set; }
        [JsonPropertyName("frame")] public NativeStaticFrame? Frame { get; set; }
        [JsonPropertyName("placements")] public NativeStaticPlacement[]? Placements { get; set; }
        [JsonIgnore] public string SourcePath { get; set; } = string.Empty;
    }

    private sealed class NativeStaticCell
    {
        [JsonPropertyName("sector")] public long[]? Sector { get; set; }
        [JsonPropertyName("tile")] public long[]? Tile { get; set; }
    }

    private sealed class NativeStaticFrame
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("origin_applied")] public bool OriginApplied { get; set; }
    }

    private sealed class NativeStaticPlacement
    {
        [JsonPropertyName("order")] public int Order { get; set; } = -1;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("scene")] public string? Scene { get; set; }
        [JsonPropertyName("position")] public float[]? Position { get; set; }
        [JsonPropertyName("rotation")] public float[]? Rotation { get; set; }
        [JsonPropertyName("scale")] public float Scale { get; set; }
        [JsonPropertyName("collision")] public bool Collision { get; set; }
        [JsonPropertyName("visual")] public bool Visual { get; set; }
        [JsonPropertyName("classification")] public string Classification { get; set; } = string.Empty;
        [JsonPropertyName("nonvisual_reason")] public string? NonVisualReason { get; set; }
        [JsonIgnore] public string ResolvedScenePath { get; set; } = string.Empty;
    }

    private readonly record struct NativeStaticCellKey(
        long SectorX,
        long SectorY,
        long TileX,
        long TileY)
    {
        public static NativeStaticCellKey From(TileCoordinateManifest manifest) => new(
            manifest.SectorIndices.X,
            manifest.SectorIndices.Y,
            manifest.TileIndices.X,
            manifest.TileIndices.Y);

        public static NativeStaticCellKey From(NativeStaticCell cell) => new(
            cell.Sector![0],
            cell.Sector[1],
            cell.Tile![0],
            cell.Tile[1]);

        public override string ToString() =>
            $"{SectorX:D3}_{SectorY:D3}/{TileX}_{TileY}";
    }

    private sealed record NativeStaticsRuntimeCell(NativeStaticsManifest Manifest);

    private sealed record NativeStaticsIndex(
        IReadOnlyList<NativeStaticsRuntimeCell> Cells,
        IReadOnlyDictionary<string, PackedScene> Scenes,
        NativeStaticsReport Report);

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
