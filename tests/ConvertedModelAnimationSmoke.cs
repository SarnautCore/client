using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Sarnaut.Protocol.V1;
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
    private const string ConvertedRoot = "res://converted/assets/classic-1.1";
    private const string CuratedPlayerScene = "res://characters/kania/female-warrior.tscn";

    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        var catalog = new EntityModelCatalog(ConvertedRoot);
        CheckPlayerModelMappings();
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
            visual.Bind(Sample(entityId, contentId), catalog, ConvertedRoot);
            visuals.Add((contentId, visual));
            entityId++;
        }

        var playerCharacter = new ConvertedCharacter
        {
            Name = "PlayerCharacter",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
        };
        PlayerCharacterModel.Apply(playerCharacter, CuratedOption());
        AddChild(playerCharacter);
        playerCharacter.LoadCharacter();
        Expect(
            playerCharacter.CharacterScene == CuratedPlayerScene,
            $"local player uses the selected chargen appearance, got '{playerCharacter.CharacterScene}'");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        int loaded = 0;
        int deformed = 0;
        foreach ((string contentId, NetworkEntityVisual visual) in visuals)
        {
            if (CheckCharacter(contentId, visual.GetNodeOrNull<ConvertedCharacter>("Model"), visual.HasModel))
            {
                loaded++;
                deformed++;
            }

            Expect(visual.GetNodeOrNull<MeshInstance3D>("Capsule") == null,
                $"{contentId}: no unexpected capsule fallback is present");
        }

        CheckNetworkAnimationStates(visuals.Single(entry =>
            entry.ContentId.Equals("mob.inst-league1.rat.rat1-1", StringComparison.OrdinalIgnoreCase)).Visual);

        bool playerDeforms = CheckCharacter("local-player", playerCharacter, playerCharacter.HasConvertedModel);
        CheckCuratedWarriorAppearance(playerCharacter);
        if (playerCharacter.HasConvertedModel)
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

    private bool CheckCharacter(string contentId, ConvertedCharacter? character, bool hasModel)
    {
        Skeleton3D? skeleton = character?.Model == null ? null : FindDescendant<Skeleton3D>(character.Model);
        AnimationPlayer? player = character?.Model == null ? null : FindDescendant<AnimationPlayer>(character.Model);
        MeshInstance3D? skinnedMesh = skeleton == null ? null : FindBoundMesh(skeleton);

        Expect(hasModel, $"{contentId}: catalog entity loads a converted model");
        Expect(character?.HasConvertedModel == true, $"{contentId}: character assembly succeeds: {character?.LastError}");
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
        return hasModel && character?.HasConvertedModel == true && skeleton != null
            && skinnedMesh?.Skin?.GetBindCount() > 0 && player?.IsPlaying() == true && deforms;
    }

    private void CheckNetworkAnimationStates(NetworkEntityVisual visual)
    {
        ConvertedCharacter? character = visual.GetNodeOrNull<ConvertedCharacter>("Model");
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
        "res://characters/kania/female-warrior.tscn",
        "zone.inst-league1",
        1,
        113.0f,
        170.5f,
        156.293f);

    private void CheckPlayerModelMappings()
    {
        (string Race, string Sex, string Scene)[] cases =
        [
            ("race.elf", "female", "Characters/Elf_female/ElfFemale.(VisObjectTemplate).scene.tscn"),
            ("race.elf", "male", "Characters/Elf_male/ElfMale.(VisObjectTemplate).scene.tscn"),
            ("race.gibberling", "female", "Characters/Gibberlings/GibberlingFemale.(VisObjectTemplate).scene.tscn"),
            ("race.gibberling", "male", "Characters/Gibberlings/GibberlingMale.(VisObjectTemplate).scene.tscn"),
            ("race.hadagan", "female", "Characters/Hadagan_female/HadaganFemale.(VisObjectTemplate).scene.tscn"),
            ("race.hadagan", "male", "Characters/Hadagan_male/HadaganMale.(VisObjectTemplate).scene.tscn"),
            ("race.kania", "female", "Characters/Kania_female/KaniaFemale.(VisObjectTemplate).scene.tscn"),
            ("race.kania", "male", "Characters/Kania_male/KaniaMale.(VisObjectTemplate).scene.tscn"),
            ("race.orc", "female", "Characters/Orc_female/OrcFemale.(VisObjectTemplate).scene.tscn"),
            ("race.orc", "male", "Characters/Orc_male/OrcMale.(VisObjectTemplate).scene.tscn"),
            ("race.undead", "female", "Characters/Undead_female/UndeadFemale.(VisObjectTemplate).scene.tscn"),
            ("race.undead", "male", "Characters/Undead_male/UndeadMale.(VisObjectTemplate).scene.tscn"),
        ];

        foreach ((string race, string sex, string expected) in cases)
        {
            ChargenOption option = CuratedOption() with { Race = race, Sex = sex, VisualRef = string.Empty };
            string actual = PlayerCharacterModel.ResolveScene(option);
            Expect(actual == expected, $"{race}/{sex}: resolves '{actual}', expected '{expected}'");
            Expect(ConvertedSceneLoader.IsLoadable($"{ConvertedRoot}/assets/{actual}", "PackedScene"),
                $"{race}/{sex}: converted player scene is loadable");
        }


        string warrior = PlayerCharacterModel.ResolveScene(CuratedOption());
        string mage = PlayerCharacterModel.ResolveScene(CuratedOption() with
        {
            Id = "chargen.league.mage",
            Class = "class.mage",
            VisualRef = "res://characters/kania/female-mage.tscn",
        });
        Expect(warrior == CuratedPlayerScene,
            $"selected warrior preserves exact visual_ref, got '{warrior}'");
        Expect(!warrior.Equals(mage, StringComparison.OrdinalIgnoreCase),
            "same-race classes do not collapse to one appearance");
    }

    private void CheckCuratedWarriorAppearance(ConvertedCharacter player)
    {
        Expect(
            (string?)player.Model?.GetMeta("sarnaut_appearance_id", string.Empty) == "chargen.league.warrior",
            "local player loads the authored Kania female warrior assembly");

        Skeleton3D? skeleton = player.Model == null ? null : FindDescendant<Skeleton3D>(player.Model);
        MeshInstance3D? mesh = skeleton == null ? null : FindBoundMesh(skeleton);
        Expect((string?)mesh?.GetMeta("sarnaut_body_atlas", string.Empty) == "kania-female-warrior-starting-kit",
            "local player uses the exact starting armor and boots atlas");
        Expect(mesh?.Mesh?.GetSurfaceCount() == 12,
            $"local player selects exactly one authored geoset per body slot, got {mesh?.Mesh?.GetSurfaceCount() ?? 0}");
        Expect(player.Model?.FindChild("Mainhand", recursive: true, owned: false) is BoneAttachment3D,
            "local player equips the starting mace on Slot_Hand_R");
        Expect(player.Model?.FindChild("Offhand", recursive: true, owned: false) is BoneAttachment3D,
            "local player equips the starting shield on Slot_Shield_Hand");
    }

    private static IEnumerable<string> ReadManifestContentIds()
    {
        string json = FileAccess.GetFileAsString($"{ConvertedRoot}/{EntityModelCatalog.ManifestFileName}");
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty model in document.RootElement.GetProperty("models").EnumerateObject())
        {
            yield return model.Name;
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
