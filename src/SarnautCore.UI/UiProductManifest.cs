namespace SarnautCore.UI;

public sealed record UiProductManifest(
    NativeContentPath CursorCatalog,
    NativeContentPath SoundCatalog,
    NativeContentPath Theme,
    UiProductResourceEncoding ResourceEncoding,
    IReadOnlyList<UiScreenDefinition> Screens)
{
    /// <summary>
    /// Screens in authored draw order. Equal-priority forms retain their product order.
    /// </summary>
    public IReadOnlyList<UiScreenDefinition> ScreensInAuthoredOrder => Screens
        .Select((screen, productIndex) => (screen, productIndex))
        .OrderBy(item => item.screen.Priority)
        .ThenBy(item => item.productIndex)
        .Select(item => item.screen)
        .ToArray();
}

public enum UiProductResourceEncoding
{
    Plain,
    Compiled,
}

public sealed record UiScreenDefinition(
    string Id,
    NativeContentPath Scene,
    int Priority,
    bool InitiallyVisible,
    IReadOnlyList<UiDocumentReference> Documents,
    NativeContentPath? Timeline,
    UiCueSet Cues,
    IReadOnlyList<UiRoleDefinition> Roles,
    IReadOnlyList<UiActionDefinition> Actions,
    IReadOnlyList<UiValueBinding> Values,
    IReadOnlyList<UiCollectionBinding> Collections,
    IReadOnlyList<UiButtonDefinition> Buttons,
    IReadOnlyList<UiSelectionGroupDefinition> SelectionGroups,
    IReadOnlyList<string> FocusOrder)
{
    public UiRoleDefinition GetRole(string roleId) =>
        Roles.FirstOrDefault(role => role.Id == roleId)
        ?? throw new KeyNotFoundException($"Screen '{Id}' has no role '{roleId}'");

    public UiButtonDefinition? FindButton(string roleId) =>
        Buttons.FirstOrDefault(button => button.Role == roleId);

    public UiSelectionGroupDefinition? FindSelectionGroup(string roleId) =>
        SelectionGroups.FirstOrDefault(group => group.Roles.Contains(roleId, StringComparer.Ordinal));

    public UiCollectionBinding? FindCollectionByItemRole(string roleId) =>
        Collections.FirstOrDefault(collection => collection.ItemRole == roleId);

    public IReadOnlyList<UiActionInvocation> ActionsFor(
        string roleId,
        UiActionEvent actionEvent,
        UiCollectionItemContext? itemContext = null,
        Func<string, string?>? selectedCollectionItem = null) =>
        Actions
            .Where(action => action.Triggers.Any(
                trigger => trigger.Role == roleId && trigger.Event == actionEvent))
            .Select(action => ResolveAction(
                action,
                roleId,
                itemContext,
                selectedCollectionItem))
            .Where(invocation => invocation is not null)
            .Select(invocation => invocation!)
            .ToArray();

    private UiActionInvocation? ResolveAction(
        UiActionDefinition action,
        string roleId,
        UiCollectionItemContext? itemContext,
        Func<string, string?>? selectedCollectionItem)
    {
        var arguments = new List<UiResolvedActionArgument>(action.Arguments.Count);
        foreach (UiActionArgument argument in action.Arguments)
        {
            string? value = argument.Kind switch
            {
                UiActionArgumentKind.ProductId => argument.Value!,
                UiActionArgumentKind.CollectionItemId
                    when itemContext is { } context
                        && context.CollectionId == argument.Collection => context.ProductItemId,
                UiActionArgumentKind.CollectionItemId
                    when selectedCollectionItem is not null =>
                        selectedCollectionItem(argument.Collection!),
                _ => throw new InvalidOperationException(
                    $"Action '{action.Id}' has unsupported argument kind '{argument.Kind}'"),
            };

            if (value is null)
            {
                UiCollectionBinding collection = Collections.Single(
                    candidate => candidate.Id == argument.Collection);
                if (collection.ItemRole == roleId)
                {
                    throw new InvalidOperationException(
                        $"Action '{action.Id}' requires item identity from collection '{argument.Collection}'");
                }

                return null;
            }

            arguments.Add(new UiResolvedActionArgument(argument.Name, argument.Kind, value));
        }

        return new UiActionInvocation(action, arguments);
    }
}

