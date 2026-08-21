namespace SarnautCore.UI.Tests;

public sealed class UiWidgetStateTests
{
    [Fact]
    public void MomentaryPressUsesVariantCueAndReturnsProductAction()
    {
        UiRoleState state = VisibleScreen().Roles["enter"];

        Assert.False(state.BeginPress().Activated);
        UiActionDispatch dispatch = state.EndPress(activate: true);

        Assert.True(dispatch.Activated);
        Assert.Equal("button_yes", dispatch.Cue);
        Assert.Equal("primary", dispatch.VisualState);
        Assert.Equal(["submit-login"], dispatch.ActionIds);
        Assert.Equal("standard", state.VariantId);
    }

    [Fact]
    public void TogglePressAdvancesNativeVisualStateAndReturnsToggleAction()
    {
        UiRoleState state = VisibleScreen().Roles["options"];

        Assert.Equal("options-open", state.VisualState);
        Assert.False(state.BeginPress().Activated);
        UiActionDispatch first = state.EndPress(activate: true);

        Assert.Equal("options-closed", first.VisualState);
        Assert.Equal(["toggle-options"], first.ActionIds);
        Assert.False(state.BeginPress().Activated);
        UiActionDispatch second = state.EndPress(activate: true);
        Assert.Equal("button_no", second.Cue);
        Assert.Equal("options-open", second.VisualState);
    }

    [Fact]
    public void VisibilityUsesVariantOverrideThenRoleFallback()
    {
        UiScreenState screen = VisibleScreen();
        UiRoleState options = screen.Roles["options"];
        UiRoleState local = screen.Roles["local"];

        Assert.Equal("bag_close", options.Hide());
        Assert.Equal("bag_open", options.Show());
        Assert.False(local.IsVisible);
        Assert.Equal("ui_menu_open", local.Show());
        Assert.Null(local.Show());
    }

    [Fact]
    public void HiddenRoleRejectsInputAndClearsTransientState()
    {
        UiRoleState state = VisibleScreen().Roles["enter"];
        state.PointerEntered();
        state.BeginPress();

        state.Hide();

        Assert.False(state.IsPointerOver);
        Assert.False(state.IsPressed);
        Assert.False(state.PointerEntered().Activated);
        Assert.False(state.BeginPress().Activated);
        Assert.False(state.EndPress(activate: true).Activated);
    }

