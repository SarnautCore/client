namespace SarnautCore.UI;

public readonly record struct UiActionDispatch(
    bool Activated,
    string VisualState,
    string? Cue,
    IReadOnlyList<UiActionInvocation> Invocations)
{
    public IReadOnlyList<UiActionDefinition> Actions =>
        Invocations.Select(invocation => invocation.Definition).ToArray();

    public IReadOnlyList<string> ActionIds => Invocations.Select(invocation => invocation.Id).ToArray();
}

public readonly record struct UiCollectionActionDispatch(
    bool Activated,
    string? PreviousProductItemId,
    string? SelectedProductItemId,
    string VisualState,
    string? Cue,
    IReadOnlyList<UiActionInvocation> Invocations);

public sealed class UiCollectionState
{
    private readonly UiScreenDefinition _screen;
    private readonly UiButtonDefinition? _itemButton;
    private readonly UiRoleDefinition _itemRole;
    private readonly Func<bool> _canReceiveInput;
    private readonly Func<string, string?> _selectedCollectionItem;

    internal UiCollectionState(
        UiScreenDefinition screen,
        UiCollectionBinding definition,
        Func<bool> canReceiveInput,
        Func<string, string?> selectedCollectionItem)
    {
        _screen = screen;
        Definition = definition;
        _itemButton = screen.FindButton(definition.ItemRole);
        _itemRole = screen.GetRole(definition.ItemRole);
        _canReceiveInput = canReceiveInput;
        _selectedCollectionItem = selectedCollectionItem;
    }

    public UiCollectionBinding Definition { get; }
    public string? SelectedProductItemId { get; private set; }

    public bool IsSelected(string productItemId) =>
        SelectedProductItemId == ValidateProductItemId(productItemId);

    public string? VariantIdFor(string productItemId) =>
        VariantFor(ValidateProductItemId(productItemId))?.Id;

    public string VisualStateFor(string productItemId) =>
        VariantFor(ValidateProductItemId(productItemId))?.VisualState ?? "default";

    public void ReconcileAvailableItems(IEnumerable<string> productItemIds)
    {
        ArgumentNullException.ThrowIfNull(productItemIds);
        HashSet<string> available = productItemIds
            .Select(ValidateProductItemId)
            .ToHashSet(StringComparer.Ordinal);
        if (SelectedProductItemId is { } selected && !available.Contains(selected))
        {
            SelectedProductItemId = null;
        }
    }

    public UiCollectionActionDispatch RouteInput(
        string productItemId,
        UiPhysicalInput input)
    {
        string itemId = ValidateProductItemId(productItemId);
        if (!_canReceiveInput())
        {
            return new UiCollectionActionDispatch(
                false,
                SelectedProductItemId,
                SelectedProductItemId,
                VisualStateFor(itemId),
                null,
                []);
        }

        UiButtonVariant? variant = VariantFor(itemId);
        UiActionEvent? actionEvent = variant?.EventFor(input);
        string? cue = CueFor(variant, input);
        return actionEvent switch
        {
            null => new UiCollectionActionDispatch(
                false,
                SelectedProductItemId,
                SelectedProductItemId,
                VisualStateFor(itemId),
                null,
                []),
            UiActionEvent.Toggled => ToggleItem(itemId, cue),
            UiActionEvent.DoublePressed => DoublePressItem(itemId, cue),
            { } mapped => new UiCollectionActionDispatch(
                true,
                SelectedProductItemId,
                SelectedProductItemId,
                VisualStateFor(itemId),
                cue,
                _screen.ActionsFor(
                    Definition.ItemRole,
                    mapped,
                    new UiCollectionItemContext(Definition.Id, itemId),
                    _selectedCollectionItem)),
        };
    }

    private UiCollectionActionDispatch ToggleItem(string productItemId, string? cue)
    {
        EnsureSingleSelection();
        string itemId = ValidateProductItemId(productItemId);
        string? previous = SelectedProductItemId;
        if (previous == itemId)
        {
            return new UiCollectionActionDispatch(
                false,
                previous,
                previous,
                VisualStateFor(itemId),
                null,
                []);
        }

        SelectedProductItemId = itemId;
        return new UiCollectionActionDispatch(
            true,
            previous,
            itemId,
            VisualStateFor(itemId),
            cue,
            _screen.ActionsFor(
                Definition.ItemRole,
                UiActionEvent.Toggled,
                new UiCollectionItemContext(Definition.Id, itemId),
                _selectedCollectionItem));
    }

