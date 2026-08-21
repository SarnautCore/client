using System;
using System.Collections.Generic;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>Chooses the local rig named by the selected chargen option.</summary>
internal static class PlayerCharacterModel
{
    private static readonly IReadOnlyDictionary<string, string> ConvertedScenes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["elf:female"] = "Characters/Elf_female/ElfFemale.(VisObjectTemplate).scene.tscn",
            ["elf:male"] = "Characters/Elf_male/ElfMale.(VisObjectTemplate).scene.tscn",
            ["gibberling:female"] = "Characters/Gibberlings/GibberlingFemale.(VisObjectTemplate).scene.tscn",
            ["gibberling:male"] = "Characters/Gibberlings/GibberlingMale.(VisObjectTemplate).scene.tscn",
            ["hadagan:female"] = "Characters/Hadagan_female/HadaganFemale.(VisObjectTemplate).scene.tscn",
            ["hadagan:male"] = "Characters/Hadagan_male/HadaganMale.(VisObjectTemplate).scene.tscn",
            ["kania:female"] = "Characters/Kania_female/KaniaFemale.(VisObjectTemplate).scene.tscn",
            ["kania:male"] = "Characters/Kania_male/KaniaMale.(VisObjectTemplate).scene.tscn",
            ["orc:female"] = "Characters/Orc_female/OrcFemale.(VisObjectTemplate).scene.tscn",
            ["orc:male"] = "Characters/Orc_male/OrcMale.(VisObjectTemplate).scene.tscn",
            ["undead:female"] = "Characters/Undead_female/UndeadFemale.(VisObjectTemplate).scene.tscn",
            ["undead:male"] = "Characters/Undead_male/UndeadMale.(VisObjectTemplate).scene.tscn",
        };

    /// <summary>
    /// Applies an authored Godot scene when it exists. The M2 option still
    /// names a planned public scene, so its race and sex select the equivalent
    /// converted base rig until that scene is authored.
    /// </summary>
    public static void Apply(ConvertedCharacter character, ChargenOption? option)
    {
        ArgumentNullException.ThrowIfNull(character);
        character.CharacterScene = ResolveScene(option);
    }

    /// <summary>
    /// The equipped League warrior the online probe player uses. The offline
    /// walkabout has no authenticated character to read an appearance from, so
    /// it gets this curated rig rather than an undressed base body.
    /// </summary>
    public const string CuratedOfflineScene = "res://characters/kania/female-warrior.tscn";

    public static string ResolveScene(ChargenOption? option)
    {
        if (option == null)
        {
            return ConvertedSceneLoader.IsLoadable(CuratedOfflineScene, "PackedScene")
                ? CuratedOfflineScene
                : ConvertedCharacter.DefaultPlayerScene;
        }

        string visualRef = option.VisualRef.Trim();
        if (visualRef.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            && ConvertedSceneLoader.IsLoadable(visualRef, "PackedScene"))
        {
            return visualRef;
        }

        string race = CanonicalToken(option.Race);
        string sex = CanonicalToken(option.Sex);
        return ConvertedScenes.TryGetValue($"{race}:{sex}", out string? scene)
            ? scene
            : ConvertedCharacter.DefaultPlayerScene;
    }

    private static string CanonicalToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        int separator = token.LastIndexOf('.');
        return separator < 0 ? token : token[(separator + 1)..];
    }
}
