using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Content;
using SarnautCore.Networking;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// Exercises the exact online entity model path against one real League rat.
/// A pass means the manifest resolves, the runtime skin is bound, and the idle
/// clip changes the skeleton instead of leaving the creature in its bind pose.
/// </summary>
public partial class ConvertedModelAnimationSmoke : Node3D
{
    private static string NativeRoot => NativeContentSettings.NativeRoot;

    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        var catalog = new EntityModelCatalog();
        CheckPlayerModelMappings(catalog);
        string[] contentIds = ReadManifestContentIds().ToArray();
        foreach (string contentId in contentIds)
        {
            Expect(catalog.TryResolve(contentId, out _), $"{contentId}: manifest path resolves to a loadable scene");
        }

        var visuals = new List<(string ContentId, NetworkEntityVisual Visual)>();
        ulong entityId = 2;
        foreach (string contentId in contentIds)
        {
            var visual = new NetworkEntityVisual { Name = $"Entity_{entityId}", EntityId = entityId };
            AddChild(visual);
            visual.Bind(Sample(entityId, contentId), catalog);
            visuals.Add((contentId, visual));
            entityId++;
        }

        var playerCharacter = new CharacterRig
        {
            Name = "PlayerCharacter",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
        };
        PlayerCharacterModel.Apply(playerCharacter, catalog, CuratedOption());
        AddChild(playerCharacter);
        playerCharacter.Load();
        Expect(
            playerCharacter.ScenePath.Contains("chargen.league.warrior", StringComparison.OrdinalIgnoreCase),
            $"local player uses the selected chargen appearance, got '{playerCharacter.ScenePath}'");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        int loaded = 0;
        int deformed = 0;
        foreach ((string contentId, NetworkEntityVisual visual) in visuals)
        {
            if (CheckCharacter(contentId, visual.GetNodeOrNull<CharacterRig>("Model"), visual.HasModel))
            {
                loaded++;
                deformed++;
            }

            Expect(visual.GetNodeOrNull<MeshInstance3D>("Capsule") == null,
                $"{contentId}: no unexpected capsule fallback is present");
        }

        CheckNetworkAnimationStates(visuals.Single(entry =>
            entry.ContentId.Equals("mob.inst-league1.rat.rat1-1", StringComparison.OrdinalIgnoreCase)).Visual);

        bool playerDeforms = CheckCharacter("local-player", playerCharacter, playerCharacter.HasModel);
        CheckCuratedWarriorAppearance(playerCharacter);
        CheckMalformedRootLifecycle();
        if (playerCharacter.HasModel)
        {
            loaded++;
        }

        if (playerDeforms)
        {
            deformed++;
        }