    [Fact]
    public void NonPointerEventsResolveThroughProductActionTable()
    {
        UiRoleState account = VisibleScreen().Roles["account"];

        Assert.Equal(["submit-login"], account.Dispatch(UiActionEvent.Submitted).Select(action => action.Id));
        Assert.Empty(account.Dispatch(UiActionEvent.Changed));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.Dispatch(UiActionEvent.Pressed));
    }

    [Fact]
    public void ScreenVisibilityEmitsScreenCues()
    {
        UiScreenDefinition definition = Assert.Single(UiProductFixture.Parse().Screens);
        var screen = new UiScreenState(definition);

        Assert.False(screen.IsVisible);
        Assert.False(screen.Roles["enter"].CanReceiveInput);
        Assert.False(screen.Roles["enter"].BeginPress().Activated);
        Assert.Equal(["ui_menu_open"], screen.Show());
        Assert.True(screen.Roles["enter"].CanReceiveInput);
        Assert.Equal(["ui_menu_close"], screen.Hide());
        Assert.False(screen.Roles["enter"].CanReceiveInput);
        Assert.Empty(screen.Hide());
    }

    private static UiScreenState VisibleScreen()
    {
        UiScreenDefinition definition = Assert.Single(UiProductFixture.Parse().Screens);
        var screen = new UiScreenState(definition);
        screen.Show();
        return screen;
    }

    [Fact]
    public void RoleShowCueWaitsUntilItsScreenIsVisible()
    {
        UiScreenDefinition definition = Assert.Single(UiProductFixture.Parse().Screens);
        var screen = new UiScreenState(definition);
        UiRoleState local = screen.Roles["local"];

        Assert.Null(local.Show());
        Assert.Equal(["ui_menu_open", "ui_menu_open"], screen.Show());
        Assert.True(local.CanReceiveInput);
    }

    [Fact]
    public void HoverTransitionsDispatchTypedPreviewActionsExactlyOnce()
    {
        UiRoleState preview = InteractionScreen().Roles["preview"];

        UiActionDispatch entered = preview.PointerEntered();
        Assert.True(entered.Activated);
        Assert.Equal("preview_hover", entered.Cue);
        UiActionDefinition previewAction = Assert.Single(entered.Actions);
        Assert.Equal("preview", previewAction.Id);
        Assert.Equal("league-warrior", Assert.Single(previewAction.Arguments).Value);
        Assert.False(preview.PointerEntered().Activated);

        UiActionDispatch exited = preview.PointerExited();
        Assert.True(exited.Activated);
        Assert.Equal("preview-end", Assert.Single(exited.Actions).Id);
        Assert.False(preview.PointerExited().Activated);
    }

    [Fact]
    public void DoublePressResolvesCollectionItemIdentityFromRowContext()
    {
        UiRoleState row = InteractionScreen().Roles["open"];
        Assert.Throws<InvalidOperationException>(() => row.DoublePress());

        UiActionDispatch dispatch = row.DoublePress(
            new UiCollectionItemContext("characters", "character-one"));

        Assert.True(dispatch.Activated);
        UiActionInvocation invocation = Assert.Single(dispatch.Invocations);
        Assert.Equal("open", invocation.Id);
        UiResolvedActionArgument argument = Assert.Single(invocation.Arguments);
        Assert.Equal(UiActionArgumentKind.CollectionItemId, argument.Kind);
        Assert.Equal("character-one", argument.Value);
        Assert.Empty(dispatch.ActionIds.Except(["open"]));
    }

    [Fact]
    public void SelectionGroupMovesExclusiveSelectionAndCanClearSelectedRole()
    {
        UiScreenState screen = InteractionScreen();
        UiRoleState choiceA = screen.Roles["choice-a"];
        UiRoleState choiceB = screen.Roles["choice-b"];

        Assert.Equal("choice-a", screen.SelectedRole("choice"));
        Assert.True(choiceA.IsSelected);
        Assert.False(choiceB.IsSelected);

        choiceB.BeginPress();
        UiActionDispatch selectedB = choiceB.EndPress(activate: true);
        Assert.Equal("choice-b", screen.SelectedRole("choice"));
        Assert.False(choiceA.IsSelected);
        Assert.True(choiceB.IsSelected);
        Assert.Equal("choice-b", Assert.Single(selectedB.Actions).Arguments[0].Value);
        Assert.Equal("clear", choiceA.VisualState);
        Assert.Equal("selected", choiceB.VisualState);

        choiceB.BeginPress();
        choiceB.EndPress(activate: true);
        Assert.Null(screen.SelectedRole("choice"));
        Assert.False(choiceB.IsSelected);
        Assert.Equal("clear", choiceB.VisualState);
    }

    [Fact]
    public void NonEmptySelectionGroupCannotClearItsSelectedRole()
    {
        string json = UiProductFixture.InteractionJson.Replace(
            "\"allow_empty\": true",
            "\"allow_empty\": false",
            StringComparison.Ordinal);
        UiScreenDefinition definition = Assert.Single(UiProductFixture.Parse(json).Screens);
        var screen = new UiScreenState(definition);
        UiRoleState choiceA = screen.Roles["choice-a"];

        choiceA.BeginPress();
        UiActionDispatch dispatch = choiceA.EndPress(activate: true);

        Assert.Equal("choice-a", screen.SelectedRole("choice"));
        Assert.True(choiceA.IsSelected);
        Assert.Equal("selected", choiceA.VisualState);
        Assert.False(dispatch.Activated);
        Assert.Empty(dispatch.Invocations);
    }

    private static UiScreenState InteractionScreen()
    {
        UiScreenDefinition definition = Assert.Single(
            UiProductFixture.Parse(UiProductFixture.InteractionJson).Screens);
        return new UiScreenState(definition);
    }

    [Fact]
    public void CollectionSelectionStartsEmptyMovesExclusivelyAndSuppressesReselect()
    {
        UiScreenState screen = InteractionScreen();
        UiCollectionState collection = screen.Collections["characters"];
        UiRoleState clonedPrototype = screen.Roles["open"];
        clonedPrototype.BeginPress();
        Assert.Throws<InvalidOperationException>(() => clonedPrototype.EndPress(
            activate: true,
            new UiCollectionItemContext("characters", "character-one")));

        Assert.Null(collection.SelectedProductItemId);
        UiCollectionActionDispatch first = collection.RouteInput(
            "character-one",
            UiPhysicalInput.PrimaryReleased);
        Assert.True(first.Activated);
        Assert.Null(first.PreviousProductItemId);
        Assert.Equal("character-one", first.SelectedProductItemId);
        Assert.Equal("character-one", Assert.Single(first.Invocations).Arguments[0].Value);
        Assert.Equal("selected", collection.VisualStateFor("character-one"));

        UiCollectionActionDispatch repeated = collection.RouteInput(
            "character-one",
            UiPhysicalInput.PrimaryReleased);
        Assert.False(repeated.Activated);
        Assert.Empty(repeated.Invocations);
        Assert.True(collection.IsSelected("character-one"));

        UiCollectionActionDispatch moved = collection.RouteInput(
            "character-two",
            UiPhysicalInput.PrimaryReleased);
        Assert.True(moved.Activated);
        Assert.Equal("character-one", moved.PreviousProductItemId);
        Assert.Equal("character-two", moved.SelectedProductItemId);
        Assert.False(collection.IsSelected("character-one"));
        Assert.True(collection.IsSelected("character-two"));
        Assert.Equal("clear", collection.VisualStateFor("character-one"));
        Assert.Equal("selected", collection.VisualStateFor("character-two"));
    }

    [Fact]
    public void CollectionDoublePressAlwaysDispatchesActivatedRowIdentity()
    {
        UiCollectionState collection = InteractionScreen().Collections["characters"];
        collection.RouteInput("character-one", UiPhysicalInput.PrimaryReleased);

        UiCollectionActionDispatch dispatch = collection.RouteInput(
            "character-two",
            UiPhysicalInput.DoublePressed);

        Assert.True(dispatch.Activated);
        Assert.Equal("character-one", dispatch.SelectedProductItemId);
        UiActionInvocation invocation = Assert.Single(dispatch.Invocations);
        Assert.Equal("open", invocation.Id);
        Assert.Equal("character-two", Assert.Single(invocation.Arguments).Value);
    }

    [Fact]
    public void CurrentVariantFiltersUnmappedPhysicalInput()
    {
        UiRoleState choice = InteractionScreen().Roles["choice-a"];

        UiActionDispatch dispatch = choice.RouteInput(UiPhysicalInput.SecondaryReleased);

        Assert.False(dispatch.Activated);
        Assert.Empty(dispatch.Invocations);
        Assert.True(choice.IsSelected);
        Assert.Equal("selected", choice.VisualState);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            choice.Dispatch(UiActionEvent.Toggled));
    }

    [Fact]
    public void NonButtonControlRoutesPrimaryPhysicalInput()
    {
        UiRoleState preview = InteractionScreen().Roles["preview"];

        UiActionDispatch dispatch = preview.RouteInput(UiPhysicalInput.PrimaryPressed);

        Assert.True(dispatch.Activated);
        Assert.Equal("begin-preview-drag", Assert.Single(dispatch.Invocations).Id);
    }

    [Fact]
    public void HiddenCollectionOwnerAndScreenRejectRowInput()
    {
        UiScreenState screen = InteractionScreen();
        UiCollectionState collection = screen.Collections["characters"];

        screen.Roles["preview"].Hide();
        Assert.False(collection.RouteInput(
            "character-one",
            UiPhysicalInput.PrimaryReleased).Activated);
        Assert.Null(collection.SelectedProductItemId);

        screen.Roles["preview"].Show();
        screen.Hide();
        Assert.False(collection.RouteInput(
            "character-one",
            UiPhysicalInput.PrimaryReleased).Activated);
        Assert.Null(collection.SelectedProductItemId);
    }

    [Fact]
    public void NonSingleCollectionWithoutAnItemButtonConstructsSafely()
    {
        const string json = """
            {
              "schema_id": "sarnaut.ui-product/v2",
              "catalogs": { "cursors": "catalogs/cursors.tres", "sounds": "catalogs/sounds.tres", "theme": "ui_theme.tres" },
              "screens": [{
                "id": "inventory",
                "scene": "screens/inventory.tscn",
                "initially_visible": true,
                "roles": [
                  { "id": "items", "node": "Items", "initially_visible": true },
                  { "id": "item", "node": "Item", "initially_visible": true }
                ],
                "actions": [],
                "values": [],
                "collections": [{
                  "id": "items", "role": "items", "item_role": "item",
                  "item_scene": "items/item.tscn", "selection": "none"
                }],
                "buttons": [],
                "selection_groups": [],
                "focus_order": []
              }]
            }
            """;
        UiScreenDefinition definition = Assert.Single(UiProductFixture.Parse(json).Screens);

        var screen = new UiScreenState(definition);

        Assert.Equal("default", screen.Collections["items"].VisualStateFor("item-one"));
        Assert.False(screen.Collections["items"].RouteInput(
            "item-one",
            UiPhysicalInput.PrimaryReleased).Activated);
    }
}
