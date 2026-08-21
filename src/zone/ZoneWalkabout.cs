using Godot;
using SarnautCore.Gameplay;
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
    /// <summary>The named input actions this scene reads, declared in <c>project.godot</c>.</summary>
    public const string TargetClick = "target_click";
    public const string TargetNearest = "target_nearest";
    public const string Interact = "interact";
    public const string OpenQuestLog = "journal";
    public const string OpenInventory = "inventory";
    public const string AbilitySlot1 = "ability_slot_1";

    [Export] public string DefaultMapName { get; set; } = ZoneLoader.DefaultMapName;
    [Export] public string DefaultZoneId { get; set; } = "InstLeague1";

    /// <summary>
    /// Where the status line is. Exported rather than hard-coded, because the
    /// path used to be a string literal four containers deep and renaming any
    /// one of them turned the whole scene into a null reference at run time.
    /// </summary>
    [Export] public NodePath StatusLabelPath { get; set; } = new("%Status");

    /// <summary>
    /// The scene-owned bottom overlay that the ability bar must stay above.
    /// Its measured rectangle includes wrapping at the active viewport width.
    /// </summary>
    [Export] public NodePath HelpPanelPath { get; set; } = new("%HelpPanel");

    private SessionHost _session = null!;
    private readonly GameplayFocusOwner _focus = new();
    private ZoneLoader _loader = null!;
    private WalkaboutController _walker = null!;
    private EntityModelCatalog _characterCatalog = null!;
    private ZoneNetworkLoop? _networkLoop;
    private GameplayHudViewModel? _hudModel;
    private GameplayHudControl? _hudControl;
    private Label _status = null!;
    private string _zoneStatus = "";

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

        ApplyAuthoredEnvironment(
            mapName,
            string.IsNullOrWhiteSpace(request.ZoneId) ? DefaultZoneId : request.ZoneId);
        _walker.Position = _loader.SuggestedSpawnPosition;
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
        _hudControl = new GameplayHudControl();
        _hudControl.Initialize(_hudModel, _networkLoop, GetNode<Control>(HelpPanelPath));
        GetNode<CanvasLayer>("Interface").AddChild(_hudControl);
        _networkLoop.Start(
            _walker,
            entityRoot,
            _characterCatalog,
            request.ServerAddress,
            string.IsNullOrWhiteSpace(request.ZoneId) ? DefaultZoneId : request.ZoneId,
            _session.ContentPackId,
            request.Ticket,
            _hudModel,
            SetNetworkStatus,
            OnAdmitted,
            OnRefused);
    }

    /// <summary>
    /// Replaces the scene's hand-authored lighting with the zone's authored 1.1
    /// environment (ZoneLights: ambient, sun, fog, post exposure). The scene
    /// defaults only survive when the converted data does not carry the zone.
    /// </summary>
    private void ApplyAuthoredEnvironment(string mapName, string zoneId)
    {
        WorldEnvironment? worldEnvironment = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        DirectionalLight3D? sun = GetNodeOrNull<DirectionalLight3D>("Sun");
        if (worldEnvironment?.Environment == null || sun == null)
        {
            return;
        }

        var tree = new AllodsResourceTree(_loader.ConvertedRoot);
        if (!ZoneEnvironmentSettings.TryLoad(tree, mapName, zoneId, out ZoneEnvironmentSettings authored, out string error))
        {
            GD.PushWarning($"ZoneWalkabout keeps placeholder lighting: {error}");
            return;
        }

        authored.Apply(worldEnvironment.Environment, sun);
        _loader.ApplyZoneLighting(authored);
        AddAuthoredSkydome(authored);
        GD.Print(
            $"ZoneWalkabout: authored environment {authored.SourcePath} | ambient={authored.AmbientColor} "
            + $"sun={authored.SunColor} fog={authored.FogColor} {authored.FogStart}..{authored.FogEnd} "
            + $"exposure={authored.ExposureMultiplier} sky={authored.SkyMeshSource}");
    }

    /// <summary>
    /// Instances the zone's authored skydome behind the world. The fog-colored
    /// background stays beneath it for zones whose sky meshes are unavailable.
    /// </summary>
    private void AddAuthoredSkydome(ZoneEnvironmentSettings authored)
    {
        if (GetNodeOrNull<ZoneSkydome>("Skydome") != null)
        {
            return;
        }

        var tree = new AllodsResourceTree(_loader.ConvertedRoot);
        ZoneSkydome? skydome = ZoneSkydome.TryCreate(tree, _loader.ConvertedRoot, authored.SkyMeshSource);
        if (skydome == null)
        {
            if (!string.IsNullOrEmpty(authored.SkyMeshSource))
            {
                GD.PushWarning($"ZoneWalkabout: authored skydome unavailable: {authored.SkyMeshSource}");
            }

            return;
        }

        AddChild(skydome);
        GD.Print($"ZoneWalkabout: skydome {authored.SkyMeshSource} parts={skydome.PartCount}");
    }

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

        if (_hudControl is not null && inputEvent.IsActionPressed(OpenInventory))
        {
            _hudControl.ToggleInventory();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_hudControl is not null && inputEvent.IsActionPressed(OpenQuestLog))
        {
            _hudControl.ToggleQuestLog();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_networkLoop is null || !_focus.WorldInputEnabled)
        {
            return;
        }

        if (inputEvent.IsActionPressed(AbilitySlot1))
        {
            _hudModel?.Abilities.TryRequestUse(0, _networkLoop.TargetEntityId);
            GetViewport().SetInputAsHandled();
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
