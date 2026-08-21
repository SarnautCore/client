using System.Text;

namespace SarnautCore.UI.Tests;

public sealed class UiDocumentReaderTests
{
    [Fact]
    public void ReadsTheClosedNativeDocumentContract()
    {
        using var stream = Json("""
            {
              "schema_id": "sarnaut.ui-document/v1",
              "id": "eula-document-01",
              "locale": "rus",
              "body": "<body><p>Agreement</p></body>"
            }
            """);

        UiDocument document = UiDocumentReader.Parse(stream);

        Assert.Equal("eula-document-01", document.Id);
        Assert.Equal("rus", document.Locale);
        Assert.Equal("<body><p>Agreement</p></body>", document.Body);
    }

    [Theory]
    [InlineData("{\"schema_id\":\"sarnaut.ui-document/v2\",\"id\":\"eula-document-01\",\"locale\":\"rus\",\"body\":\"x\"}")]
    [InlineData("{\"schema_id\":\"sarnaut.ui-document/v1\",\"id\":\"EULA\",\"locale\":\"rus\",\"body\":\"x\"}")]
    [InlineData("{\"schema_id\":\"sarnaut.ui-document/v1\",\"id\":\"eula-document-01\",\"locale\":\"rus\",\"body\":\" \"}")]
    [InlineData("{\"schema_id\":\"sarnaut.ui-document/v1\",\"id\":\"eula-document-01\",\"locale\":\"rus\",\"body\":\"x\",\"source\":\"private\"}")]
    public void RejectsAnythingOutsideTheClosedContract(string json)
    {
        using var stream = Json(json);
        Assert.Throws<InvalidDataException>(() => UiDocumentReader.Parse(stream));
    }

    private static MemoryStream Json(string json) =>
        new(Encoding.UTF8.GetBytes(json));
}
