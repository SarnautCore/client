namespace SarnautCore.UI;

/// <summary>
/// Owns the product-level lifecycle of the native out-of-game screens.
/// </summary>
/// <remarks>
/// The controller has no engine dependency. A scene adapter renders
/// <see cref="Presentation"/>, forwards <see cref="OutOfGameFlowDispatch.ScreenAction"/>
/// to the matching Shell or Network model, and reports the result through
/// <see cref="Advance"/>.
/// </remarks>
public sealed class OutOfGameFlowController
{
    public const string MainMenuScreenId = "main-menu";
    public const string LoginScreenId = "login-account";
    public const string ConnectionScreenId = "connection-progress";
    public const string ShardScreenId = "shard-select";
    public const string CharacterScreenId = "character-selector";
    public const string CharacterPreGeneratorScreenId = "character-pre-generator";
    public const string CharacterGeneratorScreenId = "character-generator";
    public const string MessageBoxScreenId = "message-box";
    public const string CreditsScreenId = "credits";
    public const string TooltipScreenId = "main-menu-tooltip";
    public const string EulaScreenId = "eula";

    private static readonly IReadOnlyDictionary<string, HashSet<string>> s_allowedActions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [MainMenuScreenId] = Set(
                "begin-primary-scene-drag",
                "end-primary-scene-drag",
                "begin-secondary-scene-drag",
                "end-secondary-scene-drag",
                "move-scene-camera-horizontal",
                "move-scene-camera-vertical",
                "zoom-scene-in",
                "zoom-scene-out"),
            [LoginScreenId] = Set(
                "submit-login",
                "cancel-login",
                "show-credits",
                "quit",
                "focus-next",
                "update-account-name",
                "update-account-password",
                "show-tooltip",
                "hide-tooltip"),
            [EulaScreenId] = Set(
                "accept-eula",
                "decline-eula",
                "close-eula",
                "show-tooltip",
                "hide-tooltip"),
            [CreditsScreenId] = Set(
                "previous-credit",
                "next-credit",
                "close-credits",
                "show-tooltip",
                "hide-tooltip"),
            [TooltipScreenId] = Set(),
            [ConnectionScreenId] = Set(
                "cancel-connection",
                "show-tooltip",
                "hide-tooltip"),
            [ShardScreenId] = Set(
                "select-shard",
                "connect-shard",
                "cancel-shard-select",
                "refresh-shards",
                "show-last-login",
                "hide-last-login",
                "navigate-previous-shard",
                "navigate-next-shard",
                "show-tooltip",
                "hide-tooltip"),
            [CharacterScreenId] = Set(
                "select-character",
                "enter-world",
                "create-character",
                "request-delete",
                "change-shard",
                "back-to-login",
                "confirm-delete",
                "cancel-delete",
                "update-delete-character-name",
                "show-tooltip",
                "hide-tooltip"),
            [CharacterPreGeneratorScreenId] = Set(
                "select-archetype",
                "continue-creation",
                "continue-selected-archetype",
                "back-to-characters",
                "preview-archetype",
                "show-tooltip",
                "hide-tooltip"),
            [CharacterGeneratorScreenId] = Set(
                "create-character",
                "cancel-name-entry",
                "back-to-archetypes",
                "randomize-character",
                "toggle-helmet",
                "toggle-armor",
                "select-preset",
                "previous-appearance-option",
                "next-appearance-option",
                "update-character-name",
                "preview-preset",
                "preview-appearance-control",
                "show-tooltip",
                "hide-tooltip"),
            [MessageBoxScreenId] = Set(
                "accept",
                "decline",
                "confirm",
                "show-tooltip",
                "hide-tooltip"),
        };

    private static readonly IReadOnlyDictionary<string, int> s_actionCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MainMenuScreenId] = 8,
            [EulaScreenId] = 5,
            [LoginScreenId] = 13,
            [CreditsScreenId] = 9,
            [TooltipScreenId] = 0,
            [ConnectionScreenId] = 3,
            [ShardScreenId] = 12,
            [CharacterScreenId] = 21,
            [CharacterPreGeneratorScreenId] = 54,
            [CharacterGeneratorScreenId] = 58,
            [MessageBoxScreenId] = 11,
        };

    private static readonly HashSet<string> s_archetypes = Set(
        "league-paladin",
        "league-warrior",
        "league-stalker",
        "league-mage",
        "league-psionic",
        "league-druid",
        "league-necromancer",
        "league-priest",
        "empire-warrior",
        "empire-mage",
        "empire-druid",
        "empire-necromancer",
        "empire-psionic",
        "empire-paladin",
        "empire-priest",
        "empire-stalker");

    private static readonly HashSet<string> s_presets = Set(
        "preset-01",
        "preset-02",
        "preset-03",
        "preset-04");

    private static readonly HashSet<string> s_appearanceControls = Set(
        "control-01",
        "control-02",
        "control-03",
        "control-04",
        "control-05",
        "control-06",
        "control-07",
        "control-08",
        "control-09",
        "control-10",
        "control-11");

    private readonly IReadOnlyDictionary<UiActionDefinition, UiScreenDefinition> _actionOwners;
    private OutOfGameScreen _screen = OutOfGameScreen.Login;
    private OutOfGameScreen _connectionReturnScreen = OutOfGameScreen.Login;
    private PendingOperation _pending;
    private bool _creditsVisible;
    private bool _eulaVisible;
    private bool _messageBoxVisible;
    private bool _running = true;
    private bool _loginPending;
    private bool _characterCreatePending;
    private string? _tooltipId;
    private string? _selectedShardId;
    private string? _selectedCharacterId;
    private string? _selectedArchetypeId;

    private OutOfGameFlowController(
        UiProductManifest manifest,
        OutOfGameFlowStart start)
    {
        ValidateContract(manifest);
        _actionOwners = IndexActions(manifest);
        ClientVersion = start.ClientVersion;
        _eulaVisible = start.RequireEula;
    }

    public string ClientVersion { get; }

    public OutOfGamePresentation Presentation => BuildPresentation();

    public static OutOfGameFlowController Open(
        UiProductManifest manifest,
        OutOfGameFlowStart start)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.ClientVersion);
        return new OutOfGameFlowController(manifest, start);
    }

    /// <summary>
    /// Routes one invocation emitted by the validated v2 widget runtime.
    /// Unknown, stale, background, and modal-blocked actions are rejected.
    /// </summary>
    public OutOfGameFlowDispatch Route(UiActionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!_running)
        {
            return Reject("The out-of-game flow is no longer running");
        }

        if (!_actionOwners.TryGetValue(invocation.Definition, out UiScreenDefinition? owner)
            || !InvocationMatchesDefinition(invocation))
        {
            return Reject("The action did not originate in this product manifest");
        }

        if (!IsAllowed(owner.Id, invocation.Id))
        {
            return Reject($"Screen '{owner.Id}' has no routed action '{invocation.Id}'");
        }

        if (_eulaVisible && owner.Id != EulaScreenId)
        {
            return Reject("The EULA layer owns input");
        }

        if (_messageBoxVisible && owner.Id != MessageBoxScreenId)
        {
            return Reject("The message box layer owns input");
        }

        if (_creditsVisible && owner.Id != CreditsScreenId && !_messageBoxVisible)
        {
            return Reject("The Credits layer owns input");
        }

        bool activeProductScreen = owner.Id == MainMenuScreenId
            || owner.Id == ScreenId(_screen)
            || (_eulaVisible && owner.Id == EulaScreenId)
            || (_creditsVisible && owner.Id == CreditsScreenId)
            || (_messageBoxVisible && owner.Id == MessageBoxScreenId);
        if (!activeProductScreen)
        {
            return Reject($"Screen '{owner.Id}' is not active");
        }

        if (invocation.Id == "show-tooltip")
        {
            _tooltipId = ProductId(invocation, "tooltip");
            return Handle();
        }

        if (invocation.Id == "hide-tooltip")
        {
            _tooltipId = null;
            return Handle();
        }

        if (owner.Id is MainMenuScreenId or EulaScreenId or CreditsScreenId)
        {
            return Forward(invocation);
        }

        return owner.Id switch
        {
            LoginScreenId => RouteLogin(invocation),
            ConnectionScreenId => RouteConnection(invocation),
            ShardScreenId => RouteShard(invocation),
            CharacterScreenId => RouteCharacter(invocation),
            CharacterPreGeneratorScreenId => RouteCharacterPreGenerator(invocation),
            CharacterGeneratorScreenId => RouteCharacterGenerator(invocation),
            MessageBoxScreenId => Forward(invocation),
            _ => Reject($"Screen '{owner.Id}' is not part of the out-of-game flow"),
        };
    }

    /// <summary>
    /// Applies a typed result from a Shell, Network, EULA, Credits, or tooltip adapter.
    /// </summary>
    public OutOfGameFlowDispatch Advance(OutOfGameFlowSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!_running)
        {
            return Reject("The out-of-game flow is no longer running");
        }

        return signal.Kind switch
        {
            OutOfGameFlowSignalKind.EulaAccepted => CloseEula(),
            OutOfGameFlowSignalKind.EulaDeclined or OutOfGameFlowSignalKind.EulaClosed =>
                QuitFromEula(),
            OutOfGameFlowSignalKind.CreditsClosed => CloseCredits(),
            OutOfGameFlowSignalKind.MessageBoxOpened => OpenMessageBox(),
            OutOfGameFlowSignalKind.MessageBoxClosed => CloseMessageBox(),
            OutOfGameFlowSignalKind.LoginSucceeded => LoginSucceeded(),
            OutOfGameFlowSignalKind.ShardListReady => ShardListReady(),
            OutOfGameFlowSignalKind.ShardConnected => ShardConnected(),
            OutOfGameFlowSignalKind.CharacterListReady => CharacterListReady(),
            OutOfGameFlowSignalKind.CharacterCreated => CharacterCreated(signal.ProductItemId),
            OutOfGameFlowSignalKind.WorldEntered => WorldEntered(),
            OutOfGameFlowSignalKind.OperationFailed => OperationFailed(),
            OutOfGameFlowSignalKind.SignedOut => SignedOut(),
            OutOfGameFlowSignalKind.TooltipShown => ShowTooltip(signal.ProductItemId),
            OutOfGameFlowSignalKind.TooltipHidden => HideTooltip(),
            _ => Reject($"Unsupported out-of-game signal '{signal.Kind}'"),
        };
    }

    private OutOfGameFlowDispatch RouteLogin(UiActionInvocation invocation) => invocation.Id switch
    {
        "submit-login" when _loginPending => Reject("Login is already pending"),
        "submit-login" => BeginLogin(invocation),
        "quit" or "cancel-login" => Quit(),
        "show-credits" => ShowCredits(),
        _ => Forward(invocation),
    };

    private OutOfGameFlowDispatch BeginLogin(UiActionInvocation invocation)
    {
        _loginPending = true;
        return Forward(invocation);
    }

    private OutOfGameFlowDispatch RouteConnection(UiActionInvocation invocation)
    {
        if (invocation.Id != "cancel-connection")
        {
            return Forward(invocation);
        }

        _screen = _connectionReturnScreen;
        _pending = PendingOperation.None;
        return Forward(invocation);
    }

    private OutOfGameFlowDispatch RouteShard(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "select-shard":
                _selectedShardId = DynamicItemId(invocation, "shard");
                return Forward(invocation);
            case "connect-shard":
                _selectedShardId = DynamicItemId(invocation, "shard");
                _connectionReturnScreen = OutOfGameScreen.ShardSelect;
                _screen = OutOfGameScreen.ConnectionProgress;
                _pending = PendingOperation.ConnectShard;
                return Forward(invocation);
            case "cancel-shard-select":
                ResetAccountSelection();
                _screen = OutOfGameScreen.Login;
                return Handle(OutOfGameFlowEffect.SignOut);
            default:
                return Forward(invocation);
        }
    }

    private OutOfGameFlowDispatch RouteCharacter(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "select-character":
                _selectedCharacterId = DynamicItemId(invocation, "character");
                return Forward(invocation);
            case "enter-world":
                _selectedCharacterId = DynamicItemId(invocation, "character");
                _connectionReturnScreen = OutOfGameScreen.CharacterSelect;
                _screen = OutOfGameScreen.ConnectionProgress;
                _pending = PendingOperation.EnterWorld;
                return Forward(invocation);
            case "create-character":
                _selectedArchetypeId = null;
                _screen = OutOfGameScreen.CharacterPreGenerator;
                return Handle();
            case "change-shard":
                _selectedCharacterId = null;
                _screen = OutOfGameScreen.ShardSelect;
                return Handle();
            case "back-to-login":
                ResetAccountSelection();
                _screen = OutOfGameScreen.Login;
                return Handle(OutOfGameFlowEffect.SignOut);
            default:
                return Forward(invocation);
        }
    }

    private OutOfGameFlowDispatch RouteCharacterPreGenerator(UiActionInvocation invocation)
    {
        switch (invocation.Id)
        {
            case "select-archetype":
                _selectedArchetypeId = ProductId(invocation, "archetype");
                return Forward(invocation);
            case "continue-creation":
                _selectedArchetypeId = ProductId(invocation, "archetype");
                _screen = OutOfGameScreen.CharacterGenerator;
                return Forward(invocation);
            case "continue-selected-archetype" when _selectedArchetypeId is not null:
                _screen = OutOfGameScreen.CharacterGenerator;
                return Forward(invocation);
            case "continue-selected-archetype":
                return Reject("No character archetype is selected");
            case "back-to-characters":
                _screen = OutOfGameScreen.CharacterSelect;
                return Handle();
            default:
                return Forward(invocation);
        }
    }

    private OutOfGameFlowDispatch RouteCharacterGenerator(UiActionInvocation invocation)
    {
        if (invocation.Id == "create-character")
        {
            if (_characterCreatePending)
            {
                return Reject("Character creation is already pending");
            }

            _characterCreatePending = true;
            return Forward(invocation);
        }

        if (invocation.Id == "back-to-archetypes")
        {
            if (_characterCreatePending)
            {
                return Reject("Character creation is pending");
            }

            _screen = OutOfGameScreen.CharacterPreGenerator;
            return Handle();
        }

        return Forward(invocation);
    }

    private OutOfGameFlowDispatch CloseEula()
    {
        if (!_eulaVisible)
        {
            return Reject("The EULA is not open");
        }

        _eulaVisible = false;
        return Handle();
    }

    private OutOfGameFlowDispatch QuitFromEula()
    {
        if (!_eulaVisible)
        {
            return Reject("The EULA is not open");
        }

        return Quit();
    }

    private OutOfGameFlowDispatch CloseCredits()
    {
        if (!_creditsVisible)
        {
            return Reject("Credits are not open");
        }

        _creditsVisible = false;
        return Handle();
    }

    private OutOfGameFlowDispatch OpenMessageBox()
    {
        if (_messageBoxVisible || _eulaVisible)
        {
            return Reject("A higher-priority modal layer is already open");
        }

        _messageBoxVisible = true;
        _tooltipId = null;
        return Handle();
    }

    private OutOfGameFlowDispatch CloseMessageBox()
    {
        if (!_messageBoxVisible)
        {
            return Reject("The message box is not open");
        }

        _messageBoxVisible = false;
        return Handle();
    }

    private OutOfGameFlowDispatch LoginSucceeded()
    {
        if (_screen != OutOfGameScreen.Login
            || !_loginPending
            || _eulaVisible
            || _creditsVisible)
        {
            return Reject("Login completion arrived without a matching pending login");
        }

        _loginPending = false;
        _connectionReturnScreen = OutOfGameScreen.Login;
        _screen = OutOfGameScreen.ConnectionProgress;
        _pending = PendingOperation.LoadShards;
        return Handle();
    }

    private OutOfGameFlowDispatch ShardListReady()
    {
        if (_screen != OutOfGameScreen.ConnectionProgress
            || _pending != PendingOperation.LoadShards)
        {
            return Reject("Shard data arrived without a pending shard load");
        }

        _screen = OutOfGameScreen.ShardSelect;
        _pending = PendingOperation.None;
        return Handle();
    }

    private OutOfGameFlowDispatch ShardConnected()
    {
        if (_screen != OutOfGameScreen.ConnectionProgress
            || _pending != PendingOperation.ConnectShard)
        {
            return Reject("Shard connection completed without a pending connection");
        }

        _connectionReturnScreen = OutOfGameScreen.ShardSelect;
        _pending = PendingOperation.LoadCharacters;
        return Handle();
    }

    private OutOfGameFlowDispatch CharacterListReady()
    {
        if (_screen != OutOfGameScreen.ConnectionProgress
            || _pending != PendingOperation.LoadCharacters)
        {
            return Reject("Character data arrived without a pending character load");
        }

        _screen = OutOfGameScreen.CharacterSelect;
        _pending = PendingOperation.None;
        return Handle();
    }

    private OutOfGameFlowDispatch CharacterCreated(string? characterId)
    {
        if (_screen != OutOfGameScreen.CharacterGenerator
            || !_characterCreatePending
            || characterId is null)
        {
            return Reject("Character creation completed without a matching pending request and id");
        }

        UiRuntimeKey.Validate(characterId, nameof(characterId));
        _characterCreatePending = false;
        _selectedCharacterId = characterId;
        _screen = OutOfGameScreen.CharacterSelect;
        return Handle();
    }

    private OutOfGameFlowDispatch WorldEntered()
    {
        if (_screen != OutOfGameScreen.ConnectionProgress
            || _pending != PendingOperation.EnterWorld)
        {
            return Reject("World entry completed without a pending entry");
        }

        _pending = PendingOperation.None;
        _running = false;
        return Handle(OutOfGameFlowEffect.EnterWorld);
    }

    private OutOfGameFlowDispatch OperationFailed()
    {
        if (_screen == OutOfGameScreen.Login && _loginPending)
        {
            _loginPending = false;
            return Handle();
        }

        if (_screen == OutOfGameScreen.CharacterGenerator && _characterCreatePending)
        {
            _characterCreatePending = false;
            return Handle();
        }

        if (_screen != OutOfGameScreen.ConnectionProgress || _pending == PendingOperation.None)
        {
            return Reject("No out-of-game operation is pending");
        }

        _screen = _connectionReturnScreen;
        _pending = PendingOperation.None;
        return Handle();
    }

    private OutOfGameFlowDispatch SignedOut()
    {
        ResetAccountSelection();
        _screen = OutOfGameScreen.Login;
        _pending = PendingOperation.None;
        return Handle();
    }

    private OutOfGameFlowDispatch ShowTooltip(string? tooltipId)
    {
        if (tooltipId is null)
        {
            return Reject("A tooltip id is required");
        }

        UiRuntimeKey.Validate(tooltipId, nameof(tooltipId));
        _tooltipId = tooltipId;
        return Handle();
    }

    private OutOfGameFlowDispatch HideTooltip()
    {
        _tooltipId = null;
        return Handle();
    }

    private OutOfGameFlowDispatch ShowCredits()
    {
        _creditsVisible = true;
        _tooltipId = null;
        return Handle();
    }

    private OutOfGameFlowDispatch Quit()
    {
        _running = false;
        _pending = PendingOperation.None;
        _loginPending = false;
        _characterCreatePending = false;
        return Handle(OutOfGameFlowEffect.Quit);
    }

    private void ResetAccountSelection()
    {
        _selectedShardId = null;
        _selectedCharacterId = null;
        _selectedArchetypeId = null;
        _tooltipId = null;
        _creditsVisible = false;
        _messageBoxVisible = false;
        _loginPending = false;
        _characterCreatePending = false;
    }

    private OutOfGameFlowDispatch Forward(UiActionInvocation invocation) =>
        new(
            OutOfGameRouteStatus.Forwarded,
            Presentation,
            invocation,
            OutOfGameFlowEffect.None,
            null);

    private OutOfGameFlowDispatch Handle(OutOfGameFlowEffect effect = OutOfGameFlowEffect.None) =>
        new(
            OutOfGameRouteStatus.Handled,
            Presentation,
            null,
            effect,
            null);

    private OutOfGameFlowDispatch Reject(string reason) =>
        new(
            OutOfGameRouteStatus.Rejected,
            Presentation,
            null,
            OutOfGameFlowEffect.None,
            reason);

    private OutOfGamePresentation BuildPresentation()
    {
        int tooltipPriority = _eulaVisible && _tooltipId is not null ? 6001 : 750;
        var layers = new List<OutOfGameLayer>
        {
            new(MainMenuScreenId, -128, Visible: true, Persistent: true),
            new(ScreenId(_screen), Priority(_screen), Visible: true, Persistent: false),
            new(CreditsScreenId, 749, _creditsVisible, Persistent: false),
            new(TooltipScreenId, tooltipPriority, _tooltipId is not null, Persistent: true),
            new(MessageBoxScreenId, 750, _messageBoxVisible, Persistent: false),
            new(EulaScreenId, 6000, _eulaVisible, Persistent: false),
        };
        layers.Sort(static (left, right) => left.Priority.CompareTo(right.Priority));
        return new OutOfGamePresentation(
            _running,
            _screen,
            ClientVersion,
            _tooltipId,
            _selectedShardId,
            _selectedCharacterId,
            _selectedArchetypeId,
            layers.ToArray());
    }

    private static string DynamicItemId(UiActionInvocation invocation, string argumentName)
    {
        UiResolvedActionArgument argument = Argument(invocation, argumentName);
        if (argument.Kind != UiActionArgumentKind.CollectionItemId)
        {
            throw new InvalidDataException(
                $"Action '{invocation.Id}' argument '{argumentName}' is not a collection item id");
        }

        return argument.Value;
    }

    private static string ProductId(UiActionInvocation invocation, string argumentName)
    {
        UiResolvedActionArgument argument = Argument(invocation, argumentName);
        if (argument.Kind != UiActionArgumentKind.ProductId)
        {
            throw new InvalidDataException(
                $"Action '{invocation.Id}' argument '{argumentName}' is not a product id");
        }

        return argument.Value;
    }

    private static UiResolvedActionArgument Argument(
        UiActionInvocation invocation,
        string argumentName) =>
        invocation.Arguments.SingleOrDefault(argument => argument.Name == argumentName)
        ?? throw new InvalidDataException(
            $"Action '{invocation.Id}' has no resolved '{argumentName}' argument");

    private bool InvocationMatchesDefinition(UiActionInvocation invocation)
    {
        if (invocation.Arguments.Count != invocation.Definition.Arguments.Count)
        {
            return false;
        }

        for (int index = 0; index < invocation.Arguments.Count; index++)
        {
            UiActionArgument declared = invocation.Definition.Arguments[index];
            UiResolvedActionArgument resolved = invocation.Arguments[index];
            if (declared.Name != resolved.Name || declared.Kind != resolved.Kind)
            {
                return false;
            }

            if (declared.Kind == UiActionArgumentKind.ProductId
                && declared.Value != resolved.Value)
            {
                return false;
            }

            try
            {
                UiRuntimeKey.Validate(resolved.Value, declared.Name);
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<UiActionDefinition, UiScreenDefinition> IndexActions(
        UiProductManifest manifest)
    {
        var owners = new Dictionary<UiActionDefinition, UiScreenDefinition>(
            ReferenceEqualityComparer.Instance);
        foreach (UiScreenDefinition screen in manifest.Screens)
        {
            foreach (UiActionDefinition action in screen.Actions)
            {
                owners.Add(action, screen);
            }
        }

        return owners;
    }

    private static void ValidateContract(UiProductManifest manifest)
    {
        UiRuntimeKey.Validate(MainMenuScreenId, nameof(MainMenuScreenId));
        string[] requiredScreens =
        [
            MainMenuScreenId,
            EulaScreenId,
            LoginScreenId,
            CreditsScreenId,
            TooltipScreenId,
            ConnectionScreenId,
            ShardScreenId,
            CharacterScreenId,
            CharacterPreGeneratorScreenId,
            CharacterGeneratorScreenId,
            MessageBoxScreenId,
        ];
        var screens = manifest.Screens.ToDictionary(screen => screen.Id, StringComparer.Ordinal);
        if (screens.Count != requiredScreens.Length)
        {
            throw new InvalidDataException(
                $"UI product must contain exactly {requiredScreens.Length} out-of-game screens");
        }

        foreach (string screenId in requiredScreens)
        {
            if (!screens.ContainsKey(screenId))
            {
                throw new InvalidDataException($"UI product has no required screen '{screenId}'");
            }
        }

        foreach (string forbidden in new[] { "local-game", "character-template" })
        {
            if (screens.ContainsKey(forbidden))
            {
                throw new InvalidDataException($"Debug-only screen '{forbidden}' cannot ship");
            }
        }

        ValidateMain(screens[MainMenuScreenId]);
        ValidateCollection(screens[ShardScreenId], "shards", "shard-row-prototype");
        ValidateCollection(screens[CharacterScreenId], "characters", "character-row-prototype");
        ValidateTooltipCollection(screens[TooltipScreenId]);

        foreach (UiScreenDefinition screen in manifest.Screens)
        {
            if (screen.Actions.Count != s_actionCounts[screen.Id])
            {
                throw new InvalidDataException(
                    $"UI product screen '{screen.Id}' must declare exactly {s_actionCounts[screen.Id]} actions");
            }

            foreach (UiActionDefinition action in screen.Actions)
            {
                if (!IsAllowed(screen.Id, action.Id))
                {
                    throw new InvalidDataException(
                        $"UI product action '{screen.Id}.{action.Id}' has no explicit route");
                }

                ValidateActionArguments(action);
            }
        }

        RequireActions(screens[EulaScreenId], "accept-eula", "decline-eula", "close-eula");
        RequireActions(screens[LoginScreenId], "submit-login", "cancel-login", "show-credits", "quit");
        RequireActions(screens[CreditsScreenId], "previous-credit", "next-credit", "close-credits");
        RequireActions(screens[ConnectionScreenId], "cancel-connection");
        RequireActions(
            screens[ShardScreenId],
            "select-shard",
            "connect-shard",
            "cancel-shard-select");
        RequireActions(
            screens[CharacterScreenId],
            "select-character",
            "enter-world",
            "create-character",
            "change-shard",
            "back-to-login");
        RequireActions(
            screens[CharacterPreGeneratorScreenId],
            "select-archetype",
            "continue-creation",
            "continue-selected-archetype",
            "back-to-characters");
        RequireActions(
            screens[CharacterGeneratorScreenId],
            "create-character",
            "back-to-archetypes");

        ValidateTooltipCoverage(screens[EulaScreenId], "close-button");
        ValidateTooltipCoverage(
            screens[LoginScreenId],
            "enter-button",
            "credits-button",
            "exit-button");
        ValidateTooltipCoverage(
            screens[CreditsScreenId],
            "previous-button",
            "next-button",
            "exit-button");
        ValidateTooltipCoverage(screens[ConnectionScreenId], "cancel-button");
        ValidateTooltipCoverage(
            screens[ShardScreenId],
            "shard-row-prototype",
            "last-login-button");
        ValidateTooltipCoverage(
            screens[CharacterScreenId],
            "character-row-prototype",
            "enter-button",
            "create-button",
            "delete-button",
            "change-shard-button",
            "back-button");
        ValidateTooltipCoverage(
            screens[CharacterPreGeneratorScreenId],
            "choose-button",
            "back-button");
        ValidateTooltipCoverage(
            screens[CharacterGeneratorScreenId],
            "create-button",
            "back-button",
            "randomize-button",
            "helmet-toggle",
            "armor-toggle");
        ValidateTooltipCoverage(
            screens[MessageBoxScreenId],
            "dialog-panel",
            "accept-button",
            "decline-button",
            "confirm-button");

        ValidateProductCoverage(
            screens[CharacterPreGeneratorScreenId],
            "select-archetype",
            "archetype",
            s_archetypes);
        ValidateProductCoverage(
            screens[CharacterPreGeneratorScreenId],
            "continue-creation",
            "archetype",
            s_archetypes);
        ValidateProductCoverage(
            screens[CharacterPreGeneratorScreenId],
            "preview-archetype",
            "archetype",
            s_archetypes);
        ValidateProductCoverage(
            screens[CharacterGeneratorScreenId],
            "select-preset",
            "preset",
            s_presets);
        ValidateProductCoverage(
            screens[CharacterGeneratorScreenId],
            "preview-preset",
            "preset",
            s_presets);
        ValidateProductCoverage(
            screens[CharacterGeneratorScreenId],
            "previous-appearance-option",
            "control",
            s_appearanceControls);
        ValidateProductCoverage(
            screens[CharacterGeneratorScreenId],
            "next-appearance-option",
            "control",
            s_appearanceControls);
        ValidateProductCoverage(
            screens[CharacterGeneratorScreenId],
            "preview-appearance-control",
            "control",
            s_appearanceControls);
    }

    private static void ValidateTooltipCoverage(
        UiScreenDefinition screen,
        params string[] roleIds)
    {
        var expected = roleIds.ToHashSet(StringComparer.Ordinal);
        foreach (string actionId in new[] { "show-tooltip", "hide-tooltip" })
        {
            UiActionDefinition[] actions = screen.Actions
                .Where(action => action.Id == actionId)
                .ToArray();
            var actual = actions
                .Select(action => action.Arguments.Single().Value!)
                .ToHashSet(StringComparer.Ordinal);
            if (actions.Length != expected.Count || !actual.SetEquals(expected))
            {
                throw new InvalidDataException(
                    $"UI product screen '{screen.Id}' has incomplete '{actionId}' coverage");
            }
        }
    }

    private static void ValidateProductCoverage(
        UiScreenDefinition screen,
        string actionId,
        string argumentName,
        IReadOnlySet<string> expectedValues)
    {
        UiActionDefinition[] actions = screen.Actions
            .Where(action => action.Id == actionId)
            .ToArray();
        var actual = actions
            .Select(action => action.Arguments.Single(argument => argument.Name == argumentName).Value!)
            .ToHashSet(StringComparer.Ordinal);
        if (actions.Length != expectedValues.Count || !actual.SetEquals(expectedValues))
        {
            throw new InvalidDataException(
                $"UI product screen '{screen.Id}' has incomplete '{actionId}' product coverage");
        }
    }

    private static void ValidateActionArguments(UiActionDefinition action)
    {
        switch (action.Id)
        {
            case "show-tooltip":
                ValidateTooltipAction(action, UiActionEvent.HoverEntered);
                return;
            case "hide-tooltip":
                ValidateTooltipAction(action, UiActionEvent.HoverExited);
                return;
            case "select-shard":
            case "connect-shard":
                ValidateDynamicArgument(action, "shard", "shards");
                return;
            case "select-character":
            case "enter-world":
                ValidateDynamicArgument(action, "character", "characters");
                return;
            case "select-archetype":
            case "continue-creation":
            case "preview-archetype":
                ValidateProductArgument(action, "archetype", s_archetypes);
                return;
            case "select-preset":
            case "preview-preset":
                ValidateProductArgument(action, "preset", s_presets);
                return;
            case "previous-appearance-option":
            case "next-appearance-option":
            case "preview-appearance-control":
                ValidateProductArgument(action, "control", s_appearanceControls);
                return;
            default:
                if (action.Arguments.Count != 0)
                {
                    throw new InvalidDataException(
                        $"UI product action '{action.Id}' cannot carry arguments");
                }

                return;
        }
    }

    private static void ValidateTooltipAction(
        UiActionDefinition action,
        UiActionEvent actionEvent)
    {
        ValidateProductArgument(action, "tooltip", null);
        if (action.Triggers.Count != 1)
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' must have exactly one trigger");
        }

        UiActionTrigger trigger = action.Triggers[0];
        string tooltip = action.Arguments[0].Value!;
        if (trigger.Event != actionEvent || trigger.Role != tooltip)
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' must bind tooltip '{tooltip}' to {actionEvent}");
        }
    }

    private static void ValidateDynamicArgument(
        UiActionDefinition action,
        string name,
        string collection)
    {
        if (action.Arguments.Count != 1)
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' must have exactly one argument");
        }

        UiActionArgument argument = action.Arguments[0];
        if (argument.Name != name
            || argument.Kind != UiActionArgumentKind.CollectionItemId
            || argument.Collection != collection
            || argument.Value is not null)
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' has the wrong collection item argument");
        }
    }

    private static void ValidateProductArgument(
        UiActionDefinition action,
        string name,
        IReadOnlySet<string>? allowedValues)
    {
        if (action.Arguments.Count != 1)
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' must have exactly one argument");
        }

        UiActionArgument argument = action.Arguments[0];
        if (argument.Name != name
            || argument.Kind != UiActionArgumentKind.ProductId
            || argument.Value is null
            || argument.Collection is not null
            || (allowedValues is not null && !allowedValues.Contains(argument.Value)))
        {
            throw new InvalidDataException(
                $"UI product action '{action.Id}' has the wrong product argument");
        }
    }

    private static void ValidateMain(UiScreenDefinition screen)
    {
        screen.GetRole("scene-content");
        screen.GetRole("client-version");
        UiValueBinding version = screen.Values.SingleOrDefault(value => value.Id == "client-version")
            ?? throw new InvalidDataException("Main menu has no client-version value");
        if (version.Role != "client-version"
            || version.Kind != UiValueKind.Text
            || version.Access is not UiValueAccess.Write and not UiValueAccess.ReadWrite
            || version.Secret)
        {
            throw new InvalidDataException("Main menu client-version value has the wrong contract");
        }
    }

    private static void ValidateCollection(
        UiScreenDefinition screen,
        string collectionId,
        string itemRole)
    {
        UiCollectionBinding collection = screen.Collections.SingleOrDefault(
            candidate => candidate.Id == collectionId)
            ?? throw new InvalidDataException(
                $"Screen '{screen.Id}' has no '{collectionId}' collection");
        if (collection.Selection != UiCollectionSelection.Single
            || collection.ItemRole != itemRole)
        {
            throw new InvalidDataException(
                $"Screen '{screen.Id}' collection '{collectionId}' has the wrong selection contract");
        }
    }

    private static void ValidateTooltipCollection(UiScreenDefinition screen)
    {
        if (screen.Collections.Count != 1)
        {
            throw new InvalidDataException("Main-menu tooltip must declare one line collection");
        }

        UiCollectionBinding collection = screen.Collections[0];
        if (collection.Id != "tooltip-lines"
            || collection.Role != "tooltip-container"
            || collection.ItemRole != "tooltip-line-prototype"
            || collection.Selection != UiCollectionSelection.None)
        {
            throw new InvalidDataException("Main-menu tooltip line collection has the wrong contract");
        }
    }

    private static void RequireActions(UiScreenDefinition screen, params string[] actionIds)
    {
        foreach (string actionId in actionIds)
        {
            if (!screen.Actions.Any(action => action.Id == actionId))
            {
                throw new InvalidDataException(
                    $"Screen '{screen.Id}' has no required action '{actionId}'");
            }
        }
    }

    private static bool IsAllowed(string screenId, string actionId) =>
        s_allowedActions.TryGetValue(screenId, out HashSet<string>? actions)
        && actions.Contains(actionId);

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);

    private static string ScreenId(OutOfGameScreen screen) => screen switch
    {
        OutOfGameScreen.Login => LoginScreenId,
        OutOfGameScreen.ConnectionProgress => ConnectionScreenId,
        OutOfGameScreen.ShardSelect => ShardScreenId,
        OutOfGameScreen.CharacterSelect => CharacterScreenId,
        OutOfGameScreen.CharacterPreGenerator => CharacterPreGeneratorScreenId,
        OutOfGameScreen.CharacterGenerator => CharacterGeneratorScreenId,
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    private static int Priority(OutOfGameScreen screen) => screen switch
    {
        OutOfGameScreen.CharacterSelect => 500,
        OutOfGameScreen.CharacterPreGenerator or OutOfGameScreen.CharacterGenerator => 600,
        OutOfGameScreen.Login or OutOfGameScreen.ShardSelect => 700,
        OutOfGameScreen.ConnectionProgress => 800,
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    private enum PendingOperation
    {
        None,
        LoadShards,
        ConnectShard,
        LoadCharacters,
        EnterWorld,
    }
}

public readonly record struct OutOfGameFlowStart(string ClientVersion, bool RequireEula);

public enum OutOfGameScreen
{
    Login,
    ConnectionProgress,
    ShardSelect,
    CharacterSelect,
    CharacterPreGenerator,
    CharacterGenerator,
}

public sealed record OutOfGameLayer(
    string ScreenId,
    int Priority,
    bool Visible,
    bool Persistent);

public sealed record OutOfGamePresentation(
    bool IsRunning,
    OutOfGameScreen Screen,
    string ClientVersion,
    string? TooltipId,
    string? SelectedShardId,
    string? SelectedCharacterId,
    string? SelectedArchetypeId,
    IReadOnlyList<OutOfGameLayer> Layers)
{
    public OutOfGameLayer Layer(string screenId) =>
        Layers.Single(layer => layer.ScreenId == screenId);
}

public enum OutOfGameRouteStatus
{
    Handled,
    Forwarded,
    Rejected,
}

public enum OutOfGameFlowEffect
{
    None,
    Quit,
    SignOut,
    EnterWorld,
}

public sealed record OutOfGameFlowDispatch(
    OutOfGameRouteStatus Status,
    OutOfGamePresentation Presentation,
    UiActionInvocation? ScreenAction,
    OutOfGameFlowEffect Effect,
    string? Rejection);

public enum OutOfGameFlowSignalKind
{
    EulaAccepted,
    EulaDeclined,
    EulaClosed,
    CreditsClosed,
    MessageBoxOpened,
    MessageBoxClosed,
    LoginSucceeded,
    ShardListReady,
    ShardConnected,
    CharacterListReady,
    CharacterCreated,
    WorldEntered,
    OperationFailed,
    SignedOut,
    TooltipShown,
    TooltipHidden,
}

public sealed record OutOfGameFlowSignal
{
    private OutOfGameFlowSignal(OutOfGameFlowSignalKind kind, string? productItemId = null)
    {
        Kind = kind;
        ProductItemId = productItemId;
    }

    public OutOfGameFlowSignalKind Kind { get; }
    public string? ProductItemId { get; }

    public static OutOfGameFlowSignal EulaAccepted { get; } =
        new(OutOfGameFlowSignalKind.EulaAccepted);
    public static OutOfGameFlowSignal EulaDeclined { get; } =
        new(OutOfGameFlowSignalKind.EulaDeclined);
    public static OutOfGameFlowSignal EulaClosed { get; } =
        new(OutOfGameFlowSignalKind.EulaClosed);
    public static OutOfGameFlowSignal CreditsClosed { get; } =
        new(OutOfGameFlowSignalKind.CreditsClosed);
    public static OutOfGameFlowSignal MessageBoxOpened { get; } =
        new(OutOfGameFlowSignalKind.MessageBoxOpened);
    public static OutOfGameFlowSignal MessageBoxClosed { get; } =
        new(OutOfGameFlowSignalKind.MessageBoxClosed);
    public static OutOfGameFlowSignal LoginSucceeded { get; } =
        new(OutOfGameFlowSignalKind.LoginSucceeded);
    public static OutOfGameFlowSignal ShardListReady { get; } =
        new(OutOfGameFlowSignalKind.ShardListReady);
    public static OutOfGameFlowSignal ShardConnected { get; } =
        new(OutOfGameFlowSignalKind.ShardConnected);
    public static OutOfGameFlowSignal CharacterListReady { get; } =
        new(OutOfGameFlowSignalKind.CharacterListReady);
    public static OutOfGameFlowSignal WorldEntered { get; } =
        new(OutOfGameFlowSignalKind.WorldEntered);
    public static OutOfGameFlowSignal OperationFailed { get; } =
        new(OutOfGameFlowSignalKind.OperationFailed);
    public static OutOfGameFlowSignal SignedOut { get; } =
        new(OutOfGameFlowSignalKind.SignedOut);
    public static OutOfGameFlowSignal TooltipHidden { get; } =
        new(OutOfGameFlowSignalKind.TooltipHidden);

    public static OutOfGameFlowSignal CharacterCreated(string characterProductId) =>
        WithProductId(OutOfGameFlowSignalKind.CharacterCreated, characterProductId);

    public static OutOfGameFlowSignal TooltipShown(string tooltipProductId) =>
        WithProductId(OutOfGameFlowSignalKind.TooltipShown, tooltipProductId);

    private static OutOfGameFlowSignal WithProductId(
        OutOfGameFlowSignalKind kind,
        string productItemId)
    {
        ArgumentNullException.ThrowIfNull(productItemId);
        UiRuntimeKey.Validate(productItemId, nameof(productItemId));
        return new OutOfGameFlowSignal(kind, productItemId);
    }
}
