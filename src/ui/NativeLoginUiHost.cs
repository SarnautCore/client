using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SarnautCore.Shell;
using SarnautCore.UI;

namespace SarnautCore;

/// <summary>Adapts the native LoginAccount product scene to the account view model.</summary>
public partial class NativeLoginUiHost : Control
{
    private const string VisualStateMetadata = "sarnaut_ui_visual_state";

    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private LoginViewModel _model = null!;
    private Action<string> _dispatchAction = null!;
    private LoginAccountProduct _product = null!;
    private UiScreenState _screenState = null!;
    private UiCursorCatalog<Texture2D> _cursors = null!;
    private UiSoundCatalog<AudioStream> _sounds = null!;
    private LineEdit _account = null!;
    private LineEdit _password = null!;
    private Button _enter = null!;
    private AudioStreamPlayer _audio = null!;
    private bool _interactive = true;

    public string ManifestPath { get; private set; } = string.Empty;

    public static bool TryMount(
        Control owner,
        LoginViewModel model,
        Action<string> dispatchAction,
        out NativeLoginUiHost? host,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(dispatchAction);

        var candidate = new NativeLoginUiHost
        {
            Name = "NativeLoginUi",
        };
        candidate.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        owner.AddChild(candidate);
        if (candidate.TryInitialize(model, dispatchAction, out status))
        {
            host = candidate;
            return true;
        }

        owner.RemoveChild(candidate);
        candidate.QueueFree();
        host = null;
        return false;
    }

    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        _account.Editable = interactive;
        _password.Editable = interactive;
        foreach (Button button in _controls.Values.OfType<Button>())
        {
            button.Disabled = !interactive;
        }