    private UiCollectionActionDispatch DoublePressItem(string productItemId, string? cue)
    {
        string itemId = ValidateProductItemId(productItemId);
        return new UiCollectionActionDispatch(
            true,
            SelectedProductItemId,
            SelectedProductItemId,
            VisualStateFor(itemId),
            cue,
            _screen.ActionsFor(
                Definition.ItemRole,
                UiActionEvent.DoublePressed,
                new UiCollectionItemContext(Definition.Id, itemId),
                _selectedCollectionItem));
    }

    private void EnsureSingleSelection()
    {
        if (Definition.Selection != UiCollectionSelection.Single)
        {
            throw new InvalidOperationException(
                $"Collection '{Definition.Id}' is not single-selection");
        }
    }

    private static string ValidateProductItemId(string productItemId)
    {
        UiRuntimeKey.Validate(productItemId, nameof(productItemId));
        return productItemId;
    }

    private UiButtonVariant? VariantFor(string productItemId)
    {
        if (_itemButton is null)
        {
            return null;
        }

        int variantIndex = _itemButton.InitialVariantIndex;
        if (SelectedProductItemId == productItemId)
        {
            variantIndex = (variantIndex + 1) % _itemButton.Variants.Count;
        }

        return _itemButton.Variants[variantIndex];
    }

    private string? CueFor(UiButtonVariant? variant, UiPhysicalInput input) => input switch
    {
        UiPhysicalInput.HoverEntered => variant?.Cues.Hover ?? _itemRole.Cues.Hover,
        UiPhysicalInput.PrimaryReleased => variant?.Cues.Press ?? _itemRole.Cues.Press,
        _ => null,
    };
}

public sealed class UiRoleState
{
    private readonly UiScreenDefinition _screen;
    private readonly UiButtonDefinition? _button;
    private readonly UiCollectionBinding? _itemCollection;
    private readonly Func<string, string?> _selectedCollectionItem;
    private int _variantIndex;
    private bool _screenVisible;
    private bool _showCuePending;
    private Func<UiRoleState, bool>? _toggleSelection;