public sealed record UiDocumentReference(string Id, NativeContentPath Path);

public sealed record UiRoleDefinition(
    string Id,
    string Node,
    bool InitiallyVisible,
    string? Cursor,
    UiCueSet Cues);

public sealed record UiActionDefinition(
    string Id,
    IReadOnlyList<UiActionArgument> Arguments,
    IReadOnlyList<UiActionTrigger> Triggers);
public sealed record UiActionArgument(
    string Name,
    UiActionArgumentKind Kind,
    string? Value,
    string? Collection);
public sealed record UiActionTrigger(string Role, UiActionEvent Event);

public sealed record UiResolvedActionArgument(
    string Name,
    UiActionArgumentKind Kind,
    string Value);

public sealed record UiActionInvocation(
    UiActionDefinition Definition,
    IReadOnlyList<UiResolvedActionArgument> Arguments)
{
    public string Id => Definition.Id;
}

public readonly record struct UiCollectionItemContext
{
    public UiCollectionItemContext(string collectionId, string productItemId)
    {
        UiRuntimeKey.Validate(collectionId, nameof(collectionId));
        UiRuntimeKey.Validate(productItemId, nameof(productItemId));
        CollectionId = collectionId;
        ProductItemId = productItemId;
    }

    public string CollectionId { get; }
    public string ProductItemId { get; }
}

public enum UiActionArgumentKind
{
    ProductId,
    CollectionItemId,
}

public enum UiActionEvent
{
    Pressed,
    Toggled,
    Submitted,
    Cancelled,
    Changed,
    HoverEntered,
    HoverExited,
    DoublePressed,
    PrimaryPressed,
    PrimaryReleased,
    SecondaryPressed,
    SecondaryReleased,
    HorizontalChanged,
    VerticalChanged,
    ZoomIn,
    ZoomOut,
    NavigatePrevious,
    NavigateNext,
}

public sealed record UiValueBinding(
    string Id,
    string Role,
    UiValueKind Kind,
    UiValueAccess Access,
    bool Secret);

public enum UiValueKind
{
    Text,
    Number,
    Boolean,
}

public enum UiValueAccess
{
    Read,
    Write,
    ReadWrite,
}

public sealed record UiCollectionBinding(
    string Id,
    string Role,
    string ItemRole,
    NativeContentPath ItemScene,
    UiCollectionSelection Selection);

public enum UiCollectionSelection
{
    None,
    Single,
    Multiple,
}

public sealed record UiButtonDefinition(
    string Role,
    bool Toggle,
    string InitialVariant,
    IReadOnlyList<UiButtonVariant> Variants)
{
    public int InitialVariantIndex =>
        Variants
            .Select((variant, index) => (variant, index))
            .First(pair => pair.variant.Id == InitialVariant)
            .index;
}

public sealed record UiButtonVariant(
    string Id,
    string VisualState,
    UiCueSet Cues,
    IReadOnlyList<UiInputRoute> Inputs)
{
    public UiActionEvent? EventFor(UiPhysicalInput input) =>
        Inputs.FirstOrDefault(route => route.Input == input)?.Event;
}

public sealed record UiInputRoute(UiPhysicalInput Input, UiActionEvent Event);

public enum UiPhysicalInput
{
    PrimaryPressed,
    PrimaryReleased,
    SecondaryPressed,
    SecondaryReleased,
    DoublePressed,
    HoverEntered,
    HoverExited,
}

public sealed record UiSelectionGroupDefinition(
    string Id,
    IReadOnlyList<string> Roles,
    bool AllowEmpty,
    string? InitialRole);

public sealed record UiCueSet(string? Show, string? Hide, string? Hover, string? Press)
{
    public static UiCueSet Empty { get; } = new(null, null, null, null);
}

public readonly record struct NativeContentPath
{
    internal NativeContentPath(string value)
    {
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}
