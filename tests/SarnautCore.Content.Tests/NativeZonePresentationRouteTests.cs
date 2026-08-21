using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeZonePresentationRouteTests
{
    private const string Root = "res://content/league-slice";

    [Fact]
    public void Canonical_map_and_zone_route_to_their_exact_native_directory()
    {
        bool valid = NativeZonePresentationRoute.TryCreate(
            Root,
            "inst-league-start",
            "inst-league1",
            out var route,
            out string error);

        Assert.True(valid, error);
        Assert.Equal("inst-league-start", route.MapId);
        Assert.Equal("inst-league1", route.ZoneId);
        Assert.Equal(
            "res://content/league-slice/maps/inst-league-start/zones/inst-league1",
            route.DirectoryPath);
        Assert.Equal($"{route.DirectoryPath}/zone-presentation.json", route.ManifestPath);
    }

    [Theory]
    [InlineData("Inst_LeagueStart")]
    [InlineData("inst_league_start")]
    [InlineData("inst/league-start")]
    [InlineData("inst\\league-start")]
    [InlineData("C:league-start")]
    [InlineData("../inst-league-start")]
    [InlineData("inst-..-league-start")]
    [InlineData("-inst-league-start")]
    [InlineData("inst-league-start-")]
    [InlineData("inst--league-start")]
    [InlineData("inst-league-start ")]
    [InlineData("")]
    public void Noncanonical_map_ids_are_rejected(string mapId)
    {
        Assert.False(NativeZonePresentationRoute.TryCreate(
            Root,
            mapId,
            "inst-league1",
            out var route,
            out _));
        Assert.Null(route);
    }

    [Theory]
    [InlineData("InstLeague1")]
    [InlineData("zone.inst-league1")]
    [InlineData("../inst-league1")]
    [InlineData("inst/league1")]
    [InlineData("inst\\league1")]
    [InlineData("C:inst-league1")]
    [InlineData("inst-league1.")]
    [InlineData("")]
    public void Noncanonical_zone_ids_are_rejected(string zoneId)
    {
        Assert.False(NativeZonePresentationRoute.TryCreate(
            Root,
            "inst-league-start",
            zoneId,
            out var route,
            out _));
        Assert.Null(route);
    }

    [Theory]
    [InlineData("zone-presentation.scn")]
    [InlineData("compiled/zone-presentation.scn")]
    [InlineData("zone-presentation.tscn")]
    public void Native_scene_references_stay_below_the_exact_zone_directory(string relativeScene)
    {
        NativeZonePresentationRoute.TryCreate(
            Root,
            "inst-league-start",
            "inst-league1",
            out var route,
            out _);

        Assert.True(route.TryResolveScenePath(relativeScene, out string scenePath, out string error), error);
        Assert.Equal($"{route.DirectoryPath}/{relativeScene}", scenePath);
    }

    [Theory]
    [InlineData("../inst-league10/zones/inst-league1/zone-presentation.scn")]
    [InlineData("../../inst-league-start-other/zone-presentation.scn")]
    [InlineData("res://content/league-slice-other/zone-presentation.scn")]
    [InlineData("compiled\\zone-presentation.scn")]
    [InlineData("C:zone-presentation.scn")]
    [InlineData("./zone-presentation.scn")]
    public void Traversal_absolute_and_sibling_prefix_scene_references_are_rejected(string relativeScene)
    {
        NativeZonePresentationRoute.TryCreate(
            Root,
            "inst-league-start",
            "inst-league1",
            out var route,
            out _);

        Assert.False(route.TryResolveScenePath(relativeScene, out string scenePath, out _));
        Assert.Empty(scenePath);
    }

    [Theory]
    [InlineData("res://content/league-slice/../league-slice")]
    [InlineData("res://content/league-slice-other/../league-slice")]
    [InlineData("res://content/league-slice/maps/.")]
    [InlineData("res://content/league-slice/maps\\native")]
    public void Unsafe_native_roots_are_rejected(string root)
    {
        Assert.False(NativeZonePresentationRoute.TryCreate(
            root,
            "inst-league-start",
            "inst-league1",
            out var route,
            out _));
        Assert.Null(route);
    }
}
