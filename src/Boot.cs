using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The development hub: the way into the session shell, and the two offline
/// tools that do not need a server.
/// </summary>
/// <remarks>
/// The layout is <c>scenes/boot.tscn</c>. This file used to build a hundred and
/// twenty-five lines of Controls in <c>_Ready</c>, which meant every styling
/// question was a C# edit and nothing was visible in the editor. Scenes are
/// declarative; scripts wire them.
/// </remarks>
public partial class Boot : Control
{
    private SessionHost _session = null!;
    private LineEdit _zoneName = null!;
    private Label _message = null!;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _zoneName = GetNode<LineEdit>("%ZoneName");
        _message = GetNode<Label>("%Message");

        _zoneName.Text = _session.Zone.MapName;
        _zoneName.TextSubmitted += _ => WalkOffline();
        _message.AddThemeColorOverride("font_color", UiTheme.ErrorInk);
        _message.Visible = false;

        GetNode<Button>("%Play").Pressed += Play;
        GetNode<Button>("%AssetViewer").Pressed += () =>
            GetTree().ChangeSceneToFile("res://scenes/asset_viewer.tscn");
        GetNode<Button>("%Walkabout").Pressed += WalkOffline;
        GetNode<Button>("%Quit").Pressed += () => GetTree().Quit();
        GetNode<Label>("%Theme").Text = $"theme: {UiTheme.Source}";
    }

    private void Play()
    {
        _session.Flow.BeginSignIn();
        _session.Show(Screen.Login);
    }

    private void WalkOffline()
    {
        string mapName = _zoneName.Text.Trim();
        if (string.IsNullOrEmpty(mapName))
        {
            _message.Text = "Enter a native map name.";
            _message.Visible = true;
            _zoneName.GrabFocus();
            return;
        }

        _message.Visible = false;
        // Offline is a tool, not a game session: no account, no ticket, no
        // shard. It reads the same baked native content as the session path.
        _session.Zone = ZoneRequest.Offline(mapName, _session.Zone.ZoneId);
        GetTree().ChangeSceneToFile("res://scenes/zone_walkabout.tscn");
    }
}
