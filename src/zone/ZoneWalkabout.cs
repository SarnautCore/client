using Godot;

namespace SarnautCore;

public partial class ZoneWalkabout : Node3D
{
    public static string RequestedMapName { get; set; } = ZoneLoader.DefaultMapName;
    public static bool RequestedOnlineMode { get; set; }
    public static string RequestedServerAddress { get; set; } = "127.0.0.1:4242";

    [Export] public string DefaultMapName { get; set; } = ZoneLoader.DefaultMapName;
    [Export] public string NetworkZoneId { get; set; } = "InstLeague1";

    private ZoneLoader _loader = null!;
    private WalkaboutController _walker = null!;
    private Label _status = null!;
    private string _zoneStatus = "";

    public override void _Ready()
    {
        _loader = GetNode<ZoneLoader>("ZoneLoader");
        _walker = GetNode<WalkaboutController>("Walker");
        _status = GetNode<Label>("Interface/Margin/Column/StatusPanel/Status");

        string mapName = string.IsNullOrWhiteSpace(RequestedMapName) ? DefaultMapName : RequestedMapName.Trim();
        bool loaded = _loader.LoadZone(mapName);
        if (loaded)
        {
            _walker.Position = _loader.SuggestedSpawnPosition;
            _zoneStatus = $"{mapName}  |  {_loader.TerrainTileCount} terrain tiles  |  {_loader.VisualObjectCount} visual objects";
            _status.Text = _zoneStatus;
            if (RequestedOnlineMode)
            {
                var entityRoot = new Node3D { Name = "NetworkEntities" };
                AddChild(entityRoot);
                var networkLoop = new ZoneNetworkLoop { Name = "NetworkLoop" };
                AddChild(networkLoop);
                networkLoop.Start(
                    _walker,
                    entityRoot,
                    RequestedServerAddress,
                    NetworkZoneId,
                    SetNetworkStatus);
            }
        }
        else
        {
            _status.Text = _loader.LastError;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    private void SetNetworkStatus(string networkStatus)
    {
        _status.Text = $"{_zoneStatus}\n{networkStatus}";
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo || !MatchesKey(key, Key.Escape))
        {
            return;
        }

        if (Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else
        {
            GetTree().ChangeSceneToFile("res://scenes/boot.tscn");
        }

        GetViewport().SetInputAsHandled();
    }

    private static bool MatchesKey(InputEventKey input, Key key)
    {
        return input.PhysicalKeycode == key || input.Keycode == key;
    }
}
