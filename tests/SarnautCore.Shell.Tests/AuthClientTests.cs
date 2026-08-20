using System.Net;
using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// The account client against a fake transport. Each refusal has to arrive as
/// its own case: a screen that cannot tell "wrong password" from "the service is
/// down" shows the wrong advice for one of them, and that is the bug report
/// nobody can answer.
/// </summary>
public sealed class AuthClientTests
{
    private const string AccountId = "0198f0cd-8f00-7000-8000-0000000000a1";
    private const string CharacterId = "0198f0cd-8f00-7000-8000-0000000000c1";
    private const string SentinelPassword = "SENTINEL-PW-2f9c41";
    private const string SentinelEmail = "sentinel-2f9c41@example.invalid";

    [Fact]
    public async Task Login_returns_the_account_session_and_sends_the_credentials()
    {
        FakeAuthTransport transport = FakeAuthTransport.Always(
            HttpStatusCode.OK,
            $$"""
            {"session_token":"sarnaut_as_abc","account_id":"{{AccountId}}","expires_at":"2026-08-21T09:00:00Z"}
            """);

        AccountSession session = await transport.Auth()
            .LoginAsync(SentinelEmail, new Secret(SentinelPassword));

        Assert.Equal(Guid.Parse(AccountId), session.AccountId);
        Assert.Equal("sarnaut_as_abc", session.Token.Reveal());
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-21T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            session.ExpiresAt);

