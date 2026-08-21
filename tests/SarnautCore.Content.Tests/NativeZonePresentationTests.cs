using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeZonePresentationTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "zone-presentation-manifest.json");

    private static string Fixture => File.ReadAllText(FixturePath);

    [Fact]
    public void Complete_plain_manifest_parses()
    {
        NativeZonePresentation presentation = Parse(Fixture);

        Assert.Equal("inst-league-start", presentation.MapId);
        Assert.Equal("inst-league-1", presentation.ZoneId);
        Assert.Equal("zone-presentation.tscn", presentation.Scene);
        Assert.True(presentation.Topology.CameraCentered);
        Assert.Equal("Environment", presentation.Topology.EnvironmentNode);
        Assert.Equal("Sun", presentation.Topology.SunNode);
        Assert.Equal("Sky", presentation.Topology.SkyRootNode);
        Assert.Equal(3, presentation.Sky.PartCount);
        Assert.Equal(1, presentation.Sky.AnimatedPartCount);
        Assert.Equal("xy", presentation.Sky.ProjectionScaling);
        Assert.Equal(["Backdrop", "Stars", "Clouds"], presentation.Sky.Parts.Select(part => part.Node));
        Assert.Equal([0.8f, 0.4f, 1.0f], presentation.Sky.Parts.Select(part => part.FovFactor));
        Assert.Equal([false, false, true], presentation.Sky.Parts.Select(part => part.Animated));
        Assert.Equal(45.0f / 510.0f, presentation.ProbeColors.Ambient.Red, 6);
        Assert.Equal(58.0f / 510.0f, presentation.ProbeColors.Ambient.Green, 6);
        Assert.Equal(179.0f / 510.0f, presentation.ProbeColors.Ambient.Blue, 6);
        Assert.Equal(70.0f / 255.0f, presentation.ProbeColors.Direct.Red, 6);
        Assert.Equal(30.0f / 255.0f, presentation.ProbeColors.Direct.Green, 6);
        Assert.Equal(0.0f, presentation.ProbeColors.Direct.Blue);
    }

    [Fact]
    public void Compiled_runtime_scene_wins_over_plain_scene()
    {
        string json = Fixture.Replace(
            "\"scene\": \"zone-presentation.tscn\"",
            "\"scene\": \"plain/zone-presentation.tscn\",\n  \"runtime_scene\": \"runtime/zone-presentation.scn\"",
            StringComparison.Ordinal);

        Assert.Equal("runtime/zone-presentation.scn", Parse(json).Scene);
    }

    [Fact]
    public void Compiled_manifest_can_carry_only_runtime_scene()
    {
        string json = Fixture.Replace(
            "\"scene\": \"zone-presentation.tscn\"",
            "\"runtime_scene\": \"zone-presentation.scn\"",
            StringComparison.Ordinal);

        Assert.Equal("zone-presentation.scn", Parse(json).Scene);
    }

    [Theory]
    [InlineData("\"schema_version\": 1", "\"schema_version\": 2")]
    [InlineData("\"manifest_type\": \"sarnaut.zone-presentation\"", "\"manifest_type\": \"other\"")]
    [InlineData("\"manifest_type\": \"sarnaut.zone-presentation\"", "\"manifest_type\": \" sarnaut.zone-presentation \"")]
    [InlineData("\"map_id\": \"inst-league-start\"", "\"map_id\": \"other\"")]
    [InlineData("\"map_id\": \"inst-league-start\"", "\"map_id\": \"inst-league-start \"")]
    [InlineData("\"zone_id\": \"inst-league-1\"", "\"zone_id\": \"other\"")]
    [InlineData("\"zone_id\": \"inst-league-1\"", "\"zone_id\": \" inst-league-1\"")]
    public void Contract_identity_must_match_exactly(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"../zone-presentation.tscn\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"res://zone-presentation.tscn\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"folder\\\\zone-presentation.tscn\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"zone-presentation.tres\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"runtime_scene\": \"zone-presentation.tscn\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"zone-presentation.tscn\",\n  \"runtime_scene\": \"\"")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"zone-presentation.tscn\",\n  \"runtime_scene\": null")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": \"zone-presentation.tscn\",\n  \"runtime_scene\": 7")]
    [InlineData("\"scene\": \"zone-presentation.tscn\"", "\"scene\": 7")]
    public void Scene_reference_must_be_confined_and_native(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"ambient\": [0.0882352941, 0.1137254902, 0.3509803922]", "\"ambient\": [-0.1, 0.1, 0.2]")]
    [InlineData("\"ambient\": [0.0882352941, 0.1137254902, 0.3509803922]", "\"ambient\": [0.1, 1.1, 0.2]")]
    [InlineData("\"ambient\": [0.0882352941, 0.1137254902, 0.3509803922]", "\"ambient\": [0.1, 1.00000001, 0.2]")]
    [InlineData("\"ambient\": [0.0882352941, 0.1137254902, 0.3509803922]", "\"ambient\": [0.1, 0.2]")]
    [InlineData("\"direct\": [0.2745098039, 0.1176470588, 0.0]", "\"direct\": [-1e-100, 0.2, 0.3]")]
    [InlineData("\"direct\": [0.2745098039, 0.1176470588, 0.0]", "\"direct\": [0.1, 0.2, 1e100]")]
    public void Probe_colors_must_be_finite_normalized_rgb(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"part_count\": 3", "\"part_count\": -1")]
    [InlineData("\"animated_part_count\": 1", "\"animated_part_count\": -1")]
    [InlineData("\"part_count\": 3", "\"part_count\": 2")]
    [InlineData("\"animated_part_count\": 1", "\"animated_part_count\": 2")]
    [InlineData("\"projection_scaling\": \"xy\"", "\"projection_scaling\": \"uniform\"")]
    [InlineData("\"projection_scaling\": \"xy\"", "\"projection_scaling\": \"xy \"")]
    [InlineData("\"node\": \"Clouds\",", "\"node\": \"Stars\",")]
    [InlineData("\"node\": \"Clouds\",", "\"node\": \"../Clouds\",")]
    [InlineData("\"node\": \"Backdrop\",", "\"node\": \"%Backdrop\",")]
    [InlineData("\"fov_factor\": 0.8", "\"fov_factor\": 0")]
    [InlineData("\"fov_factor\": 0.8", "\"fov_factor\": 1e100")]
    public void Sky_inventory_must_match_the_native_three_part_contract(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"camera_centered\": true", "\"camera_centered\": false")]
    [InlineData("\"environment_node\": \"Environment\"", "\"environment_node\": \"Other\"")]
    [InlineData("\"environment_node\": \"Environment\"", "\"environment_node\": \" Environment\"")]
    [InlineData("\"sun_node\": \"Sun\"", "\"sun_node\": \"../Sun\"")]
    [InlineData("\"sky_root_node\": \"Sky\"", "\"sky_root_node\": \"res://Sky\"")]
    public void Required_topology_must_match_exactly(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"map_id\": \"inst-league-start\",", "\"map_id\": \"inst-league-start\",\n  \"map_id\": \"inst-league-start\",")]
    [InlineData("\"probe_colors\": {", "\"unsupported\": true,\n  \"probe_colors\": {")]
    [InlineData("\"ambient\": [", "\"extra\": 1,\n    \"ambient\": [")]
    [InlineData("\"node\": \"Backdrop\",", "\"node\": \"Backdrop\",\n        \"node\": \"Backdrop\",")]
    public void Duplicate_and_unknown_properties_are_rejected(string before, string after)
    {
        AssertInvalid(Fixture.Replace(before, after, StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_scene_reference_is_rejected()
    {
        AssertInvalid(Fixture.Replace(
            "  \"scene\": \"zone-presentation.tscn\",\n",
            string.Empty,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Expected_identifiers_must_be_supplied()
    {
        Assert.Throws<ArgumentException>(() => NativeZonePresentation.Parse(Fixture, " ", "inst-league-1"));
        Assert.Throws<ArgumentException>(() => NativeZonePresentation.Parse(Fixture, "inst-league-start", ""));
    }

    private static NativeZonePresentation Parse(string json) =>
        NativeZonePresentation.Parse(json, "inst-league-start", "inst-league-1");

    private static void AssertInvalid(string json) =>
        Assert.Throws<InvalidDataException>(() => Parse(json));
}
