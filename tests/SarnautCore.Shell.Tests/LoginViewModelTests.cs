using System.Net;
using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>The login screen's behaviour, with no Godot node in sight.</summary>
public sealed class LoginViewModelTests
{
    private const string LoginJson = """
        {"session_token":"sarnaut_as_abc","account_id":"0198f0cd-8f00-7000-8000-0000000000a1",
         "expires_at":"2026-08-21T09:00:00Z"}
        """;

    [Fact]
    public void Refuses_to_submit_an_empty_form()
    {
        var model = new LoginViewModel(
            FakeAuthTransport.Always(HttpStatusCode.OK, LoginJson).Auth(),
            new PlayerSession());

        Assert.False(model.CanSubmit);

        model.Email = "  ";
        model.Password = new Secret("hunter22");
        Assert.False(model.CanSubmit);

        model.Email = "player@example.invalid";
        Assert.True(model.CanSubmit);
    }

    [Fact]
    public async Task Shows_the_service_sentence_on_bad_credentials_and_signs_nobody_in()
    {
        var session = new PlayerSession();
        var model = new LoginViewModel(
            FakeAuthTransport.Always(
                HttpStatusCode.Unauthorized,
                """{"error":"INVALID_CREDENTIALS","message":"email or password is wrong"}""").Auth(),
            session)
        {
            Email = "player@example.invalid",
            Password = new Secret("wrong-password"),
        };

        Assert.False(await model.SignInAsync());

        Assert.Equal(AuthFailure.InvalidCredentials, model.LastFailure);
        Assert.Equal("email or password is wrong", model.Message);
        Assert.True(model.MessageIsError);
        Assert.False(session.IsAuthenticated);
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task Reports_an_unreachable_service_as_its_own_case()
    {
        var model = new LoginViewModel(FakeAuthTransport.Unreachable().Auth(), new PlayerSession())
        {
            Email = "player@example.invalid",
            Password = new Secret("hunter22"),
        };

        Assert.False(await model.SignInAsync());

        Assert.Equal(AuthFailure.Unreachable, model.LastFailure);
        Assert.Contains("127.0.0.1:8083", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_logs_in_with_the_same_credentials_because_registration_mints_no_token()
    {
        var transport = FakeAuthTransport.Scripted(request => request.Path switch
        {
            "/v1/accounts" => FakeAuthTransport.Json(
                HttpStatusCode.Created,
                """{"account_id":"0198f0cd-8f00-7000-8000-0000000000a1"}"""),
            _ => FakeAuthTransport.Json(HttpStatusCode.OK, LoginJson),
        });
        var session = new PlayerSession();
        var model = new LoginViewModel(transport.Auth(), session)
        {
            Email = "player@example.invalid",
            Password = new Secret("hunter22"),
        };

        Assert.True(await model.RegisterAsync());

        Assert.Equal(["/v1/accounts", "/v1/sessions"], transport.Requests.Select(request => request.Path));
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task A_taken_address_arrives_as_EmailTaken()
    {
        var model = new LoginViewModel(
            FakeAuthTransport.Always(
                HttpStatusCode.Conflict,
                """{"error":"EMAIL_TAKEN","message":"that email address is already registered"}""").Auth(),
            new PlayerSession())
        {
            Email = "player@example.invalid",
            Password = new Secret("hunter22"),
        };

        Assert.False(await model.RegisterAsync());

        Assert.Equal(AuthFailure.EmailTaken, model.LastFailure);
    }
}
