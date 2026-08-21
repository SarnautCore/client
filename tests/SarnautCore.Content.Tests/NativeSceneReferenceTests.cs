using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeSceneReferenceTests
{
    [Theory]
    [InlineData("scene.tscn")]
    [InlineData("scene.scn")]
    [InlineData("folder/scene.SCN")]
    public void Scene_field_accepts_plain_or_compiled_native_scenes(string path)
    {
        Assert.Equal(path, NativeSceneReference.Select(path, null));
    }

    [Fact]
    public void Runtime_scene_wins_over_plain_scene()
    {
        string selected = NativeSceneReference.Select("plain/scene.tscn", "runtime/scene.scn");

        Assert.Equal("runtime/scene.scn", selected);
        Assert.Equal(".scn", NativeSceneReference.Extension(selected));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("scene.tres", null)]
    [InlineData("scene.tscn", "scene.tscn")]
    [InlineData("scene.tscn", "")]
    [InlineData("scene.tscn", "res://scene.scn")]
    [InlineData("scene.tscn", "../scene.scn")]
    [InlineData("scene.tscn", "folder//scene.scn")]
    public void Unsafe_or_unsupported_paths_are_rejected(string? scene, string? runtimeScene)
    {
        Assert.Throws<InvalidDataException>(
            () => NativeSceneReference.Select(scene, runtimeScene));
    }

    [Fact]
    public void Owner_relative_parent_segment_is_an_explicit_loader_choice()
    {
        Assert.Equal(
            "../scenes/object.scn",
            NativeSceneReference.Select(null, "../scenes/object.scn", allowParentSegments: true));
    }
}
