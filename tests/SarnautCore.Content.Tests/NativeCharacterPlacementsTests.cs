using SarnautCore.Content;
using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeCharacterPlacementsTests
{
    private const string Valid = """
        {
          "schema_version": 2,
          "manifest_type": "sarnaut.character-placements",
          "map_id": "inst-league-start",
          "frame": {
            "id": "godot-world-v1",
            "origin_applied": true
          },
          "counts": {
            "cells": 36,
            "authored_rows": 1,
            "resolved_rows": 1,
            "unresolved_rows": 0
          },
          "presentation_spawn": {
            "position": [321.8298, 156.142, -5793.858],
            "rotation": [0.0, 0.0, 0.0, 1.0]
          },
          "placements": [
            {
              "spawn_id": "inst-league-start.000-020.1-2.3",
              "character_key": "mob.inst-league1.rat.rat1-1",
              "position": [316.46, 55.65, -5768.1],
              "rotation": [0.0, 0.0, 0.0, 1.0]
            }
          ]
        }
        """;

    [Fact]
    public void ParsesWorldSpacePlacementsInManifestOrder()
    {
        NativeCharacterPlacements parsed = NativeCharacterPlacements.Parse(
            Valid,
            "inst-league-start");

        NativeCharacterPlacement placement = Assert.Single(parsed.Placements);
        Assert.Equal(36, parsed.CellCount);
        Assert.Equal(321.8298f, parsed.PresentationSpawn.PositionX);
        Assert.Equal(156.142f, parsed.PresentationSpawn.PositionY);
        Assert.Equal(-5793.858f, parsed.PresentationSpawn.PositionZ);
        Assert.Equal("inst-league-start.000-020.1-2.3", placement.SpawnId);
        Assert.Equal("mob.inst-league1.rat.rat1-1", placement.CharacterKey);
        Assert.Equal(316.46f, placement.PositionX);
        Assert.Equal(-5768.1f, placement.PositionZ);
        Assert.Equal(1.0f, placement.RotationW);
    }

    [Theory]
    [InlineData("visual_ref")]
    [InlineData("provenance")]
    [InlineData("source")]
    [InlineData("skmesh")]
    public void RejectsSourceAndConversionFields(string field)
    {
        string corrupted = Valid.Replace(
            "\"character_key\": \"mob.inst-league1.rat.rat1-1\",",
            $"\"character_key\": \"mob.inst-league1.rat.rat1-1\", \"{field}\": \"forbidden\",",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(corrupted, "inst-league-start"));
        Assert.Contains("unsupported property", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnresolvedRows()
    {
        string corrupted = Valid.Replace(
            "\"unresolved_rows\": 0",
            "\"unresolved_rows\": 1",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(corrupted, "inst-league-start"));
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMapMismatch()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(Valid, "another-map"));
        Assert.Contains("contract mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsWrongSchemaVersion()
    {
        string corrupted = Valid.Replace(
            "\"schema_version\": 2",
            "\"schema_version\": 1",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(corrupted, "inst-league-start"));
        Assert.Contains("contract mismatch", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("legacy-world-v0", "true")]
    [InlineData("godot-world-v1", "false")]
    public void RejectsWrongFrame(string frameId, string originApplied)
    {
        string corrupted = Valid
            .Replace("godot-world-v1", frameId, StringComparison.Ordinal)
            .Replace("\"origin_applied\": true", $"\"origin_applied\": {originApplied}", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(corrupted, "inst-league-start"));
        Assert.Contains("frame mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonUnitPresentationSpawnRotation()
    {
        string corrupted = Valid.Replace(
            "\"rotation\": [0.0, 0.0, 0.0, 1.0]",
            "\"rotation\": [0.0, 0.0, 0.0, 2.0]",
            StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeCharacterPlacements.Parse(corrupted, "inst-league-start"));
        Assert.Contains("not a unit quaternion", error.Message, StringComparison.Ordinal);
    }
}