        RenderModelState();
    }

    public void RenderModelState()
    {
        _enter.Disabled = !_interactive || !_model.CanSubmit;
    }

    public void ClearPassword()
    {
        _password.Clear();
    }

    public void FocusPassword()
    {
        _password.GrabFocus();
    }

    public override void _ExitTree()
    {
        Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
    }

    private bool TryInitialize(
        LoginViewModel model,
        Action<string> dispatchAction,
        out string status)
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
            UiProductManifest manifest = NativeUiProductManifestParser.Parse(manifestStream);
            _product = LoginAccountProduct.Bind(manifest);
            _model = model;
            _dispatchAction = dispatchAction;

            string scenePath = NativeUiProductLocation.Resolve(ManifestPath, _product.Screen.Scene);
            string cursorPath = NativeUiProductLocation.Resolve(ManifestPath, manifest.CursorCatalog);
            string soundPath = NativeUiProductLocation.Resolve(ManifestPath, manifest.SoundCatalog);
            _cursors = LoadCursorCatalog(cursorPath);
            _sounds = LoadSoundCatalog(soundPath);
            UiProductCatalogBinding.Validate(manifest, _cursors, _sounds);

            using PackedScene scene = ResourceLoader.Load<PackedScene>(scenePath)
                ?? throw new FileNotFoundException($"Native login scene is missing: {scenePath}");
            if (scene.Instantiate() is not Control screenRoot)
            {
                throw new InvalidDataException(
                    $"Native login scene root must be Control: {scenePath}");
            }

            screenRoot.Name = "LoginAccount";
            screenRoot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(screenRoot);

            _audio = new AudioStreamPlayer { Name = "CuePlayer" };
            AddChild(_audio);
            _screenState = new UiScreenState(_product.Screen);
            ResolveRoles(screenRoot);
            WireRoles();

            foreach ((string roleId, UiRoleState roleState) in _screenState.Roles)
            {
                _controls[roleId].Visible = roleState.IsVisible;
            }

            ApplyFocusOrder();

            screenRoot.Visible = true;
            foreach (string cue in _screenState.Show())
            {
                PlayCue(cue);
            }

            _account.Text = model.Email;
            _password.Secret = true;
            RenderModelState();
            ResetCursor();
            _account.GrabFocus();
            status = $"native UI: {ManifestPath} [{_product.Screen.Id}]";
            return true;
        }
        catch (Exception exception)
        {
            status = $"native login content unavailable: {exception.Message}";
            GD.PushError(status);
            return false;
        }
    }

    private void ResolveRoles(Control screenRoot)
    {
        foreach (UiRoleDefinition role in _product.Screen.Roles)
        {
            Control control = screenRoot.GetNodeOrNull<Control>(role.Node)
                ?? throw new InvalidDataException(
                    $"Native login role '{role.Id}' has no Control at '{role.Node}'");
            if (!_controls.TryAdd(role.Id, control))
            {
                throw new InvalidDataException($"Native login role '{role.Id}' is duplicated");
            }
        }

        _account = RequireRole<LineEdit>(LoginAccountProduct.AccountRole);
        _password = RequireRole<LineEdit>(LoginAccountProduct.PasswordRole);
        _enter = RequireRole<Button>(LoginAccountProduct.EnterRole);
        RequireRole<Button>(LoginAccountProduct.OptionsRole);
        RequireRole<Button>(LoginAccountProduct.LocalRole);
        RequireRole<Button>(LoginAccountProduct.CreditsRole);
        RequireRole<Button>(LoginAccountProduct.ExitRole);
    }

    private void WireRoles()
    {
        foreach (UiRoleDefinition definition in _product.Screen.Roles)
        {
            Control control = _controls[definition.Id];
            UiRoleState state = _screenState.Roles[definition.Id];
            control.MouseEntered += () =>
            {
                SetCursor(definition.Cursor);
                PlayCue(state.PointerEntered());
            };
            control.MouseExited += () =>
            {
                state.PointerExited();
                ResetCursor();
            };

            if (control is LineEdit input)
            {
                WireInput(input, definition.Id, state);
            }

            if (control is Button button)
            {
                WireButton(button, definition.Id, state);
            }
        }
    }

    private void WireInput(LineEdit input, string roleId, UiRoleState state)
    {
        UiValueBinding value = _product.Screen.Values.Single(binding => binding.Role == roleId);
        input.Secret = value.Secret;
        input.TextChanged += text =>
        {
            if (value.Id == LoginAccountProduct.AccountValue)
            {
                _model.Email = text;
            }
            else if (value.Id == LoginAccountProduct.PasswordValue)
            {
                _model.Password = new Secret(text);
            }

            Dispatch(state.Dispatch(UiActionEvent.Changed));
            RenderModelState();
        };
        input.TextSubmitted += _ =>
        {
            if (_interactive)
            {
                Dispatch(state.Dispatch(UiActionEvent.Submitted));
            }
        };
        input.GuiInput += inputEvent =>
        {
            if (_interactive
                && inputEvent is InputEventKey
                {
                    Pressed: true,
                    Echo: false,
                    Keycode: Key.Escape,
                })
            {
                Dispatch(state.Dispatch(UiActionEvent.Cancelled));
                input.AcceptEvent();
            }
        };
    }

    private void WireButton(Button button, string roleId, UiRoleState state)
    {
        UiButtonDefinition definition = _product.Screen.FindButton(roleId)
            ?? throw new InvalidDataException(
                $"Native login button role '{roleId}' has no button definition");
        button.ToggleMode = definition.Toggle;
        ApplyVisualState(button, state);
        button.ButtonDown += () => PlayCue(state.BeginPress());
        button.Pressed += () =>
        {
            if (!state.IsPressed)
            {
                PlayCue(state.BeginPress());
            }

            UiActionDispatch dispatch = state.EndPress(activate: true);
            ApplyVisualState(button, state);
            Dispatch(dispatch.ActionIds);
        };
    }

    private void ApplyFocusOrder()
    {
        Control[] focusable = _product.Screen.FocusOrder
            .Select(roleId => _controls[roleId])
            .Where(control => control.Visible && control.FocusMode != FocusModeEnum.None)
            .ToArray();
        if (focusable.Length == 0)
        {
            throw new InvalidDataException("Native login screen has no visible focus target");
        }

        for (int index = 0; index < focusable.Length; index++)
        {
            Control current = focusable[index];
            Control next = focusable[(index + 1) % focusable.Length];
            Control previous = focusable[(index + focusable.Length - 1) % focusable.Length];
            current.FocusNext = current.GetPathTo(next);
            current.FocusPrevious = current.GetPathTo(previous);
        }
    }

    private TControl RequireRole<TControl>(string roleId) where TControl : Control
    {
        if (_controls[roleId] is TControl control)
        {
            return control;
        }

        throw new InvalidDataException(
            $"Native login role '{roleId}' must be {typeof(TControl).Name}");
    }

    private void Dispatch(IEnumerable<string> actions)
    {
        foreach (string action in actions)
        {
            _dispatchAction(action);
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
            return;
        }

        Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
    }

    private static void ApplyVisualState(Button button, UiRoleState state)
    {
        button.SetMeta(VisualStateMetadata, state.VisualState);
    }

    private static UiCursorCatalog<Texture2D> LoadCursorCatalog(string path)
    {
        using Resource resource = ResourceLoader.Load<Resource>(path)
            ?? throw new FileNotFoundException($"Native cursor catalog is missing: {path}");
        Godot.Collections.Dictionary entries = RequireDictionaryMetadata(
            resource,
            "cursors",
            path);
        var cursors = new List<UiCursorAsset<Texture2D>>(entries.Count);
        foreach (Variant rawKey in entries.Keys)
        {
            string key = rawKey.AsString();
            Godot.Collections.Dictionary entry = entries[rawKey].AsGodotDictionary();
            Texture2D texture = entry["texture"].AsGodotObject() as Texture2D
                ?? throw new InvalidDataException(
                    $"Native cursor '{key}' has no Texture2D in {path}");
            Vector2I hotspot = entry["hotspot"].AsVector2I();
            cursors.Add(new UiCursorAsset<Texture2D>(
                key,
                new UiCursorHotspot(hotspot.X, hotspot.Y),
                texture));
        }

        return new UiCursorCatalog<Texture2D>(cursors);
    }

    private static UiSoundCatalog<AudioStream> LoadSoundCatalog(string path)
    {
        using Resource resource = ResourceLoader.Load<Resource>(path)
            ?? throw new FileNotFoundException($"Native sound catalog is missing: {path}");
        Godot.Collections.Dictionary entries = RequireDictionaryMetadata(
            resource,
            "sounds",
            path);
        var sounds = new List<UiSoundAsset<AudioStream>>(entries.Count);
        foreach (Variant rawKey in entries.Keys)
        {
            string key = rawKey.AsString();
            AudioStream sound = entries[rawKey].AsGodotObject() as AudioStream
                ?? throw new InvalidDataException(
                    $"Native sound '{key}' has no AudioStream in {path}");
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
}
