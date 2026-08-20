using System.Net;
using System.Text.Json;
using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// The creation screen's behaviour: the option list is whatever the server sent,
/// the name rule is the server's, and the submit payload has one construction
/// site.
/// </summary>
public sealed class CharacterCreateViewModelTests
{
    private static ChargenOption Option(string id, string race, string @class, string faction = "league") => new(
        id,
        race,
        @class,
        Sex: "male",
        faction,
        NameKey: $"loc.{id}.name",
        DescriptionKey: $"loc.{id}.description",
        VisualRef: $"vis.{race}.male",
        SpawnZoneId: "InstLeague1",
        StartingLevel: 1,
        SpawnX: 1,
        SpawnY: 2,
        SpawnZ: 3);

    private static CharacterCreateViewModel ViewModel(
        out FakeAuthTransport transport,
        Func<string, string?>? localize = null)
    {
        transport = FakeAuthTransport.Always(HttpStatusCode.Created, "{}");
        return new CharacterCreateViewModel(transport.Auth(), new PlayerSession(), localize);
    }

    [Fact]
    public void Renders_whatever_option_list_the_server_supplies()
    {
        CharacterCreateViewModel model = ViewModel(out _);

        model.SetOptions([Option("chargen.league.warrior", "kanian", "warrior")]);
        Assert.Equal(["chargen.league.warrior"], model.Options.Select(view => view.Id));
        Assert.Equal("Kanian Warrior", model.Options[0].Title);

        // A different list, of a race and class no client constant knows, has to
        // render just as well: that is what "the client asks the server for the
        // option list and never derives it" means (ADR 0032 section 2).
        model.SetOptions(
        [
            Option("chargen.empire.mage", "xadaganian", "mage", "empire"),
            Option("chargen.empire.scout", "orc", "scout", "empire"),
        ]);

        Assert.Equal(
            ["chargen.empire.mage", "chargen.empire.scout"],
            model.Options.Select(view => view.Id));
        Assert.Equal(["Xadaganian Mage", "Orc Scout"], model.Options.Select(view => view.Title));
        Assert.All(model.Options, view => Assert.Contains("Empire", view.Subtitle, StringComparison.Ordinal));
        Assert.Equal(0, model.SelectedIndex);
        Assert.Equal("chargen.empire.mage", model.Selected!.Id);
    }

    [Fact]
    public void Renders_the_localized_name_when_a_locale_lookup_answers()
    {
        CharacterCreateViewModel model = ViewModel(
            out _,
            key => key.EndsWith(".name", StringComparison.Ordinal) ? "Vanguard of the League" : null);

        model.SetOptions([Option("chargen.league.warrior", "kanian", "warrior")]);

        Assert.Equal("Vanguard of the League", model.Options[0].Title);
    }

    [Theory]
    [InlineData("Ab")]
    [InlineData("Ann--e")]
    [InlineData("ANNE")]
    [InlineData("Ann3")]
    [InlineData("Аnne")]
    [InlineData("")]
    public void Refuses_a_name_the_server_would_refuse_before_a_request_is_made(string name)
    {
        CharacterCreateViewModel model = ViewModel(out FakeAuthTransport transport);
        model.SetOptions([Option("chargen.league.warrior", "kanian", "warrior")]);

        model.Name = name;

        Assert.NotNull(model.NameMessage);
        Assert.False(model.CanSubmit);
        Assert.Throws<InvalidOperationException>(() => model.BuildSubmission());
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void Builds_the_submit_payload_from_the_typed_name_and_the_selected_option()
    {
        CharacterCreateViewModel model = ViewModel(out _);
        model.SetOptions(
        [
            Option("chargen.league.warrior", "kanian", "warrior"),
            Option("chargen.empire.mage", "xadaganian", "mage", "empire"),
        ]);
        model.SelectedIndex = 1;
        model.Name = "  O'brien  ";

        CreateCharacterSubmission submission = model.BuildSubmission();

        Assert.Equal("O'brien", submission.Name);
        Assert.Equal("chargen.empire.mage", submission.ChargenOptionId);
        Assert.True(model.CanSubmit);
    }

    [Fact]
    public async Task Submits_the_payload_as_the_document_the_server_reads()
    {
        var transport = FakeAuthTransport.Scripted(_ => FakeAuthTransport.Json(
            HttpStatusCode.Created,
            """
            {"character_id":"0198f0cd-8f00-7000-8000-0000000000c1","name":"Anne",
            "chargen_option_id":"chargen.league.warrior","created_at":"2026-08-20T09:00:00Z"}
            """));
        var session = new PlayerSession();
        session.SignIn(new AccountSession(
            Guid.Parse("0198f0cd-8f00-7000-8000-0000000000a1"),
            new Secret("sarnaut_as_abc"),
            DateTimeOffset.UnixEpoch));
        var model = new CharacterCreateViewModel(transport.Auth(), session);
        model.SetOptions([Option("chargen.league.warrior", "kanian", "warrior")]);
        model.Name = "Anne";

        CharacterSummary? created = await model.SubmitAsync();

        Assert.NotNull(created);
        Assert.Equal("Anne", created.Name);
        RecordedRequest request = Assert.Single(transport.Requests);
        Assert.Equal("/v1/characters", request.Path);
        Assert.Equal("sarnaut_as_abc", request.Authorization);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        // The field names are the server's, not the CLR property names: the
        // service decodes with DisallowUnknownFields, so a camelCased body is a
        // 400 rather than a create.
        Assert.Equal("Anne", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            "chargen.league.warrior",
            body.RootElement.GetProperty("chargen_option_id").GetString());
    }

    [Fact]
    public async Task Renders_NAME_TAKEN_from_the_server_rather_than_assuming_its_own_check_was_enough()
    {
        var transport = FakeAuthTransport.Always(
            HttpStatusCode.Conflict,
            """{"error":"NAME_TAKEN","message":"that name is already taken"}""");
        var model = new CharacterCreateViewModel(transport.Auth(), new PlayerSession());
        model.SetOptions([Option("chargen.league.warrior", "kanian", "warrior")]);
        model.Name = "Anne";

        CharacterSummary? created = await model.SubmitAsync();

        Assert.Null(created);
        Assert.Equal(AuthFailure.NameTaken, model.LastFailure);
        Assert.Equal("that name is already taken", model.Message);
        Assert.True(model.MessageIsError);
    }

    [Fact]
    public async Task Loads_the_option_list_and_keeps_nothing_when_the_server_offers_nothing()
    {
        var transport = FakeAuthTransport.Always(HttpStatusCode.OK, """{"options":[]}""");
        var model = new CharacterCreateViewModel(transport.Auth(), new PlayerSession());

        bool loaded = await model.LoadOptionsAsync();

        Assert.False(loaded);
        Assert.Empty(model.Options);
        Assert.Null(model.Selected);
        Assert.True(model.MessageIsError);
    }
}
