namespace SarnautCore.UI.Tests;

public sealed class NativeUiProductManifestParserTests
{
    [Fact]
    public void ParsesTheOwnedProductContractWithoutBakeProvenance()
    {
        UiProductManifest manifest = UiProductFixture.Parse();

        Assert.Equal("ui/cursor_catalog.tres", manifest.CursorCatalog.Value);
        Assert.Equal("ui/sound_catalog.tres", manifest.SoundCatalog.Value);
        Assert.Equal(UiProductResourceEncoding.Plain, manifest.ResourceEncoding);
        UiScreenDefinition screen = Assert.Single(manifest.Screens);
        Assert.Equal("login", screen.Id);
        Assert.Equal("ui/LoginAccount.ui.tscn", screen.Scene.Value);
        Assert.False(screen.InitiallyVisible);
        Assert.Equal(6, screen.Roles.Count);
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

        Assert.Equal(["account", "password", "enter", "options", "local", "saved-row"],
            screen.Roles.Select(role => role.Id));
        Assert.Equal(["enter", "account", "password"],
            screen.Actions[0].Triggers.Select(trigger => trigger.Role));
        Assert.Equal(["account", "password", "enter", "options", "local"], screen.FocusOrder);
    }

    [Theory]
    [InlineData("\"schema_id\": \"sarnaut.ui-product/v2\"", "\"schema_id\": \"sarnaut.ui-product/v1\"")]
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
            "\"schema_id\": \"sarnaut.ui-product/v2\"",
            $"\"schema_id\": \"sarnaut.ui-product/v2\", \"{field}\": \"{value}\"",
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
            "\"schema_id\": \"sarnaut.ui-product/v2\"",
            "\"schema_id\": \"old/v1\", \"schema_id\": \"sarnaut.ui-product/v2\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void AcceptsAStaticScreenWithoutRolesOrBindings()
    {
        const string json = """
            {
              "schema_id": "sarnaut.ui-product/v2",
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
                  "selection_groups": [],
                  "focus_order": []
                }
              ]
            }
            """;

        UiProductManifest manifest = UiProductFixture.Parse(json);

        Assert.Empty(manifest.Screens[0].Roles);
        Assert.Empty(new UiScreenState(manifest.Screens[0]).Roles);
    }

    [Fact]
    public void PreservesTypedActionArgumentsAndAllowsParameterizedActionIdentity()
    {
        UiScreenDefinition screen = Assert.Single(
            UiProductFixture.Parse(UiProductFixture.InteractionJson).Screens);

        UiActionDefinition[] select = screen.Actions
            .Where(action => action.Id == "select"
                && action.Arguments[0].Kind == UiActionArgumentKind.ProductId)
            .ToArray();
        Assert.Equal(2, select.Length);
        Assert.Equal(["choice-a", "choice-b"],
            select.Select(action => Assert.Single(action.Arguments).Value));
        Assert.All(select, action =>
            Assert.Equal(UiActionArgumentKind.ProductId, Assert.Single(action.Arguments).Kind));
    }

