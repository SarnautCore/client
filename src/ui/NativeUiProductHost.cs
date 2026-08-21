using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SarnautCore.UI;

namespace SarnautCore;

/// <summary>
/// Mounts one compiled native UI product and adapts its validated roles to Godot controls.
/// Product controllers own behavior. This class owns resource loading, input, focus, cues,
/// cursors, visibility, typed values, and collection item instances.
/// </summary>
public partial class NativeUiProductHost : Control
{
    private const string VisualStateMetadata = "sarnaut_ui_visual_state";
    private static WeakReference<NativeUiProductHost>? s_cursorOwner;

    private readonly Dictionary<string, ScreenBinding> _screens = new(StringComparer.Ordinal);
    private readonly List<PackedScene> _retainedScenes = [];
    private UiCursorCatalog<Texture2D> _cursors = null!;
    private UiSoundCatalog<AudioStream> _sounds = null!;
    private Theme _productTheme = null!;
    private AudioStreamPlayer _audio = null!;
    private bool _renderingValue;

    public string ManifestPath { get; private set; } = string.Empty;
    public UiProductManifest Manifest { get; private set; } = null!;

    public static bool TryMount(
        Control owner,
        out NativeUiProductHost? host,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var candidate = new NativeUiProductHost
        {
            Name = "NativeUiProduct",
        };
        candidate.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        owner.AddChild(candidate);
        if (candidate.TryInitialize(out status))
        {
            host = candidate;
            return true;
        }

        owner.RemoveChild(candidate);
        candidate.Free();
        host = null;
        return false;
    }

    public UiScreenDefinition GetScreen(string screenId) =>
        _screens.TryGetValue(screenId, out ScreenBinding? screen)
            ? screen.Definition
            : throw new KeyNotFoundException($"Native UI product has no screen '{screenId}'");

    public UiRoleDefinition GetRole(UiScreenDefinition screen, string roleId) =>
        RequireScreen(screen).Definition.GetRole(roleId);

    public UiValueBinding GetValue(UiScreenDefinition screen, string valueId) =>
        RequireScreen(screen).Definition.Values.SingleOrDefault(value => value.Id == valueId)
        ?? throw new KeyNotFoundException($"Screen '{screen.Id}' has no value '{valueId}'");

    public UiCollectionBinding GetCollection(UiScreenDefinition screen, string collectionId) =>
        RequireScreen(screen).Definition.Collections.SingleOrDefault(
            collection => collection.Id == collectionId)
        ?? throw new KeyNotFoundException(
            $"Screen '{screen.Id}' has no collection '{collectionId}'");

    /// <summary>
    /// Registers a controller against the exact screen definition parsed by this host.
    /// The callback receives the complete invocation, including resolved typed arguments.
    /// Return true only when the controller accepts the invocation.
    /// </summary>
    public void RegisterController(
        UiScreenDefinition screen,
        Func<UiActionInvocation, bool> controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        RequireScreen(screen).Controllers.Add(controller);
    }

    public void SetScreenVisible(
        UiScreenDefinition screen,
        bool visible,
        bool focusFirst = true)
    {
        ScreenBinding binding = RequireScreen(screen);
        IReadOnlyList<string> cues = visible
            ? binding.State.Show()
            : binding.State.Hide();
        binding.Root.Visible = visible;
        foreach (string cue in cues)
        {
            PlayCue(cue);
        }

        if (visible && focusFirst)
        {
            ApplyFocusOrder(binding, grabFirst: true);
        }
        else if (!visible)
        {
            ResetCursor();
        }
    }

    public void SetRoleVisible(
        UiScreenDefinition screen,
        UiRoleDefinition role,
        bool visible)
    {
        ScreenBinding binding = RequireScreen(screen);
        UiRoleState state = RequireRole(binding, role);
        string? cue = visible ? state.Show() : state.Hide();
        if (binding.CollectionItemRoles.Contains(role.Id))
        {
            CollectionBinding collection = binding.Collections.Values.Single(
                candidate => candidate.Definition.ItemRole == role.Id);
            collection.ItemsRoot.Visible = visible;
        }
        else
        {
            binding.Controls[role.Id].Visible = visible;
        }

        PlayCue(cue);
        ApplyFocusOrder(binding, grabFirst: false);
    }

