using System.Text;
using System.Text.Json;

namespace SarnautCore.UI.Tests;

public sealed class OutOfGameFlowControllerTests
{
    [Fact]
    public void OpensWithTheAuthoredLayerOrderAndPersistentMainContent()
    {
        OutOfGameFlowController flow = Flow(requireEula: true);

        OutOfGamePresentation state = flow.Presentation;
        Assert.True(state.IsRunning);
        Assert.Equal(OutOfGameScreen.Login, state.Screen);
        Assert.Equal("1.1.02.0-native", state.ClientVersion);
        Assert.Equal(-128, state.Layer(OutOfGameFlowController.MainMenuScreenId).Priority);
        Assert.True(state.Layer(OutOfGameFlowController.MainMenuScreenId).Persistent);
        Assert.True(state.Layer(OutOfGameFlowController.MainMenuScreenId).Visible);
        Assert.Equal(700, state.Layer(OutOfGameFlowController.LoginScreenId).Priority);
        Assert.Equal(749, state.Layer(OutOfGameFlowController.CreditsScreenId).Priority);
        Assert.Equal(750, state.Layer(OutOfGameFlowController.TooltipScreenId).Priority);
        Assert.True(state.Layer(OutOfGameFlowController.TooltipScreenId).Persistent);
        Assert.Equal(6000, state.Layer(OutOfGameFlowController.EulaScreenId).Priority);
        Assert.True(state.Layer(OutOfGameFlowController.EulaScreenId).Visible);
    }

