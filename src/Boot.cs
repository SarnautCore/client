using Godot;

namespace SarnautCore;

public partial class Boot : Control
{
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

        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(0, 48) };
        quit.Pressed += () => GetTree().Quit();
        menu.AddChild(quit);
    }

    private void OpenAssetViewer()
    {
        GetTree().ChangeSceneToFile("res://scenes/asset_viewer.tscn");
    }
}
