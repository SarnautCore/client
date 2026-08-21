using System.Text.Json;

namespace SarnautCore.UI.Tests;

internal static class CreditsFixture
{
    public static CreditsTimeline Timeline(string locale = "eng")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_id", CreditsTimeline.SchemaId);
            writer.WriteString("locale", locale);
            writer.WriteString("media_node", "CreditsMedia");
            WriteText(writer);
            WriteVisual(writer, "pictures", "picture", 20, 100, "multiply");
            WriteVisual(writer, "backgrounds", "background", 8, 0, "alpha");
            writer.WriteString("music_cue", "credits_music");
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return CreditsTimelineReader.Parse(stream);
    }

    public static byte[] Json(Action<Utf8JsonWriter>? extraRootField = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_id", CreditsTimeline.SchemaId);
            writer.WriteString("locale", "eng");
            writer.WriteString("media_node", "CreditsMedia");
            WriteText(writer);
            WriteVisual(writer, "pictures", "picture", 20, 100, "multiply");
            WriteVisual(writer, "backgrounds", "background", 8, 0, "alpha");
            writer.WriteString("music_cue", "credits_music");
            extraRootField?.Invoke(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteText(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("text");
        writer.WriteStartObject();
        writer.WriteNumber("priority", 100);
        WriteTiming(writer, 1, 6, 1);
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        for (int index = 1; index <= 107; index++)
        {
            writer.WriteStartObject();
            writer.WriteString("id", $"credits-text-{index:000}");
            writer.WriteString("body", $"Credit {index}");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVisual(
        Utf8JsonWriter writer,
        string property,
        string label,
        int count,
        int priority,
        string blend)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteNumber("priority", priority);
        writer.WriteString("blend", blend);
        WriteTiming(writer, 4, 8, 4);
        writer.WritePropertyName("frames");
        writer.WriteStartArray();
        for (int index = 1; index <= count; index++)
        {
            writer.WriteStartObject();
            writer.WriteString("id", $"credits-{label}-{index:00}");
            writer.WriteString("texture_id", $"credits-{label}-{index:00}");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTiming(
        Utf8JsonWriter writer,
        int fadeIn,
        int hold,
        int fadeOut)
    {
        writer.WritePropertyName("timing");
        writer.WriteStartObject();
        writer.WriteNumber("fade_in_seconds", fadeIn);
        writer.WriteNumber("solid_seconds", hold);
        writer.WriteNumber("fade_out_seconds", fadeOut);
        writer.WriteEndObject();
    }
}
