using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// The shell's state machine. The moves it refuses matter more than the ones it
/// makes: every refused move is a screen the session state could not support.
/// </summary>
public sealed class ScreenFlowTests
{
    [Fact]
    public void Walks_the_whole_slice_from_the_hub_to_the_world_and_back()
    {
        var observed = new List<Screen>();
        var flow = new ScreenFlow();
        flow.Changed += observed.Add;

        flow.BeginSignIn();
        flow.SignedIn();
        flow.CreateCharacter();
        flow.LeaveCreateCharacter();
        flow.EnterWorld();
        flow.EnteredWorld();
        flow.LeftWorld();

        Assert.Equal(Screen.CharacterSelect, flow.Current);
        Assert.Equal(
            [
                Screen.Login,
                Screen.CharacterSelect,
                Screen.CharacterCreate,
                Screen.CharacterSelect,
                Screen.EnteringWorld,
                Screen.InWorld,
                Screen.CharacterSelect,
            ],
            observed);
    }

    [Fact]
    public void Killing_the_app_and_signing_in_again_lands_on_character_select()
    {
        // A fresh process starts at the hub with no token; signing in has to put
        // the player on the roster, not back in the world they left.
        var flow = new ScreenFlow();

        flow.BeginSignIn();
        flow.SignedIn();

        Assert.Equal(Screen.CharacterSelect, flow.Current);
    }

    [Fact]
    public void Refuses_to_reach_the_roster_without_going_through_the_login_form()
    {
        var flow = new ScreenFlow();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => flow.SignedIn());

        Assert.Contains("Start", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Screen.Start, flow.Current);
    }

    [Fact]
    public void Refuses_to_enter_the_world_from_the_creation_form()
    {
        var flow = new ScreenFlow();
        flow.BeginSignIn();
        flow.SignedIn();
        flow.CreateCharacter();

        Assert.Throws<InvalidOperationException>(() => flow.EnterWorld());
        Assert.Equal(Screen.CharacterCreate, flow.Current);
    }

    [Fact]
    public void A_refused_ticket_returns_to_the_roster_rather_than_stalling()
    {
        var flow = new ScreenFlow();
        flow.BeginSignIn();
        flow.SignedIn();
        flow.EnterWorld();

        flow.EnterWorldFailed();

        Assert.Equal(Screen.CharacterSelect, flow.Current);
    }

    [Fact]
    public void Signing_out_is_legal_from_every_screen_because_a_token_can_expire_on_any_of_them()
    {
        foreach (Screen start in Enum.GetValues<Screen>())
        {
            var flow = new ScreenFlow(start);

            flow.SignedOut();

            Assert.Equal(Screen.Login, flow.Current);
        }
    }

    [Fact]
    public void Moving_to_the_screen_already_shown_changes_nothing_and_raises_nothing()
    {
        int changes = 0;
        var flow = new ScreenFlow(Screen.Login);
        flow.Changed += _ => changes++;

        flow.SignedOut();

        Assert.Equal(Screen.Login, flow.Current);
        Assert.Equal(0, changes);
        Assert.Empty(flow.History);
    }
}
