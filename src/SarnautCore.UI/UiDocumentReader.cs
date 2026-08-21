using System.Text.Json;

namespace SarnautCore.UI;

public sealed record UiDocument(string Id, string Locale, string Body)
{
    public const string SchemaId = "sarnaut.ui-document/v1";
}

public static class UiDocumentReader
{
    public static UiDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            const string Context = "UI document";
            UiManifestJson.Object(root, Context);
            UiManifestJson.Only(root, Context, "schema_id", "id", "locale", "body");
            string schema = UiManifestJson.String(root, "schema_id", Context);
            if (schema != UiDocument.SchemaId)
            {
                throw new InvalidDataException($"Unsupported UI document schema '{schema}'");
            }

            string body = UiManifestJson.String(root, "body", Context);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new InvalidDataException("UI document body must not be empty");
            }

            return new UiDocument(
                UiManifestJson.Key(root, "id", Context),
                UiManifestJson.String(root, "locale", Context),
                body);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("UI document is not valid JSON", exception);
        }
    }
}