    [Fact]
    public void EulaIsModalAndDeclineQuitsWithoutASecondRoundTrip()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product, requireEula: true);

        OutOfGameFlowDispatch blocked = flow.Route(Invoke(product, "login-account", "submit-login"));
        Assert.Equal(OutOfGameRouteStatus.Rejected, blocked.Status);
        Assert.Null(blocked.ScreenAction);

        AssertForwarded(flow.Route(Invoke(product, "eula", "decline-eula")));
        OutOfGameFlowDispatch quit = flow.Advance(OutOfGameFlowSignal.EulaDeclined);
        Assert.Equal(OutOfGameRouteStatus.Handled, quit.Status);
        Assert.Equal(OutOfGameFlowEffect.Quit, quit.Effect);
        Assert.False(quit.Presentation.IsRunning);
    }

    [Fact]
    public void AcceptedEulaRevealsLoginAndCreditsRestoresIt()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product, requireEula: true);

        AssertForwarded(flow.Route(Invoke(product, "eula", "accept-eula")));
        Assert.Equal(
            OutOfGameRouteStatus.Handled,
            flow.Advance(OutOfGameFlowSignal.EulaAccepted).Status);
        OutOfGameFlowDispatch credits = flow.Route(
            Invoke(product, "login-account", "show-credits"));
        Assert.True(credits.Presentation.Layer(OutOfGameFlowController.CreditsScreenId).Visible);
        Assert.Equal(749, credits.Presentation.Layer(OutOfGameFlowController.CreditsScreenId).Priority);
        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Route(Invoke(product, "login-account", "submit-login")).Status);

        AssertForwarded(flow.Route(Invoke(product, "credits", "close-credits")));
        OutOfGameFlowDispatch closed = flow.Advance(OutOfGameFlowSignal.CreditsClosed);
        Assert.False(closed.Presentation.Layer(OutOfGameFlowController.CreditsScreenId).Visible);
        Assert.Equal(OutOfGameScreen.Login, closed.Presentation.Screen);
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("cancel-login")]
    public void LoginExitIsImmediate(string actionId)
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        OutOfGameFlowDispatch dispatch = flow.Route(
            Invoke(product, "login-account", actionId));

        Assert.Equal(OutOfGameFlowEffect.Quit, dispatch.Effect);
        Assert.False(dispatch.Presentation.IsRunning);
        Assert.Null(dispatch.ScreenAction);
    }

    [Fact]
    public void RunsLoginShardCharacterCreateAndWorldEntryLifecycle()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        AssertForwarded(flow.Route(Invoke(product, "login-account", "submit-login")));
        Assert.Equal(
            OutOfGameScreen.ConnectionProgress,
            flow.Advance(OutOfGameFlowSignal.LoginSucceeded).Presentation.Screen);
        Assert.Equal(
            800,
            flow.Presentation.Layer(OutOfGameFlowController.ConnectionScreenId).Priority);
        Assert.Equal(
            OutOfGameScreen.ShardSelect,
            flow.Advance(OutOfGameFlowSignal.ShardListReady).Presentation.Screen);

        AssertForwarded(flow.Route(Invoke(
            product,
            "shard-select",
            "select-shard",
            "shard-one")));
        OutOfGameFlowDispatch connecting = flow.Route(Invoke(
            product,
            "shard-select",
            "connect-shard",
            "shard-two"));
        AssertForwarded(connecting);
        Assert.Equal("shard-two", connecting.Presentation.SelectedShardId);
        Assert.Equal(OutOfGameScreen.ConnectionProgress, connecting.Presentation.Screen);

        Assert.Equal(
            OutOfGameScreen.ConnectionProgress,
            flow.Advance(OutOfGameFlowSignal.ShardConnected).Presentation.Screen);
        Assert.Equal(
            OutOfGameScreen.CharacterSelect,
            flow.Advance(OutOfGameFlowSignal.CharacterListReady).Presentation.Screen);
        Assert.Equal(
            500,
            flow.Presentation.Layer(OutOfGameFlowController.CharacterScreenId).Priority);

        AssertForwarded(flow.Route(Invoke(
            product,
            "character-selector",
            "select-character",
            "character-one")));
        Assert.Equal(
            OutOfGameScreen.CharacterPreGenerator,
            flow.Route(Invoke(product, "character-selector", "create-character"))
                .Presentation.Screen);
        Assert.Equal(
            600,
            flow.Presentation.Layer(OutOfGameFlowController.CharacterPreGeneratorScreenId).Priority);
        AssertForwarded(flow.Route(Invoke(
            product,
            "character-pre-generator",
            "select-archetype",
            "league-warrior")));
        Assert.Equal(
            OutOfGameScreen.CharacterGenerator,
            flow.Route(Invoke(
                product,
                "character-pre-generator",
                "continue-selected-archetype"))
                .Presentation.Screen);
        Assert.Equal(
            600,
            flow.Presentation.Layer(OutOfGameFlowController.CharacterGeneratorScreenId).Priority);

        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Advance(OutOfGameFlowSignal.CharacterCreated("character-two")).Status);
        AssertForwarded(flow.Route(Invoke(
            product,
            "character-generator",
            "create-character")));
        OutOfGameFlowDispatch created = flow.Advance(
            OutOfGameFlowSignal.CharacterCreated("character-two"));
        Assert.Equal(OutOfGameScreen.CharacterSelect, created.Presentation.Screen);
        Assert.Equal("character-two", created.Presentation.SelectedCharacterId);

        OutOfGameFlowDispatch entering = flow.Route(Invoke(
            product,
            "character-selector",
            "enter-world",
            "character-two"));
        AssertForwarded(entering);
        Assert.Equal(OutOfGameScreen.ConnectionProgress, entering.Presentation.Screen);

        OutOfGameFlowDispatch entered = flow.Advance(OutOfGameFlowSignal.WorldEntered);
        Assert.Equal(OutOfGameFlowEffect.EnterWorld, entered.Effect);
        Assert.False(entered.Presentation.IsRunning);
    }

    [Fact]
    public void ConnectionFailureAndCancellationRestoreTheOwningScreen()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = AtShard(product);
        flow.Route(Invoke(product, "shard-select", "connect-shard", "shard-one"));

        Assert.Equal(
            OutOfGameScreen.ShardSelect,
            flow.Advance(OutOfGameFlowSignal.OperationFailed).Presentation.Screen);

        flow.Route(Invoke(product, "shard-select", "connect-shard", "shard-one"));
        OutOfGameFlowDispatch cancelled = flow.Route(
            Invoke(product, "connection-progress", "cancel-connection"));
        AssertForwarded(cancelled);
        Assert.Equal(OutOfGameScreen.ShardSelect, cancelled.Presentation.Screen);
    }

    [Fact]
    public void CollectionIdentityComesFromTheResolvedProductItemArgument()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = AtShard(product);

        flow.Route(Invoke(product, "shard-select", "select-shard", "shard-one"));
        OutOfGameFlowDispatch dispatch = flow.Route(Invoke(
            product,
            "shard-select",
            "connect-shard",
            "shard-two"));

        Assert.Equal("shard-two", dispatch.Presentation.SelectedShardId);
        UiResolvedActionArgument argument = Assert.Single(dispatch.ScreenAction!.Arguments);
        Assert.Equal(UiActionArgumentKind.CollectionItemId, argument.Kind);
        Assert.Equal("shard-two", argument.Value);
    }

    [Fact]
    public void StaleAndForgedInvocationsFailClosed()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Route(Invoke(product, "shard-select", "refresh-shards")).Status);

        UiActionInvocation valid = Invoke(product, "login-account", "submit-login");
        var forgedDefinition = valid.Definition with { Id = "submit-login" };
        var forged = new UiActionInvocation(forgedDefinition, valid.Arguments);
        Assert.Equal(OutOfGameRouteStatus.Rejected, flow.Route(forged).Status);
    }

    [Fact]
    public void TooltipLayerKeepsRetailPriorityAndProductIdentity()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        OutOfGameFlowDispatch shown = flow.Route(
            Invoke(product, "login-account", "show-tooltip", "credits-button"));
        OutOfGameLayer tooltip = shown.Presentation.Layer(OutOfGameFlowController.TooltipScreenId);
        Assert.True(tooltip.Visible);
        Assert.True(tooltip.Persistent);
        Assert.Equal(750, tooltip.Priority);
        Assert.Equal("credits-button", shown.Presentation.TooltipId);

        OutOfGameFlowDispatch hidden = flow.Route(
            Invoke(product, "login-account", "hide-tooltip", "credits-button"));
        Assert.False(hidden.Presentation.Layer(OutOfGameFlowController.TooltipScreenId).Visible);
        Assert.Null(hidden.Presentation.TooltipId);
    }

    [Fact]
    public void TooltipRendersAboveTheEulaWhileItsCloseControlIsHovered()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product, requireEula: true);

        OutOfGameFlowDispatch shown = flow.Route(
            Invoke(product, "eula", "show-tooltip", "close-button"));

        Assert.True(shown.Presentation.Layer(OutOfGameFlowController.TooltipScreenId).Visible);
        Assert.Equal(
            6001,
            shown.Presentation.Layer(OutOfGameFlowController.TooltipScreenId).Priority);
        Assert.True(
            shown.Presentation.Layer(OutOfGameFlowController.TooltipScreenId).Priority
            > shown.Presentation.Layer(OutOfGameFlowController.EulaScreenId).Priority);
    }

    [Fact]
    public void CompletionSignalsRequireTheirMatchingPendingAction()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Advance(OutOfGameFlowSignal.LoginSucceeded).Status);
        flow.Route(Invoke(product, "login-account", "submit-login"));
        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Route(Invoke(product, "login-account", "submit-login")).Status);
        flow.Advance(OutOfGameFlowSignal.OperationFailed);
        AssertForwarded(flow.Route(Invoke(product, "login-account", "submit-login")));
    }

    [Fact]
    public void MessageBoxIsARealPriority750ModalLayer()
    {
        UiProductManifest product = Product();
        OutOfGameFlowController flow = Flow(product);

        OutOfGameFlowDispatch opened = flow.Advance(OutOfGameFlowSignal.MessageBoxOpened);
        OutOfGameLayer message = opened.Presentation.Layer(
            OutOfGameFlowController.MessageBoxScreenId);
        Assert.True(message.Visible);
        Assert.Equal(750, message.Priority);
        Assert.Equal(
            OutOfGameRouteStatus.Rejected,
            flow.Route(Invoke(product, "login-account", "submit-login")).Status);
        AssertForwarded(flow.Route(Invoke(product, "message-box", "confirm")));
        Assert.False(flow.Advance(OutOfGameFlowSignal.MessageBoxClosed)
            .Presentation.Layer(OutOfGameFlowController.MessageBoxScreenId).Visible);
    }

    [Fact]
    public void ProductContractRejectsDebugScreensAndUnroutedActions()
    {
        string debugProduct = ProductJson(extraScreenId: "character-template");
        using var debugStream = new MemoryStream(Encoding.UTF8.GetBytes(debugProduct));
        UiProductManifest debug = NativeUiProductManifestParser.Parse(debugStream);
        Assert.Throws<InvalidDataException>(() => OutOfGameFlowController.Open(
            debug,
            new OutOfGameFlowStart("test", RequireEula: false)));

        string unknownProduct = ProductJson(extraLoginAction: "open-settings");
        using var unknownStream = new MemoryStream(Encoding.UTF8.GetBytes(unknownProduct));
        UiProductManifest unknown = NativeUiProductManifestParser.Parse(unknownStream);
        Assert.Throws<InvalidDataException>(() => OutOfGameFlowController.Open(
            unknown,
            new OutOfGameFlowStart("test", RequireEula: false)));
    }

    [Fact]
    public void ProductContractRejectsDuplicatedStaticProductIdentity()
    {
        UiProductManifest product = Product();
        UiScreenDefinition preGenerator = product.Screens.Single(
            screen => screen.Id == OutOfGameFlowController.CharacterPreGeneratorScreenId);
        UiActionDefinition first = preGenerator.Actions.First(
            action => action.Id == "select-archetype");
        UiActionDefinition second = preGenerator.Actions.Where(
                action => action.Id == "select-archetype")
            .Skip(1)
            .First();
        UiActionArgument duplicatedArgument = second.Arguments.Single() with
        {
            Value = first.Arguments.Single().Value,
        };
        UiActionDefinition duplicatedAction = second with
        {
            Arguments = [duplicatedArgument],
        };
        UiActionDefinition[] actions = preGenerator.Actions
            .Select(action => ReferenceEquals(action, second) ? duplicatedAction : action)
            .ToArray();
        UiScreenDefinition malformedScreen = preGenerator with { Actions = actions };
        UiProductManifest malformed = product with
        {
            Screens = product.Screens
                .Select(screen => ReferenceEquals(screen, preGenerator) ? malformedScreen : screen)
                .ToArray(),
        };

        Assert.Throws<InvalidDataException>(() => OutOfGameFlowController.Open(
            malformed,
            new OutOfGameFlowStart("test", RequireEula: false)));
    }

    private static void AssertForwarded(OutOfGameFlowDispatch dispatch)
    {
        Assert.Equal(OutOfGameRouteStatus.Forwarded, dispatch.Status);
        Assert.NotNull(dispatch.ScreenAction);
        Assert.Equal(OutOfGameFlowEffect.None, dispatch.Effect);
        Assert.Null(dispatch.Rejection);
    }

    private static OutOfGameFlowController AtShard(UiProductManifest product)
    {
        OutOfGameFlowController flow = Flow(product);
        flow.Route(Invoke(product, "login-account", "submit-login"));
        flow.Advance(OutOfGameFlowSignal.LoginSucceeded);
        flow.Advance(OutOfGameFlowSignal.ShardListReady);
        return flow;
    }

    private static OutOfGameFlowController Flow(bool requireEula = false) =>
        Flow(Product(), requireEula);

    private static OutOfGameFlowController Flow(
        UiProductManifest product,
        bool requireEula = false) =>
        OutOfGameFlowController.Open(
            product,
            new OutOfGameFlowStart("1.1.02.0-native", requireEula));

    private static UiProductManifest Product()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ProductJson()));
        return NativeUiProductManifestParser.Parse(stream);
    }

    private static UiActionInvocation Invoke(
        UiProductManifest product,
        string screenId,
        string actionId,
        string? value = null)
    {
        UiActionDefinition definition = product.Screens.Single(screen => screen.Id == screenId)
            .Actions.Where(action => action.Id == actionId)
            .First(action => value is null
                || action.Arguments.Count == 0
                || action.Arguments[0].Kind == UiActionArgumentKind.CollectionItemId
                || action.Arguments[0].Value == value);
        UiResolvedActionArgument[] arguments = definition.Arguments.Select(argument =>
            new UiResolvedActionArgument(
                argument.Name,
                argument.Kind,
                argument.Kind == UiActionArgumentKind.ProductId
                    ? argument.Value!
                    : value ?? throw new ArgumentException("A collection item id is required")))
            .ToArray();
        return new UiActionInvocation(definition, arguments);
    }

    private static string ProductJson(
        string? extraScreenId = null,
        string? extraLoginAction = null)
    {
        var screens = new List<object>
        {
            Screen(
                "main-menu",
                [
                    "begin-primary-scene-drag",
                    "end-primary-scene-drag",
                    "begin-secondary-scene-drag",
                    "end-secondary-scene-drag",
                    "move-scene-camera-horizontal",
                    "move-scene-camera-vertical",
                    "zoom-scene-in",
                    "zoom-scene-out",
                ],
                extraRoles:
                [
                    Role("scene-content", "Control3D"),
                    Role("client-version", "Version"),
                ],
                values:
                [
                    new
                    {
                        id = "client-version",
                        role = "client-version",
                        kind = "text",
                        access = "write",
                        secret = false,
                    },
                ]),
            Screen(
                "eula",
                ["accept-eula", "decline-eula", "close-eula"],
                tooltipRoles: ["close-button"]),
            Screen(
                "login-account",
                AddOptional(
                    [
                        "submit-login",
                        "cancel-login",
                        "show-credits",
                        "quit",
                        "focus-next",
                        "update-account-name",
                        "update-account-password",
                    ],
                    extraLoginAction),
                tooltipRoles: ["enter-button", "credits-button", "exit-button"]),
            Screen(
                "credits",
                ["previous-credit", "next-credit", "close-credits"],
                tooltipRoles: ["previous-button", "next-button", "exit-button"]),
            Screen(
                "main-menu-tooltip",
                [],
                extraRoles:
                [
                    Role("tooltip-panel", "Tooltip"),
                    Role("tooltip-container", "Tooltip/Container"),
                    Role("tooltip-line-prototype", "SmartLine"),
                ],
                collections:
                [
                    new
                    {
                        id = "tooltip-lines",
                        role = "tooltip-container",
                        item_role = "tooltip-line-prototype",
                        item_scene = "items/tooltip_line.tscn",
                        selection = "none",
                    },
                ]),
            Screen(
                "connection-progress",
                ["cancel-connection"],
                tooltipRoles: ["cancel-button"]),
            CollectionScreen(
                "shard-select",
                "shards",
                "shard-list",
                "shard-row-prototype",
                "shard",
                "select-shard",
                "connect-shard",
                [
                    "cancel-shard-select",
                    "refresh-shards",
                    "show-last-login",
                    "hide-last-login",
                    "navigate-previous-shard",
                    "navigate-next-shard",
                ],
                ["shard-row-prototype", "last-login-button"]),
            CollectionScreen(
                "character-selector",
                "characters",
                "character-list",
                "character-row-prototype",
                "character",
                "select-character",
                "enter-world",
                [
                    "create-character",
                    "request-delete",
                    "change-shard",
                    "back-to-login",
                    "confirm-delete",
                    "cancel-delete",
                    "update-delete-character-name",
                ],
                [
                    "character-row-prototype",
                    "enter-button",
                    "create-button",
                    "delete-button",
                    "change-shard-button",
                    "back-button",
                ]),
            Screen(
                "character-pre-generator",
                ["continue-selected-archetype", "back-to-characters"],
                staticActions:
                [
                    .. StaticActions("select-archetype", "archetype", Archetypes),
                    .. StaticActions("continue-creation", "archetype", Archetypes),
                    .. StaticActions("preview-archetype", "archetype", Archetypes),
                ],
                tooltipRoles: ["choose-button", "back-button"]),
            Screen(
                "character-generator",
                [
                    "create-character",
                    "cancel-name-entry",
                    "back-to-archetypes",
                    "randomize-character",
                    "toggle-helmet",
                    "toggle-armor",
                    "update-character-name",
                ],
                staticActions:
                [
                    .. StaticActions("select-preset", "preset", Presets),
                    .. StaticActions("previous-appearance-option", "control", AppearanceControls),
                    .. StaticActions("next-appearance-option", "control", AppearanceControls),
                    .. StaticActions("preview-preset", "preset", Presets),
                    .. StaticActions("preview-appearance-control", "control", AppearanceControls),
                ],
                tooltipRoles:
                [
                    "create-button",
                    "back-button",
                    "randomize-button",
                    "helmet-toggle",
                    "armor-toggle",
                ]),
            Screen(
                "message-box",
                ["accept", "decline", "confirm"],
                tooltipRoles:
                ["dialog-panel", "accept-button", "decline-button", "confirm-button"]),
        };
        if (extraScreenId is not null)
        {
            screens.Add(Screen(extraScreenId, []));
        }

        return JsonSerializer.Serialize(new
        {
            schema_id = "sarnaut.ui-product/v2",
            catalogs = new
            {
                cursors = "catalogs/cursors.tres",
                sounds = "catalogs/sounds.tres",
                theme = "catalogs/theme.tres",
            },
            screens,
        });
    }

    private static object CollectionScreen(
        string id,
        string collectionId,
        string collectionRole,
        string itemRole,
        string argumentName,
        string selectAction,
        string openAction,
        IReadOnlyList<string> ordinaryActions,
        IReadOnlyList<string> tooltipRoles)
    {
        object select = DynamicAction(
            selectAction,
            argumentName,
            collectionId,
            itemRole,
            "toggled");
        object open = DynamicAction(
            openAction,
            argumentName,
            collectionId,
            itemRole,
            "double-pressed");
        return Screen(
            id,
            ordinaryActions,
            extraRoles:
            [
                Role(collectionRole, "Collection"),
                Role(itemRole, "Collection/Prototype"),
            ],
            extraActions: [select, open],
            tooltipRoles: tooltipRoles,
            collections:
            [
                new
                {
                    id = collectionId,
                    role = collectionRole,
                    item_role = itemRole,
                    item_scene = $"items/{collectionId}.tscn",
                    selection = "single",
                },
            ],
            buttons:
            [
                new
                {
                    role = itemRole,
                    toggle = true,
                    initial_variant = "default",
                    variants = new object[]
                    {
                        new
                        {
                            id = "default",
                            visual_state = "default",
                            inputs = new object[]
                            {
                                new { input = "primary-pressed", @event = "toggled" },
                                new { input = "double-pressed", @event = "double-pressed" },
                                new { input = "hover-entered", @event = "hover-entered" },
                                new { input = "hover-exited", @event = "hover-exited" },
                            },
                        },
                        new
                        {
                            id = "selected",
                            visual_state = "selected",
                            inputs = new object[]
                            {
                                new { input = "double-pressed", @event = "double-pressed" },
                                new { input = "hover-entered", @event = "hover-entered" },
                                new { input = "hover-exited", @event = "hover-exited" },
                            },
                        },
                    },
                },
            ]);
    }

    private static object Screen(
        string id,
        IReadOnlyList<string> actionIds,
        IReadOnlyList<object>? extraRoles = null,
        IReadOnlyList<object>? extraActions = null,
        IReadOnlyList<object>? staticActions = null,
        IReadOnlyList<object>? values = null,
        IReadOnlyList<object>? collections = null,
        IReadOnlyList<object>? buttons = null,
        IReadOnlyList<string>? tooltipRoles = null)
    {
        var roles = new List<object>();
        var actions = new List<object>();
        int index = 0;
        foreach (string actionId in actionIds)
        {
            string roleId = $"action-{index++}";
            roles.Add(Role(roleId, $"Action{index}"));
            actions.Add(new
            {
                id = actionId,
                arguments = Array.Empty<object>(),
                triggers = new[]
                {
                    new { role = roleId, @event = "primary-pressed" },
                },
            });
        }

        if (extraRoles is not null)
        {
            roles.AddRange(extraRoles);
        }

        if (extraActions is not null)
        {
            actions.AddRange(extraActions);
        }

        if (staticActions is not null)
        {
            foreach (object action in staticActions)
            {
                string roleId = $"static-{index++}";
                roles.Add(Role(roleId, $"Static{index}"));
                actions.Add(AttachTrigger(action, roleId));
            }
        }

        if (tooltipRoles is not null)
        {
            var declaredRoles = roles
                .Select(role => JsonSerializer.SerializeToElement(role).GetProperty("id").GetString())
                .ToHashSet(StringComparer.Ordinal);
            foreach (string roleId in tooltipRoles)
            {
                if (declaredRoles.Add(roleId))
                {
                    roles.Add(Role(roleId, $"Tooltip{index++}"));
                }

                actions.Add(TooltipAction("show-tooltip", roleId, "hover-entered"));
                actions.Add(TooltipAction("hide-tooltip", roleId, "hover-exited"));
            }
        }

        return new
        {
            id,
            scene = $"screens/{id}.tscn",
            initially_visible = false,
            roles,
            actions,
            values = values ?? [],
            collections = collections ?? [],
            buttons = buttons ?? [],
            selection_groups = Array.Empty<object>(),
            focus_order = Array.Empty<string>(),
        };
    }

    private static object Role(string id, string node) => new
    {
        id,
        node,
        initially_visible = true,
    };

    private static object DynamicAction(
        string id,
        string argumentName,
        string collection,
        string role,
        string actionEvent) => new
        {
            id,
            arguments = new[]
        {
            new
            {
                name = argumentName,
                kind = "collection-item-id",
                collection,
            },
        },
            triggers = new[]
        {
            new { role, @event = actionEvent },
        },
        };

    private static object StaticAction(string id, string name, string value) => new
    {
        id,
        arguments = new[]
        {
            new { name, kind = "product-id", value },
        },
    };

    private static IEnumerable<object> StaticActions(
        string id,
        string name,
        IEnumerable<string> values) =>
        values.Select(value => StaticAction(id, name, value));

    private static object TooltipAction(string id, string role, string actionEvent) => new
    {
        id,
        arguments = new[]
        {
            new { name = "tooltip", kind = "product-id", value = role },
        },
        triggers = new[]
        {
            new { role, @event = actionEvent },
        },
    };

    private static object AttachTrigger(object action, string role)
    {
        string json = JsonSerializer.Serialize(action);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new
        {
            id = root.GetProperty("id").GetString(),
            arguments = root.GetProperty("arguments").Clone(),
            triggers = new[]
            {
                new { role, @event = "primary-pressed" },
            },
        };
    }

    private static string[] AddOptional(IReadOnlyList<string> values, string? optional) =>
        optional is null ? [.. values] : [.. values, optional];

    private static readonly string[] Archetypes =
    [
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
        "empire-stalker",
    ];

    private static readonly string[] Presets =
    [
        "preset-01",
        "preset-02",
        "preset-03",
        "preset-04",
    ];

    private static readonly string[] AppearanceControls =
    [
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
        "control-11",
    ];
}
