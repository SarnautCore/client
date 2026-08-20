using Godot;
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

    [Export] public string DefaultMapName { get; set; } = ZoneLoader.DefaultMapName;
    [Export] public string DefaultZoneId { get; set; } = "InstLeague1";

    /// <summary>
    /// Where the status line is. Exported rather than hard-coded, because the
    /// path used to be a string literal four containers deep and renaming any
    /// one of them turned the whole scene into a null reference at run time.
    /// </summary>
    [Export] public NodePath StatusLabelPath { get; set; } = new("%Status");

    private SessionHost _session = null!;
    private ZoneLoader _loader = null!;
    private WalkaboutController _walker = null!;
    private ZoneNetworkLoop? _networkLoop;
    private Label _status = null!;
    private string _zoneStatus = "";

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _loader = GetNode<ZoneLoader>("ZoneLoader");
        _walker = GetNode<WalkaboutController>("Walker");
        _status = GetNode<Label>(StatusLabelPath);

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
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return;
        }

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
        var catalog = new EntityModelCatalog(_loader.ConvertedRoot);
        if (!catalog.IsAvailable)
        {
            GD.Print($"ZoneWalkabout: {catalog.LastError}");
        }

        _networkLoop = new ZoneNetworkLoop { Name = "NetworkLoop" };
        AddChild(_networkLoop);
        _networkLoop.Start(
            _walker,
            entityRoot,
            catalog,
            _loader.ConvertedRoot,
            request.ServerAddress,
            string.IsNullOrWhiteSpace(request.ZoneId) ? DefaultZoneId : request.ZoneId,
            _session.ContentPackId,
            request.Ticket,
            SetNetworkStatus,
            OnAdmitted,
            OnRefused);
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
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                Input.MouseMode = Input.MouseModeEnum.Visible;
            }
            else
            {
                Leave();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (_networkLoop is null)
        {
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
            Vector2 point = Input.MouseMode == Input.MouseModeEnum.Captured
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
}