        bool passed = _failures.Count == 0;
        GD.Print(
            $"CONVERTED_MODEL_ANIMATION cases={contentIds.Length + 1} loaded={loaded} "
            + $"deformed={deformed} catalog={contentIds.Length} unresolved={catalog.Unresolved.Count} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"CONVERTED_MODEL_ANIMATION {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private void CheckMalformedRootLifecycle()
    {
        var rig = new CharacterRig
        {
            Name = "MalformedRootRig",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
            ScenePath = "res://tests/fixtures/native-character-content/malformed-root.tscn",
        };
        AddChild(rig);
        Expect(!rig.Load(), "a native character scene with a non-Node3D root is rejected");
        Expect(rig.Model == null, "a rejected native character scene leaves no model wrapper behind");
        RemoveChild(rig);
        rig.Free();
    }

    private bool CheckCharacter(string contentId, CharacterRig? character, bool hasModel)
    {
        Skeleton3D? skeleton = character?.Model == null ? null : FindDescendant<Skeleton3D>(character.Model);
        AnimationPlayer? player = character?.Model == null ? null : FindDescendant<AnimationPlayer>(character.Model);
        MeshInstance3D? skinnedMesh = skeleton == null ? null : FindBoundMesh(skeleton);

        Expect(hasModel, $"{contentId}: catalog entity loads a native model");
        Expect(character?.HasModel == true, $"{contentId}: character assembly succeeds: {character?.LastError}");
        Expect(skeleton?.GetBoneCount() > 0, $"{contentId}: model has a skeleton");
        Expect(skinnedMesh?.Skin?.GetBindCount() > 0, $"{contentId}: runtime mesh has a skin bound to skeleton bones");
        Expect(player != null, $"{contentId}: model has an AnimationPlayer");
        Expect(!string.IsNullOrEmpty(character?.ActiveClip),
            $"{contentId}: an idle/default clip is selected");
        Expect(player?.IsPlaying() == true, $"{contentId}: idle playback is active");

        bool deforms = false;
        if (skeleton != null && player != null)
        {
            player.Seek(0.0, update: true);
            Transform3D[] first = CapturePose(skeleton);
            player.Seek(0.5, update: true);
            Transform3D[] second = CapturePose(skeleton);
            deforms = PoseChanged(first, second);
        }

        Expect(deforms, $"{contentId}: idle clip changes at least one skeleton bone pose");
        return hasModel && character?.HasModel == true && skeleton != null
            && skinnedMesh?.Skin?.GetBindCount() > 0 && player?.IsPlaying() == true && deforms;
    }

    private void CheckNetworkAnimationStates(NetworkEntityVisual visual)
    {
        CharacterRig? character = visual.GetNodeOrNull<CharacterRig>("Model");
        visual.Apply(Sample(visual.EntityId, "mob.inst-league1.rat.rat1-1") with { AnimationState = AnimationState.Moving });
        Expect(character?.ActiveClip.Equals("run", StringComparison.OrdinalIgnoreCase) == true,
            $"network moving state selects run, got '{character?.ActiveClip}'");
        Expect(FindDescendant<AnimationPlayer>(character!.Model!)?.IsPlaying() == true,
            "network moving state keeps animation playback active");
        Expect(visual.PlayAttack(), "network combat event selects an attack clip");
        Expect(character.ActiveClip.Contains("attack", StringComparison.OrdinalIgnoreCase),
            $"attack event plays attack, got '{character.ActiveClip}'");
        Expect(visual.PlayHit(), "network damage event selects a hit clip");
        Expect(character.ActiveClip.Contains("hit", StringComparison.OrdinalIgnoreCase),
            $"hit event plays hit, got '{character.ActiveClip}'");
        Expect(visual.PlayDeath(), "network death event selects a death clip");
        Expect(character.ActiveClip.Contains("death", StringComparison.OrdinalIgnoreCase),
            $"death event plays death, got '{character.ActiveClip}'");
    }

    private static SampledEntity Sample(ulong entityId, string contentId) => new(
        entityId,
        EntityKind.Npc,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        AnimationState.Idle,
        contentId,
        "Rat1_1_Name.txt",
        2,
        "faction.wild",
        30,
        60,
        true);

    private static ChargenOption CuratedOption() => new(
        "chargen.league.warrior",
        "race.kania",
        "class.warrior",
        "female",
        "faction.league",
        "M2.Chargen.LeagueWarrior.Name",
        "M2.Chargen.LeagueWarrior.Description",
        "unused-by-native-runtime",
        "zone.inst-league1",
        1,
        113.0f,
        170.5f,
        156.293f);

    private void CheckPlayerModelMappings(EntityModelCatalog catalog)
    {
        (string Race, string Sex, string Key)[] cases =
        [
            ("race.elf", "female", "player.elf.female"),
            ("race.elf", "male", "player.elf.male"),
            ("race.gibberling", "female", "player.gibberling.female"),
            ("race.gibberling", "male", "player.gibberling.male"),
            ("race.kania", "female", "player.kania.female"),
            ("race.kania", "male", "player.kania.male"),
        ];

        foreach ((string race, string sex, string expected) in cases)
        {
            Expect(catalog.TryResolvePlayer(expected, out EntityModel model),
                $"{race}/{sex}: native player key '{expected}' resolves");
            Expect(model.Scale == 1.0f, $"{race}/{sex}: baked scale stays inside the scene");
        }

        Expect(catalog.TryResolvePlayer(PlayerCharacterModel.DefaultCharacterKey, out _),
            "the dressed League warrior chargen key resolves");
    }

    private void CheckCuratedWarriorAppearance(CharacterRig player)
    {
        var manifest = new NativeCharacterManifestReader();
        bool resolved = manifest.TryResolve("chargen.league.warrior", out NativeCharacterModel model);
        Expect(resolved, "the native manifest resolves the selected League warrior");
        if (resolved)
        {
            Expect(player.ScenePath.Equals(
                    manifest.ResolveScenePath(model),
                    StringComparison.OrdinalIgnoreCase),
                $"local player loads the manifest scene, got '{player.ScenePath}'");

            NativeCharacterLod? lod = model.Lod;
            Expect(lod != null, "the selected League warrior declares authored LOD ranges");
            if (player.Model != null && lod != null)
            {
                try
                {
                    IReadOnlyList<MeshInstance3D> levels = NativeCharacterLodContract.Inspect(
                        player.Model,
                        lod);
                    Expect(levels.Count == lod.Levels,
                        $"local player loads all {lod.Levels} authored body LODs");
                }
                catch (Exception exception)
                {
                    Expect(false, $"local player satisfies the native LOD contract: {exception.Message}");
                }
            }
        }

        Expect(NativeCharacterLodContract.HasAttachment(
                player.Model,
                "Attach_Mace_1H_Club_A_01",
                "Slot_Hand_R"),
            "local player equips the starting mace on Slot_Hand_R");
        Expect(NativeCharacterLodContract.HasAttachment(
                player.Model,
                "Attach_Shield_1H_Simple_A_01",
                "Slot_Shield_Hand"),
            "local player equips the starting shield on Slot_Shield_Hand");
    }

    private static IEnumerable<string> ReadManifestContentIds()
    {
        string json = FileAccess.GetFileAsString($"{NativeRoot}/{NativeCharacterManifestReader.RelativeManifestPath}");
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty model in document.RootElement.GetProperty("characters").EnumerateObject())
        {
            if (model.Value.GetProperty("kind").GetString() == "mob")
            {
                yield return model.Name;
            }
        }
    }

    private static Transform3D[] CapturePose(Skeleton3D skeleton)
    {
        var poses = new Transform3D[skeleton.GetBoneCount()];
        for (int bone = 0; bone < poses.Length; bone++)
        {
            poses[bone] = new Transform3D(
                new Basis(skeleton.GetBonePoseRotation(bone)).Scaled(skeleton.GetBonePoseScale(bone)),
                skeleton.GetBonePosePosition(bone));
        }

        return poses;
    }

    private static bool PoseChanged(IReadOnlyList<Transform3D> first, IReadOnlyList<Transform3D> second)
    {
        for (int bone = 0; bone < first.Count; bone++)
        {
            if (!first[bone].IsEqualApprox(second[bone]))
            {
                return true;
            }
        }

        return false;
    }

    private static MeshInstance3D? FindBoundMesh(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is MeshInstance3D { Visible: true, Skin: not null } mesh)
            {
                return mesh;
            }

            MeshInstance3D? descendant = FindBoundMesh(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(Node parent) where T : Node
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }
}