        RecordedRequest request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/sessions", request.Path);
        // The body is the one place the password travels, and it has to.
        Assert.Contains(SentinelPassword, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_credentials_arrive_as_InvalidCredentials_with_the_server_sentence()
    {
        AuthClient auth = FakeAuthTransport.Always(
            HttpStatusCode.Unauthorized,
            """{"error":"INVALID_CREDENTIALS","message":"email or password is wrong"}""").Auth();

        AuthException failure = await Assert.ThrowsAsync<AuthException>(
            () => auth.LoginAsync(SentinelEmail, new Secret(SentinelPassword)));

        Assert.Equal(AuthFailure.InvalidCredentials, failure.Failure);
        Assert.Equal("email or password is wrong", failure.Message);
    }

    [Fact]
    public async Task An_unreachable_service_arrives_as_Unreachable_and_names_the_address()
    {
        AuthClient auth = FakeAuthTransport.Unreachable("connection refused").Auth();

        AuthException failure = await Assert.ThrowsAsync<AuthException>(
            () => auth.LoginAsync(SentinelEmail, new Secret(SentinelPassword)));

        Assert.Equal(AuthFailure.Unreachable, failure.Failure);
        Assert.Contains("127.0.0.1:8083", failure.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_name_arrives_as_NameTaken_rather_than_a_generic_conflict()
    {
        AuthClient auth = FakeAuthTransport.Always(
            HttpStatusCode.Conflict,
            """{"error":"NAME_TAKEN","message":"that name is already taken"}""").Auth();

        AuthException failure = await Assert.ThrowsAsync<AuthException>(
            () => auth.CreateCharacterAsync(
                new Secret("sarnaut_as_abc"),
                new CreateCharacterSubmission("Anne", "chargen.league.warrior")));

        Assert.Equal(AuthFailure.NameTaken, failure.Failure);
        Assert.Equal("that name is already taken", failure.Message);
    }

    [Fact]
    public async Task A_refused_shape_arrives_as_NameInvalid_and_a_disabled_option_as_its_own_case()
    {
        AuthClient invalid = FakeAuthTransport.Always(
            HttpStatusCode.BadRequest,
            """{"error":"NAME_INVALID","message":"a name is 3 to 16 characters"}""").Auth();
        AuthClient disabled = FakeAuthTransport.Always(
            HttpStatusCode.BadRequest,
            """{"error":"OPTION_DISABLED","message":"that character-creation option is not playable"}""").Auth();

        AuthException first = await Assert.ThrowsAsync<AuthException>(
            () => invalid.CreateCharacterAsync(Secret.None, new CreateCharacterSubmission("Ab", "x")));
        AuthException second = await Assert.ThrowsAsync<AuthException>(
            () => disabled.CreateCharacterAsync(Secret.None, new CreateCharacterSubmission("Anne", "x")));

        Assert.Equal(AuthFailure.NameInvalid, first.Failure);
        Assert.Equal(AuthFailure.OptionDisabled, second.Failure);
    }

    [Fact]
    public async Task An_expired_token_arrives_as_Unauthenticated_on_a_bearer_route()
    {
        AuthClient auth = FakeAuthTransport.Always(
            HttpStatusCode.Unauthorized,
            """{"error":"UNAUTHENTICATED","message":"the session token is not valid"}""").Auth();

        AuthException failure = await Assert.ThrowsAsync<AuthException>(
            () => auth.ListCharactersAsync(new Secret("sarnaut_as_stale")));

        Assert.Equal(AuthFailure.Unauthenticated, failure.Failure);
    }

    [Fact]
    public async Task An_answer_this_build_cannot_read_arrives_as_ProtocolError()
    {
        AuthClient auth = FakeAuthTransport.Always(HttpStatusCode.OK, "<html>not json</html>").Auth();

        AuthException failure = await Assert.ThrowsAsync<AuthException>(
            () => auth.LoginAsync(SentinelEmail, new Secret(SentinelPassword)));

        Assert.Equal(AuthFailure.ProtocolError, failure.Failure);
    }

    [Fact]
    public async Task No_refusal_message_ever_carries_the_password_or_the_token()
    {
        // The failure paths are enumerated deliberately: success paths rarely
        // leak, and error wrapping is where the input string gets stapled to the
        // message (ADR 0030 section 5).
        var failures = new List<AuthException>();
        AuthClient refusing = FakeAuthTransport.Always(
            HttpStatusCode.Unauthorized,
            """{"error":"INVALID_CREDENTIALS","message":"email or password is wrong"}""").Auth();
        AuthClient unreachable = FakeAuthTransport.Unreachable().Auth();
        AuthClient garbled = FakeAuthTransport.Always(HttpStatusCode.OK, "{").Auth();

        failures.Add(await Assert.ThrowsAsync<AuthException>(
            () => refusing.LoginAsync(SentinelEmail, new Secret(SentinelPassword))));
        failures.Add(await Assert.ThrowsAsync<AuthException>(
            () => unreachable.LoginAsync(SentinelEmail, new Secret(SentinelPassword))));
        failures.Add(await Assert.ThrowsAsync<AuthException>(
            () => garbled.ListCharactersAsync(new Secret("sarnaut_as_secret"))));
        failures.Add(await Assert.ThrowsAsync<AuthException>(
            () => refusing.MintTicketAsync(new Secret("sarnaut_as_secret"), Guid.Parse(CharacterId))));

        foreach (AuthException failure in failures)
        {
            Assert.DoesNotContain(SentinelPassword, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelEmail, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sarnaut_as_secret", failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Mint_ticket_sends_the_bearer_token_and_returns_the_opaque_ticket()
    {
        FakeAuthTransport transport = FakeAuthTransport.Always(
            HttpStatusCode.Created,
            $$"""
            {"ticket":"sarnaut_tk_xyz","character_id":"{{CharacterId}}","expires_at":"2026-08-20T10:01:00Z"}
            """);

        ShardTicket ticket = await transport.Auth()
            .MintTicketAsync(new Secret("sarnaut_as_abc"), Guid.Parse(CharacterId));

        Assert.Equal("sarnaut_tk_xyz", ticket.Token.Reveal());
        Assert.Equal(Guid.Parse(CharacterId), ticket.CharacterId);
        Assert.Equal("sarnaut_as_abc", Assert.Single(transport.Requests).Authorization);
    }

    [Fact]
    public async Task Chargen_options_are_read_whole_from_the_server()
    {
        AuthClient auth = FakeAuthTransport.Always(
            HttpStatusCode.OK,
            """
            {"options":[{"id":"chargen.league.warrior","race":"kanian","class":"warrior","sex":"male",
            "faction":"league","name_key":"loc.chargen.league.warrior.name",
            "description_key":"loc.chargen.league.warrior.description","visual_ref":"vis.kanian.male",
            "spawn_zone_id":"InstLeague1","starting_level":1,"spawn_x":12.5,"spawn_y":-3.25,"spawn_z":40}]}
            """).Auth();

        IReadOnlyList<ChargenOption> options = await auth.ListChargenOptionsAsync();

        ChargenOption option = Assert.Single(options);
        Assert.Equal("chargen.league.warrior", option.Id);
        Assert.Equal("InstLeague1", option.SpawnZoneId);
        Assert.Equal(1u, option.StartingLevel);
        Assert.Equal(12.5f, option.SpawnX);
        Assert.Equal(-3.25f, option.SpawnY);
        Assert.Equal(40f, option.SpawnZ);
    }
}