    public void SetInteractive(UiScreenDefinition screen, bool interactive)
    {
        ScreenBinding binding = RequireScreen(screen);
        binding.Interactive = interactive;
        foreach (Control control in binding.Controls.Values)
        {
            SetControlInteractive(control, interactive);
        }

        foreach (CollectionBinding collection in binding.Collections.Values)
        {
            foreach (CollectionItemBinding item in collection.Items.Values)
            {
                SetControlInteractive(item.Control, interactive && item.Enabled);
            }
        }
    }

    public void SetRoleEnabled(
        UiScreenDefinition screen,
        UiRoleDefinition role,
        bool enabled)
    {
        ScreenBinding binding = RequireScreen(screen);
        Control control = RequireControl(binding, role);
        SetControlInteractive(control, enabled && binding.Interactive);
    }

    public void Focus(UiScreenDefinition screen, UiRoleDefinition role)
    {
        ScreenBinding binding = RequireScreen(screen);
        Control control = RequireControl(binding, role);
        if (!binding.State.IsVisible || !control.Visible || control.FocusMode == FocusModeEnum.None)
        {
            throw new InvalidOperationException(
                $"Role '{screen.Id}.{role.Id}' cannot receive focus");
        }

        control.GrabFocus();
    }

    public string ReadText(UiScreenDefinition screen, UiValueBinding value)
    {
        ScreenBinding binding = RequireScreen(screen);
        RequireValue(binding, value, UiValueKind.Text, readable: true);
        return RequireControl(binding, binding.Definition.GetRole(value.Role)) switch
        {
            LineEdit input => input.Text,
            Label label => label.Text,
            Button button => button.Text,
            _ => throw ValueControlError(binding, value, "text"),
        };
    }

    public void SetText(UiScreenDefinition screen, UiValueBinding value, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ScreenBinding binding = RequireScreen(screen);
        RequireValue(binding, value, UiValueKind.Text, readable: false);
        Control control = RequireControl(binding, binding.Definition.GetRole(value.Role));
        _renderingValue = true;
        try
        {
            switch (control)
            {
                case LineEdit input:
                    input.Secret = value.Secret;
                    input.Text = text;
                    break;
                case Label label when !value.Secret:
                    label.Text = text;
                    break;
                case Button button when !value.Secret:
                    button.Text = text;
                    break;
                default:
                    throw ValueControlError(binding, value, "text");
            }
        }
        finally
        {
            _renderingValue = false;
        }
    }

    public void SetNumber(UiScreenDefinition screen, UiValueBinding value, double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        ScreenBinding binding = RequireScreen(screen);
        RequireValue(binding, value, UiValueKind.Number, readable: false);
        Control control = RequireControl(binding, binding.Definition.GetRole(value.Role));
        if (control is Godot.Range range)
        {
            range.Value = number;
            return;
        }

        if (control is not Control progress)
        {
            throw ValueControlError(binding, value, "number");
        }

        float width = binding.NumberWidths.TryGetValue(value.Id, out float originalWidth)
            ? originalWidth
            : progress.Size.X;
        if (width <= 0)
        {
            throw new InvalidDataException(
                $"Numeric role '{screen.Id}.{value.Role}' has no positive authored width");
        }

        binding.NumberWidths[value.Id] = width;
        progress.Size = new Vector2(width * Math.Clamp((float)number, 0f, 1f), progress.Size.Y);
    }

    public void SetBoolean(UiScreenDefinition screen, UiValueBinding value, bool enabled)
    {
        ScreenBinding binding = RequireScreen(screen);
        RequireValue(binding, value, UiValueKind.Boolean, readable: false);
        Control control = RequireControl(binding, binding.Definition.GetRole(value.Role));
        if (control is not BaseButton button)
        {
            throw ValueControlError(binding, value, "boolean");
        }

        button.ButtonPressed = enabled;
    }

