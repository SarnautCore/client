namespace SarnautCore.UI;

public static class CreditsProduct
{
    public const string ScreenId = "credits";
    public const string TextRole = "credit-text";
    public const string PictureRole = "picture-layer";
    public const string BackgroundRole = "background-layer";
    public const string PreviousRole = "previous-button";
    public const string NextRole = "next-button";
    public const string ExitRole = "exit-button";
    public const string OpenAction = "show-credits";

    internal static bool IsTooltipTarget(string productId) => productId is
        PreviousRole or NextRole or ExitRole;
}

public enum CreditsActionKind
{
    Previous,
    Next,
    Close,
    ShowTooltip,
    HideTooltip,
}

public sealed record CreditsAction
{
    private CreditsAction(CreditsActionKind kind, string? productId)
    {
        Kind = kind;
        ProductId = productId;
    }

    public CreditsActionKind Kind { get; }
    public string? ProductId { get; }

    public static CreditsAction Previous { get; } = new(CreditsActionKind.Previous, null);
    public static CreditsAction Next { get; } = new(CreditsActionKind.Next, null);
    public static CreditsAction Close { get; } = new(CreditsActionKind.Close, null);
    public static CreditsAction HideTooltip { get; } = new(CreditsActionKind.HideTooltip, null);

    public static CreditsAction ShowTooltip(string productId)
    {
        UiRuntimeKey.Validate(productId, nameof(productId));
        if (!CreditsProduct.IsTooltipTarget(productId))
        {
            throw new InvalidDataException(
                $"Credits tooltip target '{productId}' is not an authored button role");
        }

        return new CreditsAction(CreditsActionKind.ShowTooltip, productId);
    }
}

public enum CreditsActionArgumentKind
{
    ProductId,
}

public sealed record CreditsActionArgument(
    string Name,
    CreditsActionArgumentKind Kind,
    string Value);

public static class CreditsActions
{
    public const string PreviousId = "previous-credit";
    public const string NextId = "next-credit";
    public const string CloseId = "close-credits";
    public const string ShowTooltipId = "show-tooltip";
    public const string HideTooltipId = "hide-tooltip";
    public const string TooltipArgument = "tooltip";

    public static CreditsAction Resolve(
        string actionId,
        IReadOnlyList<CreditsActionArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        ArgumentNullException.ThrowIfNull(arguments);
        return actionId switch
        {
            PreviousId => WithoutArguments(CreditsAction.Previous, actionId, arguments),
            NextId => WithoutArguments(CreditsAction.Next, actionId, arguments),
            CloseId => WithoutArguments(CreditsAction.Close, actionId, arguments),
            HideTooltipId => WithoutArguments(CreditsAction.HideTooltip, actionId, arguments),
            ShowTooltipId => ResolveTooltip(arguments),
            _ => throw new InvalidOperationException($"Credits received unknown action '{actionId}'"),
        };
    }

    private static CreditsAction WithoutArguments(
        CreditsAction action,
        string actionId,
        IReadOnlyList<CreditsActionArgument> arguments)
    {
        if (arguments.Count != 0)
        {
            throw new InvalidOperationException(
                $"Credits action '{actionId}' does not accept arguments");
        }

        return action;
    }

    private static CreditsAction ResolveTooltip(IReadOnlyList<CreditsActionArgument> arguments)
    {
        if (arguments.Count != 1
            || arguments[0].Name != TooltipArgument
            || arguments[0].Kind != CreditsActionArgumentKind.ProductId)
        {
            throw new InvalidOperationException(
                $"Credits action '{ShowTooltipId}' requires one '{TooltipArgument}' argument");
        }

        return CreditsAction.ShowTooltip(arguments[0].Value);
    }
}
