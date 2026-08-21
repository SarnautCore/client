using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using SarnautCore.Content;
using InvalidDataException = System.IO.InvalidDataException;

namespace SarnautCore;

public partial class ZoneLoader : Node3D
{
    public const string DefaultMapName = "Inst_LeagueStart";

    private const string DefaultConvertedRoot = "res://converted/assets/classic-1.1";
    private const string TileCoordinateFrameId = "allods-tile-local-v1";
    private const string TileCoordinateScope = "tile-local";
    private const float TerrainTilePitch = 256.0f;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private const string NativeTerrainFrameId = "godot-world-v1";
    private readonly HashSet<string> _npcModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private BakedLightProbe _lightProbe = new();
    private readonly List<Aabb> _terrainSpawnBounds = [];
    private readonly List<Vector3> _spawnHints = [];
    private readonly List<Vector3> _presentationSpawnHints = [];
    private Node3D? _terrainRoot;
    private Node3D? _objectsRoot;
    private Node3D? _charactersRoot;
    private bool _terrainFatal;
    private bool _staticFatal;
    private bool _characterFatal;
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
    public int ServerObjectCount { get; private set; }
    public int NpcPlacementCount { get; private set; }
    public int NpcVisualCount { get; private set; }
    public int NpcPlaceholderCount { get; private set; }
    public int ServerPlacementFileCount { get; private set; }
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
    /// True when every authoritative native inventory loaded without error.
    /// </summary>
    public bool IsFullyResolved => string.IsNullOrWhiteSpace(LastError);
    public Aabb TerrainBounds { get; private set; }
    public bool HasTerrainBounds { get; private set; }
    public IReadOnlyCollection<string> NpcModelFailures => _npcModelFailures;

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
        MapName = mapName.Trim();
        if (!IsSafeMapName(MapName))
        {
            return Fail($"Invalid map name '{MapName}'.");
        }

        _terrainRoot = new Node3D { Name = "Terrain" };
        _objectsRoot = new Node3D { Name = "StaticObjects" };
        _charactersRoot = new Node3D { Name = "NpcCharacters" };
        AddChild(_terrainRoot);
        AddChild(_objectsRoot);
        AddChild(_charactersRoot);

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

        bool staticsReady = TryIndexNativeStatics(
            out NativeStaticsIndex? nativeStatics,
            out string staticError);
        using NativeStaticsIndex? ownedNativeStatics = nativeStatics;
        if (staticsReady)
        {
            foreach (NativeStaticCell nativeCell in nativeStatics!.Bake.Cells)
            {
                LoadNativeStaticPlacements(nativeCell, nativeStatics);
            }
        }
        else
        {
            _staticFatal = true;
            Fail(staticError);
        }

