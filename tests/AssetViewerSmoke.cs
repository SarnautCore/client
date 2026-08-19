using Godot;

namespace SarnautCore.Tests;

public partial class AssetViewerSmoke : Node
{
    private const string TexturePath = "res://converted/samples/slot_pressed.png";
    private const string MeshPath = "res://converted/samples/stone.skmesh";

    public override async void _Ready()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://scenes/asset_viewer.tscn");
        var viewer = scene?.Instantiate<SarnautCore.AssetViewer>();
        if (viewer == null)
        {
            Fail("Asset Viewer scene did not instantiate.");
            return;
        }

        AddChild(viewer);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        bool textureLoaded = viewer.PreviewAsset(TexturePath)
            && viewer.CurrentPreviewKind == "Texture2D";
        bool meshLoaded = viewer.PreviewAsset(MeshPath)
            && viewer.CurrentPreviewKind == "Converted skinned mesh";

        if (!textureLoaded || !meshLoaded)
        {
            Fail($"Preview results: texture={textureLoaded}, mesh={meshLoaded}");
            return;
        }

        GD.Print("ASSET_VIEWER_SMOKE_OK texture=Texture2D mesh=ConvertedSkinnedMesh");
        GetTree().Quit(0);
    }

    private void Fail(string message)
    {
        GD.PushError($"ASSET_VIEWER_SMOKE_FAILED {message}");
        GetTree().Quit(1);
    }
}
