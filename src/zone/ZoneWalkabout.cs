using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore.Content;
using SarnautCore.Gameplay;
using SarnautCore.Networking;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The zone scene. It reads what to load from the session autoload rather than
/// from statics of its own.
/// </summary>
/// <remarks>
/// <c>RequestedMapName</c>, <c>RequestedOnlineMode</c> and
/// <c>RequestedServerAddress</c> used to be static properties here, written by
/// the boot menu just before the scene change. They are now fields on
/// <see cref="SessionHost.Zone"/>, alongside the ticket the shard needs, which a
/// static could not have carried safely.
/// </remarks>
public partial class ZoneWalkabout : Node3D
{
    [Signal]
    public delegate void NativeProductRequestedEventHandler(string productKey);

    /// <summary>The named input actions this scene reads, declared in <c>project.godot</c>.</summary>
    public const string TargetClick = "target_click";
    public const string TargetNearest = "target_nearest";
    public const string Interact = "interact";

    [Export] public string DefaultMapName { get; set; } = ZoneLoader.DefaultMapName;
    [Export] public string DefaultZoneId { get; set; } = "InstLeague1";

    /// <summary>
    /// Where the status line is. Exported rather than hard-coded, because the
    /// path used to be a string literal four containers deep and renaming any
    /// one of them turned the whole scene into a null reference at run time.
    /// </summary>
    [Export] public NodePath StatusLabelPath { get; set; } = new("%Status");

    private SessionHost _session = null!;
    private readonly GameplayFocusOwner _focus = new();
    private ZoneLoader _loader = null!;
    private WalkaboutController _walker = null!;
    private EntityModelCatalog _characterCatalog = null!;
    private ZoneNetworkLoop? _networkLoop;
    private GameplayHudViewModel? _hudModel;
    private NativeGameplayHudHost? _nativeHud;
    private Label _status = null!;
    private string _zoneStatus = "";

    public string NativePresentationManifestPath { get; private set; } = string.Empty;
    public string NativePresentationScenePath { get; private set; } = string.Empty;

    public override void _EnterTree()
    {
        // Parent _EnterTree runs before the child character's _Ready, so the
        // selected native scene is in place before CharacterRig auto-loads it.
        SessionHost session = SessionHost.Of(this);
        _characterCatalog = new EntityModelCatalog();
        CharacterRig? character = GetNodeOrNull<CharacterRig>("Walker/Character");
        if (character != null)
        {
            PlayerCharacterModel.Apply(character, _characterCatalog, session.Player.Option);
        }
    }

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _loader = GetNode<ZoneLoader>("ZoneLoader");
        _walker = GetNode<WalkaboutController>("Walker");
        _status = GetNode<Label>(StatusLabelPath);
        _focus.Changed += ApplyFocusState;
        ApplyFocusState();

        ZoneRequest request = _session.Zone;
        string mapName = string.IsNullOrWhiteSpace(request.MapName) ? DefaultMapName : request.MapName.Trim();

        // Offline there is no shard to be authoritative, so the map's own mob
        // placements are all there is to draw. Online they are duplicates of
        // entities the shard replicates, and drawing them puts two of every
        // creature in the zone.
        _loader.SpawnNpcVisuals = !request.Online;
        if (!_loader.LoadZone(mapName))
        {
            _status.Text = _loader.LastError;
            _focus.Cancel();
            return;
        }

        string zoneId = string.IsNullOrWhiteSpace(request.ZoneId) ? DefaultZoneId : request.ZoneId;
        if (!TryLoadNativePresentation(mapName, zoneId, out string presentationError))
        {
            _status.Text = presentationError;
            _focus.Cancel();
            return;
        }

        ApplyPresentationSpawn(
            _walker,
            _loader.SuggestedSpawnPosition,
            _loader.SuggestedSpawnRotation);
        _zoneStatus =
            $"{mapName}  |  {_loader.TerrainTileCount} terrain tiles  |  {_loader.VisualObjectCount} visual objects";
        _status.Text = _zoneStatus;
        if (!request.Online)
        {
            return;
        }

