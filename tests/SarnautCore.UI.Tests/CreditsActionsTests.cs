namespace SarnautCore.UI.Tests;

public sealed class CreditsActionsTests
{
    [Theory]
    [InlineData(CreditsActions.PreviousId, CreditsActionKind.Previous)]
    [InlineData(CreditsActions.NextId, CreditsActionKind.Next)]
    [InlineData(CreditsActions.CloseId, CreditsActionKind.Close)]
    [InlineData(CreditsActions.HideTooltipId, CreditsActionKind.HideTooltip)]
    public void ResolvesArgumentFreeProductActions(string id, CreditsActionKind expected)
    {
        CreditsAction action = CreditsActions.Resolve(id, []);

        Assert.Equal(expected, action.Kind);
        Assert.Null(action.ProductId);
    }

    [Fact]
    public void ResolvesTheTypedTooltipProductId()
    {
        CreditsAction action = CreditsActions.Resolve(
            CreditsActions.ShowTooltipId,
            [new CreditsActionArgument(
                CreditsActions.TooltipArgument,
                CreditsActionArgumentKind.ProductId,
                CreditsProduct.PreviousRole)]);

        Assert.Equal(CreditsActionKind.ShowTooltip, action.Kind);
        Assert.Equal("previous-button", action.ProductId);
    }

    [Fact]
    public void RejectsUnknownOrMalformedActions()
    {
        Assert.Throws<InvalidOperationException>(() => CreditsActions.Resolve("run-script", []));
        Assert.Throws<InvalidOperationException>(
            () => CreditsActions.Resolve(
                CreditsActions.ShowTooltipId,
                [new CreditsActionArgument(
                    "callback",
                    CreditsActionArgumentKind.ProductId,
                    CreditsProduct.PreviousRole)]));
        Assert.Throws<InvalidDataException>(() => CreditsAction.ShowTooltip("PreviousButton"));
        Assert.Throws<InvalidDataException>(() => CreditsAction.ShowTooltip("account-input"));
    }
}
