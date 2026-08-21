using System;
using System.Collections.Generic;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The creation screen's scene half. The option list, the starting kit and the
/// spawn all come from the server; nothing on this screen is a client constant
/// (ADR 0032 section 2).
/// </summary>
public partial class CharacterCreateScreen : Control
{
    private SessionHost _session = null!;
    private CharacterCreateViewModel _model = null!;
    private ItemList _options = null!;
    private LineEdit _name = null!;
    private Label _nameMessage = null!;
    private Label _message = null!;
    private Label _description = null!;
    private Button _create = null!;
    private Button _back = null!;
    private CharacterPreview _preview = null!;
    private IReadOnlyList<ChargenOptionView>? _renderedOptions;
    private string _previewedOptionId = string.Empty;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _model = new CharacterCreateViewModel(_session.Auth, _session.Player);

        _options = GetNode<ItemList>("%Options");
        _name = GetNode<LineEdit>("%Name");
        _nameMessage = GetNode<Label>("%NameMessage");
        _message = GetNode<Label>("%Message");
        _description = GetNode<Label>("%Description");
        _create = GetNode<Button>("%Create");
        _back = GetNode<Button>("%Back");
        _preview = GetNode<CharacterPreview>("%Preview");

        ConvertedChrome.Mount(this, ConvertedChrome.CharacterCreateForm);

        _options.ItemSelected += index =>
        {
            _model.SelectedIndex = (int)index;
            Render();
        };
        _name.TextChanged += text =>
        {
            _model.Name = text;
            Render();
        };
        _name.TextSubmitted += _ => Submit();
        _create.Pressed += Submit;
        _back.Pressed += Leave;

        _name.MaxLength = CharacterName.MaximumLength;
        _name.PlaceholderText = $"{CharacterName.MinimumLength} to {CharacterName.MaximumLength} characters";
        Load();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            Leave();
            GetViewport().SetInputAsHandled();
        }
    }

    private async void Load()
    {
        await _model.LoadOptionsAsync();
        Render();
        _name.GrabFocus();
    }

    private void Leave()
    {
        _session.Flow.LeaveCreateCharacter();
        _session.Show(Screen.CharacterSelect);
    }

    private async void Submit()
    {
        if (!_model.CanSubmit)
        {
            Render();
            return;
        }

        SetInteractive(false);
        try
        {
            CharacterSummary? created = await _model.SubmitAsync();
            Render();
            if (created is null)
            {
                // NAME_TAKEN and NAME_INVALID are the server's answer, and the
                // form shows the server's sentence rather than its own guess.
                _name.GrabFocus();
                return;
            }

            _session.Player.SelectCharacter(created, _model.Selected);
            Leave();
        }
        catch (Exception exception)
        {
            GD.PushError($"Character create failed unexpectedly: {exception.GetType().Name}");
            _message.Text = "Something went wrong in the client. See the log.";
        }
        finally
        {
            SetInteractive(true);
        }
    }

    private void SetInteractive(bool interactive)
    {
        _create.Disabled = !interactive;
        _name.Editable = interactive;
        _options.FocusMode = interactive ? FocusModeEnum.All : FocusModeEnum.None;
    }

    private void Render()
    {
        // Rendering happens on every keystroke in the name field, and rebuilding
        // the list each time would drop the scroll position under the player.
        if (_renderedOptions != _model.Options)
        {
            _renderedOptions = _model.Options;
            _options.Clear();
            foreach (ChargenOptionView view in _model.Options)
            {
                _options.AddItem(view.Title);
            }
        }

        if (_model.SelectedIndex >= 0)
        {
            _options.Select(_model.SelectedIndex);
        }

        ChargenOptionView? selected = _model.SelectedView;
        if (selected is null)
        {
            _description.Text = "The server offers no playable options.";
            _previewedOptionId = string.Empty;
            _preview.Clear();
        }
        else
        {
            _description.Text = string.Join(
                '\n',
                selected.Subtitle,
                selected.Description,
                $"Spawns in {selected.SpawnZoneId}");

            // Rendering happens on every keystroke in the name field; rebuilding
            // the rig each time would reload a native character scene per character
            // typed.
            if (_previewedOptionId != selected.Id)
            {
                _previewedOptionId = selected.Id;
                _preview.ShowOption(_model.Selected!, selected.Title);
            }
        }

        _nameMessage.Text = _model.NameMessage ?? string.Empty;
        _nameMessage.Visible = _model.NameMessage is not null;
        _nameMessage.AddThemeColorOverride("font_color", UiTheme.MutedInk);
        _message.Text = _model.Message;
        _message.Visible = _model.Message.Length > 0;
        _message.AddThemeColorOverride(
            "font_color",
            _model.MessageIsError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        _create.Disabled = !_model.CanSubmit;
    }
}
