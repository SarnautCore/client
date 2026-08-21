using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class CameraCenteredSkySourceTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "CameraCenteredSky.cs"));

    [Fact]
    public void Sky_follows_the_active_camera_without_changing_rotation_or_scale()
    {
        Assert.Contains("override void _Process", Source, StringComparison.Ordinal);
        Assert.Contains("GetViewport()?.GetCamera3D()", Source, StringComparison.Ordinal);
        Assert.Contains("camera is not null", Source, StringComparison.Ordinal);
        Assert.Contains("GlobalPosition = camera.GlobalPosition", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalRotation", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalTransform =", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Scale =", Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Converted")]
    [InlineData("Allods")]
    [InlineData("Xdb")]
    [InlineData("SkyMesh")]
    [InlineData("ResourceTree")]
    [InlineData("System.Xml")]
    [InlineData("ResourceLoader")]
    [InlineData("FileAccess")]
    [InlineData("PackedScene")]
    public void Script_has_no_source_format_awareness(string forbiddenTerm)
    {
        Assert.DoesNotContain(forbiddenTerm, Source, StringComparison.OrdinalIgnoreCase);
    }
}