    [Theory]
    [InlineData("\"arguments\": []", "\"args\": []")]
    [InlineData("\"kind\": \"product-id\"", "\"kind\": \"string\"")]
    [InlineData("\"name\": \"identity\"", "\"name\": \"source_path\"")]
    [InlineData("\"value\": \"league-warrior\"", "\"value\": \"WidgetButton\"")]
    public void RejectsMissingUntypedOrNonProductActionArguments(string oldValue, string newValue)
    {
        string json = UiProductFixture.InteractionJson.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void RejectsDuplicateArgumentNamesAndEquivalentActionSignatures()
    {
        string duplicateName = UiProductFixture.InteractionJson.Replace(
            "{ \"name\": \"identity\", \"kind\": \"product-id\", \"value\": \"league-warrior\" }",
            "{ \"name\": \"identity\", \"kind\": \"product-id\", \"value\": \"league-warrior\" }, { \"name\": \"identity\", \"kind\": \"product-id\", \"value\": \"preview\" }",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(duplicateName));

        string duplicateSignature = UiProductFixture.InteractionJson.Replace(
            "\"value\": \"choice-b\"",
            "\"value\": \"choice-a\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(duplicateSignature));
    }

    [Fact]
    public void ParsesStrictSelectionGroupContract()
    {
        UiSelectionGroupDefinition group = Assert.Single(
            Assert.Single(UiProductFixture.Parse(UiProductFixture.InteractionJson).Screens)
                .SelectionGroups);

        Assert.Equal("choice", group.Id);
        Assert.Equal(["choice-a", "choice-b"], group.Roles);
        Assert.True(group.AllowEmpty);
        Assert.Equal("choice-a", group.InitialRole);
    }

    [Theory]
    [InlineData("\"roles\": [\"choice-a\", \"choice-b\"]", "\"roles\": [\"choice-a\"]")]
    [InlineData("\"roles\": [\"choice-a\", \"choice-b\"]", "\"roles\": [\"choice-a\", \"missing\"]")]
    [InlineData("\"initial_role\": \"choice-a\"", "\"initial_role\": \"open\"")]
    [InlineData("\"initial_role\": \"choice-a\"", "\"initial\": \"choice-a\"")]
    public void RejectsUnusableSelectionGroups(string oldValue, string newValue)
    {
        string json = UiProductFixture.InteractionJson.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void NonEmptySelectionGroupMayStartWithoutASelection()
    {
        string json = UiProductFixture.InteractionJson.Replace(
            "\"allow_empty\": true, \"initial_role\": \"choice-a\"",
            "\"allow_empty\": false, \"initial_role\": null",
            StringComparison.Ordinal);

        UiSelectionGroupDefinition group = Assert.Single(
            Assert.Single(UiProductFixture.Parse(json).Screens).SelectionGroups);

        Assert.False(group.AllowEmpty);
        Assert.Null(group.InitialRole);
    }

    [Theory]
    [InlineData("\"role\": \"open\", \"event\": \"double-pressed\"", "\"role\": \"preview\", \"event\": \"double-pressed\"")]
    [InlineData("\"role\": \"screen-input\", \"event\": \"navigate-previous\"", "\"role\": \"preview\", \"event\": \"navigate-previous\"")]
    [InlineData("\"role\": \"open\", \"event\": \"double-pressed\"", "\"role\": \"open\", \"event\": \"toggled\"")]
    public void RejectsStructurallyUnreachableEvents(string oldValue, string newValue)
    {
        string json = UiProductFixture.InteractionJson.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Theory]
    [InlineData("\"id\": \"screen-input\", \"node\": \".\"", "\"id\": \"screen-input\", \"node\": \"Panel\"")]
    [InlineData("\"id\": \"preview\", \"node\": \"Preview\"", "\"id\": \"preview\", \"node\": \".\"")]
    public void ReservesRootNodeAddressForScreenInput(string oldValue, string newValue)
    {
        string json = UiProductFixture.InteractionJson.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void AcceptsAClosedCompiledProductUsingOnlyBinaryResourcePaths()
    {
        string json = UiProductFixture.Json
            .Replace(".tres", ".res", StringComparison.Ordinal)
            .Replace(".tscn", ".scn", StringComparison.Ordinal);

        UiProductManifest manifest = UiProductFixture.Parse(json);

        Assert.Equal(UiProductResourceEncoding.Compiled, manifest.ResourceEncoding);
        Assert.EndsWith(".res", manifest.CursorCatalog.Value, StringComparison.Ordinal);
        Assert.EndsWith(".res", manifest.SoundCatalog.Value, StringComparison.Ordinal);
        Assert.All(manifest.Screens, screen =>
            Assert.EndsWith(".scn", screen.Scene.Value, StringComparison.Ordinal));
        Assert.All(manifest.Screens.SelectMany(screen => screen.Collections), collection =>
            Assert.EndsWith(".scn", collection.ItemScene.Value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ui/sound_catalog.tres", "ui/sound_catalog.res")]
    [InlineData("ui/LoginAccount.ui.tscn", "ui/LoginAccount.ui.scn")]
    [InlineData("ui/SavedAccountRow.tscn", "ui/SavedAccountRow.scn")]
    [InlineData("ui/cursor_catalog.tres", "ui/cursor.png")]
    [InlineData("ui/sound_catalog.tres", "ui/click.wav")]
    public void RejectsMixedOrImporterBackedProductPaths(string oldValue, string newValue)
    {
        string json = UiProductFixture.Json.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void ParsesCollectionItemIdentityBindingWithoutALiteralItemId()
    {
        UiScreenDefinition screen = Assert.Single(
            UiProductFixture.Parse(UiProductFixture.InteractionJson).Screens);
        UiCollectionBinding collection = Assert.Single(screen.Collections);
        UiActionArgument argument = Assert.Single(
            screen.Actions.Single(action => action.Id == "open").Arguments);

        Assert.Equal("open", collection.ItemRole);
        Assert.Equal(UiActionArgumentKind.CollectionItemId, argument.Kind);
        Assert.Equal("characters", argument.Collection);
        Assert.Null(argument.Value);
    }

    [Theory]
    [InlineData("\"collection\": \"characters\"", "\"collection\": \"missing\"")]
    [InlineData("\"kind\": \"collection-item-id\", \"collection\": \"characters\"", "\"kind\": \"collection-item-id\", \"value\": \"character-one\"")]
    [InlineData("\"item_role\": \"open\"", "\"item_role\": \"choice-a\"")]
    [InlineData("\"selection\": \"single\"", "\"selection\": \"multiple\"")]
    public void RejectsUnresolvableCollectionItemIdentity(string oldValue, string newValue)
    {
        string json = UiProductFixture.InteractionJson.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Theory]
    [InlineData("\"inputs\": [{ \"input\": \"primary-released\", \"event\": \"pressed\" }]", "\"routes\": []")]
    [InlineData("\"input\": \"primary-released\", \"event\": \"pressed\"", "\"input\": \"mouse-up\", \"event\": \"pressed\"")]
    [InlineData("\"input\": \"primary-released\", \"event\": \"pressed\"", "\"input\": \"primary-released\", \"event\": \"changed\"")]
    [InlineData("\"inputs\": [{ \"input\": \"primary-released\", \"event\": \"pressed\" }]", "\"inputs\": [{ \"input\": \"primary-released\", \"event\": \"pressed\" }, { \"input\": \"primary-released\", \"event\": \"pressed\" }]")]
    public void RejectsMissingDuplicateOrUnreachableVariantInputRoutes(
        string oldValue,
        string newValue)
    {
        string json = UiProductFixture.Json.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void RejectsAButtonActionUnreachableFromEveryVariant()
    {
        string json = UiProductFixture.Json
            .Replace(
                "\"inputs\": [{ \"input\": \"primary-released\", \"event\": \"toggled\" }]",
                "\"inputs\": []",
                StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => UiProductFixture.Parse(json));
    }

    [Fact]
    public void AcceptsConverterValidDynamicArgumentsFromMultipleCollections()
    {
        string json = UiProductFixture.InteractionJson
            .Replace(
                "{ \"id\": \"open\", \"node\": \"Open\", \"initially_visible\": true }",
                "{ \"id\": \"open\", \"node\": \"Open\", \"initially_visible\": true },\n                { \"id\": \"portraits\", \"node\": \"Portraits\", \"initially_visible\": true },\n                { \"id\": \"inspect\", \"node\": \"Inspect\", \"initially_visible\": true }",
                StringComparison.Ordinal)
            .Replace(
                "{ \"id\": \"open\", \"arguments\": [{ \"name\": \"character\", \"kind\": \"collection-item-id\", \"collection\": \"characters\" }], \"triggers\": [{ \"role\": \"open\", \"event\": \"double-pressed\" }] }",
                "{ \"id\": \"open\", \"arguments\": [{ \"name\": \"character\", \"kind\": \"collection-item-id\", \"collection\": \"characters\" }, { \"name\": \"portrait\", \"kind\": \"collection-item-id\", \"collection\": \"portraits\" }], \"triggers\": [{ \"role\": \"open\", \"event\": \"double-pressed\" }, { \"role\": \"inspect\", \"event\": \"double-pressed\" }] }",
                StringComparison.Ordinal)
            .Replace(
                "{ \"id\": \"characters\", \"role\": \"preview\", \"item_role\": \"open\", \"item_scene\": \"items/character.tscn\", \"selection\": \"single\" }",
                "{ \"id\": \"characters\", \"role\": \"preview\", \"item_role\": \"open\", \"item_scene\": \"items/character.tscn\", \"selection\": \"single\" },\n                { \"id\": \"portraits\", \"role\": \"portraits\", \"item_role\": \"inspect\", \"item_scene\": \"items/portrait.tscn\", \"selection\": \"single\" }",
                StringComparison.Ordinal)
            .Replace(
                "{ \"role\": \"open\", \"toggle\": true, \"initial_variant\": \"clear\", \"variants\": [{ \"id\": \"clear\", \"visual_state\": \"clear\", \"inputs\": [{ \"input\": \"primary-released\", \"event\": \"toggled\" }, { \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }, { \"id\": \"selected\", \"visual_state\": \"selected\", \"inputs\": [{ \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }] }",
                "{ \"role\": \"open\", \"toggle\": true, \"initial_variant\": \"clear\", \"variants\": [{ \"id\": \"clear\", \"visual_state\": \"clear\", \"inputs\": [{ \"input\": \"primary-released\", \"event\": \"toggled\" }, { \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }, { \"id\": \"selected\", \"visual_state\": \"selected\", \"inputs\": [{ \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }] },\n                { \"role\": \"inspect\", \"toggle\": true, \"initial_variant\": \"clear\", \"variants\": [{ \"id\": \"clear\", \"visual_state\": \"clear\", \"inputs\": [{ \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }, { \"id\": \"selected\", \"visual_state\": \"selected\", \"inputs\": [{ \"input\": \"double-pressed\", \"event\": \"double-pressed\" }] }] }",
                StringComparison.Ordinal);

        UiProductManifest manifest = UiProductFixture.Parse(json);

        UiActionDefinition action = Assert.Single(
            manifest.Screens[0].Actions,
            candidate => candidate.Id == "open");
        Assert.Equal(2, action.Arguments.Count);
    }
}