    public void ReplaceCollection(
        UiScreenDefinition screen,
        UiCollectionBinding collection,
        IReadOnlyList<NativeUiCollectionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ScreenBinding binding = RequireScreen(screen);
        CollectionBinding mounted = RequireCollection(binding, collection);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeUiCollectionItem item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!ids.Add(item.ProductItemId))
            {
                throw new ArgumentException(
                    $"Collection '{screen.Id}.{collection.Id}' repeats item '{item.ProductItemId}'",
                    nameof(items));
            }
        }

        foreach (CollectionItemBinding old in mounted.Items.Values)
        {
            mounted.ItemsRoot.RemoveChild(old.Control);
            old.Control.Free();
        }

        mounted.Items.Clear();
        foreach (NativeUiCollectionItem item in items)
        {
            Node instance = mounted.ItemScene.Instantiate();
            if (instance is not Control control)
            {
                instance.Free();
                throw new InvalidDataException(
                    $"Collection item scene '{collection.ItemScene}' must have a Control root");
            }

            control.Name = $"Item_{mounted.Items.Count:D3}";
            mounted.ItemsRoot.AddChild(control);
            if (control is Label label)
            {
                label.Text = item.Text;
            }
            else if (control is Button button)
            {
                button.Text = item.Text;
                button.ToggleMode = collection.Selection == UiCollectionSelection.Single;
            }

            var itemBinding = new CollectionItemBinding(item.ProductItemId, control, item.Enabled);
            mounted.Items.Add(item.ProductItemId, itemBinding);
            SetControlInteractive(control, binding.Interactive && item.Enabled);
            WireCollectionItem(binding, mounted, itemBinding);
            ApplyCollectionVisualState(binding, mounted, itemBinding);
        }

        mounted.ItemsRoot.Visible = binding.State.Roles[collection.ItemRole].IsVisible;
        ApplyFocusOrder(binding, grabFirst: false);
    }

    public override void _ExitTree()
    {
        if (s_cursorOwner?.TryGetTarget(out NativeUiProductHost? owner) == true
            && ReferenceEquals(owner, this))
        {
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            s_cursorOwner = null;
        }

        foreach (PackedScene scene in _retainedScenes)
        {
            scene.Dispose();
        }
        _retainedScenes.Clear();
    }

    private bool TryInitialize(out string status)
    {
        try
        {
            ManifestPath = NativeUiProductLocation
                .ManifestCandidates(NativeContentSettings.NativeRoot)
                .FirstOrDefault(Godot.FileAccess.FileExists)
                ?? throw new FileNotFoundException(
                    $"No {NativeUiProductLocation.ManifestFile} under {NativeContentSettings.NativeRoot}");

            using var manifestStream = new MemoryStream(
                Godot.FileAccess.GetFileAsBytes(ManifestPath));
            Manifest = NativeUiProductManifestParser.Parse(manifestStream);
            if (Manifest.ResourceEncoding != UiProductResourceEncoding.Compiled)
            {
                throw new InvalidDataException(
                    "Runtime UI product must use compiled .scn and .res resources");
            }

            _productTheme = LoadRequired<Theme>(Manifest.Theme, "theme");
            Theme = _productTheme;
            _cursors = LoadCursorCatalog(Resolve(Manifest.CursorCatalog));
            _sounds = LoadSoundCatalog(Resolve(Manifest.SoundCatalog));
            UiProductCatalogBinding.Validate(Manifest, _cursors, _sounds);

            _audio = new AudioStreamPlayer { Name = "CuePlayer" };
            AddChild(_audio);

            foreach (UiScreenDefinition screen in Manifest.Screens)
            {
                BindScreen(screen);
            }

            ResetCursor();
            status = $"native UI v2: {ManifestPath} [{_screens.Count} screens]";
            return true;
        }
        catch (Exception exception)
        {
            status = $"native UI product unavailable: {exception.Message}";
            GD.PushError(status);
            return false;
        }
    }

    private void BindScreen(UiScreenDefinition definition)
    {
        PackedScene scene = LoadRequired<PackedScene>(definition.Scene, "screen scene");
        _retainedScenes.Add(scene);
        Node instance = scene.Instantiate();
        if (instance is not Control root)
        {
            instance.Free();
            throw new InvalidDataException(
                $"Native UI screen '{definition.Id}' must have a Control root");
        }

        root.Name = $"Screen_{definition.Id.Replace('-', '_')}";
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.Theme = _productTheme;
        AddChild(root);

        var binding = new ScreenBinding(definition, root);
        if (!_screens.TryAdd(definition.Id, binding))
        {
            throw new InvalidDataException($"Native UI screen '{definition.Id}' is duplicated");
        }

        foreach (UiRoleDefinition role in definition.Roles)
        {
            Control control = role.Node == "."
                ? root
                : root.GetNodeOrNull<Control>(role.Node)
                    ?? throw new InvalidDataException(
                        $"Native UI role '{definition.Id}.{role.Id}' has no Control at '{role.Node}'");
            if (!binding.Controls.TryAdd(role.Id, control))
            {
                throw new InvalidDataException(
                    $"Native UI role '{definition.Id}.{role.Id}' is duplicated");
            }

            control.Visible = binding.State.Roles[role.Id].IsVisible;
        }

        foreach (UiCollectionBinding collection in definition.Collections)
        {
            BindCollection(binding, collection);
        }

        foreach (UiRoleDefinition role in definition.Roles)
        {
            if (!binding.CollectionItemRoles.Contains(role.Id))
            {
                WireRole(binding, role);
            }
        }

        ValidateValueControls(binding);
        ApplyVisualStates(binding);
        ApplyFocusOrder(binding, grabFirst: false);
        root.Visible = binding.State.IsVisible;
    }

    private void BindCollection(ScreenBinding screen, UiCollectionBinding definition)
    {
        Control container = screen.Controls[definition.Role];
        Control prototype = screen.Controls[definition.ItemRole];
        prototype.Visible = false;
        screen.CollectionItemRoles.Add(definition.ItemRole);

        foreach (CanvasItem child in container.GetChildren().OfType<CanvasItem>())
        {
            child.Visible = false;
        }

        var itemsRoot = new VBoxContainer
        {
            Name = $"NativeItems_{definition.Id.Replace('-', '_')}",
            MouseFilter = MouseFilterEnum.Pass,
        };
        itemsRoot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        container.AddChild(itemsRoot);

        PackedScene itemScene = LoadRequired<PackedScene>(definition.ItemScene, "collection item scene");
        _retainedScenes.Add(itemScene);
        screen.Collections.Add(
            definition.Id,
            new CollectionBinding(definition, itemsRoot, itemScene));
    }

    private void WireRole(ScreenBinding screen, UiRoleDefinition definition)
    {
        Control control = screen.Controls[definition.Id];
        UiRoleState state = screen.State.Roles[definition.Id];
        control.MouseEntered += () => RouteRoleInput(screen, state, UiPhysicalInput.HoverEntered);
        control.MouseExited += () => RouteRoleInput(screen, state, UiPhysicalInput.HoverExited);
        control.GuiInput += input => HandleRoleGuiInput(screen, state, control, input);

        UiValueBinding? value = screen.Definition.Values.SingleOrDefault(
            candidate => candidate.Role == definition.Id);
        if (value is not null && control is LineEdit lineEdit)
        {
            lineEdit.Secret = value.Secret;
            lineEdit.TextChanged += _ =>
            {
                if (!_renderingValue && screen.Interactive)
                {
                    DispatchInvocations(screen, state.Dispatch(UiActionEvent.Changed));
                }
            };
            lineEdit.TextSubmitted += _ =>
            {
                if (screen.Interactive)
                {
                    DispatchInvocations(screen, state.Dispatch(UiActionEvent.Submitted));
                }
            };
        }

        if (control is BaseButton button
            && screen.Definition.FindButton(definition.Id) is { } buttonDefinition)
        {
            button.ToggleMode = buttonDefinition.Toggle;
        }
    }

    private void HandleRoleGuiInput(
        ScreenBinding screen,
        UiRoleState state,
        Control control,
        InputEvent input)
    {
        if (!screen.Interactive)
        {
            return;
        }

        if (input is InputEventMouseButton mouse)
        {
            UiPhysicalInput? physical = mouse.ButtonIndex switch
            {
                MouseButton.Left when mouse.Pressed && mouse.DoubleClick =>
                    UiPhysicalInput.DoublePressed,
                MouseButton.Left when mouse.Pressed => UiPhysicalInput.PrimaryPressed,
                MouseButton.Left => UiPhysicalInput.PrimaryReleased,
                MouseButton.Right when mouse.Pressed => UiPhysicalInput.SecondaryPressed,
                MouseButton.Right => UiPhysicalInput.SecondaryReleased,
                _ => null,
            };
            if (physical is { } routed)
            {
                RouteRoleInput(screen, state, routed);
                control.AcceptEvent();
                return;
            }

            if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelUp)
            {
                DispatchInvocations(screen, state.Dispatch(UiActionEvent.ZoomIn));
                control.AcceptEvent();
            }
            else if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelDown)
            {
                DispatchInvocations(screen, state.Dispatch(UiActionEvent.ZoomOut));
                control.AcceptEvent();
            }
            return;
        }

        if (input is not InputEventKey { Echo: false } key)
        {
            return;
        }

        if (key.Keycode is Key.Enter or Key.KpEnter or Key.Space)
        {
            RouteRoleInput(
                screen,
                state,
                key.Pressed ? UiPhysicalInput.PrimaryPressed : UiPhysicalInput.PrimaryReleased);
            control.AcceptEvent();
        }
        else if (key.Pressed && key.Keycode == Key.Escape)
        {
            DispatchInvocations(screen, state.Dispatch(UiActionEvent.Cancelled));
            control.AcceptEvent();
        }
        else if (key.Pressed && key.Keycode == Key.Tab)
        {
            DispatchInvocations(
                screen,
                state.Dispatch(
                    key.ShiftPressed
                        ? UiActionEvent.NavigatePrevious
                        : UiActionEvent.NavigateNext));
        }
    }

    private void RouteRoleInput(
        ScreenBinding screen,
        UiRoleState state,
        UiPhysicalInput input)
    {
        if (!screen.Interactive)
        {
            return;
        }

        if (input == UiPhysicalInput.HoverEntered)
        {
            SetCursor(state.Definition.Cursor);
        }
        else if (input == UiPhysicalInput.HoverExited)
        {
            ResetCursor();
        }

        UiActionDispatch dispatch = state.RouteInput(input);
        ApplyVisualStates(screen);
        PlayCue(dispatch.Cue);
        DispatchInvocations(screen, dispatch.Invocations);
    }

    private void WireCollectionItem(
        ScreenBinding screen,
        CollectionBinding collection,
        CollectionItemBinding item)
    {
        item.Control.MouseEntered += () =>
            RouteCollectionInput(screen, collection, item, UiPhysicalInput.HoverEntered);
        item.Control.MouseExited += () =>
            RouteCollectionInput(screen, collection, item, UiPhysicalInput.HoverExited);
        item.Control.GuiInput += input =>
        {
            if (!screen.Interactive || !item.Enabled)
            {
                return;
            }

            if (input is InputEventMouseButton mouse)
            {
                UiPhysicalInput? physical = mouse.ButtonIndex switch
                {
                    MouseButton.Left when mouse.Pressed && mouse.DoubleClick =>
                        UiPhysicalInput.DoublePressed,
                    MouseButton.Left when mouse.Pressed => UiPhysicalInput.PrimaryPressed,
                    MouseButton.Left => UiPhysicalInput.PrimaryReleased,
                    MouseButton.Right when mouse.Pressed => UiPhysicalInput.SecondaryPressed,
                    MouseButton.Right => UiPhysicalInput.SecondaryReleased,
                    _ => null,
                };
                if (physical is { } routed)
                {
                    RouteCollectionInput(screen, collection, item, routed);
                    item.Control.AcceptEvent();
                }
            }
            else if (input is InputEventKey { Echo: false } key
                && key.Keycode is Key.Enter or Key.KpEnter or Key.Space)
            {
                RouteCollectionInput(
                    screen,
                    collection,
                    item,
                    key.Pressed
                        ? UiPhysicalInput.PrimaryPressed
                        : UiPhysicalInput.PrimaryReleased);
                item.Control.AcceptEvent();
            }
        };
    }

    private void RouteCollectionInput(
        ScreenBinding screen,
        CollectionBinding collection,
        CollectionItemBinding item,
        UiPhysicalInput input)
    {
        if (!screen.Interactive || !item.Enabled)
        {
            return;
        }

        UiRoleDefinition role = screen.Definition.GetRole(collection.Definition.ItemRole);
        if (input == UiPhysicalInput.HoverEntered)
        {
            SetCursor(role.Cursor);
        }
        else if (input == UiPhysicalInput.HoverExited)
        {
            ResetCursor();
        }

        UiCollectionActionDispatch dispatch = screen.State.Collections[collection.Definition.Id]
            .RouteInput(item.ProductItemId, input);
        foreach (CollectionItemBinding candidate in collection.Items.Values)
        {
            ApplyCollectionVisualState(screen, collection, candidate);
        }
        DispatchInvocations(screen, dispatch.Invocations);
    }

    private void DispatchInvocations(
        ScreenBinding screen,
        IReadOnlyList<UiActionInvocation> invocations)
    {
        foreach (UiActionInvocation invocation in invocations)
        {
            bool handled = false;
            foreach (Func<UiActionInvocation, bool> controller in screen.Controllers.ToArray())
            {
                handled |= controller(invocation);
            }

            if (!handled)
            {
                GD.PushError(
                    $"Native UI action '{screen.Definition.Id}.{invocation.Id}' has no accepting controller");
            }
        }
    }

    private void ApplyVisualStates(ScreenBinding screen)
    {
        foreach (UiButtonDefinition buttonDefinition in screen.Definition.Buttons)
        {
            if (screen.CollectionItemRoles.Contains(buttonDefinition.Role))
            {
                continue;
            }

            UiRoleState state = screen.State.Roles[buttonDefinition.Role];
            Control control = screen.Controls[buttonDefinition.Role];
            control.SetMeta(VisualStateMetadata, state.VisualState);
            if (control is BaseButton button)
            {
                button.ButtonPressed = state.IsSelected;
            }
        }
    }

    private static void ApplyCollectionVisualState(
        ScreenBinding screen,
        CollectionBinding collection,
        CollectionItemBinding item)
    {
        UiCollectionState state = screen.State.Collections[collection.Definition.Id];
        string visualState = state.VisualStateFor(item.ProductItemId);
        item.Control.SetMeta(VisualStateMetadata, visualState);
        if (item.Control is BaseButton button)
        {
            button.ButtonPressed = state.IsSelected(item.ProductItemId);
        }

        Node? highlight = item.Control.GetNodeOrNull("Highlight");
        if (highlight is CanvasItem highlightCanvas)
        {
            highlightCanvas.Visible = state.IsSelected(item.ProductItemId);
        }
    }

    private void ApplyFocusOrder(ScreenBinding screen, bool grabFirst)
    {
        var focusable = new List<Control>();
        foreach (string roleId in screen.Definition.FocusOrder)
        {
            CollectionBinding? collection = screen.Collections.Values.SingleOrDefault(
                candidate => candidate.Definition.ItemRole == roleId);
            if (collection is not null)
            {
                focusable.AddRange(collection.Items.Values
                    .Where(item => item.Enabled && item.Control.Visible)
                    .Select(item => item.Control));
                continue;
            }

            Control control = screen.Controls[roleId];
            if (control.Visible && control.FocusMode != FocusModeEnum.None)
            {
                focusable.Add(control);
            }
        }

        if (focusable.Count == 0)
        {
            return;
        }

        for (int index = 0; index < focusable.Count; index++)
        {
            Control current = focusable[index];
            Control next = focusable[(index + 1) % focusable.Count];
            Control previous = focusable[(index + focusable.Count - 1) % focusable.Count];
            current.FocusNext = current.GetPathTo(next);
            current.FocusPrevious = current.GetPathTo(previous);
        }

        if (grabFirst && screen.State.IsVisible)
        {
            focusable[0].GrabFocus();
        }
    }

    private void ValidateValueControls(ScreenBinding screen)
    {
        foreach (UiValueBinding value in screen.Definition.Values)
        {
            Control control = screen.Controls[value.Role];
            bool valid = value.Kind switch
            {
                UiValueKind.Text => control is LineEdit or Label or Button,
                UiValueKind.Number => control is Godot.Range or Control,
                UiValueKind.Boolean => control is BaseButton,
                _ => false,
            };
            if (!valid || value.Secret && control is not LineEdit)
            {
                throw ValueControlError(screen, value, value.Kind.ToString().ToLowerInvariant());
            }
        }
    }

    private TResource LoadRequired<TResource>(NativeContentPath path, string label)
        where TResource : Resource
    {
        string resolved = Resolve(path);
        return ResourceLoader.Load<TResource>(resolved)
            ?? throw new FileNotFoundException($"Native UI {label} is missing: {resolved}");
    }

    private string Resolve(NativeContentPath path) =>
        NativeUiProductLocation.Resolve(ManifestPath, path);

    private ScreenBinding RequireScreen(UiScreenDefinition screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (_screens.TryGetValue(screen.Id, out ScreenBinding? binding)
            && ReferenceEquals(binding.Definition, screen))
        {
            return binding;
        }

        throw new ArgumentException(
            "Screen definition does not belong to this native UI host",
            nameof(screen));
    }

    private static UiRoleState RequireRole(ScreenBinding screen, UiRoleDefinition role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (screen.State.Roles.TryGetValue(role.Id, out UiRoleState? state)
            && ReferenceEquals(state.Definition, role))
        {
            return state;
        }

        throw new ArgumentException(
            "Role definition does not belong to this native UI screen",
            nameof(role));
    }

    private static Control RequireControl(ScreenBinding screen, UiRoleDefinition role)
    {
        RequireRole(screen, role);
        return screen.Controls[role.Id];
    }

    private static void RequireValue(
        ScreenBinding screen,
        UiValueBinding value,
        UiValueKind kind,
        bool readable)
    {
        ArgumentNullException.ThrowIfNull(value);
        UiValueBinding? owned = screen.Definition.Values.SingleOrDefault(
            candidate => candidate.Id == value.Id);
        if (!ReferenceEquals(owned, value))
        {
            throw new ArgumentException(
                "Value definition does not belong to this native UI screen",
                nameof(value));
        }

        if (value.Kind != kind)
        {
            throw new ArgumentException(
                $"Value '{screen.Definition.Id}.{value.Id}' is not {kind}",
                nameof(value));
        }

        bool allowed = readable
            ? value.Access is UiValueAccess.Read or UiValueAccess.ReadWrite
            : value.Access is UiValueAccess.Write or UiValueAccess.ReadWrite;
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"Value '{screen.Definition.Id}.{value.Id}' does not allow this access");
        }
    }

    private static CollectionBinding RequireCollection(
        ScreenBinding screen,
        UiCollectionBinding collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (screen.Collections.TryGetValue(collection.Id, out CollectionBinding? binding)
            && ReferenceEquals(binding.Definition, collection))
        {
            return binding;
        }

        throw new ArgumentException(
            "Collection definition does not belong to this native UI screen",
            nameof(collection));
    }

    private static InvalidDataException ValueControlError(
        ScreenBinding screen,
        UiValueBinding value,
        string expected) => new(
            $"Native UI value '{screen.Definition.Id}.{value.Id}' has no compatible {expected} control at role '{value.Role}'");

    private static void SetControlInteractive(Control control, bool interactive)
    {
        if (control is BaseButton button)
        {
            button.Disabled = !interactive;
        }
        if (control is LineEdit input)
        {
            input.Editable = interactive;
        }
    }

    private void PlayCue(string? cue)
    {
        if (cue is null)
        {
            return;
        }

        _audio.Stream = _sounds.GetRequired(cue).Sound;
        _audio.Play();
    }

    private void SetCursor(string? key)
    {
        if (key is null
            || !_cursors.TryGet(key, out UiCursorAsset<Texture2D>? cursor)
            || cursor is null)
        {
            return;
        }

        Input.SetCustomMouseCursor(
            cursor.Texture,
            Input.CursorShape.Arrow,
            new Vector2(cursor.Hotspot.X, cursor.Hotspot.Y));
        s_cursorOwner = new WeakReference<NativeUiProductHost>(this);
    }

    private void ResetCursor()
    {
        if (_cursors.TryGet("default", out UiCursorAsset<Texture2D>? cursor)
            && cursor is not null)
        {
            Input.SetCustomMouseCursor(
                cursor.Texture,
                Input.CursorShape.Arrow,
                new Vector2(cursor.Hotspot.X, cursor.Hotspot.Y));
        }
        else
        {
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
        }

        s_cursorOwner = new WeakReference<NativeUiProductHost>(this);
    }

    private static UiCursorCatalog<Texture2D> LoadCursorCatalog(string path)
    {
        using Resource resource = ResourceLoader.Load<Resource>(path)
            ?? throw new FileNotFoundException($"Native cursor catalog is missing: {path}");
        Godot.Collections.Dictionary entries = RequireDictionaryMetadata(resource, "cursors", path);
        var cursors = new List<UiCursorAsset<Texture2D>>(entries.Count);
        foreach (Variant rawKey in entries.Keys)
        {
            if (rawKey.VariantType != Variant.Type.String)
            {
                throw new InvalidDataException(
                    $"Native cursor catalog contains a non-string key: {path}");
            }

            string key = rawKey.AsString();
            Variant rawEntry = entries[rawKey];
            if (rawEntry.VariantType != Variant.Type.Dictionary)
            {
                throw new InvalidDataException(
                    $"Native cursor '{key}' is not a dictionary in {path}");
            }

            Godot.Collections.Dictionary entry = rawEntry.AsGodotDictionary();
            RequireExactCursorFields(entry, path, key);
            Texture2D? texture = entry["texture"].VariantType == Variant.Type.Object
                ? entry["texture"].AsGodotObject() as Texture2D
                : null;
            if (texture is null || entry["hotspot"].VariantType != Variant.Type.Vector2I)
            {
                throw new InvalidDataException(
                    $"Native cursor '{key}' has an incompatible resource in {path}");
            }

            Vector2I hotspot = entry["hotspot"].AsVector2I();
            if (hotspot.X < 0
                || hotspot.Y < 0
                || hotspot.X >= texture.GetWidth()
                || hotspot.Y >= texture.GetHeight())
            {
                throw new InvalidDataException(
                    $"Native cursor '{key}' hotspot is outside its texture in {path}");
            }

            cursors.Add(new UiCursorAsset<Texture2D>(
                key,
                new UiCursorHotspot(hotspot.X, hotspot.Y),
                texture));
        }

        var catalog = new UiCursorCatalog<Texture2D>(cursors);
        catalog.GetRequired("default");
        return catalog;
    }

    private static UiSoundCatalog<AudioStream> LoadSoundCatalog(string path)
    {
        using Resource resource = ResourceLoader.Load<Resource>(path)
            ?? throw new FileNotFoundException($"Native sound catalog is missing: {path}");
        Godot.Collections.Dictionary entries = RequireDictionaryMetadata(resource, "sounds", path);
        var sounds = new List<UiSoundAsset<AudioStream>>(entries.Count);
        foreach (Variant rawKey in entries.Keys)
        {
            if (rawKey.VariantType != Variant.Type.String)
            {
                throw new InvalidDataException(
                    $"Native sound catalog contains a non-string key: {path}");
            }

            string key = rawKey.AsString();
            AudioStream? sound = entries[rawKey].VariantType == Variant.Type.Object
                ? entries[rawKey].AsGodotObject() as AudioStream
                : null;
            if (sound is null)
            {
                throw new InvalidDataException(
                    $"Native sound '{key}' has no AudioStream in {path}");
            }

            sounds.Add(new UiSoundAsset<AudioStream>(key, sound));
        }

        return new UiSoundCatalog<AudioStream>(sounds);
    }

    private static Godot.Collections.Dictionary RequireDictionaryMetadata(
        Resource resource,
        string key,
        string path)
    {
        if (!resource.HasMeta(key))
        {
            throw new InvalidDataException(
                $"Native UI catalog has no '{key}' dictionary: {path}");
        }

        Variant metadata = resource.GetMeta(key);
        if (metadata.VariantType != Variant.Type.Dictionary)
        {
            throw new InvalidDataException(
                $"Native UI catalog '{key}' metadata is not a dictionary: {path}");
        }

        return metadata.AsGodotDictionary();
    }

    private static void RequireExactCursorFields(
        Godot.Collections.Dictionary dictionary,
        string path,
        string cursor)
    {
        if (dictionary.Keys.Any(key => key.VariantType != Variant.Type.String))
        {
            throw new InvalidDataException(
                $"Native cursor '{cursor}' has a non-string field in {path}");
        }

        var fields = dictionary.Keys
            .Select(key => key.AsString())
            .ToHashSet(StringComparer.Ordinal);
        if (fields.Count != 2 || !fields.SetEquals(["texture", "hotspot"]))
        {
            throw new InvalidDataException(
                $"Native cursor '{cursor}' has incompatible fields in {path}");
        }
    }

    private sealed class ScreenBinding
    {
        public ScreenBinding(UiScreenDefinition definition, Control root)
        {
            Definition = definition;
            Root = root;
            State = new UiScreenState(definition);
        }

        public UiScreenDefinition Definition { get; }
        public Control Root { get; }
        public UiScreenState State { get; }
        public Dictionary<string, Control> Controls { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CollectionBinding> Collections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CollectionItemRoles { get; } = new(StringComparer.Ordinal);
        public List<Func<UiActionInvocation, bool>> Controllers { get; } = [];
        public Dictionary<string, float> NumberWidths { get; } = new(StringComparer.Ordinal);
        public bool Interactive { get; set; } = true;
    }

    private sealed class CollectionBinding
    {
        public CollectionBinding(
            UiCollectionBinding definition,
            VBoxContainer itemsRoot,
            PackedScene itemScene)
        {
            Definition = definition;
            ItemsRoot = itemsRoot;
            ItemScene = itemScene;
        }

        public UiCollectionBinding Definition { get; }
        public VBoxContainer ItemsRoot { get; }
        public PackedScene ItemScene { get; }
        public Dictionary<string, CollectionItemBinding> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CollectionItemBinding(
        string ProductItemId,
        Control Control,
        bool Enabled);
}

public sealed record NativeUiCollectionItem(
    string ProductItemId,
    string Text,
    bool Enabled = true);
