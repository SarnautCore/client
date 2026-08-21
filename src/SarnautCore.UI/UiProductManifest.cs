namespace SarnautCore.UI;

public sealed record UiProductManifest(
    NativeContentPath CursorCatalog,
    NativeContentPath SoundCatalog,
    IReadOnlyList<UiScreenDefinition> Screens);

public sealed record UiScreenDefinition(
    string Id,
    NativeContentPath Scene,
    bool InitiallyVisible,
    UiCueSet Cues,
    IReadOnlyList<UiRoleDefinition> Roles,
    IReadOnlyList<UiActionDefinition> Actions,
    IReadOnlyList<UiValueBinding> Values,
    IReadOnlyList<UiCollectionBinding> Collections,
    IReadOnlyList<UiButtonDefinition> Buttons,
    IReadOnlyList<string> FocusOrder)
{
    public UiRoleDefinition GetRole(string roleId) =>
        Roles.FirstOrDefault(role => role.Id == roleId)
        ?? throw new KeyNotFoundException($"Screen '{Id}' has no role '{roleId}'");

    public UiButtonDefinition? FindButton(string roleId) =>
        Buttons.FirstOrDefault(button => button.Role == roleId);

    public IReadOnlyList<string> ActionsFor(string roleId, UiActionEvent actionEvent) =>
        Actions
            .Where(action => action.Triggers.Any(
                trigger => trigger.Role == roleId && trigger.Event == actionEvent))
            .Select(action => action.Id)
            .ToArray();
}

public sealed record UiRoleDefinition(
    string Id,
    string Node,
    bool InitiallyVisible,
    string? Cursor,
    UiCueSet Cues);

public sealed record UiActionDefinition(string Id, IReadOnlyList<UiActionTrigger> Triggers);
public sealed record UiActionTrigger(string Role, UiActionEvent Event);

public enum UiActionEvent
{
    Pressed,
    Toggled,
    Submitted,
    Cancelled,
    Changed,
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

public sealed record UiButtonVariant(string Id, string VisualState, UiCueSet Cues);

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
