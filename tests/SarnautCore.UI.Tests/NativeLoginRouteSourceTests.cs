namespace SarnautCore.UI.Tests;

public sealed class NativeLoginRouteSourceTests
{
    [Fact]
    public void LoginProductRouteCannotCallLegacyChrome()
    {
        string login = ReadSource("LoginScreen.cs");

        Assert.Contains("NativeLoginUiHost.TryMount", login, StringComparison.Ordinal);
        Assert.Contains("ShowFailure(nativeStatus)", login, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedChrome", login, StringComparison.Ordinal);
        Assert.DoesNotContain("converted", login, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevelopmentFallback", login, StringComparison.Ordinal);

        string scene = ReadSource("login.tscn");
        Assert.DoesNotContain("node name=\"Content\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"SignIn\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"Register\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"Back\"", scene, StringComparison.Ordinal);
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

    [Fact]
    public void LoginTeardownCancelsNavigationAndForgetsThePassword()
    {
        string login = ReadSource("LoginScreen.cs");

        Assert.Contains(
            "if (cancellation.IsCancellationRequested || !IsInsideTree())",
            login,
            StringComparison.Ordinal);
        Assert.Contains("_model.Password = Secret.None", login, StringComparison.Ordinal);
        Assert.Contains(
            "ForgetPassword();\n        _session.Flow.CancelSignIn()",
            login,
            StringComparison.Ordinal);
    }

    private static string ReadSource(string name)
    {
        string directory = name.EndsWith(".tscn", StringComparison.Ordinal)
            ? "contract-scenes"
            : "contract-source";
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, directory, name));
    }
}
