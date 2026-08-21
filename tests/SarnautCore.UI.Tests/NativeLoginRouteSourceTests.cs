namespace SarnautCore.UI.Tests;

public sealed class NativeLoginRouteSourceTests
{
    [Fact]
    public void LoginUsesTheTypedProductHost()
    {
        string login = ReadSource("LoginScreen.cs");

        Assert.Contains("NativeUiProductHost.TryMount", login, StringComparison.Ordinal);
        Assert.Contains("RegisterController(_loginScreen, HandleNativeAction)", login, StringComparison.Ordinal);
        Assert.Contains("HandleNativeAction(UiActionInvocation invocation)", login, StringComparison.Ordinal);
        Assert.DoesNotContain("LoginAccountProduct", login, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedChrome", login, StringComparison.Ordinal);
        Assert.DoesNotContain("DevelopmentFallback", login, StringComparison.Ordinal);

        string scene = ReadSource("login.tscn");
        Assert.DoesNotContain("node name=\"Content\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"SignIn\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"Register\"", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("node name=\"Back\"", scene, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductHostAcceptsOnlyCompiledNativeResources()
    {
        string host = ReadSource("NativeUiProductHost.cs");

        Assert.Contains("UiProductResourceEncoding.Compiled", host, StringComparison.Ordinal);
        Assert.Contains("LoadRequired<Theme>(Manifest.Theme", host, StringComparison.Ordinal);
        Assert.Contains("LoadRequired<PackedScene>", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedSceneLoader", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Allods.ttf", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".godot", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".import", host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductHostKeepsTypedRuntimeSemantics()
    {
        string host = ReadSource("NativeUiProductHost.cs");

        Assert.Contains("state.RouteInput(input)", host, StringComparison.Ordinal);
        Assert.Contains("dispatch.Cue", host, StringComparison.Ordinal);
        Assert.Contains("dispatch.Invocations", host, StringComparison.Ordinal);
        Assert.Contains("UiCollectionItemContext", ReadSource("UiWidgetState.cs"), StringComparison.Ordinal);
        Assert.Contains("screen.State.Collections", host, StringComparison.Ordinal);
        Assert.Contains("item.Control.SetMeta(VisualStateMetadata, dispatch.VisualState)", host, StringComparison.Ordinal);
        Assert.Contains("PlayCue(dispatch.Cue)", host, StringComparison.Ordinal);
        Assert.Contains("state.IsSelected", host, StringComparison.Ordinal);
        Assert.Contains("Func<UiActionInvocation, bool>", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionIds", host, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductHostOwnsVisibilityFocusCursorAndSound()
    {
        string host = ReadSource("NativeUiProductHost.cs");

        Assert.Contains("SetScreenVisible", host, StringComparison.Ordinal);
        Assert.Contains("SetRoleVisible", host, StringComparison.Ordinal);
        Assert.Contains("ApplyFocusOrder", host, StringComparison.Ordinal);
        Assert.Contains("Input.SetCustomMouseCursor", host, StringComparison.Ordinal);
        Assert.Contains("AudioStreamPlayer", host, StringComparison.Ordinal);
        Assert.Contains("ReplaceCollection", host, StringComparison.Ordinal);
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
