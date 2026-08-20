using Godot;

namespace SarnautCore;

public partial class Boot : Control
{
    private LineEdit _zoneName = null!;
    private Label _zoneError = null!;
    private CheckButton _onlineMode = null!;
    private LineEdit _serverAddress = null!;

    public override void _Ready()
    {
        var backdrop = new ColorRect
        {
            Color = new Color("10151d"),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var menu = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(320, 0),
        };
        menu.AddThemeConstantOverride("separation", 16);
        center.AddChild(menu);

        var title = new Label
        {
            Text = "SarnautCore",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        menu.AddChild(title);
        menu.AddChild(new Label
        {
            Text = "Development client",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color("9aa8b8"),
        });

        var assetViewer = new Button { Text = "Asset Viewer", CustomMinimumSize = new Vector2(0, 48) };
        assetViewer.Pressed += OpenAssetViewer;
        menu.AddChild(assetViewer);

        var zoneLabel = new Label
        {
            Text = "Zone name",
            Modulate = new Color("cbd5e1"),
        };
        menu.AddChild(zoneLabel);

        _zoneName = new LineEdit
        {
            Text = ZoneLoader.DefaultMapName,
            PlaceholderText = "Converted map directory",
            CustomMinimumSize = new Vector2(0, 44),
            TooltipText = "Directory name below converted/assets/classic-1.1/assets/Maps/",
        };
        _zoneName.TextSubmitted += _ => OpenZoneWalkabout();
        menu.AddChild(_zoneName);

        _onlineMode = new CheckButton
        {
            Text = "Online mode",
            ButtonPressed = false,
            TooltipText = "Join the local authoritative shard instead of moving offline.",
        };
        menu.AddChild(_onlineMode);

        _serverAddress = new LineEdit
        {
            Text = System.Environment.GetEnvironmentVariable("SARNAUT_SERVER_ADDRESS") ?? "127.0.0.1:4242",
            PlaceholderText = "Server host:port",
            CustomMinimumSize = new Vector2(0, 44),
            TooltipText = "QUIC shard endpoint",
        };
        _serverAddress.TextSubmitted += _ => OpenZoneWalkabout();
        menu.AddChild(_serverAddress);

        _zoneError = new Label
        {
            Text = "Enter a converted map name.",
            Modulate = new Color("ef9a9a"),
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        menu.AddChild(_zoneError);

        var walkabout = new Button { Text = "Zone Walkabout", CustomMinimumSize = new Vector2(0, 48) };
        walkabout.Pressed += OpenZoneWalkabout;
        menu.AddChild(walkabout);

        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(0, 48) };
        quit.Pressed += () => GetTree().Quit();
        menu.AddChild(quit);
    }

    private void OpenAssetViewer()
    {
        GetTree().ChangeSceneToFile("res://scenes/asset_viewer.tscn");
    }

    private void OpenZoneWalkabout()
    {
        string mapName = _zoneName.Text.Trim();
        if (string.IsNullOrEmpty(mapName))
        {
            _zoneError.Visible = true;
            _zoneName.GrabFocus();
            return;
        }

        _zoneError.Visible = false;
        ZoneWalkabout.RequestedMapName = mapName;
        ZoneWalkabout.RequestedOnlineMode = _onlineMode.ButtonPressed;
        ZoneWalkabout.RequestedServerAddress = _serverAddress.Text.Trim();
        GetTree().ChangeSceneToFile("res://scenes/zone_walkabout.tscn");
    }
}
