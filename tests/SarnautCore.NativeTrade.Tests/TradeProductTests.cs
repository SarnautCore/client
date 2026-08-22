using System.Text;
using System.Text.Json;

namespace SarnautCore.NativeTrade.Tests;

public sealed class TradeProductTests
{
    [Fact]
    public void Parser_accepts_a_closed_product_and_rejects_unknown_fields()
    {
        TradeProduct expected = NativeTradeTests.Product();
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        string json = JsonSerializer.Serialize(expected, options);

        TradeProduct parsed = TradeProduct.Parse(Encoding.UTF8.GetBytes(json));
        string invalid = json.Insert(1, "\"unknown\":true,");

        Assert.Equal(expected.Scene, parsed.Scene);
        Assert.Equal(5, parsed.Roles.Own.Slots.Count);
        Assert.Throws<JsonException>(() => TradeProduct.Parse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void Product_rejects_unsafe_role_paths_and_wrong_retail_shape()
    {
        TradeProduct valid = NativeTradeTests.Product();
        TradeRoles unsafeRoles = valid.Roles with { Close = "../Close" };
        TradePolicy wrongPolicy = valid.Policy with { SlotCount = 6 };

        Assert.Throws<InvalidDataException>(() => new TradeProduct(
            TradeProduct.SchemaId,
            valid.Scene,
            valid.Resources,
            valid.ItemPresentationCatalog,
            valid.Art,
            valid.Placement,
            valid.OwnPanel,
            valid.PartnerPanel,
            valid.ConfirmationPanel,
            valid.AuthoredRootChildOrder,
            valid.NativeRootChildOrder,
            unsafeRoles,
            valid.Policy,
            valid.Semantics));
        Assert.Throws<InvalidDataException>(() => new TradeProduct(
            TradeProduct.SchemaId,
            valid.Scene,
            valid.Resources,
            valid.ItemPresentationCatalog,
            valid.Art,
            valid.Placement,
            valid.OwnPanel,
            valid.PartnerPanel,
            valid.ConfirmationPanel,
            valid.AuthoredRootChildOrder,
            valid.NativeRootChildOrder,
            valid.Roles,
            wrongPolicy,
            valid.Semantics));
    }

    [Fact]
    public void Parser_accepts_the_clean_converter_proof_when_provided()
    {
        string? path = Environment.GetEnvironmentVariable("SARNAUT_TRADE_PRODUCT_PROOF");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TradeProduct parsed = TradeProduct.Parse(File.ReadAllBytes(path));

        Assert.Equal(TradeProduct.SchemaId, parsed.SchemaIdValue);
        Assert.Equal("hud.items.inst-league1", parsed.ItemPresentationCatalog);
        Assert.Equal(5, parsed.Roles.Own.Slots.Count);
        Assert.Equal(5, parsed.Roles.Partner.Slots.Count);
        Assert.Empty(parsed.Semantics.AuthoredTimelines);
    }
}
