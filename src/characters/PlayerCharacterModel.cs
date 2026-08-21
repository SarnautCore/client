using System;
using System.Collections.Generic;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>Chooses the native rig named by the selected chargen option.</summary>
internal static class PlayerCharacterModel
{
    public const string DefaultCharacterKey = "chargen.league.warrior";

    private static readonly IReadOnlyDictionary<string, string> RaceKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kania"] = "kania",
            ["kanian"] = "kania",
            ["elf"] = "elf",
            ["elven"] = "elf",
            ["gibberling"] = "gibberling",
            ["gibberlings"] = "gibberling",
        };

    public static bool Apply(
        CharacterRig character,
        EntityModelCatalog catalog,
        ChargenOption? option)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!TryResolve(catalog, option, out EntityModel model))
        {
            character.ScenePath = string.Empty;
            return false;
        }

        character.ScenePath = model.ScenePath;
        return true;
    }

    public static bool TryResolve(
        EntityModelCatalog catalog,
        ChargenOption? option,
        out EntityModel model)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (option is null)
        {
            return catalog.TryResolvePlayer(DefaultCharacterKey, out model);
        }

        foreach (string candidate in CandidateKeys(option))
        {
            if (catalog.TryResolvePlayer(candidate, out model))
            {
                return true;
            }
        }

        model = default;
        return false;
    }

    private static IEnumerable<string> CandidateKeys(ChargenOption option)
    {
        if (!string.IsNullOrWhiteSpace(option.Id))
        {
            yield return option.Id.Trim();
        }

        string race = CanonicalToken(option.Race);
        string sex = CanonicalToken(option.Sex);
        if (RaceKeys.TryGetValue(race, out string? raceKey) && sex.Length > 0)
        {
            yield return $"player.{raceKey}.{sex}";
        }
    }

    private static string CanonicalToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        int separator = token.LastIndexOf('.');
        return (separator < 0 ? token : token[(separator + 1)..]).ToLowerInvariant();
    }
}
