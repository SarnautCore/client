using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The roster screen's scene half. Character selection happens entirely against
/// the account service, before any game connection exists (session spec
/// rule 5.3), so nothing here talks to a shard.
/// </summary>
public partial class CharacterSelectScreen : Control
{
    private SessionHost _session = null!;
    private CharacterSelectViewModel _model = null!;
    private IReadOnlyList<ChargenOption> _options = [];
    private IReadOnlyList<CharacterSummary>? _renderedCharacters;
    private ItemList _characters = null!;
    private Label _message = null!;
    private Label _account = null!;
    private Label _detail = null!;
    private Button _enter = null!;
    private Button _create = null!;
    private Button _signOut = null!;

    public override void _Ready()
    {
        _session = SessionHost.Of(this);
        _model = new CharacterSelectViewModel(_session.Auth, _session.Player);

        _characters = GetNode<ItemList>("%Characters");
        _message = GetNode<Label>("%Message");
        _account = GetNode<Label>("%Account");
        _detail = GetNode<Label>("%Detail");
        _enter = GetNode<Button>("%Enter");
        _create = GetNode<Button>("%Create");
        _signOut = GetNode<Button>("%SignOut");

        ConvertedChrome.Mount(this, ConvertedChrome.CharacterSelectForm);
        _account.Text = _session.Player.Account is null
            ? "Not signed in"
            : $"Account {_session.Player.Account.AccountId}";

        _characters.ItemSelected += index =>
        {
            _model.SelectedIndex = (int)index;
            Render();
        };
        _characters.ItemActivated += _ => Enter();
        _enter.Pressed += Enter;
        _create.Pressed += () =>
        {
            _session.Flow.CreateCharacter();
            _session.Show(Screen.CharacterCreate);
        };
        _signOut.Pressed += _session.SignOut;

        Load();
    }

    private async void Load()
    {
        try
        {
            // The option list comes with the roster so the spawn zone a
            // character enters is the server's, not a client constant.
            _options = await _session.Auth.ListChargenOptionsAsync();
        }
        catch (AuthException exception)
        {
            _options = [];
            GD.Print($"Character select: no chargen options ({exception.Failure}).");
        }

        await _model.RefreshAsync();
        if (_session.Player.Character is not null)
        {
            _model.SelectById(_session.Player.Character.CharacterId);
        }

        Render();
        if (_model.LastFailure == AuthFailure.Unauthenticated)
        {
            _session.SignOut();
        }
    }

    private async void Enter()
    {
        if (!_model.CanEnterWorld)
        {
            return;
        }

        CharacterSummary character = _model.Selected!;
        ChargenOption? option = OptionFor(character);
        SetInteractive(false);
        try
        {
            ShardTicket? ticket = await _model.EnterWorldAsync(option);
            Render();
            if (ticket is null)
            {
                return;
            }

            _session.Zone = new ZoneRequest(
                _session.Zone.MapName,
                option?.SpawnZoneId ?? _session.Zone.ZoneId,
                _session.ServerAddress,
                Online: true,
                ticket.Token,
                option is null ? null : new ZoneSpawn(option.SpawnX, option.SpawnY, option.SpawnZ));
            _session.Flow.EnterWorld();
            _session.Show(Screen.EnteringWorld);
        }
        catch (Exception exception)
        {
            GD.PushError($"Character select failed unexpectedly: {exception.GetType().Name}");
            _message.Text = "Something went wrong in the client. See the log.";
        }
        finally
        {
            SetInteractive(true);
        }
    }

    private ChargenOption? OptionFor(CharacterSummary character) =>
        _options.FirstOrDefault(option => option.Id == character.ChargenOptionId);

    private void SetInteractive(bool interactive)
    {
        _enter.Disabled = !interactive;
        _create.Disabled = !interactive;
        _characters.FocusMode = interactive ? FocusModeEnum.All : FocusModeEnum.None;
    }

    private void Render()
    {
        // Rebuilt only when the roster itself changed; rendering also runs on a
        // plain selection change, and clearing the list there would fight the
        // click that caused it.
        if (_renderedCharacters != _model.Characters)
        {
            _renderedCharacters = _model.Characters;
            _characters.Clear();
            foreach (CharacterSummary character in _model.Characters)
            {
                ChargenOption? option = OptionFor(character);
                string suffix = option is null
                    ? character.ChargenOptionId
                    : ChargenOptionView.From(option).Title;
                _characters.AddItem($"{character.Name}   —   {suffix}");
            }
        }

        if (_model.SelectedIndex >= 0)
        {
            _characters.Select(_model.SelectedIndex);
        }

        CharacterSummary? selected = _model.Selected;
        ChargenOption? selectedOption = selected is null ? null : OptionFor(selected);
        _detail.Text = selected is null
            ? "Choose a character, or create one."
            : $"{selected.Name}\n{selectedOption?.SpawnZoneId ?? "zone from the server"}"
                + $"\ncreated {selected.CreatedAt:yyyy-MM-dd}";

        _message.Text = _model.Message;
        _message.Visible = _model.Message.Length > 0;
        _message.AddThemeColorOverride(
            "font_color",
            _model.MessageIsError ? UiTheme.ErrorInk : UiTheme.MutedInk);
        _enter.Disabled = !_model.CanEnterWorld;
    }
}
