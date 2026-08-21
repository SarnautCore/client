using System.Text.Json;

namespace SarnautCore.UI;

public static class CreditsTimelineReader
{
    public static CreditsTimeline Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            UiManifestJson.Object(root, "credits timeline");
            UiManifestJson.Only(
                root,
                "credits timeline",
                "schema_id",
                "locale",
                "text",
                "pictures",
                "backgrounds",
                "music_cue");
            string schema = UiManifestJson.String(root, "schema_id", "credits timeline");
            if (schema != CreditsTimeline.SchemaId)
            {
                throw new InvalidDataException($"Unsupported Credits timeline schema '{schema}'");
            }

            var timeline = new CreditsTimeline(
                UiManifestJson.String(root, "locale", "credits timeline"),
                ReadText(UiManifestJson.Required(root, "text", JsonValueKind.Object, "credits timeline")),
                ReadVisual(
                    UiManifestJson.Required(root, "pictures", JsonValueKind.Object, "credits timeline"),
                    "pictures"),
                ReadVisual(
                    UiManifestJson.Required(root, "backgrounds", JsonValueKind.Object, "credits timeline"),
                    "backgrounds"),
                UiManifestJson.String(root, "music_cue", "credits timeline"));
            timeline.ValidateAuthoredContract();
            return timeline;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Credits timeline is not valid JSON", exception);
        }
    }

    private static CreditsTextTrack ReadText(JsonElement element)
    {
        const string Context = "credits timeline.text";
        UiManifestJson.Only(element, Context, "priority", "timing", "entries");
        JsonElement entriesElement = UiManifestJson.Required(
            element,
            "entries",
            JsonValueKind.Array,
            Context);
        CreditsTextEntry[] entries = entriesElement
            .EnumerateArray()
            .Select(ReadTextEntry)
            .ToArray();
        return new CreditsTextTrack(
            Integer(element, "priority", Context),
            ReadTiming(UiManifestJson.Required(element, "timing", JsonValueKind.Object, Context), Context),
            entries);
    }

    private static CreditsTextEntry ReadTextEntry(JsonElement element, int index)
    {
        string context = $"credits timeline.text.entries[{index}]";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "body");
        return new CreditsTextEntry(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.String(element, "body", context));
    }

    private static CreditsVisualTrack ReadVisual(JsonElement element, string name)
    {
        string context = $"credits timeline.{name}";
        UiManifestJson.Only(element, context, "priority", "blend", "timing", "frames");
        JsonElement framesElement = UiManifestJson.Required(
            element,
            "frames",
            JsonValueKind.Array,
            context);
        CreditsVisualFrame[] frames = framesElement
            .EnumerateArray()
            .Select(frame => ReadFrame(frame, context))
            .ToArray();
        return new CreditsVisualTrack(
            Integer(element, "priority", context),
            UiManifestJson.Enum<CreditsBlend>(element, "blend", context),
            ReadTiming(UiManifestJson.Required(element, "timing", JsonValueKind.Object, context), context),
            frames);
    }

    private static CreditsVisualFrame ReadFrame(JsonElement element, string parent)
    {
        string context = $"{parent}.frames[]";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "texture");
        return new CreditsVisualFrame(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Path(element, "texture", ".png", context));
    }

    private static CreditsTiming ReadTiming(JsonElement element, string parent)
    {
        string context = $"{parent}.timing";
        UiManifestJson.Only(
            element,
            context,
            "fade_in_seconds",
            "solid_seconds",
            "fade_out_seconds");
        return new CreditsTiming(
            TimeSpan.FromSeconds(PositiveInteger(element, "fade_in_seconds", context)),
            TimeSpan.FromSeconds(PositiveInteger(element, "solid_seconds", context)),
            TimeSpan.FromSeconds(PositiveInteger(element, "fade_out_seconds", context)));
    }

    private static int PositiveInteger(JsonElement element, string property, string context)
    {
        int value = Integer(element, property, context);
        if (value <= 0)
        {
            throw new InvalidDataException($"{context}.{property} must be positive");
        }

        return value;
    }

    private static int Integer(JsonElement element, string property, string context)
    {
        JsonElement value = UiManifestJson.Required(element, property, JsonValueKind.Number, context);
        if (!value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"{context}.{property} must be a 32-bit integer");
        }

        return result;
    }
}
