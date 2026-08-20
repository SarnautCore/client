using System.Net;
using System.Text.Json;
using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// ADR 0030 section 5, client side: no rendered string ever carries a password or
/// a token. The mechanism is the type, not a habit — every conversion
/// <see cref="Secret"/> offers is already redacted, so an accidental
/// interpolation of a session is safe by construction.
/// </summary>
public sealed class SessionSecrecyTests
{
    private const string SentinelPassword = "SENTINEL-PW-2f9c41";
    private const string SentinelToken = "sarnaut_as_SENTINEL-TK-2f9c41";

    [Fact]
    public void Every_conversion_a_secret_offers_is_redacted()
    {
        var secret = new Secret(SentinelPassword);

        Assert.Equal(Secret.Redacted, secret.ToString());
        Assert.Equal(Secret.Redacted, $"{secret}");
        Assert.Equal(Secret.Redacted, string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}",
            secret));
        Assert.Equal(SentinelPassword, secret.Reveal());
    }

    [Fact]
    public void Printing_a_session_names_identifiers_and_nothing_else()
    {
        var session = new PlayerSession();
        session.SignIn(new AccountSession(
            Guid.Parse("0198f0cd-8f00-7000-8000-0000000000a1"),
            new Secret(SentinelToken),
            DateTimeOffset.UnixEpoch));
        session.SelectCharacter(new CharacterSummary(
            Guid.Parse("0198f0cd-8f00-7000-8000-0000000000c1"),
            "Anne",
            "chargen.league.warrior",
            DateTimeOffset.UnixEpoch));
        session.HoldTicket(new ShardTicket(
            new Secret("sarnaut_tk_SENTINEL"),
            Guid.Parse("0198f0cd-8f00-7000-8000-0000000000c1"),
            DateTimeOffset.UnixEpoch));

        string printed = $"{session} {session.Account} {session.Ticket} {session.Token}";

        Assert.DoesNotContain(SentinelToken, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("sarnaut_tk_SENTINEL", printed, StringComparison.Ordinal);
        Assert.Contains("0198f0cd-8f00-7000-8000-0000000000a1", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void Printing_a_zone_request_never_prints_the_ticket_it_carries()
    {
        var request = new ZoneRequest(
            "Inst_LeagueStart",
            "InstLeague1",
            "127.0.0.1:4242",
            Online: true,
            new Secret("sarnaut_tk_SENTINEL"),
            new ZoneSpawn(1, 2, 3));

        string printed = $"{request}";

        Assert.DoesNotContain("sarnaut_tk_SENTINEL", printed, StringComparison.Ordinal);
        Assert.Contains("InstLeague1", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_view_model_drops_the_password_it_was_given()
    {
        var transport = FakeAuthTransport.Always(
            HttpStatusCode.OK,
            """
            {"session_token":"sarnaut_as_abc","account_id":"0198f0cd-8f00-7000-8000-0000000000a1",
             "expires_at":"2026-08-21T09:00:00Z"}
            """);
        var session = new PlayerSession();
        var model = new LoginViewModel(transport.Auth(), session)
        {
            Email = "player@example.invalid",
            Password = new Secret(SentinelPassword),
        };

        Assert.True(await model.SignInAsync());

        Assert.True(model.Password.IsEmpty);
        Assert.True(session.IsAuthenticated);
        Assert.Equal("sarnaut_as_abc", session.Token.Reveal());
    }

    [Fact]
    public void A_secret_serializes_to_its_real_value_only_where_a_request_body_needs_it()
    {
        string json = JsonSerializer.Serialize(new Secret(SentinelPassword));

        Assert.Equal($"\"{SentinelPassword}\"", json);
    }
}
