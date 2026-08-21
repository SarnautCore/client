namespace SarnautCore.UI.Tests;

public sealed class UiWidgetStateTests
{
    [Fact]
    public void MomentaryPressUsesVariantCueAndReturnsProductAction()
    {
        UiRoleState state = VisibleScreen().Roles["enter"];

        Assert.Equal("button_yes", state.BeginPress());
        UiActionDispatch dispatch = state.EndPress(activate: true);

        Assert.True(dispatch.Activated);
        Assert.Equal("primary", dispatch.VisualState);
        Assert.Equal(["submit-login"], dispatch.ActionIds);
        Assert.Equal("standard", state.VariantId);
    }

    [Fact]
    public void TogglePressAdvancesNativeVisualStateAndReturnsToggleAction()
    {
        UiRoleState state = VisibleScreen().Roles["options"];

        Assert.Equal("options-open", state.VisualState);
        Assert.Null(state.BeginPress());
        UiActionDispatch first = state.EndPress(activate: true);

        Assert.Equal("options-closed", first.VisualState);
        Assert.Equal(["toggle-options"], first.ActionIds);
        Assert.Equal("button_no", state.BeginPress());
        Assert.Equal("options-open", state.EndPress(activate: true).VisualState);
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
        Assert.Null(state.PointerEntered());
        Assert.Null(state.BeginPress());
        Assert.False(state.EndPress(activate: true).Activated);
    }

    [Fact]
    public void NonPointerEventsResolveThroughProductActionTable()
    {
        UiRoleState account = VisibleScreen().Roles["account"];

        Assert.Equal(["submit-login"], account.Dispatch(UiActionEvent.Submitted));
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
        Assert.Null(screen.Roles["enter"].BeginPress());
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
}