        if (nativeStatics != null && _nativeStaticPlacementCount > 0)
        {
            GD.Print(
                $"ZoneLoader: native statics | map={MapName} cells={nativeStatics.Bake.Cells.Count} "
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
            $"| server={ServerObjectCount} " +
            $"| npc={NpcVisualCount}/{NpcPlacementCount} | npc_placeholders={NpcPlaceholderCount}");
        return TerrainTileCount > 0
            && !_terrainFatal
            && !_staticFatal
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
        _staticFatal = false;
        _characterFatal = false;
        _npcModelFailures.Clear();
        if (BakedLightProbe.Active == _lightProbe)
        {
            BakedLightProbe.Activate(null);
        }

        _lightProbe = new BakedLightProbe();
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
        ServerObjectCount = 0;
        NpcPlacementCount = 0;
        NpcVisualCount = 0;
        NpcPlaceholderCount = 0;
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
        try
        {
            foreach (NativeTerrainTile entry in manifest.Tiles)
            {
                string selectedScene;
                try
                {
                    selectedScene = NativeSceneReference.Select(
                        entry.ScenePath,
                        entry.RuntimeScene,
                        allowParentSegments: true);
                }
                catch (InvalidDataException exception)
                {
                    error = $"Native terrain entry '{entry.TileId}' has an invalid scene: {exception.Message}";
                    return false;
                }

                string pathError = string.Empty;
                if (string.IsNullOrWhiteSpace(entry.TileId)
                    || entry.TileId.IndexOfAny(['/', '\\', ':']) >= 0
                    || !seenTileIds.Add(entry.TileId)
                    || entry.Origin == null
                    || !entry.Origin.IsFinite
                    || !TryResolveRelativeResourcePath(
                        manifestPath,
                        selectedScene,
                        terrainRoot,
                        NativeSceneReference.Extension(selectedScene),
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
        finally
        {
            DisposePackedScenes(pending.Select(runtime => runtime.Scene));
        }
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
    /// Builds the complete native static inventory before adding any instance.
    /// The bake is authoritative, so every manifest and scene must resolve.
    /// </summary>
    private bool TryIndexNativeStatics(out NativeStaticsIndex? index, out string error)
    {
        index = null;
        string staticsRoot = $"{NativeContentSettings.NativeRoot}/maps/"
            + $"{MapNameTransform.ToKebabCase(MapName)}/statics";
        string bakePath = $"{staticsRoot}/bake.json";
        if (!FileAccess.FileExists(bakePath))
        {
            error = $"Native static bake manifest is missing: {bakePath}";
            return false;
        }

        var scenes = new Dictionary<string, PackedScene>(StringComparer.Ordinal);
        bool indexOwnsScenes = false;
        try
        {
            NativeStaticBake bake = NativeStaticBake.Parse(
                FileAccess.GetFileAsString(bakePath),
                MapName,
                relativePath =>
                {
                    string path = $"{staticsRoot}/{relativePath}";
                    return FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : null;
                });
            foreach (NativeStaticPlacement placement in bake.Cells.SelectMany(cell => cell.Placements))
            {
                if (placement.ScenePath == null)
                {
                    continue;
                }

                string scenePath = $"{staticsRoot}/{placement.ScenePath}";
                if (scenes.ContainsKey(scenePath))
                {
                    continue;
                }

                if (!FileAccess.FileExists(scenePath))
                {
                    error = $"Native static scene is missing: {scenePath}";
                    return false;
                }

                PackedScene? scene = ResourceLoader.Load<PackedScene>(scenePath);
                Node? probe = scene?.Instantiate();
                bool validRoot = probe is Node3D;
                bool validCollision = placement.Visual
                    || !placement.Collision
                    || probe != null && HasUsableNativeCollision(probe);
                probe?.Free();
                if (scene == null || !validRoot || !validCollision)
                {
                    scene?.Dispose();
                    error = !validCollision
                        ? $"Native nonvisual collision scene has no usable collision shape: {scenePath}"
                        : $"Native static scene does not instantiate as Node3D: {scenePath}";
                    return false;
                }

                scenes.Add(scenePath, scene);
            }

            index = new NativeStaticsIndex(staticsRoot, bake, scenes);
            indexOwnsScenes = true;
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException exception)
        {
            error = $"Native static bake is invalid: {bakePath}: {exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            error = $"Native static bake could not be indexed: {bakePath}: {exception.Message}";
            return false;
        }
        finally
        {
            if (!indexOwnsScenes)
            {
                DisposePackedScenes(scenes.Values);
            }
        }
    }

    private static void DisposePackedScenes(IEnumerable<PackedScene> scenes)
    {
        foreach (PackedScene scene in scenes)
        {
            scene.Dispose();
        }
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

    private void LoadNativeStaticPlacements(
        NativeStaticCell cell,
        NativeStaticsIndex index)
    {
        foreach (NativeStaticPlacement placement in cell.Placements)
        {
            var position = new Vector3(placement.PositionX, placement.PositionY, placement.PositionZ);
            var rotation = new Quaternion(
                placement.RotationX,
                placement.RotationY,
                placement.RotationZ,
                placement.RotationW);
            Node3D instance;
            if (placement.Visual)
            {
                string scenePath = $"{index.StaticsRoot}/{placement.ScenePath}";
                PackedScene scene = index.Scenes[scenePath];
                instance = (Node3D)scene.Instantiate();
                ConfigureNativeStaticLighting(instance);
                _nativeStaticVisualCount++;
                VisualObjectCount++;
            }
            else
            {
                instance = placement.ScenePath == null
                    ? new Node3D()
                    : (Node3D)index.Scenes[$"{index.StaticsRoot}/{placement.ScenePath}"].Instantiate();
                instance.SetMeta("native_nonvisual_reason", placement.NonVisualReason ?? string.Empty);
                _nativeStaticNonVisualCount++;
                NonVisualObjectCount++;
            }

            instance.Name = placement.Name;
            instance.Position = position;
            instance.Quaternion = rotation;
            instance.Scale = Vector3.One * placement.Scale;
            instance.SetMeta("native_static", true);
            instance.SetMeta("native_visual", placement.Visual);
            instance.SetMeta("native_collision", placement.Collision);
            instance.SetMeta("native_classification", placement.Classification);
            if (placement.ScenePath != null)
            {
                instance.SetMeta("native_scene", $"{index.StaticsRoot}/{placement.ScenePath}");
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

    private static bool HasUsableNativeCollision(Node node)
    {
        if (node is CollisionShape3D { Shape: not null, Disabled: false }
            && node.GetParent() is StaticBody3D)
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HasUsableNativeCollision(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Activates terrain lightmap sampling for dynamic entities.</summary>
    public void ApplyZoneLighting(ZoneEnvironmentSettings settings)
    {
        Color ambient = settings.BakedAmbientLight;
        Color direct = settings.DirectLightColor;
        _lightProbe.SetZoneColors(ambient, direct);
        BakedLightProbe.Activate(_lightProbe);
        GD.Print(
            $"ZoneLoader: native lighting | ambient={ambient} direct={direct} "
            + $"probe_tiles={_lightProbe.TileCount}");
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

    private static CoordinateManifestDocument? ReadCoordinateManifestDocument(string path)
    {
        try
        {
            string json = FileAccess.GetFileAsString(path);
            return JsonSerializer.Deserialize<CoordinateManifestDocument>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ZoneLoader could not parse {path}: {exception.Message}");
            return null;
        }
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
        CoordinateManifestDocument? document = ReadCoordinateManifestDocument(resourcePath);
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
        [JsonPropertyName("runtime_scene")] public string? RuntimeScene { get; set; }
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

    private sealed class NativeStaticsIndex : IDisposable
    {
        public NativeStaticsIndex(
            string staticsRoot,
            NativeStaticBake bake,
            IReadOnlyDictionary<string, PackedScene> scenes)
        {
            StaticsRoot = staticsRoot;
            Bake = bake;
            Scenes = scenes;
        }

        public string StaticsRoot { get; }
        public NativeStaticBake Bake { get; }
        public IReadOnlyDictionary<string, PackedScene> Scenes { get; }

        public void Dispose() => DisposePackedScenes(Scenes.Values);
    }

    private sealed class CoordinateManifestDocument
    {
        [JsonPropertyName("coordinate_manifest")] public TileCoordinateManifest? CoordinateManifest { get; set; }
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

}
