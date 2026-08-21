namespace SarnautCore.UI.Tests;

public sealed class NativeLoginRouteSourceTests
{
    [Fact]
    public void LoginUsesTheTypedProductHost()
    {
        string login = ReadSource("LoginScreen.cs");
        string binding = ReadSource("NativeOutOfGameBinding.cs");

        Assert.Contains("NativeUiProductHost.TryMount", login, StringComparison.Ordinal);
        Assert.Contains("NativeOutOfGameBinding.Open", login, StringComparison.Ordinal);
        Assert.Contains("host.RegisterController(screen", binding, StringComparison.Ordinal);
        Assert.Contains("HandleAction(UiScreenDefinition screen, UiActionInvocation invocation)", binding, StringComparison.Ordinal);
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
        Assert.Contains("ReconcileAvailableItems", host, StringComparison.Ordinal);
        Assert.Contains("items.Where(item => item.Enabled)", host, StringComparison.Ordinal);
        Assert.Contains("IsAuthoredProgressControl", host, StringComparison.Ordinal);
        Assert.Contains("Manifest.ScreensInAuthoredOrder", host, StringComparison.Ordinal);
        Assert.Contains("SetScreenSiblingOrder", host, StringComparison.Ordinal);
        Assert.Contains("BindEulaPresentation", host, StringComparison.Ordinal);
        Assert.Contains("PresentEula", host, StringComparison.Ordinal);
        Assert.Contains("BindCreditsPresentation", host, StringComparison.Ordinal);
        Assert.Contains("PresentCredits", host, StringComparison.Ordinal);
        Assert.Contains("ResourcePreloader", host, StringComparison.Ordinal);
        Assert.Contains("media.GetResource(presentation.TextureId)", host, StringComparison.Ordinal);
        Assert.Contains("CanvasItemMaterial.BlendModeEnum.Mul", host, StringComparison.Ordinal);
        Assert.DoesNotContain("MainTitle", host, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginSceneOwnsTheOutOfGameProductLifetime()
    {
        string login = ReadSource("LoginScreen.cs");
        string session = ReadSource("SessionHost.cs");
        string scene = ReadSource("login.tscn");

        Assert.Contains("NativeUiProductHost.TryMount(this", login, StringComparison.Ordinal);
        Assert.Contains("script = ExtResource(\"1_login\")", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeUiProductHost", session, StringComparison.Ordinal);
        Assert.DoesNotContain("ui-product", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Screen.Login or Screen.CharacterSelect or Screen.CharacterCreate",
            session,
            StringComparison.Ordinal);
        Assert.DoesNotContain("character_select.tscn", session, StringComparison.Ordinal);
        Assert.DoesNotContain("character_create.tscn", session, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginTeardownCancelsNavigationAndForgetsThePassword()
    {
        string login = ReadSource("LoginScreen.cs");
        string binding = ReadSource("NativeOutOfGameBinding.cs");

        Assert.Contains(
            "if (token.IsCancellationRequested || _disposed)",
            binding,
            StringComparison.Ordinal);
        Assert.Contains("_login.Password = Secret.None", binding, StringComparison.Ordinal);
        Assert.Contains("_binding?.Dispose()", login, StringComparison.Ordinal);
        Assert.Contains("CanPresent(token)", binding, StringComparison.Ordinal);
        Assert.Contains("_native?.QueueFree()", login, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginOwnsTheCompleteNativeOutOfGameLifecycle()
    {
        string login = ReadSource("LoginScreen.cs");
        string binding = ReadSource("NativeOutOfGameBinding.cs");

        Assert.Contains("OutOfGameFlowController.Open", binding, StringComparison.Ordinal);
        Assert.Contains("EulaClientBinding", binding, StringComparison.Ordinal);
        Assert.Contains("CreditsController", binding, StringComparison.Ordinal);
        Assert.Contains("SetScreenSiblingOrder", binding, StringComparison.Ordinal);
        Assert.Contains("_creditsTimeline = ReadCreditsTimeline()", binding, StringComparison.Ordinal);
        Assert.Contains("presentation.TextureId", binding, StringComparison.Ordinal);
        Assert.Contains("main_menu_music", binding, StringComparison.Ordinal);
        Assert.Contains("credits_music", binding, StringComparison.Ordinal);
        Assert.Contains("ConfigFile", binding, StringComparison.Ordinal);
        Assert.Contains("GetTree().Quit()", binding, StringComparison.Ordinal);
        Assert.Contains("delete-character-panel", binding, StringComparison.Ordinal);
        Assert.Contains("delete-character-status", binding, StringComparison.Ordinal);
        Assert.Contains("Could not clear EULA acceptance", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedChrome", login, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertedChrome", binding, StringComparison.Ordinal);
    }

    private static string ReadSource(string name)
    {
        string directory = name.EndsWith(".tscn", StringComparison.Ordinal)
            ? "contract-scenes"
            : "contract-source";
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, directory, name));
    }
}
