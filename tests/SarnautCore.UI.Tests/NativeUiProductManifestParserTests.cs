namespace SarnautCore.UI.Tests;

public sealed class NativeUiProductManifestParserTests
{
    [Fact]
    public void ParsesTheOwnedProductContractWithoutBakeProvenance()
    {
        UiProductManifest manifest = UiProductFixture.Parse();

        Assert.Equal("ui/cursor_catalog.tres", manifest.CursorCatalog.Value);
        Assert.Equal("ui/sound_catalog.tres", manifest.SoundCatalog.Value);
        UiScreenDefinition screen = Assert.Single(manifest.Screens);
        Assert.Equal("login", screen.Id);
        Assert.Equal("ui/LoginAccount.ui.tscn", screen.Scene.Value);
        Assert.False(screen.InitiallyVisible);
        Assert.Equal(5, screen.Roles.Count);
        Assert.Equal(["account", "password", "enter", "options", "local"], screen.FocusOrder);
        Assert.Equal(UiValueAccess.ReadWrite, screen.Values[0].Access);
        Assert.True(screen.Values[1].Secret);
        Assert.Equal(UiCollectionSelection.Single, Assert.Single(screen.Collections).Selection);
        Assert.Equal("options-open", screen.Buttons[1].Variants[0].VisualState);

        string[] runtimeProperties = typeof(UiProductManifest)
            .Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("Source", runtimeProperties);
        Assert.DoesNotContain("Class", runtimeProperties);
        Assert.DoesNotContain("Reactions", runtimeProperties);
    }

    [Fact]
    public void PreservesDeclaredRoleTriggerAndFocusOrder()
    {
        UiScreenDefinition screen = Assert.Single(UiProductFixture.Parse().Screens);

        Assert.Equal(["account", "password", "enter", "options", "local"],
            screen.Roles.Select(role => role.Id));
        Assert.Equal(["enter", "account", "password"],
            screen.Actions[0].Triggers.Select(trigger => trigger.Role));
        Assert.Equal(["account", "password", "enter", "options", "local"], screen.FocusOrder);
    }

    [Theory]
    [InlineData("\"schema_id\": \"sarnaut.ui-product/v1\"", "\"schema_id\": \"old/v1\"")]
    [InlineData("\"cursors\": \"ui/cursor_catalog.tres\"", "\"cursors\": \"../cursor_catalog.tres\"")]
    [InlineData("\"role\": \"enter\", \"event\": \"pressed\"", "\"role\": \"missing\", \"event\": \"pressed\"")]
    [InlineData("\"initial_variant\": \"standard\"", "\"initial_variant\": \"missing\"")]
    [InlineData("\"focus_order\": [\"account\", \"password\", \"enter\", \"options\", \"local\"]", "\"focus_order\": [\"account\", \"account\"]")]
    [InlineData("\"event\": \"pressed\"", "\"event\": \"clicked\"")]
    [InlineData("\"access\": \"read-write\"", "\"access\": \"read--write\"")]
    public void RejectsBrokenProductReferencesAndEnums(string oldValue, string newValue)
    {
        string json = UiProductFixture.Json.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Theory]
    [InlineData("source", "private/input/file")]
    [InlineData("class", "OldButtonType")]
    [InlineData("reactions", "pressed")]
    [InlineData("lua", "handler")]
    public void RejectsForbiddenInputFieldsAnywhere(string field, string value)
    {
        string json = UiProductFixture.Json.Replace(
            "\"schema_id\": \"sarnaut.ui-product/v1\"",
            $"\"schema_id\": \"sarnaut.ui-product/v1\", \"{field}\": \"{value}\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void RejectsAnInputFormatPathWhereANativeSceneIsRequired()
    {
        string json = UiProductFixture.Json.Replace(
            "\"scene\": \"ui/LoginAccount.ui.tscn\"",
            "\"scene\": \"private/input/LoginAccount.xdb\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void RejectsCurrentBakeSidecarsInsteadOfLearningTheirFormat()
    {
        const string sidecar = """
            {
              "source": "private/input/widget.xdb",
              "class": "WidgetButton",
              "reactions": ["login_pressed"]
            }
            """;

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(sidecar));
    }

    [Theory]
    [InlineData("\"id\": \"login\"", "\"id\": \"Login\"")]
    [InlineData("\"id\": \"login\"", "\"id\": \"widget-login\"")]
    [InlineData("\"id\": \"login\"", "\"id\": \"login_name\"")]
    public void EnforcesProductIdentifierGrammar(string oldValue, string newValue)
    {
        string json = UiProductFixture.Json.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void UsesSnakeCaseOnlyForCatalogReferences()
    {
        string json = UiProductFixture.Json.Replace(
            "\"press\": \"button_press\"",
            "\"press\": \"button-press\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void AcceptsConfinedGodotNodeSegmentsThatAreNotIdentifiers()
    {
        string json = UiProductFixture.Json.Replace(
            "LoginPanel/Account",
            "Login Panel/Account Input",
            StringComparison.Ordinal);

        UiProductManifest manifest = UiProductFixture.Parse(json);

        Assert.Equal("Login Panel/Account Input", manifest.Screens[0].Roles[0].Node);
    }

    [Theory]
    [InlineData("\"node\": \"LoginPanel/Password\"", "\"node\": \"LoginPanel/Account\"")]
    [InlineData("\"role\": \"password\", \"kind\": \"text\"", "\"role\": \"account\", \"kind\": \"text\"")]
    [InlineData("\"kind\": \"text\", \"access\": \"write\", \"secret\": true", "\"kind\": \"number\", \"access\": \"write\", \"secret\": true")]
    [InlineData("\"role\": \"options\", \"event\": \"toggled\"", "\"role\": \"enter\", \"event\": \"pressed\"")]
    [InlineData("\"toggle\": true", "\"toggle\": false")]
    public void EnforcesOwnershipTriggerAndButtonInvariants(string oldValue, string newValue)
    {
        string json = UiProductFixture.Json.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void RejectsDuplicateJsonMembers()
    {
        string json = UiProductFixture.Json.Replace(
            "\"schema_id\": \"sarnaut.ui-product/v1\"",
            "\"schema_id\": \"old/v1\", \"schema_id\": \"sarnaut.ui-product/v1\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void AcceptsAStaticScreenWithoutRolesOrBindings()
    {
        const string json = """
            {
              "schema_id": "sarnaut.ui-product/v1",
              "catalogs": {
                "cursors": "catalogs/cursors.tres",
                "sounds": "catalogs/sounds.tres"
              },
              "screens": [
                {
                  "id": "splash",
                  "scene": "screens/splash.tscn",
                  "initially_visible": true,
                  "roles": [],
                  "actions": [],
                  "values": [],
                  "collections": [],
                  "buttons": [],
                  "focus_order": []
                }
              ]
            }
            """;

        UiProductManifest manifest = UiProductFixture.Parse(json);

        Assert.Empty(manifest.Screens[0].Roles);
        Assert.Empty(new UiScreenState(manifest.Screens[0]).Roles);
    }
}
