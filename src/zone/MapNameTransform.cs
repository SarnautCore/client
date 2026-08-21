using System;
using System.Text;

namespace SarnautCore;

/// <summary>
/// Transforms authored map names to kebab-case directory names for content staging.
/// Allods map names use CamelCase (e.g., "Inst_LeagueStart"), which are converted
/// to kebab-case (e.g., "inst-league-start") for file system paths.
/// </summary>
public static class MapNameTransform
{
    /// <summary>
    /// Converts a CamelCase map name to kebab-case.
    /// Splits on word boundaries (uppercase letters) and underscores,
    /// then joins with hyphens and converts to lowercase.
    /// </summary>
    public static string ToKebabCase(string mapName)
    {
        if (string.IsNullOrEmpty(mapName))
        {
            return mapName;
        }

        var result = new StringBuilder();
        var currentWord = new StringBuilder();

        for (int i = 0; i < mapName.Length; i++)
        {
            char current = mapName[i];

            if (current == '_')
            {
                // Underscore is a separator; flush current word and add hyphen
                if (currentWord.Length > 0)
                {
                    if (result.Length > 0)
                    {
                        result.Append('-');
                    }
                    result.Append(currentWord.ToString().ToLowerInvariant());
                    currentWord.Clear();
                }
            }
            else if (char.IsUpper(current))
            {
                // Uppercase letter starts a new word
                if (currentWord.Length > 0)
                {
                    if (result.Length > 0)
                    {
                        result.Append('-');
                    }
                    result.Append(currentWord.ToString().ToLowerInvariant());
                    currentWord.Clear();
                }
                currentWord.Append(current);
            }
            else
            {
                currentWord.Append(current);
            }
        }

        // Flush the last word
        if (currentWord.Length > 0)
        {
            if (result.Length > 0)
            {
                result.Append('-');
            }
            result.Append(currentWord.ToString().ToLowerInvariant());
        }

        return result.ToString();
    }
}