        var entityRoot = new Node3D { Name = "NetworkEntities" };
        AddChild(entityRoot);
        if (!_characterCatalog.IsAvailable)
        {
            GD.Print($"ZoneWalkabout: {_characterCatalog.LastError}");
        }

        _networkLoop = new ZoneNetworkLoop { Name = "NetworkLoop" };
        AddChild(_networkLoop);

        var hudSession = new SessionHudAdapter(sourceEpoch: 1);
        _networkLoop.AttachHudSession(hudSession);

        var hudWorld = new ZoneNativeHudWorld(_networkLoop, this);
        if (!NativeGameplayHudHost.TryMount(
                GetNode<CanvasLayer>("Interface"),
                NativeHudContentPaths.Canonical(),
                _networkLoop.HudSession,
                hudWorld,
                RequestNativeProduct,
                out _nativeHud,
                out string hudError))
        {
            _status.Text = hudError;
            _focus.Cancel();
            _networkLoop.Free();
            _networkLoop = null;
            entityRoot.Free();
            return;
        }

        // ZoneNetworkLoop still feeds the old model while its remaining inventory,
        // loot, and dialogue consumers migrate. It has no visual mount.
        _hudModel = new GameplayHudViewModel(
            ownEntityId: 0,
            abilities:
            [
                new AbilityDefinition(
                    "ability.melee.novice-cleave",
                    "M2.Ability.NoviceCleave.Name",
                    string.Empty),
            ],
            inventoryCapacity: 16,
            stackLimit: _ => 20,
            focus: _focus);
        _networkLoop.Start(
            _walker,
            entityRoot,
            _characterCatalog,
            request.ServerAddress,
            zoneId,
            _session.ContentPackId,
            request.Ticket,
            _hudModel,
            SetNetworkStatus,
            OnAdmitted,
            OnRefused);
    }

    private void RequestNativeProduct(string productKey)
    {
        if (!StringComparer.Ordinal.Equals(productKey, "options"))
        {
            throw new InvalidOperationException($"Unknown native gameplay product '{productKey}'.");
        }

        EmitSignal(SignalName.NativeProductRequested, productKey);
    }

    internal static void ApplyPresentationSpawn(
        Node3D walker,
        Vector3 position,
        Quaternion rotation)
    {
        walker.Position = position;
        walker.Quaternion = rotation;
    }

    private bool TryLoadNativePresentation(string mapName, string requestedZoneId, out string error)
    {
        if (!TryNormalizeContentId(mapName, out string mapId))
        {
            error = $"Native zone presentation has an invalid map id: '{mapName}'.";
            return false;
        }

        if (!TryNormalizeZoneId(requestedZoneId, out string zoneId))
        {
            error = $"Native zone presentation has an invalid zone id: '{requestedZoneId}'.";
            return false;
        }

        if (!NativeZonePresentationRoute.TryCreate(
            NativeContentSettings.NativeRoot,
            mapId,
            zoneId,
            out NativeZonePresentationRoute route,
            out string routeError))
        {
            error = $"Native zone presentation route is invalid: {routeError}";
            return false;
        }

        string manifestPath = route.ManifestPath;
        if (!FileAccess.FileExists(manifestPath))
        {
            error = $"Native zone presentation manifest is missing: {manifestPath}";
            return false;
        }

        NativeZonePresentation presentation;
        try
        {
            presentation = NativeZonePresentation.Parse(
                FileAccess.GetFileAsString(manifestPath),
                mapId,
                zoneId);
        }
        catch (Exception exception)
        {
            error = $"Native zone presentation manifest is invalid: {manifestPath}. {exception.Message}";
            return false;
        }

        if (!route.TryResolveScenePath(presentation.Scene, out string scenePath, out routeError))
        {
            error = $"Native zone presentation scene path is invalid: {routeError}";
            return false;
        }

        if (!FileAccess.FileExists(scenePath))
        {
            error = $"Native zone presentation scene is missing: {scenePath}";
            return false;
        }

        PackedScene? packed = ResourceLoader.Load<PackedScene>(scenePath);
        if (packed == null)
        {
            error = $"Native zone presentation scene is not loadable: {scenePath}";
            return false;
        }

        Node instance;
        try
        {
            instance = packed.Instantiate();
        }
        finally
        {
            packed.Dispose();
        }

        if (instance is not Node3D root)
        {
            instance.Free();
            error = $"Native zone presentation scene root is not Node3D: {scenePath}";
            return false;
        }

        if (!TryValidatePresentationTopology(root, presentation, out error))
        {
            root.Free();
            error = $"Native zone presentation scene is incompatible: {scenePath}. {error}";
            return false;
        }

        root.Name = "ZonePresentation";
        Node3D sky = root.GetNode<Node3D>(presentation.Topology.SkyRootNode);
        sky.Owner = null;
        root.RemoveChild(sky);
        var cameraCenteredSky = new CameraCenteredSky { Name = "CameraCenteredSky" };
        cameraCenteredSky.AddChild(sky);
        root.AddChild(cameraCenteredSky);
        root.SetMeta("native_manifest", manifestPath);
        root.SetMeta("native_scene", scenePath);
        AddChild(root);

        _loader.ApplyZoneLighting(
            ToGodotColor(presentation.ProbeColors.Ambient),
            ToGodotColor(presentation.ProbeColors.Direct));
        NativePresentationManifestPath = manifestPath;
        NativePresentationScenePath = scenePath;
        GD.Print(
            $"ZoneWalkabout: native presentation map={mapId} zone={zoneId} "
            + $"scene={scenePath} sky={presentation.Sky.PartCount}/{presentation.Sky.AnimatedPartCount}");
        error = string.Empty;
        return true;
    }

    private static bool TryValidatePresentationTopology(
        Node3D root,
        NativeZonePresentation presentation,
        out string error)
    {
        WorldEnvironment? worldEnvironment = root.GetNodeOrNull<WorldEnvironment>(
            presentation.Topology.EnvironmentNode);
        DirectionalLight3D? sun = root.GetNodeOrNull<DirectionalLight3D>(
            presentation.Topology.SunNode);
        Node3D? sky = root.GetNodeOrNull<Node3D>(presentation.Topology.SkyRootNode);
        if (worldEnvironment?.Environment == null
            || worldEnvironment.GetParent() != root
            || sun == null
            || sun.GetParent() != root
            || sky == null
            || sky.GetParent() != root)
        {
            error = "required Environment, Sun, and Sky nodes are missing or misplaced";
            return false;
        }

        if (Descendants<WorldEnvironment>(root).Count() != 1
            || Descendants<DirectionalLight3D>(root).Count() != 1)
        {
            error = "the scene must contain exactly one WorldEnvironment and one DirectionalLight3D";
            return false;
        }

        if (sky.GetChildCount() != presentation.Sky.PartCount)
        {
            error = $"sky part count is {sky.GetChildCount()}, expected {presentation.Sky.PartCount}";
            return false;
        }

        int animatedParts = 0;
        foreach (NativeZoneSkyPart part in presentation.Sky.Parts)
        {
            Node3D? partNode = sky.GetNodeOrNull<Node3D>(part.Node);
            if (partNode == null || partNode.GetParent() != sky)
            {
                error = $"sky part '{part.Node}' is missing or is not a direct child of Sky";
                return false;
            }

            bool animated = Descendants<AnimationPlayer>(partNode)
                .Any(player => player.GetAnimationList().Length > 0);
            if (animated != part.Animated)
            {
                error = $"sky part '{part.Node}' animated state is {animated}, expected {part.Animated}";
                return false;
            }

            if (animated)
            {
                animatedParts++;
            }
        }

        if (animatedParts != presentation.Sky.AnimatedPartCount)
        {
            error = $"animated sky part count is {animatedParts}, expected {presentation.Sky.AnimatedPartCount}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static IEnumerable<T> Descendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool TryNormalizeZoneId(string source, out string zoneId)
    {
        string candidate = source.Trim();
        if (candidate.StartsWith("zone.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["zone.".Length..];
        }

        return TryNormalizeContentId(candidate, out zoneId);
    }

    private static bool TryNormalizeContentId(string source, out string contentId)
    {
        contentId = MapNameTransform.ToKebabCase(source);
        return contentId.Length > 0
            && contentId.All(character =>
                character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-')
            && !contentId.StartsWith('-')
            && !contentId.EndsWith('-')
            && !contentId.Contains("--", StringComparison.Ordinal);
    }

    private static Color ToGodotColor(NativeZoneColor color) =>
        new(color.Red, color.Green, color.Blue, 1.0f);

    private void SetNetworkStatus(string networkStatus)
    {
        _status.Text = $"{_zoneStatus}\n{networkStatus}";
    }

    /// <summary>
    /// The shard admitted the session and named the spawn.
    /// </summary>
    /// <remarks>
    /// The ticket is single use and has been burned by now, so the session drops
    /// its copy rather than keeping one that cannot work twice.
    /// </remarks>
    private void OnAdmitted()
    {
        _session.Player.ReleaseTicket();
        if (_session.Flow.Current == Screen.EnteringWorld)
        {
            _session.Flow.EnteredWorld();
        }
    }

    private void OnRefused(string reason)
    {
        GD.PushWarning($"Zone entry refused: {reason}");
        _session.Player.ReleaseTicket();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            if (_focus.Cancel() == FocusCancelResult.LeaveWalkabout)
            {
                Leave();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is InputEventMouseButton button
            && button.Pressed
            && button.ButtonIndex == MouseButton.Right
            && _focus.TryCaptureWorld())
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_networkLoop is null || !_focus.WorldInputEnabled)
        {
            return;
        }

        if (inputEvent.IsActionPressed(Interact))
        {
            _networkLoop.RequestInteract(_networkLoop.TargetEntityId);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent.IsActionPressed(TargetNearest))
        {
            _networkLoop.TryCycleTarget(out _);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent.IsActionPressed(TargetClick))
        {
            // With the cursor captured there is no cursor to pick under, so the
            // pick goes through the middle of the screen, which is where the
            // crosshair would be.
            Viewport viewport = GetViewport();
            Vector2 point = _focus.MouseCaptured
                ? viewport.GetVisibleRect().Size * 0.5f
                : viewport.GetMousePosition();
            if (_networkLoop.TryTargetAtScreenPoint(point, out _))
            {
                viewport.SetInputAsHandled();
            }
        }
    }

    private void Leave()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (_session.Player.IsAuthenticated)
        {
            if (_session.Flow.Current is Screen.InWorld or Screen.EnteringWorld)
            {
                _session.Flow.LeftWorld();
            }

            _session.Show(Screen.CharacterSelect);
            return;
        }

        // The offline walkabout came from the hub and goes back to it.
        GetTree().ChangeSceneToFile("res://scenes/boot.tscn");
    }

    public override void _ExitTree()
    {
        _focus.Changed -= ApplyFocusState;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void ApplyFocusState()
    {
        _walker.InputEnabled = _focus.WorldInputEnabled;
        _walker.LookInputEnabled = _focus.WorldLookEnabled;
        if (DisplayServer.GetName() != "headless")
        {
            Input.MouseMode = _focus.MouseCaptured
                ? Input.MouseModeEnum.Captured
                : Input.MouseModeEnum.Visible;
        }
    }
}
