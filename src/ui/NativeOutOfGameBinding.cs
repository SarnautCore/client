using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SarnautCore.Shell;
using SarnautCore.UI;

namespace SarnautCore;

/// <summary>
/// Joins the compiled native out-of-game product to the plain controllers and
/// the account/session models. LoginScreen owns this object's whole lifetime.
/// </summary>
internal sealed partial class NativeOutOfGameBinding : IDisposable,
    IEulaViewPort,
    ICreditsClock,
    ICreditsMusic,
    ICreditsTooltip,
    ICreditsContent
{
    internal const string ClientVersion = "1.1.02.0-native";
    private const string DefaultShardId = "default-shard";
    private const string MainMenuMusicCue = "main_menu_music";
    private const string EulaSection = "eula";
    private const string EulaVersionKey = "accepted-version";
    private const string EulaConfigPath = "user://sarnaut-ui.cfg";

    private readonly LoginScreen _owner;
    private readonly NativeUiProductHost _host;
    private readonly SessionHost _session;
    private readonly Action<string, bool> _showStatus;
    private readonly OutOfGameFlowController _flow;
    private readonly LoginViewModel _login;
    private readonly CharacterSelectViewModel _characters;
    private readonly CharacterCreateViewModel _creation;
    private readonly EulaClientBinding? _eula;
    private readonly CreditsTimeline _creditsTimeline;
    private readonly UiScreenDefinition _mainScreen;
    private readonly UiScreenDefinition _eulaScreen;
    private readonly UiScreenDefinition _loginScreen;
    private readonly UiScreenDefinition _creditsScreen;
    private readonly UiScreenDefinition _tooltipScreen;
    private readonly UiScreenDefinition _connectionScreen;
    private readonly UiScreenDefinition _shardScreen;
    private readonly UiScreenDefinition _characterScreen;
    private readonly UiScreenDefinition _preGeneratorScreen;
    private readonly UiScreenDefinition _generatorScreen;
    private readonly UiScreenDefinition _messageScreen;
    private readonly UiValueBinding _accountValue;
    private readonly UiValueBinding _passwordValue;
    private readonly UiValueBinding _versionValue;
    private readonly UiValueBinding _connectionStatusValue;
    private readonly UiValueBinding _currentShardValue;
    private readonly UiValueBinding _deleteNameValue;
    private readonly UiValueBinding _deleteStatusValue;
    private readonly UiValueBinding _characterNameValue;
    private readonly UiValueBinding _messageTitleValue;
    private readonly UiValueBinding _messageBodyValue;
    private readonly UiCollectionBinding _shardsCollection;
    private readonly UiCollectionBinding _charactersCollection;
    private readonly UiCollectionBinding _tooltipCollection;
    private readonly UiRoleDefinition _deletePanelRole;
    private readonly IReadOnlyList<UiScreenDefinition> _screens;
    private readonly Dictionary<string, CharacterSummary> _charactersByProductId =
        new(StringComparer.Ordinal);
    private IReadOnlyList<ChargenOption> _chargenOptions = [];
    private readonly string _deletePromptTemplate;
    private CancellationTokenSource? _operation;
    private EulaViewState? _eulaState;
    private bool _creditsOpen;
    private bool _initialLoadStarted;
    private bool _disposed;
    private CreditsController? _credits;

    private NativeOutOfGameBinding(
        LoginScreen owner,
        NativeUiProductHost host,
        SessionHost session,
        Action<string, bool> showStatus)
    {
        _owner = owner;
        _host = host;
        _session = session;
        _showStatus = showStatus;

        _mainScreen = host.GetScreen(OutOfGameFlowController.MainMenuScreenId);
        _eulaScreen = host.GetScreen(OutOfGameFlowController.EulaScreenId);
        _loginScreen = host.GetScreen(OutOfGameFlowController.LoginScreenId);
        _creditsScreen = host.GetScreen(OutOfGameFlowController.CreditsScreenId);
        _tooltipScreen = host.GetScreen(OutOfGameFlowController.TooltipScreenId);
        _connectionScreen = host.GetScreen(OutOfGameFlowController.ConnectionScreenId);
        _shardScreen = host.GetScreen(OutOfGameFlowController.ShardScreenId);
        _characterScreen = host.GetScreen(OutOfGameFlowController.CharacterScreenId);
        _preGeneratorScreen = host.GetScreen(OutOfGameFlowController.CharacterPreGeneratorScreenId);
        _generatorScreen = host.GetScreen(OutOfGameFlowController.CharacterGeneratorScreenId);
        _messageScreen = host.GetScreen(OutOfGameFlowController.MessageBoxScreenId);
        _screens = host.Manifest.ScreensInAuthoredOrder;

        _versionValue = host.GetValue(_mainScreen, "client-version");
        _accountValue = host.GetValue(_loginScreen, "account-name");
        _passwordValue = host.GetValue(_loginScreen, "account-password");
        _connectionStatusValue = host.GetValue(_connectionScreen, "status-message");
        _currentShardValue = host.GetValue(_characterScreen, "current-shard");
        _deleteNameValue = host.GetValue(_characterScreen, "delete-character-name");
        _deleteStatusValue = host.GetValue(_characterScreen, "delete-character-status");
        _characterNameValue = host.GetValue(_generatorScreen, "character-name");
        _messageTitleValue = host.GetValue(_messageScreen, "title");
        _messageBodyValue = host.GetValue(_messageScreen, "message");
        _shardsCollection = host.GetCollection(_shardScreen, "shards");
        _charactersCollection = host.GetCollection(_characterScreen, "characters");
        _tooltipCollection = host.GetCollection(_tooltipScreen, "tooltip-lines");
        _deletePanelRole = host.GetRole(_characterScreen, "delete-character-panel");
        _deletePromptTemplate = host.ReadText(_characterScreen, _deleteStatusValue);

        _login = new LoginViewModel(session.Auth, session.Player);
        _characters = new CharacterSelectViewModel(session.Auth, session.Player);
        _creation = new CharacterCreateViewModel(session.Auth, session.Player);

        var version = new GameVersion(ClientVersion);
        var acceptance = new GodotEulaAcceptanceStore();
        bool requireEula = acceptance.AcceptedVersion != version;
        OutOfGameScreen initialScreen = session.Player.IsAuthenticated
            ? session.Flow.Current == Screen.CharacterCreate
                ? OutOfGameScreen.CharacterPreGenerator
                : OutOfGameScreen.CharacterSelect
            : OutOfGameScreen.Login;
        _flow = OutOfGameFlowController.Open(
            host.Manifest,
            new OutOfGameFlowStart(ClientVersion, requireEula, initialScreen));

        _creditsTimeline = ReadCreditsTimeline();
        host.BindEulaPresentation(
            _eulaScreen,
            host.GetRole(_eulaScreen, "document-container"),
            host.GetRole(_eulaScreen, "document-view"),
            host.GetRole(_eulaScreen, "accept-button"),
            atEnd => _eula?.ScrollChanged(atEnd));
        host.BindCreditsPresentation(
            _creditsScreen,
            host.GetRole(_creditsScreen, CreditsProduct.TextRole),
            host.GetRole(_creditsScreen, CreditsProduct.PictureRole),
            host.GetRole(_creditsScreen, CreditsProduct.BackgroundRole));

        if (requireEula)
        {
            EulaDocument[] documents = ReadEulaDocuments();
            var controller = new EulaController(
                documents,
                new FixedVersionSource(version),
                acceptance,
                new ExitRequest(this),
                new EulaContinuation(this));
            _eula = new EulaClientBinding(controller, this);
        }

        foreach (UiScreenDefinition screen in _screens)
        {
            host.RegisterController(screen, invocation => HandleAction(screen, invocation));
        }

        host.SetText(_mainScreen, _versionValue, ClientVersion);
        host.SetText(_loginScreen, _accountValue, string.Empty);
        host.SetText(_loginScreen, _passwordValue, string.Empty);
        host.ReplaceCollection(
            _shardScreen,
            _shardsCollection,
            [new NativeUiCollectionItem(DefaultShardId, session.ServerAddress, Enabled: true)]);
        host.ReplaceCollection(_characterScreen, _charactersCollection, []);
        host.ReplaceCollection(_tooltipScreen, _tooltipCollection, []);
        RenderFlow(_flow.Presentation);
        host.PlayMusic(MainMenuMusicCue);
        _eula?.Start();
        if (!requireEula)
        {
            StartInitialLoad();
        }
    }

    public TimeSpan Now => TimeSpan.FromMilliseconds(Time.GetTicksMsec());

    internal static NativeOutOfGameBinding Open(
        LoginScreen owner,
        NativeUiProductHost host,
        SessionHost session,
        Action<string, bool> showStatus) =>
        new(owner, host, session, showStatus);

    internal void Tick()
    {
        if (!_disposed && _creditsOpen)
        {
            _credits?.Tick();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
        _credits = null;
        _login.Password = Secret.None;
        _host.SetText(_loginScreen, _passwordValue, string.Empty);
        _host.StopMusic();
    }

    public void Present(EulaViewState state)
    {
        _eulaState = state;
        if (state.Document is { } document)
        {
            _host.PresentEula(
                _eulaScreen,
                new NativeUiEulaPresentation(document.Id, document.Body, state.CanAccept));
        }
        RenderFlow(_flow.Presentation);
    }

    public void Dismiss()
    {
        _eulaState = null;
        RenderFlow(_flow.Presentation);
    }

    public void StopMainMenu() => _host.StopMusic();

    public void PlayCredits(string cue)
    {
        if (cue != "credits_music")
        {
            throw new InvalidDataException($"Credits requested unknown music cue '{cue}'");
        }

        _host.PlayMusic(cue);
    }

    public void StopCredits() => _host.StopMusic();

    public void PlayMainMenu() => _host.PlayMusic(MainMenuMusicCue);

    public void Show(string productId)
    {
        Apply(_flow.Advance(OutOfGameFlowSignal.TooltipShown(productId)));
    }

    public void Hide()
    {
        Apply(_flow.Advance(OutOfGameFlowSignal.TooltipHidden));
    }

    public void Present(CreditsPresentation presentation)
    {
        _host.PresentCredits(
            _creditsScreen,
            new NativeUiCreditsPresentation(
                presentation.FormOpacity,
                presentation.Text is { } text
                    ? new NativeUiCreditsTextPresentation(text.Body, text.Opacity)
                    : null,
                ToNativeVisual(presentation.Picture),
                ToNativeVisual(presentation.Background)));
    }

    public void Close()
    {
        _creditsOpen = false;
        if (_disposed)
        {
            return;
        }
        Apply(_flow.Advance(OutOfGameFlowSignal.CreditsClosed));
    }

    private bool HandleAction(UiScreenDefinition screen, UiActionInvocation invocation)
    {
        if (screen == _preGeneratorScreen
            && invocation.Id is "select-archetype" or "continue-creation"
            && !HasArchetype(Argument(invocation, "archetype")))
        {
            _showStatus("That character archetype is not offered by the server.", true);
            return true;
        }

        OutOfGamePresentation before = _flow.Presentation;
        OutOfGameFlowDispatch dispatch = _flow.Route(invocation);
        if (dispatch.Status == OutOfGameRouteStatus.Rejected)
        {
            GD.Print($"Native UI ignored {screen.Id}.{invocation.Id}: {dispatch.Rejection}");
            return true;
        }

        Apply(dispatch);
        HandleTransition(before, dispatch.Presentation);
        if (screen == _creditsScreen)
        {
            DispatchCredits(invocation);
            return true;
        }

        if (dispatch.Status == OutOfGameRouteStatus.Forwarded)
        {
            DispatchForwarded(screen, invocation);
        }
        return true;
    }

    private void DispatchForwarded(UiScreenDefinition screen, UiActionInvocation invocation)
    {
        if (screen == _eulaScreen)
        {
            DispatchEula(invocation);
        }
        else if (screen == _loginScreen)
        {
            DispatchLogin(invocation);
        }
        else if (screen == _connectionScreen)
        {
            CancelOperation();
        }
        else if (screen == _shardScreen)
        {
            DispatchShard(invocation);
        }
        else if (screen == _characterScreen)
        {
            DispatchCharacter(invocation);
        }
        else if (screen == _preGeneratorScreen)
        {
            DispatchPreGenerator(invocation);
        }
        else if (screen == _generatorScreen)
        {
            DispatchGenerator(invocation);
        }
        else if (screen == _messageScreen)
        {
            DispatchMessage(invocation);
        }
    }

    private void DispatchEula(UiActionInvocation invocation)
    {
        if (_eula is null)
        {
            return;
        }

        EulaCommand? command = invocation.Id switch
        {
            "accept-eula" => EulaCommand.Accept,
            "decline-eula" => EulaCommand.Decline,
            "close-eula" => EulaCommand.Close,
            _ => null,
        };
        if (command is { } routed)
        {
            _eula.Dispatch(routed);
        }
    }

    private void DispatchLogin(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "submit-login":
                SignIn();
                break;
            case "update-account-name":
                _login.Email = _host.ReadText(_loginScreen, _accountValue);
                RenderLogin();
                break;
            case "update-account-password":
                _login.Password = new Secret(_host.ReadText(_loginScreen, _passwordValue));
                RenderLogin();
                break;
        }
    }

    private void DispatchShard(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "connect-shard":
                ConnectShard();
                break;
            case "refresh-shards":
                _host.ReplaceCollection(
                    _shardScreen,
                    _shardsCollection,
                    [new NativeUiCollectionItem(DefaultShardId, _session.ServerAddress, true)]);
                break;
            case "show-last-login":
                SetOptionalRoleVisible(_shardScreen, "last-login-panel", true);
                break;
            case "hide-last-login":
                SetOptionalRoleVisible(_shardScreen, "last-login-panel", false);
                break;
        }
    }

    private void DispatchCharacter(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "select-character":
                SelectCharacter(Argument(invocation, "character"));
                break;
            case "enter-world":
                SelectCharacter(Argument(invocation, "character"));
                EnterWorld();
                break;
            case "request-delete":
                OpenDeletePanel();
                break;
            case "update-delete-character-name":
                break;
            case "confirm-delete":
                DeleteCharacter();
                break;
            case "cancel-delete":
                CloseDeletePanel();
                break;
        }
    }

    private void DispatchPreGenerator(UiActionInvocation invocation)
    {
        if (invocation.Id is "select-archetype" or "continue-creation")
        {
            SelectArchetype(Argument(invocation, "archetype"));
        }
    }

    private void DispatchGenerator(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "update-character-name":
                _creation.Name = _host.ReadText(_generatorScreen, _characterNameValue);
                RenderCreation();
                break;
            case "cancel-name-entry":
                _creation.Name = string.Empty;
                _host.SetText(_generatorScreen, _characterNameValue, string.Empty);
                RenderCreation();
                break;
            case "create-character":
                CreateCharacter();
                break;
        }
    }

    private void DispatchMessage(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "confirm":
            case "accept":
            case "decline":
                Apply(_flow.Advance(OutOfGameFlowSignal.MessageBoxClosed));
                break;
        }
    }

    private void DispatchCredits(UiActionInvocation invocation)
    {
        CreditsActionArgument[] arguments = invocation.Arguments
            .Select(argument => new CreditsActionArgument(
                argument.Name,
                CreditsActionArgumentKind.ProductId,
                argument.Value))
            .ToArray();
        (_credits ?? throw new InvalidOperationException("Credits are not open"))
            .Dispatch(CreditsActions.Resolve(invocation.Id, arguments));
    }

    private async void SignIn()
    {
        if (!_login.CanSubmit)
        {
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
            RenderLogin();
            return;
        }

        CancellationToken token = BeginOperation();
        SetScreenInteractive(_loginScreen, false);
        try
        {
            bool signedIn = await _login.SignInAsync(token);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            ForgetPassword();
            if (!signedIn)
            {
                Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
                RenderLogin();
                return;
            }

            _session.Flow.SignedIn();
            Apply(_flow.Advance(OutOfGameFlowSignal.LoginSucceeded));
            _host.SetText(_connectionScreen, _connectionStatusValue, "Reading shards...");
            Apply(_flow.Advance(OutOfGameFlowSignal.ShardListReady));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Sign-in failed unexpectedly", exception);
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
        }
        catch (Exception)
        {
        }
        finally
        {
            bool canPresent = CanPresent(token);
            EndOperation(token);
            if (canPresent)
            {
                SetScreenInteractive(_loginScreen, true);
                RenderLogin();
            }
        }
    }

    private async void ConnectShard()
    {
        CancellationToken token = BeginOperation();
        _host.SetText(_connectionScreen, _connectionStatusValue, "Reading characters...");
        try
        {
            Apply(_flow.Advance(OutOfGameFlowSignal.ShardConnected));
            _chargenOptions = await _session.Auth.ListChargenOptionsAsync(token);
            bool loaded = await _characters.RefreshAsync(token);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!loaded)
            {
                Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
                RenderCharacterRoster();
                return;
            }

            RenderCharacterRoster();
            Apply(_flow.Advance(OutOfGameFlowSignal.CharacterListReady));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Character list failed unexpectedly", exception);
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
        }
        catch (Exception)
        {
        }
        finally
        {
            EndOperation(token);
        }
    }

    private async void EnterWorld()
    {
        CharacterSummary? character = _characters.Selected;
        if (character is null)
        {
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
            return;
        }

        CancellationToken token = BeginOperation();
        _host.SetText(_connectionScreen, _connectionStatusValue, $"Entering as {character.Name}...");
        try
        {
            ChargenOption? option = OptionFor(character.ChargenOptionId);
            ShardTicket? ticket = await _characters.EnterWorldAsync(option, token);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (ticket is null)
            {
                Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
                return;
            }

            _session.Zone = new ZoneRequest(
                _session.Zone.MapName,
                option?.SpawnZoneId ?? _session.Zone.ZoneId,
                _session.ServerAddress,
                Online: true,
                ticket.Token,
                option is null ? null : new ZoneSpawn(option.SpawnX, option.SpawnY, option.SpawnZ));
            Apply(_flow.Advance(OutOfGameFlowSignal.WorldEntered));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("World entry failed unexpectedly", exception);
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
        }
        catch (Exception)
        {
        }
        finally
        {
            EndOperation(token);
        }
    }

    private async void LoadCreationOptions()
    {
        CancellationToken token = BeginOperation();
        SetScreenInteractive(_preGeneratorScreen, false);
        try
        {
            if (_chargenOptions.Count == 0)
            {
                _chargenOptions = await _session.Auth.ListChargenOptionsAsync(token);
            }
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            _creation.SetOptions(_chargenOptions);
            if (_chargenOptions.Count == 0)
            {
                _showStatus("The server offers no playable character options.", true);
            }

            string? selected = _flow.Presentation.SelectedArchetypeId;
            if (selected is not null)
            {
                SelectArchetype(selected);
            }
            RenderCreation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Character options failed unexpectedly", exception);
        }
        catch (Exception)
        {
        }
        finally
        {
            bool canPresent = CanPresent(token);
            EndOperation(token);
            if (canPresent)
            {
                SetScreenInteractive(_preGeneratorScreen, true);
                ApplyArchetypeAvailability();
            }
        }
    }

    private async void CreateCharacter()
    {
        if (!_creation.CanSubmit)
        {
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
            RenderCreation();
            return;
        }

        CancellationToken token = BeginOperation();
        SetScreenInteractive(_generatorScreen, false);
        try
        {
            CharacterSummary? created = await _creation.SubmitAsync(token);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (created is null)
            {
                Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
                RenderCreation();
                return;
            }

            _session.Player.SelectCharacter(created, _creation.Selected);
            if (_session.Flow.Current == Screen.CharacterCreate)
            {
                _session.Flow.LeaveCreateCharacter();
            }
            Apply(_flow.Advance(OutOfGameFlowSignal.CharacterCreated(ProductId(created))));
            await _characters.RefreshAsync(token);
            if (!CanPresent(token))
            {
                return;
            }
            RenderCharacterRoster();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Character creation failed unexpectedly", exception);
            Apply(_flow.Advance(OutOfGameFlowSignal.OperationFailed));
        }
        catch (Exception)
        {
        }
        finally
        {
            bool canPresent = CanPresent(token);
            EndOperation(token);
            if (canPresent)
            {
                SetScreenInteractive(_generatorScreen, true);
            }
        }
    }

    private async void DeleteCharacter()
    {
        string? selectedId = _flow.Presentation.SelectedCharacterId;
        if (selectedId is null
            || !_charactersByProductId.TryGetValue(selectedId, out CharacterSummary? character))
        {
            return;
        }

        string confirmation = _host.ReadText(_characterScreen, _deleteNameValue).Trim();
        if (!confirmation.Equals(character.Name, StringComparison.Ordinal))
        {
            _host.SetText(
                _characterScreen,
                _deleteStatusValue,
                $"The entered name does not match {character.Name}.");
            return;
        }

        CancellationToken token = BeginOperation();
        SetScreenInteractive(_characterScreen, false);
        try
        {
            await _session.Auth.DeleteCharacterAsync(
                _session.Player.Token,
                character.CharacterId,
                token);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            await _characters.RefreshAsync(token);
            if (!CanPresent(token))
            {
                return;
            }
            RenderCharacterRoster();
            CloseDeletePanel();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Character deletion failed unexpectedly", exception);
        }
        catch (Exception)
        {
        }
        finally
        {
            bool canPresent = CanPresent(token);
            EndOperation(token);
            if (canPresent)
            {
                SetScreenInteractive(_characterScreen, true);
            }
        }
    }

    private async void ResumeCharacterSelect()
    {
        CancellationToken token = BeginOperation();
        SetScreenInteractive(_characterScreen, false);
        try
        {
            _chargenOptions = await _session.Auth.ListChargenOptionsAsync(token);
            bool loaded = await _characters.RefreshAsync(token);
            if (!CanPresent(token))
            {
                return;
            }

            RenderCharacterRoster();
            if (!loaded)
            {
                _showStatus(_characters.Message, true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (CanPresent(token))
        {
            Fail("Character list resume failed unexpectedly", exception);
        }
        catch (Exception)
        {
        }
        finally
        {
            bool canPresent = CanPresent(token);
            EndOperation(token);
            if (canPresent)
            {
                SetScreenInteractive(_characterScreen, true);
            }
        }
    }

    private void HandleTransition(OutOfGamePresentation before, OutOfGamePresentation after)
    {
        bool creditsWasVisible = before.Layer(OutOfGameFlowController.CreditsScreenId).Visible;
        bool creditsIsVisible = after.Layer(OutOfGameFlowController.CreditsScreenId).Visible;
        if (!creditsWasVisible && creditsIsVisible)
        {
            _credits?.Dispose();
            _credits = new CreditsController(_creditsTimeline, this, this, this, this);
            _creditsOpen = true;
            _credits.Open();
        }

        if (before.Screen != after.Screen)
        {
            if (before.Screen == OutOfGameScreen.CharacterSelect)
            {
                CancelOperation();
                CloseDeletePanel();
            }
            if (after.Screen == OutOfGameScreen.CharacterPreGenerator
                && before.Screen == OutOfGameScreen.CharacterSelect)
            {
                if (_session.Flow.Current == Screen.CharacterSelect)
                {
                    _session.Flow.CreateCharacter();
                }
                LoadCreationOptions();
            }
            else if (after.Screen == OutOfGameScreen.CharacterSelect
                && before.Screen == OutOfGameScreen.CharacterPreGenerator
                && _session.Flow.Current == Screen.CharacterCreate)
            {
                _session.Flow.LeaveCreateCharacter();
            }
        }
    }

    private void Apply(OutOfGameFlowDispatch dispatch)
    {
        RenderFlow(dispatch.Presentation);
        switch (dispatch.Effect)
        {
            case OutOfGameFlowEffect.Quit:
                _owner.GetTree().Quit();
                break;
            case OutOfGameFlowEffect.SignOut:
                CancelOperation();
                SignOutWithoutSceneSwap();
                break;
            case OutOfGameFlowEffect.EnterWorld:
                _session.Flow.EnterWorld();
                _session.Show(Screen.EnteringWorld);
                break;
        }
    }

    private void RenderFlow(OutOfGamePresentation presentation)
    {
        var layers = presentation.Layers.ToDictionary(layer => layer.ScreenId, StringComparer.Ordinal);
        UiScreenDefinition[] ordered = _screens
            .Select((screen, index) => new
            {
                Screen = screen,
                Index = index,
                Priority = layers.TryGetValue(screen.Id, out OutOfGameLayer? layer)
                    ? layer.Priority
                    : screen.Priority,
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Screen)
            .ToArray();
        _host.SetScreenSiblingOrder(ordered);

        foreach (UiScreenDefinition screen in _screens)
        {
            bool visible = layers.TryGetValue(screen.Id, out OutOfGameLayer? layer)
                && layer.Visible;
            _host.SetScreenVisible(screen, visible, focusFirst: false);
            _host.SetInteractive(screen, CanInteract(presentation, screen));
        }

        if (presentation.TooltipId is { } tooltip)
        {
            _host.ReplaceCollection(
                _tooltipScreen,
                _tooltipCollection,
                [new NativeUiCollectionItem("tooltip-line", TooltipText(tooltip), true)]);
        }
        else
        {
            _host.ReplaceCollection(_tooltipScreen, _tooltipCollection, []);
        }

        ApplyArchetypeAvailability();
        RenderLogin();
        if (_eulaState is { } eula)
        {
            _host.SetRoleEnabled(
                _eulaScreen,
                _host.GetRole(_eulaScreen, "accept-button"),
                eula.CanAccept);
        }
    }

    private void RenderLogin()
    {
        _showStatus(_login.Message, _login.MessageIsError);
        _host.SetRoleEnabled(
            _loginScreen,
            _host.GetRole(_loginScreen, "enter-button"),
            _login.CanSubmit);
    }

    private void RenderCharacterRoster()
    {
        _charactersByProductId.Clear();
        var items = new List<NativeUiCollectionItem>(_characters.Characters.Count);
        foreach (CharacterSummary character in _characters.Characters)
        {
            string id = ProductId(character);
            _charactersByProductId.Add(id, character);
            ChargenOption? option = OptionFor(character.ChargenOptionId);
            string description = option is null
                ? character.Name
                : $"{character.Name}  {ChargenOptionView.From(option).Title}";
            items.Add(new NativeUiCollectionItem(id, description, true));
        }
        _host.ReplaceCollection(_characterScreen, _charactersCollection, items);
        _host.SetText(_characterScreen, _currentShardValue, _session.ServerAddress);
        _showStatus(_characters.Message, _characters.MessageIsError);
    }

    private void RenderCreation()
    {
        _host.SetRoleEnabled(
            _generatorScreen,
            _host.GetRole(_generatorScreen, "create-button"),
            _creation.CanSubmit);
        _showStatus(
            _creation.NameMessage ?? _creation.Message,
            _creation.MessageIsError || _creation.NameMessage is not null);
    }

    private void SelectCharacter(string productId)
    {
        if (_charactersByProductId.TryGetValue(productId, out CharacterSummary? character))
        {
            _characters.SelectById(character.CharacterId);
        }
    }

    private void OpenDeletePanel()
    {
        string? selectedId = _flow.Presentation.SelectedCharacterId;
        if (selectedId is null
            || !_charactersByProductId.TryGetValue(selectedId, out CharacterSummary? selected))
        {
            _showStatus("Select a character before deleting it.", true);
            return;
        }

        _showStatus(string.Empty, false);
        _host.SetText(_characterScreen, _deleteNameValue, string.Empty);
        _host.SetText(
            _characterScreen,
            _deleteStatusValue,
            _deletePromptTemplate.Replace(
                "{avatar_name}",
                selected.Name,
                StringComparison.Ordinal));
        _host.SetRoleVisible(_characterScreen, _deletePanelRole, true);
        _host.Focus(
            _characterScreen,
            _host.GetRole(_characterScreen, "delete-name-input"));
    }

    private void CloseDeletePanel()
    {
        _host.SetText(_characterScreen, _deleteNameValue, string.Empty);
        _host.SetText(_characterScreen, _deleteStatusValue, string.Empty);
        _host.SetRoleVisible(_characterScreen, _deletePanelRole, false);
    }

    private void StartInitialLoad()
    {
        if (_initialLoadStarted)
        {
            return;
        }

        _initialLoadStarted = true;
        switch (_flow.Presentation.Screen)
        {
            case OutOfGameScreen.CharacterSelect:
                ResumeCharacterSelect();
                break;
            case OutOfGameScreen.CharacterPreGenerator:
                LoadCreationOptions();
                break;
        }
    }

    private void SelectArchetype(string productId)
    {
        for (int index = 0; index < _creation.Options.Count; index++)
        {
            ChargenOption? option = _chargenOptions.FirstOrDefault(
                candidate => candidate.Id == _creation.Options[index].Id);
            if (option is not null && ArchetypeId(option) == productId)
            {
                _creation.SelectedIndex = index;
                return;
            }
        }
    }

    private bool HasArchetype(string productId) =>
        _chargenOptions.Any(option => ArchetypeId(option) == productId);

    private void ApplyArchetypeAvailability()
    {
        foreach (UiRoleDefinition role in _preGeneratorScreen.Roles.Where(
            role => role.Id.EndsWith("-option", StringComparison.Ordinal)))
        {
            string archetype = role.Id[..^"-option".Length];
            _host.SetRoleEnabled(_preGeneratorScreen, role, HasArchetype(archetype));
        }
    }

    private CreditsTimeline ReadCreditsTimeline()
    {
        NativeContentPath path = _creditsScreen.Timeline
            ?? throw new InvalidDataException("Credits screen has no timeline");
        using var stream = ReadNativeBytes(_host.ResolveResource(path));
        return CreditsTimelineReader.Parse(stream);
    }

    private EulaDocument[] ReadEulaDocuments()
    {
        if (_eulaScreen.Documents.Count != EulaController.RequiredDocumentCount)
        {
            throw new InvalidDataException("EULA screen must declare exactly three documents");
        }

        return _eulaScreen.Documents.Select(reference =>
        {
            using var stream = ReadNativeBytes(_host.ResolveResource(reference.Path));
            UiDocument document = UiDocumentReader.Parse(stream);
            if (document.Id != reference.Id)
            {
                throw new InvalidDataException(
                    $"EULA document '{reference.Id}' resolved document '{document.Id}'");
            }
            return new EulaDocument(document.Id, document.Body);
        }).ToArray();
    }

    private static MemoryStream ReadNativeBytes(string path)
    {
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0)
        {
            throw new FileNotFoundException($"Native UI resource is missing or empty: {path}");
        }
        return new MemoryStream(bytes, writable: false);
    }

    private void SetOptionalRoleVisible(UiScreenDefinition screen, string roleId, bool visible)
    {
        UiRoleDefinition? role = screen.Roles.FirstOrDefault(candidate => candidate.Id == roleId);
        if (role is not null)
        {
            _host.SetRoleVisible(screen, role, visible);
        }
    }

    private void SetScreenInteractive(UiScreenDefinition screen, bool interactive)
    {
        OutOfGamePresentation presentation = _flow.Presentation;
        if (presentation.Layers.Any(layer => layer.ScreenId == screen.Id && layer.Visible))
        {
            _host.SetInteractive(screen, interactive && CanInteract(presentation, screen));
        }
    }

    private static bool CanInteract(
        OutOfGamePresentation presentation,
        UiScreenDefinition screen)
    {
        if (!presentation.Layers.Any(layer => layer.ScreenId == screen.Id && layer.Visible))
        {
            return false;
        }

        string? modal = presentation.Layers.Any(
            layer => layer.ScreenId == OutOfGameFlowController.EulaScreenId && layer.Visible)
                ? OutOfGameFlowController.EulaScreenId
                : presentation.Layers.Any(
                    layer => layer.ScreenId == OutOfGameFlowController.MessageBoxScreenId
                        && layer.Visible)
                    ? OutOfGameFlowController.MessageBoxScreenId
                    : presentation.Layers.Any(
                        layer => layer.ScreenId == OutOfGameFlowController.CreditsScreenId
                            && layer.Visible)
                        ? OutOfGameFlowController.CreditsScreenId
                        : null;
        return modal is null
            ? screen.Id == OutOfGameFlowController.MainMenuScreenId
                || screen.Id == ScreenId(presentation.Screen)
            : screen.Id == modal;
    }

    private CancellationToken BeginOperation()
    {
        CancelOperation();
        _operation = new CancellationTokenSource();
        return _operation.Token;
    }

    private bool CanPresent(CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested;

    private void EndOperation(CancellationToken token)
    {
        if (_operation?.Token == token)
        {
            _operation.Dispose();
            _operation = null;
        }
    }

    private void CancelOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private async void SignOutWithoutSceneSwap()
    {
        Secret token = _session.Player.Token;
        _session.Player.SignOut();
        _session.Flow.SignedOut();
        _charactersByProductId.Clear();
        _host.ReplaceCollection(_characterScreen, _charactersCollection, []);
        if (token.IsEmpty)
        {
            return;
        }
        try
        {
            await _session.Auth.LogoutAsync(token);
        }
        catch (AuthException exception)
        {
            GD.Print($"Native UI sign-out was not confirmed ({exception.Failure}).");
        }
    }

    private void ForgetPassword()
    {
        _login.Password = Secret.None;
        _host.SetText(_loginScreen, _passwordValue, string.Empty);
    }

    private void Fail(string message, Exception exception)
    {
        GD.PushError($"{message}: {exception.GetType().Name}");
        const string body = "Something went wrong in the client. See the log.";
        _showStatus(body, true);
        OutOfGameFlowDispatch modal = _flow.Advance(OutOfGameFlowSignal.MessageBoxOpened);
        if (modal.Status == OutOfGameRouteStatus.Handled)
        {
            _host.SetText(_messageScreen, _messageTitleValue, "Client error");
            _host.SetText(_messageScreen, _messageBodyValue, body);
            Apply(modal);
        }
    }

    private static string Argument(UiActionInvocation invocation, string name) =>
        invocation.Arguments.Single(argument => argument.Name == name).Value;

    private ChargenOption? OptionFor(string id) =>
        _chargenOptions.FirstOrDefault(option => option.Id == id);

    private static string ProductId(CharacterSummary character) =>
        character.CharacterId.ToString("N");

    private static string ArchetypeId(ChargenOption option) =>
        $"{ProductToken(option.Faction)}-{ProductToken(option.Class)}";

    private static string ProductToken(string value)
    {
        char[] token = value.ToLowerInvariant()
            .Where(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            .ToArray();
        return new string(token);
    }

    private static string ScreenId(OutOfGameScreen screen) => screen switch
    {
        OutOfGameScreen.Login => OutOfGameFlowController.LoginScreenId,
        OutOfGameScreen.ConnectionProgress => OutOfGameFlowController.ConnectionScreenId,
        OutOfGameScreen.ShardSelect => OutOfGameFlowController.ShardScreenId,
        OutOfGameScreen.CharacterSelect => OutOfGameFlowController.CharacterScreenId,
        OutOfGameScreen.CharacterPreGenerator => OutOfGameFlowController.CharacterPreGeneratorScreenId,
        OutOfGameScreen.CharacterGenerator => OutOfGameFlowController.CharacterGeneratorScreenId,
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    private static string TooltipText(string productId) => productId switch
    {
        "enter-button" => "Enter",
        "credits-button" => "Credits",
        "exit-button" => "Exit",
        "previous-button" => "Previous",
        "next-button" => "Next",
        "close-button" => "Close",
        "cancel-button" => "Cancel",
        "create-button" => "Create character",
        "delete-button" => "Delete character",
        "change-shard-button" => "Change shard",
        "back-button" => "Back",
        "choose-button" => "Choose",
        "randomize-button" => "Randomize",
        "helmet-toggle" => "Show helmet",
        "armor-toggle" => "Show armor",
        _ => productId.Replace('-', ' '),
    };

    private static NativeUiCreditsVisualPresentation? ToNativeVisual(
        CreditsVisualPresentation? presentation) =>
        presentation is null
            ? null
            : new NativeUiCreditsVisualPresentation(
                presentation.TextureId,
                presentation.Blend == CreditsBlend.Multiply
                    ? NativeUiCreditsBlend.Multiply
                    : NativeUiCreditsBlend.Alpha,
                presentation.Opacity);

    private sealed record FixedVersionSource(GameVersion Current) : IGameVersionSource;

    private sealed class ExitRequest(NativeOutOfGameBinding owner) : IApplicationExitRequest
    {
        public void RequestExit()
        {
            owner.Apply(owner._flow.Advance(OutOfGameFlowSignal.EulaDeclined));
        }
    }

    private sealed class EulaContinuation(NativeOutOfGameBinding owner) : IEulaContinuation
    {
        public void ContinueAfterEula()
        {
            owner.Apply(owner._flow.Advance(OutOfGameFlowSignal.EulaAccepted));
            owner.StartInitialLoad();
        }
    }

    private sealed class GodotEulaAcceptanceStore : IEulaAcceptanceStore
    {
        public GameVersion? AcceptedVersion
        {
            get
            {
                var config = new ConfigFile();
                if (config.Load(EulaConfigPath) != Error.Ok)
                {
                    return null;
                }
                string value = config.GetValue(EulaSection, EulaVersionKey, string.Empty).AsString();
                return string.IsNullOrWhiteSpace(value) ? null : new GameVersion(value);
            }
        }

        public void Accept(GameVersion version)
        {
            var config = Load();
            config.SetValue(EulaSection, EulaVersionKey, version.Value);
            Save(config);
        }

        public void Clear()
        {
            try
            {
                var config = Load();
                config.EraseSectionKey(EulaSection, EulaVersionKey);
                Save(config);
            }
            catch (Exception exception)
            {
                // Refusing the EULA must always reach the exit request. A stale
                // acceptance entry is harmless because refusal is asked again.
                GD.PushWarning(
                    $"Could not clear EULA acceptance ({exception.GetType().Name}).");
            }
        }

        private static ConfigFile Load()
        {
            var config = new ConfigFile();
            Error error = config.Load(EulaConfigPath);
            if (error is not Error.Ok and not Error.FileNotFound)
            {
                throw new IOException($"Could not read EULA acceptance store ({error})");
            }
            return config;
        }

        private static void Save(ConfigFile config)
        {
            Error error = config.Save(EulaConfigPath);
            if (error != Error.Ok)
            {
                throw new IOException($"Could not save EULA acceptance store ({error})");
            }
        }
    }
}
