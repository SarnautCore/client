using System.Net;
using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// The roster screen. Everything it does happens before a game connection
/// exists (session spec rule 5.3), so all of it is testable without a shard.
/// </summary>
public sealed class CharacterSelectViewModelTests
{
    private const string AccountId = "0198f0cd-8f00-7000-8000-0000000000a1";
    private const string FirstCharacter = "0198f0cd-8f00-7000-8000-0000000000c1";
    private const string SecondCharacter = "0198f0cd-8f00-7000-8000-0000000000c2";

    private const string RosterJson = $$"""
        {"characters":[
          {"character_id":"{{FirstCharacter}}","name":"Anne","chargen_option_id":"chargen.league.warrior",
           "created_at":"2026-08-20T09:00:00Z"},
          {"character_id":"{{SecondCharacter}}","name":"O'brien","chargen_option_id":"chargen.league.warrior",
           "created_at":"2026-08-20T09:05:00Z"}
        ]}
        """;

    private static PlayerSession SignedIn()
    {
        var session = new PlayerSession();
        session.SignIn(new AccountSession(
            Guid.Parse(AccountId),
            new Secret("sarnaut_as_abc"),
            DateTimeOffset.UnixEpoch));
        return session;
    }

    [Fact]
    public async Task Lists_the_existing_characters_after_a_relogin()
    {
        var transport = FakeAuthTransport.Always(HttpStatusCode.OK, RosterJson);
        var model = new CharacterSelectViewModel(transport.Auth(), SignedIn());

        Assert.True(await model.RefreshAsync());

        Assert.Equal(["Anne", "O'brien"], model.Characters.Select(character => character.Name));
        Assert.Equal(0, model.SelectedIndex);
        Assert.False(model.IsEmpty);
        Assert.True(model.CanEnterWorld);
    }

    [Fact]
    public async Task Keeps_the_chosen_character_across_a_refresh()
    {
        var transport = FakeAuthTransport.Always(HttpStatusCode.OK, RosterJson);
        var model = new CharacterSelectViewModel(transport.Auth(), SignedIn());
        await model.RefreshAsync();
        model.SelectedIndex = 1;

        await model.RefreshAsync();

        Assert.Equal(Guid.Parse(SecondCharacter), model.Selected!.CharacterId);
    }

    [Fact]
    public async Task An_empty_roster_says_so_rather_than_looking_broken()
    {
        var transport = FakeAuthTransport.Always(HttpStatusCode.OK, """{"characters":[]}""");
        var model = new CharacterSelectViewModel(transport.Auth(), SignedIn());

        await model.RefreshAsync();

        Assert.True(model.IsEmpty);
        Assert.False(model.CanEnterWorld);
        Assert.False(model.MessageIsError);
        Assert.NotEmpty(model.Message);
    }

    [Fact]
    public async Task Entering_the_world_mints_a_ticket_and_records_the_choice_on_the_session()
    {
        var transport = FakeAuthTransport.Scripted(request => request.Path switch
        {
            "/v1/characters" => FakeAuthTransport.Json(HttpStatusCode.OK, RosterJson),
            "/v1/tickets" => FakeAuthTransport.Json(
                HttpStatusCode.Created,
                $$"""
                {"ticket":"sarnaut_tk_xyz","character_id":"{{FirstCharacter}}",
                 "expires_at":"2026-08-20T09:01:00Z"}
                """),
            _ => FakeAuthTransport.Json(HttpStatusCode.NotFound, "{}"),
        });
        PlayerSession session = SignedIn();
        var model = new CharacterSelectViewModel(transport.Auth(), session);
        await model.RefreshAsync();

        ShardTicket? ticket = await model.EnterWorldAsync();

        Assert.NotNull(ticket);
        Assert.Equal("sarnaut_tk_xyz", ticket.Token.Reveal());
        Assert.Equal("Anne", session.Character!.Name);
        Assert.Equal("sarnaut_tk_xyz", session.Ticket!.Token.Reveal());
    }

    [Fact]
    public async Task A_refused_ticket_leaves_the_session_without_one()
    {
        var transport = FakeAuthTransport.Scripted(request => request.Path == "/v1/characters"
            ? FakeAuthTransport.Json(HttpStatusCode.OK, RosterJson)
            : FakeAuthTransport.Json(
                HttpStatusCode.NotFound,
                """{"error":"CHARACTER_NOT_FOUND","message":"no such character"}"""));
        PlayerSession session = SignedIn();
        var model = new CharacterSelectViewModel(transport.Auth(), session);
        await model.RefreshAsync();

        ShardTicket? ticket = await model.EnterWorldAsync();

        Assert.Null(ticket);
        Assert.Null(session.Ticket);
        Assert.Equal(AuthFailure.CharacterNotFound, model.LastFailure);
    }

    [Fact]
    public async Task An_expired_token_empties_the_roster_and_reports_its_own_case()
    {
        var transport = FakeAuthTransport.Always(
            HttpStatusCode.Unauthorized,
            """{"error":"UNAUTHENTICATED","message":"the session token is not valid"}""");
        var model = new CharacterSelectViewModel(transport.Auth(), SignedIn());

        Assert.False(await model.RefreshAsync());

        Assert.Equal(AuthFailure.Unauthenticated, model.LastFailure);
        Assert.Empty(model.Characters);
    }
}