    internal UiRoleState(
        UiScreenDefinition screen,
        string roleId,
        Func<string, string?> selectedCollectionItem)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        Definition = screen.GetRole(roleId);
        _button = screen.FindButton(roleId);
        _itemCollection = screen.FindCollectionByItemRole(roleId);
        _selectedCollectionItem = selectedCollectionItem;
        _variantIndex = _button?.InitialVariantIndex ?? 0;
        _screenVisible = screen.InitiallyVisible;
        IsVisible = Definition.InitiallyVisible;
        IsSelected = false;
    }

    public UiRoleDefinition Definition { get; }
    public bool IsVisible { get; private set; }
    public bool IsPointerOver { get; private set; }
    public bool IsPressed { get; private set; }
    public bool IsSelected { get; private set; }
    public bool CanReceiveInput => _screenVisible && IsVisible;
    public string? VariantId => CurrentVariant?.Id;
    public string VisualState => CurrentVariant?.VisualState ?? "default";

    private UiButtonVariant? CurrentVariant => _button?.Variants[_variantIndex];

    public UiActionDispatch RouteInput(
        UiPhysicalInput input,
        UiCollectionItemContext? itemContext = null) => input switch
        {
            UiPhysicalInput.PrimaryPressed when _button is null =>
                CanReceiveInput
                    ? DispatchPhysical(input, null, itemContext)
                    : EmptyDispatch,
            UiPhysicalInput.PrimaryPressed => BeginPress(itemContext),
            UiPhysicalInput.PrimaryReleased when _button is null =>
                CanReceiveInput
                    ? DispatchPhysical(input, null, itemContext)
                    : EmptyDispatch,
            UiPhysicalInput.PrimaryReleased => EndPress(activate: true, itemContext: itemContext),
            UiPhysicalInput.DoublePressed => DoublePress(itemContext),
            UiPhysicalInput.HoverEntered => PointerEntered(itemContext),
            UiPhysicalInput.HoverExited => PointerExited(itemContext),
            UiPhysicalInput.SecondaryPressed or UiPhysicalInput.SecondaryReleased =>
                CanReceiveInput
                    ? DispatchPhysical(input, null, itemContext)
                    : EmptyDispatch,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input, null),
        };

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

    public UiActionDispatch PointerEntered(UiCollectionItemContext? itemContext = null)
    {
        if (!CanReceiveInput || IsPointerOver)
        {
            return EmptyDispatch;
        }

        IsPointerOver = true;
        return DispatchPhysical(
            UiPhysicalInput.HoverEntered,
            CurrentVariant?.Cues.Hover ?? Definition.Cues.Hover,
            itemContext);
    }

    public UiActionDispatch PointerExited(UiCollectionItemContext? itemContext = null)
    {
        if (!IsPointerOver)
        {
            return EmptyDispatch;
        }

        IsPointerOver = false;
        IsPressed = false;
        return DispatchPhysical(UiPhysicalInput.HoverExited, null, itemContext);
    }

    public UiActionDispatch BeginPress(UiCollectionItemContext? itemContext = null)
    {
        if (!CanReceiveInput || _button is null || IsPressed)
        {
            return EmptyDispatch;
        }

        IsPressed = true;
        return DispatchPhysical(UiPhysicalInput.PrimaryPressed, null, itemContext);
    }

    public UiActionDispatch EndPress(
        bool activate,
        UiCollectionItemContext? itemContext = null)
    {
        if (!IsPressed)
        {
            return EmptyDispatch;
        }

        IsPressed = false;
        if (!activate || !CanReceiveInput || _button is null)
        {
            return EmptyDispatch;
        }

        return DispatchPhysical(
            UiPhysicalInput.PrimaryReleased,
            CurrentVariant?.Cues.Press ?? Definition.Cues.Press,
            itemContext);
    }

    public UiActionDispatch DoublePress(UiCollectionItemContext? itemContext = null)
    {
        if (!CanReceiveInput || _button is null)
        {
            return EmptyDispatch;
        }

        return DispatchPhysical(UiPhysicalInput.DoublePressed, null, itemContext);
    }

    public IReadOnlyList<UiActionInvocation> Dispatch(
        UiActionEvent actionEvent,
        UiCollectionItemContext? itemContext = null)
    {
        if (_button is not null
            || actionEvent is UiActionEvent.Pressed
            or UiActionEvent.Toggled
            or UiActionEvent.DoublePressed
            or UiActionEvent.HoverEntered
            or UiActionEvent.HoverExited
            or UiActionEvent.PrimaryPressed
            or UiActionEvent.PrimaryReleased
            or UiActionEvent.SecondaryPressed
            or UiActionEvent.SecondaryReleased)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actionEvent),
                actionEvent,
                "Button and physical input actions must pass through RouteInput");
        }

        return CanReceiveInput
            ? _screen.ActionsFor(
                Definition.Id,
                actionEvent,
                itemContext,
                _selectedCollectionItem)
            : [];
    }

    internal void SetToggleSelection(Func<UiRoleState, bool> toggleSelection) =>
        _toggleSelection = toggleSelection;

    internal void SetSelected(bool selected, bool advanceVariant)
    {
        IsSelected = selected;
        if (advanceVariant && _button is not null)
        {
            _variantIndex = (_variantIndex + 1) % _button.Variants.Count;
        }
    }

    internal string? SetScreenVisible(bool visible)
    {
        _screenVisible = visible;
        if (!visible)
        {
            IsPointerOver = false;
            IsPressed = false;
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

    private UiActionDispatch DispatchPhysical(
        UiPhysicalInput input,
        string? cue,
        UiCollectionItemContext? itemContext)
    {
        UiActionEvent? actionEvent = _button is null
            ? DefaultEvent(input)
            : CurrentVariant?.EventFor(input);
        if (actionEvent is null)
        {
            return EmptyDispatch;
        }

        if (actionEvent == UiActionEvent.Toggled)
        {
            if (_itemCollection is not null)
            {
                throw new InvalidOperationException(
                    $"Collection item role '{Definition.Id}' must route input through collection state '{_itemCollection.Id}'");
            }

            if (_toggleSelection is null)
            {
                throw new InvalidOperationException(
                    $"Toggle role '{Definition.Id}' is not attached to a screen state");
            }

            if (!_toggleSelection(this))
            {
                return EmptyDispatch;
            }
        }

        return new UiActionDispatch(
            true,
            VisualState,
            cue,
            _screen.ActionsFor(
                Definition.Id,
                actionEvent.Value,
                itemContext,
                _selectedCollectionItem));

    }

    private static UiActionEvent DefaultEvent(UiPhysicalInput input) => input switch
    {
        UiPhysicalInput.PrimaryPressed => UiActionEvent.PrimaryPressed,
        UiPhysicalInput.PrimaryReleased => UiActionEvent.PrimaryReleased,
        UiPhysicalInput.SecondaryPressed => UiActionEvent.SecondaryPressed,
        UiPhysicalInput.SecondaryReleased => UiActionEvent.SecondaryReleased,
        UiPhysicalInput.DoublePressed => UiActionEvent.DoublePressed,
        UiPhysicalInput.HoverEntered => UiActionEvent.HoverEntered,
        UiPhysicalInput.HoverExited => UiActionEvent.HoverExited,
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, null),
    };

    private UiActionDispatch EmptyDispatch => new(false, VisualState, null, []);
}

