using Godot;
using SarnautCore.Gameplay;

namespace SarnautCore;

/// <summary>Loads converted Ingame UI text catalogs when the private tree is mounted.</summary>
public static class ConvertedUiTranslations
{
    private static readonly string[] Catalogs =
    [
        "Interface/Ingame/ContextLootBag/LootBag.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextMultibag/RelatedTexts.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextQuestLog/RelatedTexts/RelatedTexts.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextQuestTrackerNew/QuestTracker.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextRelatedTexts/ItemQuality/ItemQuality.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextRelatedTexts/QuestTypes/QuestTypes.(UIRelatedTexts).translation.csv",
        "Interface/Ingame/ContextRelatedTexts/TakeItemsResult/TakeItemsResult.(UIRelatedTexts).translation.csv",
    ];

    private static bool _mounted;

    public static int Mount()
    {
        if (_mounted)
        {
            return 0;
        }

        _mounted = true;
        int loaded = 0;
        string root = ConvertedSceneLoader.DefaultConvertedRoot.TrimEnd('/');
        foreach (string relativePath in Catalogs)
        {
            string path = $"{root}/assets/{relativePath}";
            if (!ConvertedSceneLoader.IsLoadable(path, "Translation"))
            {
                continue;
            }

            Translation? translation = ResourceLoader.Load<Translation>(path);
            if (translation is null)
            {
                continue;
            }

            TranslationServer.AddTranslation(translation);
            loaded++;
        }

        return loaded;
    }
}

/// <summary>TranslationServer lookup with a canonical-id slug fallback.</summary>
public static class LocalizedText
{
    public static string Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        string translated = TranslationServer.Translate(key);
        return string.IsNullOrWhiteSpace(translated) || translated == key
            ? CanonicalText.Fallback(key)
            : translated;
    }
}
