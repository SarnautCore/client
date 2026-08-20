using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// Reads the converter's output: an <c>.xdb</c> source path resolves to a
/// <c>.tres</c> under <c>resources/</c> holding the original XML, and the mesh
/// or scene it names resolves to a file under <c>assets/</c>.
/// </summary>
/// <remarks>
/// The zone loader owned this walk privately, which was fine while placements
/// were the only thing that needed a model. Server entities need the same walk
/// from a different starting point — a mob's <c>VisualMob</c> rather than a map
/// placement's template — so it lives here and both callers share it.
/// </remarks>
public sealed class AllodsResourceTree
{
    public AllodsResourceTree(string convertedRoot)
    {
        ConvertedRoot = (convertedRoot ?? string.Empty).TrimEnd('/');
    }

    public string ConvertedRoot { get; }

    /// <summary>True when the converted tree this reads is actually present.</summary>
    public bool Exists => ConvertedRoot.Length > 0 && DirAccess.Open(ConvertedRoot) != null;

    /// <summary>Loads the wrapper resource holding one source document's XML.</summary>
    public AllodsResource? Load(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        return ResourceLoader.Load<AllodsResource>($"{ConvertedRoot}/resources/{sourcePath}.tres");
    }

    /// <summary>The converted file a source path produced, for example a <c>.scene.tscn</c>.</summary>
    public string AssetPath(string sourcePath, string suffix) =>
        $"{ConvertedRoot}/assets/{StripXdbSuffix(sourcePath)}{suffix}";

    /// <summary>
    /// Follows a <c>VisualMob</c> to the scene a creature renders as, and the
    /// scale the visual asks for.
    /// </summary>
    /// <remarks>
    /// The chain is <c>VisualMob</c> to <c>VisCharacterTemplate</c> to
    /// <c>VisObjectTemplate</c>, and a visual that already points straight at a
    /// <c>VisObjectTemplate</c> skips the middle step.
    /// </remarks>
    public bool TryResolveVisualMob(string visualMobSource, out string scenePath, out float scale)
    {
        scenePath = string.Empty;
        scale = 1.0f;
        AllodsResource? visualMob = Load(visualMobSource);
        if (visualMob == null)
        {
            return false;
        }

        string characterSource = NormalizeHref(visualMobSource, ReadHref(visualMob.raw_xml, "character"));
        string visualObjectSource = characterSource;
        if (!characterSource.Contains("(VisObjectTemplate).xdb", StringComparison.OrdinalIgnoreCase))
        {
            AllodsResource? character = Load(characterSource);
            visualObjectSource = NormalizeHref(
                characterSource,
                ReadHref(character?.raw_xml ?? string.Empty, "characterVisObject"));
        }

        if (string.IsNullOrEmpty(visualObjectSource))
        {
            return false;
        }

        float authored = ReadFloat(visualMob.raw_xml, "scale", 1.0f);
        scale = authored <= 0 ? 1.0f : authored;
        scenePath = AssetPath(visualObjectSource, ".scene.tscn");
        return true;
    }

    /// <summary>
    /// Resolves an <c>href</c> against the document that carried it: strips the
    /// XPointer fragment, normalizes separators, and folds <c>.</c> and
    /// <c>..</c> so nothing escapes the tree.
    /// </summary>
    public static string NormalizeHref(string ownerSource, string href)
    {
        string path = href.Split('#', 2)[0].Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        bool absolute = path.StartsWith('/');
        path = path.TrimStart('/');
        if (!absolute && !string.IsNullOrEmpty(ownerSource))
        {
            int slash = ownerSource.LastIndexOf('/');
            path = slash < 0 ? path : $"{ownerSource[..slash]}/{path}";
        }

        var segments = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    public static string StripXdbSuffix(string path) =>
        path.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    public static string ReadHref(string xml, string elementName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            XElement? element = XDocument.Parse(xml).Descendants()
                .FirstOrDefault(candidate => candidate.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
            return element?.Attribute("href")?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static IReadOnlyList<string> ReadHrefs(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        try
        {
            return XDocument.Parse(xml).Descendants()
                .Select(element => element.Attribute("href")?.Value ?? string.Empty)
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static float ReadFloat(string xml, string elementName, float fallback)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return fallback;
        }

        try
        {
            string? value = XDocument.Parse(xml).Descendants()
                .FirstOrDefault(candidate => candidate.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
