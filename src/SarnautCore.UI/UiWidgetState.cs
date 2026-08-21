namespace SarnautCore.UI;

public readonly record struct UiActionDispatch(
    bool Activated,
    string VisualState,
    IReadOnlyList<string> ActionIds);

public sealed class UiRoleState
{
    private readonly UiScreenDefinition _screen;
    private readonly UiButtonDefinition? _button;
    private int _variantIndex;
    private bool _screenVisible;
    private bool _showCuePending;

    public UiRoleState(UiScreenDefinition screen, string roleId)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        Definition = screen.GetRole(roleId);
        _button = screen.FindButton(roleId);
        _variantIndex = _button?.InitialVariantIndex ?? 0;
        _screenVisible = screen.InitiallyVisible;
        IsVisible = Definition.InitiallyVisible;
    }

    public UiRoleDefinition Definition { get; }
    public bool IsVisible { get; private set; }
    public bool IsPointerOver { get; private set; }
    public bool IsPressed { get; private set; }
    public bool CanReceiveInput => _screenVisible && IsVisible;
    public string? VariantId => CurrentVariant?.Id;
    public string VisualState => CurrentVariant?.VisualState ?? "default";

    private UiButtonVariant? CurrentVariant => _button?.Variants[_variantIndex];

    public string? Show()
    {
        if (IsVisible)
        {
            return null;
        }

        IsVisible = true;
        if (!_screenVisible)
        {
            _showCuePending = true;
            return null;
        }

        return ShowCue;
    }

    public string? Hide()
    {
        if (!IsVisible)
        {
            return null;
        }

        IsVisible = false;
        _showCuePending = false;
        IsPointerOver = false;
        IsPressed = false;
        return _screenVisible ? CurrentVariant?.Cues.Hide ?? Definition.Cues.Hide : null;
    }

    public string? PointerEntered()
    {
        if (!CanReceiveInput || IsPointerOver)
        {
            return null;
        }

        IsPointerOver = true;
        return CurrentVariant?.Cues.Hover ?? Definition.Cues.Hover;
    }

    public void PointerExited()
    {
        IsPointerOver = false;
        IsPressed = false;
    }

    public string? BeginPress()
    {
        if (!CanReceiveInput || _button is null || IsPressed)
        {
            return null;
        }

        IsPressed = true;
        return CurrentVariant?.Cues.Press ?? Definition.Cues.Press;
    }

    public UiActionDispatch EndPress(bool activate)
    {
        if (!IsPressed)
        {
            return new UiActionDispatch(false, VisualState, []);
        }

        IsPressed = false;
        if (!activate || !CanReceiveInput || _button is null)
        {
            return new UiActionDispatch(false, VisualState, []);
        }

        UiActionEvent actionEvent = UiActionEvent.Pressed;
        if (_button.Toggle)
        {
            _variantIndex = (_variantIndex + 1) % _button.Variants.Count;
            actionEvent = UiActionEvent.Toggled;
        }

        return new UiActionDispatch(
            true,
            VisualState,
            _screen.ActionsFor(Definition.Id, actionEvent));
    }

    public IReadOnlyList<string> Dispatch(UiActionEvent actionEvent) =>
        actionEvent is UiActionEvent.Pressed or UiActionEvent.Toggled
            ? throw new ArgumentOutOfRangeException(
                nameof(actionEvent),
                actionEvent,
                "Pointer actions must pass through BeginPress and EndPress")
            : CanReceiveInput
                ? _screen.ActionsFor(Definition.Id, actionEvent)
                : [];

    internal string? SetScreenVisible(bool visible)
    {
        _screenVisible = visible;
        if (!visible)
        {
            PointerExited();
            return null;
        }

        if (!_showCuePending || !IsVisible)
        {
            return null;
        }

        _showCuePending = false;
        return ShowCue;
    }

    private string? ShowCue => CurrentVariant?.Cues.Show ?? Definition.Cues.Show;
}

public sealed class UiScreenState
{
    public UiScreenState(UiScreenDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        IsVisible = definition.InitiallyVisible;
        Roles = definition.Roles.ToDictionary(
            role => role.Id,
            role => new UiRoleState(definition, role.Id),
            StringComparer.Ordinal);
        foreach (UiRoleState role in Roles.Values)
        {
            role.SetScreenVisible(IsVisible);
        }
    }

    public UiScreenDefinition Definition { get; }
    public bool IsVisible { get; private set; }
    public IReadOnlyDictionary<string, UiRoleState> Roles { get; }

    public IReadOnlyList<string> Show()
    {
        if (IsVisible)
        {
            return [];
        }

        IsVisible = true;
        var cues = new List<string>();
        if (Definition.Cues.Show is { } screenCue)
        {
            cues.Add(screenCue);
        }

        foreach (UiRoleState role in Roles.Values)
        {
            if (role.SetScreenVisible(true) is { } roleCue)
            {
                cues.Add(roleCue);
            }
        }

        return cues;
    }

    public IReadOnlyList<string> Hide()
    {
        if (!IsVisible)
        {
            return [];
        }

        IsVisible = false;
        foreach (UiRoleState role in Roles.Values)
        {
            role.SetScreenVisible(false);
        }

        return Definition.Cues.Hide is { } cue ? [cue] : [];
    }
}
