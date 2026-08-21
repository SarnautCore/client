using SarnautCore.Content;
using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeStaticBakeTests
{
    private const string EmptyAggregate = """
        {
          "format": "sarnaut-native-statics-v2",
          "schema_version": 2,
          "map": "EmptyMap",
          "zone": "EmptyZone",
          "frame": {
            "id": "godot-world-v1",
            "coordinate_scope": "world",
            "origin_applied": true
          },
          "cell_policy": "nonempty_placements_only",
          "report": {
            "cells": 0,
            "placements": 0,
            "visual": 0,
            "non_visual": 0,
            "unresolved": 0,
            "point_lights": 0,
            "anti_lights": 0
          },
          "cells": []
        }
        """;

    private const string Aggregate = """
        {
          "format": "sarnaut-native-statics-v2",
          "schema_version": 2,
          "map": "Inst_LeagueStart",
          "zone": "InstLeague1",
          "frame": {
            "id": "godot-world-v1",
            "coordinate_scope": "world",
            "origin_applied": true
          },
          "cell_policy": "nonempty_placements_only",
          "report": {
            "cells": 1,
            "placements": 2,
            "visual": 1,
            "non_visual": 1,
            "unresolved": 0,
            "point_lights": 0,
            "anti_lights": 0
          },
          "cells": [
            {
              "order": 0,
              "cell": { "sector": [0, 20], "tile": [1, 2] },
              "placements": "placements/000_020__1_2.json",
              "report": {
                "placements": 2,
                "visual": 1,
                "non_visual": 1,
                "unresolved": 0,
                "point_lights": 0,
                "anti_lights": 0
              }
            }
          ]
        }
        """;

    private const string Cell = """
        {
          "format": "sarnaut-native-statics-v1",
          "map": "Inst_LeagueStart",
          "zone": "InstLeague1",
          "cell": { "sector": [0, 20], "tile": [1, 2] },
          "frame": { "id": "godot-world-v1", "origin_applied": true },
          "placements": [
            {
              "order": 0,
              "name": "Rock_000",
              "classification": "visual",
              "scene": "../scenes/Rock.tscn",
              "position": [311.0, 64.0, -5788.0],
              "rotation": [0.0, 0.0, 0.0, 1.0],
              "scale": 1.0,
              "collision": true,
              "visual": true
            },
            {
              "order": 1,
              "name": "Portal_001",
              "classification": "invisible_portal",
              "position": [300.0, 0.0, -5800.0],
              "rotation": [0.0, 0.0, 0.0, 1.0],
              "scale": 1.0,
              "collision": false,
              "visual": false,
              "nonvisual_reason": "invisible_portal"
            }
          ]
        }
        """;

    [Fact]
    public void ParsesCompleteInventoryAndNormalizesScenePaths()
    {
        NativeStaticBake bake = Parse(Aggregate, Cell);

        NativeStaticCell cell = Assert.Single(bake.Cells);
        Assert.Equal(2, bake.PlacementCount);
        Assert.Equal(1, bake.VisualCount);
        Assert.Equal(1, bake.NonVisualCount);
        Assert.Equal(new NativeStaticCellKey(0, 20, 1, 2), cell.Key);
        Assert.Equal("placements/000_020__1_2.json", cell.ManifestPath);
        Assert.Equal("scenes/Rock.tscn", cell.Placements[0].ScenePath);
    }

    [Fact]
    public void AcceptsExplicitlyCompleteMapWithNoStatics()
    {
        NativeStaticBake bake = NativeStaticBake.Parse(
            EmptyAggregate,
            "EmptyMap",
            _ => throw new InvalidOperationException("No cell manifest should be read."));

        Assert.Empty(bake.Cells);
        Assert.Equal(0, bake.PlacementCount);
    }

    [Fact]
    public void RejectsMalformedAggregateJson()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse("{ not-json", Cell));

        Assert.Contains("invalid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingDeclaredCellManifest()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeStaticBake.Parse(Aggregate, "Inst_LeagueStart", _ => null));

        Assert.Contains("cell manifest is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAggregateWithPartialCellInventory()
    {
        string partial = Aggregate.Replace(
            "\"cells\": 1",
            "\"cells\": 2",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(partial, Cell));

        Assert.Contains("declares 2 cells but carries 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnresolvedAggregateReport()
    {
        string unresolved = Aggregate.Replace(
            "\"unresolved\": 0",
            "\"unresolved\": 1",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(unresolved, Cell));

        Assert.Contains("partial or unresolved", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCellReportThatDoesNotMatchItsRows()
    {
        string wrongReport = ReplaceLast(
            ReplaceLast(Aggregate, "\"visual\": 1", "\"visual\": 2"),
            "\"non_visual\": 1",
            "\"non_visual\": 0");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(wrongReport, Cell));

        Assert.Contains("cell report does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonvisualCollisionWithoutNativeScene()
    {
        string missingCollisionScene = Cell.Replace(
            "\"collision\": false",
            "\"collision\": true",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(Aggregate, missingCollisionScene));

        Assert.Contains("native collision scene", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsCollisionOnlyPlacementWithNativeScene()
    {
        string collisionOnly = Cell
            .Replace("\"collision\": false", "\"collision\": true", StringComparison.Ordinal)
            .Replace("\"classification\": \"invisible_portal\"", "\"classification\": \"collision_only\"", StringComparison.Ordinal)
            .Replace("\"nonvisual_reason\": \"invisible_portal\"", "\"nonvisual_reason\": \"collision_only\"", StringComparison.Ordinal)
            .Replace(
                "\"position\": [300.0, 0.0, -5800.0],",
                "\"scene\": \"../scenes/Collision.tscn\", \"position\": [300.0, 0.0, -5800.0],",
                StringComparison.Ordinal);

        NativeStaticBake bake = Parse(Aggregate, collisionOnly);

        NativeStaticPlacement placement = bake.Cells[0].Placements[1];
        Assert.Equal("collision_only", placement.Classification);
        Assert.True(placement.Collision);
        Assert.Equal("scenes/Collision.tscn", placement.ScenePath);
    }

    [Fact]
    public void ParsesCompiledRuntimeScenesWithoutPlainSceneFields()
    {
        string compiled = Cell
            .Replace(
                "\"scene\": \"../scenes/Rock.tscn\"",
                "\"runtime_scene\": \"../scenes/Rock.scn\"",
                StringComparison.Ordinal)
            .Replace("\"collision\": false", "\"collision\": true", StringComparison.Ordinal)
            .Replace(
                "\"classification\": \"invisible_portal\"",
                "\"classification\": \"collision_only\"",
                StringComparison.Ordinal)
            .Replace(
                "\"nonvisual_reason\": \"invisible_portal\"",
                "\"nonvisual_reason\": \"collision_only\"",
                StringComparison.Ordinal)
            .Replace(
                "\"position\": [300.0, 0.0, -5800.0],",
                "\"runtime_scene\": \"../scenes/Collision.scn\", \"position\": [300.0, 0.0, -5800.0],",
                StringComparison.Ordinal);

        NativeStaticBake bake = Parse(Aggregate, compiled);

        Assert.Equal("scenes/Rock.scn", bake.Cells[0].Placements[0].ScenePath);
        Assert.Equal("scenes/Collision.scn", bake.Cells[0].Placements[1].ScenePath);
    }

    [Fact]
    public void RuntimeSceneWinsOverAValidPlainScene()
    {
        string compiled = Cell.Replace(
            "\"scene\": \"../scenes/Rock.tscn\"",
            "\"scene\": \"../scenes/Rock.tscn\", \"runtime_scene\": \"../scenes/Rock.scn\"",
            StringComparison.Ordinal);

        NativeStaticBake bake = Parse(Aggregate, compiled);

        Assert.Equal("scenes/Rock.scn", bake.Cells[0].Placements[0].ScenePath);
    }

    [Theory]
    [InlineData("../scenes/Rock.tscn")]
    [InlineData("../../outside/Rock.scn")]
    [InlineData("")]
    public void RejectsInvalidCompiledRuntimeScene(string runtimeScene)
    {
        string compiled = Cell.Replace(
            "\"scene\": \"../scenes/Rock.tscn\"",
            $"\"runtime_scene\": \"{runtimeScene}\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Parse(Aggregate, compiled));
    }

    [Fact]
    public void RuntimeSceneDoesNotHideAnInvalidPlainScene()
    {
        string compiled = Cell.Replace(
            "\"scene\": \"../scenes/Rock.tscn\"",
            "\"scene\": \"../../outside/Rock.tscn\", \"runtime_scene\": \"../scenes/Rock.scn\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Parse(Aggregate, compiled));
    }

    [Fact]
    public void RejectsUnsupportedSourceField()
    {
        string coupled = Cell.Replace(
            "\"scene\": \"../scenes/Rock.tscn\",",
            "\"scene\": \"../scenes/Rock.tscn\", \"source\": \"legacy.xdb\",",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(Aggregate, coupled));

        Assert.Contains("property 'source'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsScenePathThatEscapesStaticsDirectory()
    {
        string escaping = Cell.Replace(
            "../scenes/Rock.tscn",
            "../../outside/Rock.tscn",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Parse(Aggregate, escaping));

        Assert.Contains("escapes the statics directory", error.Message, StringComparison.Ordinal);
    }

    private static NativeStaticBake Parse(string aggregate, string cell) =>
        NativeStaticBake.Parse(
            aggregate,
            "Inst_LeagueStart",
            path => path == "placements/000_020__1_2.json" ? cell : null);

    private static string ReplaceLast(string source, string oldValue, string newValue)
    {
        int index = source.LastIndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture does not contain '{oldValue}'.");
        return source.Remove(index, oldValue.Length).Insert(index, newValue);
    }
}
