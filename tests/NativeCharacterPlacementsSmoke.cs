using System;
using System.Linq;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

/// <summary>Pins the native offline NPC placement route and its exact counters.</summary>
public partial class NativeCharacterPlacementsSmoke : Node
{
    private const int ExpectedPlacements = 24;

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Node3D root = loader.GetNode<Node3D>("NpcCharacters");
        CharacterRig[] characters = root.GetChildren().OfType<CharacterRig>().ToArray();
        string mapId = MapNameTransform.ToKebabCase(loader.MapName);
        string path = $"{NativeContentSettings.NativeRoot}/maps/{mapId}/character-placements.json";
        NativeCharacterPlacements manifest = NativeCharacterPlacements.Parse(
            FileAccess.GetFileAsString(path),
            mapId);
        var expectedSpawn = new Vector3(
            manifest.PresentationSpawn.PositionX,
            manifest.PresentationSpawn.PositionY,
            manifest.PresentationSpawn.PositionZ);
        var expectedSpawnRotation = new Quaternion(
            manifest.PresentationSpawn.RotationX,
            manifest.PresentationSpawn.RotationY,
            manifest.PresentationSpawn.RotationZ,
            manifest.PresentationSpawn.RotationW);

        int transformMismatches = 0;
        int metadataMismatches = 0;
        int incomplete = 0;
        for (int index = 0; index < Math.Min(characters.Length, manifest.Placements.Count); index++)
        {
            CharacterRig character = characters[index];
            NativeCharacterPlacement placement = manifest.Placements[index];
            var expectedPosition = new Vector3(
                placement.PositionX,
                placement.PositionY,
                placement.PositionZ);
            var expectedRotation = new Quaternion(
                placement.RotationX,
                placement.RotationY,
                placement.RotationZ,
                placement.RotationW);
            if (!character.Position.IsEqualApprox(expectedPosition)
                || MathF.Abs(character.Quaternion.Dot(expectedRotation)) < 0.99999f)
            {
                transformMismatches++;
            }

            string scene = character.GetMeta("native_scene", string.Empty).AsString();
            if (character.GetMeta("native_spawn_id", string.Empty).AsString() != placement.SpawnId
                || character.GetMeta("native_character_key", string.Empty).AsString() != placement.CharacterKey
                || !scene.StartsWith($"{NativeContentSettings.NativeRoot}/characters/", StringComparison.Ordinal)
                || character.HasMeta("source")
                || character.HasMeta("provenance")
                || character.HasMeta("visual_ref")
                || character.GetMetaList().Any(name =>
                    name.ToString().StartsWith("allods_", StringComparison.OrdinalIgnoreCase)))
            {
                metadataMismatches++;
            }

            if (!character.HasModel || character.SkeletonBoneCount <= 0 || character.ClipCount <= 0)
            {
                incomplete++;
            }
        }

        bool passed = manifest.Placements.Count == ExpectedPlacements
            && characters.Length == ExpectedPlacements
            && loader.ServerObjectCount == ExpectedPlacements
            && loader.NpcPlacementCount == ExpectedPlacements
            && loader.NativeCharacterPlacementCount == ExpectedPlacements
            && loader.NpcVisualCount == ExpectedPlacements
            && loader.NativeCharacterVisualCount == ExpectedPlacements
            && loader.NpcPlaceholderCount == 0
            && loader.NpcModelFailures.Count == 0
            && loader.SuggestedSpawnPosition.IsEqualApprox(expectedSpawn)
            && MathF.Abs(loader.SuggestedSpawnRotation.Dot(expectedSpawnRotation)) >= 0.99999f
            && transformMismatches == 0
            && metadataMismatches == 0
            && incomplete == 0;

        GD.Print(
            $"NATIVE_CHARACTER_PLACEMENTS manifest={manifest.Placements.Count} "
            + $"placements={loader.NpcPlacementCount} native={loader.NativeCharacterPlacementCount} "
            + $"visuals={loader.NpcVisualCount} native_visuals={loader.NativeCharacterVisualCount} "
            + $"placeholders={loader.NpcPlaceholderCount} transform_mismatches={transformMismatches} "
            + $"metadata_mismatches={metadataMismatches} incomplete={incomplete} "
            + $"presentation_spawn={loader.SuggestedSpawnPosition} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        if (!passed && loader.LastError.Length > 0)
        {
            GD.PushError(loader.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }
}
