using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeAssetReferenceTests
{
    private const string Root = "res://content/league-slice";

    [Theory]
    [InlineData("res://content/league-slice/maps/tile.scn", NativeAssetKind.Scene)]
    [InlineData("res://content/league-slice/ui/login.tscn", NativeAssetKind.Scene)]
    [InlineData("res://content/league-slice/materials/stone.res", NativeAssetKind.Resource)]
    [InlineData("res://content/league-slice/materials/stone.TRES", NativeAssetKind.Resource)]
    public void Compiled_and_text_Godot_assets_beneath_native_root_are_accepted(
        string path,
        NativeAssetKind expectedKind)
    {
        bool valid = NativeAssetReference.TryCreate(Root, path, out var reference, out string error);

        Assert.True(valid, error);
        Assert.Equal(path, reference.Path);
        Assert.Equal(expectedKind, reference.Kind);
    }

    [Theory]
    [InlineData("res://content/league-slice-other/maps/tile.scn")]
    [InlineData("res://content/league-slice/../private/tile.scn")]
    [InlineData("res://content/league-slice/maps/./tile.scn")]
    [InlineData("res://content/league-slice/maps//tile.scn")]
    [InlineData("res://content/league-slice/maps\\tile.scn")]
    [InlineData("res://content/league-slice/maps/tile.png")]
    [InlineData("res://content/league-slice/maps/tile.skmesh")]
    [InlineData("res://content/league-slice/manifest.json")]
    [InlineData("user://content/league-slice/maps/tile.scn")]
    [InlineData("")]
    public void Outside_unsafe_or_unsupported_asset_paths_are_rejected(string path)
    {
        Assert.False(NativeAssetReference.TryCreate(Root, path, out var reference, out _));
        Assert.Null(reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("res://")]
    [InlineData("user://content/league-slice")]
    [InlineData("res://content/../league-slice")]
    [InlineData("res://content//league-slice")]
    [InlineData("res://content\\league-slice")]
    public void Unsafe_native_roots_are_rejected(string root)
    {
        Assert.False(
            NativeAssetReference.TryCreate(
                root,
                "res://content/league-slice/maps/tile.scn",
                out _,
                out _));
    }

    [Theory]
    [InlineData("scene.scn", NativeAssetKind.Scene)]
    [InlineData("scene.tscn", NativeAssetKind.Scene)]
    [InlineData("mesh.res", NativeAssetKind.Resource)]
    [InlineData("mesh.tres", NativeAssetKind.Resource)]
    public void Supported_file_names_are_classified(string fileName, NativeAssetKind expectedKind)
    {
        Assert.True(NativeAssetReference.IsSupportedFile(fileName));
        Assert.Equal(expectedKind, NativeAssetReference.ExtensionKind(System.IO.Path.GetExtension(fileName)));
    }
}