public sealed class UiScreenState
{
    private readonly Dictionary<string, string?> _selectedRoles = new(StringComparer.Ordinal);

    public UiScreenState(UiScreenDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        IsVisible = definition.InitiallyVisible;
        Roles = definition.Roles.ToDictionary(
            role => role.Id,
            role => new UiRoleState(definition, role.Id, SelectedCollectionItem),
            StringComparer.Ordinal);
        Collections = definition.Collections.ToDictionary(
            collection => collection.Id,
            collection => new UiCollectionState(
                definition,
                collection,
                () => Roles[collection.Role].CanReceiveInput
                    && Roles[collection.ItemRole].CanReceiveInput,
                SelectedCollectionItem),
            StringComparer.Ordinal);
        foreach (UiSelectionGroupDefinition group in definition.SelectionGroups)
        {
            _selectedRoles.Add(group.Id, group.InitialRole);
            if (group.InitialRole is { } initialRole)
            {
                Roles[initialRole].SetSelected(true, advanceVariant: true);
            }
        }

        foreach (UiRoleState role in Roles.Values)
        {
            role.SetToggleSelection(ToggleSelection);
            role.SetScreenVisible(IsVisible);
        }
    }

    public UiScreenDefinition Definition { get; }
    public bool IsVisible { get; private set; }
    public IReadOnlyDictionary<string, UiRoleState> Roles { get; }
    public IReadOnlyDictionary<string, UiCollectionState> Collections { get; }

    public string? SelectedRole(string groupId) =>
        _selectedRoles.TryGetValue(groupId, out string? selected)
            ? selected
            : throw new KeyNotFoundException($"Screen '{Definition.Id}' has no selection group '{groupId}'");

    private string? SelectedCollectionItem(string collectionId) =>
        Collections.TryGetValue(collectionId, out UiCollectionState? collection)
            ? collection.SelectedProductItemId
            : null;

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

    private bool ToggleSelection(UiRoleState role)
    {
        UiSelectionGroupDefinition? group = Definition.FindSelectionGroup(role.Definition.Id);
        if (group is null)
        {
            role.SetSelected(!role.IsSelected, advanceVariant: true);
            return true;
        }

        string? selected = _selectedRoles[group.Id];
        if (selected == role.Definition.Id)
        {
            if (group.AllowEmpty)
            {
                role.SetSelected(false, advanceVariant: true);
                _selectedRoles[group.Id] = null;
                return true;
            }

            return false;
        }

        if (selected is not null)
        {
            Roles[selected].SetSelected(false, advanceVariant: true);
        }

        role.SetSelected(true, advanceVariant: true);
        _selectedRoles[group.Id] = role.Definition.Id;
        return true;
    }
}
