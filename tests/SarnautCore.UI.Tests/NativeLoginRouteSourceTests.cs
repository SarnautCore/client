namespace SarnautCore.UI.Tests;

public sealed class NativeLoginRouteSourceTests
{
    [Fact]
    public void LoginProductRouteCannotCallLegacyChrome()
    {
        string login = ReadSource("LoginScreen.cs");

        Assert.Contains("NativeLoginUiHost.TryMount", login, StringComparison.Ordinal);
        Assert.Contains(
            "GetNode<CanvasLayer>(\"Content\").Visible = false",
            login,
            StringComparison.Ordinal);
        Assert.Contains("ShowFailure(nativeStatus)", login, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedChrome", login, StringComparison.Ordinal);
        Assert.DoesNotContain("converted", login, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevelopmentFallback", login, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ConvertedChrome")]
    [InlineData("converted/")]
    [InlineData(".ui.json")]
    [InlineData(".xdb")]
    public void NativeHostHasNoInputOrConversionVocabulary(string forbidden)
    {
        string runtime = string.Join(
            '\n',
            ReadSource("NativeLoginUiHost.cs"),
            ReadSource("UiRuntimeKey.cs"),
            ReadSource("UiManifestJson.cs"));

        Assert.DoesNotContain(forbidden, runtime, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledNativeInputsCannotDispatchKeyboardActions()
    {
        string host = ReadSource("NativeLoginUiHost.cs");

        Assert.Contains("if (_interactive)", host, StringComparison.Ordinal);
        Assert.Contains("if (_interactive\n                && inputEvent", host, StringComparison.Ordinal);
    }

    private static string ReadSource(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "contract-source",
        name));
}
