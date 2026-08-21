namespace SarnautCore.UI.Tests;

public sealed class LoginAccountProductTests
{
    [Fact]
    public void BindsTheBakedLoginAccountContract()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "native-ui-product",
            "ui-product.json");
        using FileStream stream = File.OpenRead(fixture);

        LoginAccountProduct login = LoginAccountProduct.Bind(
            NativeUiProductManifestParser.Parse(stream));

        Assert.Equal(LoginAccountProduct.ScreenId, login.Screen.Id);
        Assert.Equal(LoginAccountProduct.AccountRole, login.Account.Role);
        Assert.Equal(LoginAccountProduct.PasswordRole, login.Password.Role);
        Assert.True(login.Password.Secret);
        Assert.Equal(7, login.Screen.Roles.Count);
        Assert.Equal(6, login.Screen.Actions.Count);
        Assert.Equal(7, login.Screen.FocusOrder.Count);
    }

    [Fact]
    public void RejectsAProductThatReassignsTheSecretValue()
    {
        using FileStream stream = File.OpenRead(FixturePath());
        UiProductManifest parsed = NativeUiProductManifestParser.Parse(stream);
        UiScreenDefinition screen = Assert.Single(parsed.Screens);
        UiValueBinding password = screen.Values.Single(
            value => value.Id == LoginAccountProduct.PasswordValue);
        UiProductManifest manifest = parsed with
        {
            Screens =
            [
                screen with
                {
                    Values = screen.Values
                        .Select(value => value == password
                            ? value with { Role = LoginAccountProduct.AccountRole }
                            : value)
                        .ToArray(),
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() => LoginAccountProduct.Bind(manifest));
    }

    [Fact]
    public void RejectsAnExtraTriggerThatWouldBypassSubmitSemantics()
    {
        using FileStream stream = File.OpenRead(FixturePath());
        UiProductManifest parsed = NativeUiProductManifestParser.Parse(stream);
        UiScreenDefinition screen = Assert.Single(parsed.Screens);
        UiActionDefinition submit = screen.Actions.Single(
            action => action.Id == LoginAccountProduct.SubmitAction);
        UiProductManifest manifest = parsed with
        {
            Screens =
            [
                screen with
                {
                    Actions = screen.Actions
                        .Select(action => action == submit
                            ? action with
                            {
                                Triggers =
                                [
                                    .. action.Triggers,
                                    new UiActionTrigger(
                                        LoginAccountProduct.AccountRole,
                                        UiActionEvent.Changed),
                                ],
                            }
                            : action)
                        .ToArray(),
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() => LoginAccountProduct.Bind(manifest));
    }

    [Fact]
    public void ResolvesProductResourcesFromBothSupportedMountLayouts()
    {
        Assert.Equal(
            [
                "res://content/league-slice/ui/ui-product.json",
                "res://content/league-slice/ui-product.json",
            ],
            NativeUiProductLocation.ManifestCandidates("res://content/league-slice/"));

        UiProductManifest manifest = UiProductFixture.Parse();
        Assert.Equal(
            "res://content/league-slice/ui/ui/LoginAccount.ui.tscn",
            NativeUiProductLocation.Resolve(
                "res://content/league-slice/ui/ui-product.json",
                manifest.Screens[0].Scene));
    }

    [Theory]
    [InlineData("res://content/../private")]
    [InlineData("C:\\content")]
    [InlineData("user://content")]
    public void RejectsRootsOutsideTheMountedNativeProduct(string root)
    {
        Assert.Throws<ArgumentException>(() => NativeUiProductLocation.ManifestCandidates(root));
    }

    private static string FixturePath() => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "native-ui-product",
        "ui-product.json");
}
