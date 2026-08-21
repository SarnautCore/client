using System.Text;
using System.Text.Json;
using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudChatAntiSpamJsonTests
{
    [Fact]
    public void ParsesTheFrozenSourceFreeProductAndPreservesDuplicatePatterns()
    {
        HudChatAntiSpamCatalog catalog = HudChatAntiSpamJson.Parse(Encoding.UTF8.GetBytes(ValidJson));

        Assert.Equal(200, catalog.Score(HudChatChannel.Say, "gold", "Sender", []));
        Assert.Equal(250, catalog.Score(HudChatChannel.Say, "!!!", "Sender", []));
    }

    [Theory]
    [InlineData("\"case_fold\":\"locale-wchar\"", "\"case_fold\":\"invariant\"")]
    [InlineData("\"score_scale\":100", "\"score_scale\":99")]
    [InlineData("\"weight_hundredths\":250", "\"weight_hundredths\":-1")]
    public void RejectsSemanticDrift(string original, string replacement)
    {
        string invalid = ValidJson.Replace(original, replacement, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => HudChatAntiSpamJson.Parse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void RejectsUnknownAndDuplicateProperties()
    {
        string unknown = ValidJson.Replace("\"score_scale\":100,", "\"score_scale\":100,\"source_path\":\"private.xdb\",", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HudChatAntiSpamJson.Parse(Encoding.UTF8.GetBytes(unknown)));
        string duplicate = ValidJson.Replace("\"score_scale\":100,", "\"score_scale\":100,\"score_scale\":100,", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HudChatAntiSpamJson.Parse(Encoding.UTF8.GetBytes(duplicate)));
    }

    private const string ValidJson = """
        {
          "schema":"sarnaut.chat-antispam/v1",
          "score_scale":100,
          "empty_score":100,
          "normalization":{"trim_both":true,"collapse_ascii_space":true,"case_fold":"locale-wchar"},
          "case_culture":"ru-RU",
          "category_aggregation":"maximum",
          "categories":[{
            "id":"trade",
            "aggregation":"sum",
            "filters":[
              {"kind":"caps-lock","weight_hundredths":250},
              {"kind":"trash","weight_hundredths":250,"symbols":" !"},
              {"kind":"weighted-wildcards","weight_hundredths":100,"trash_symbols":" !","patterns":[
                {"pattern":"*gold*","weight_hundredths":100},
                {"pattern":"*gold*","weight_hundredths":100}
              ]}
            ]
          }]
        }
        """;
}
