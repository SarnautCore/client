namespace SarnautCore.UI.Tests;

public sealed class NativeHudZoneRouteSourceTests
{
    [Fact]
    public void ZoneAttachesSessionAndMountsNativeHudBeforeNetworkStart()
    {
        string source = ReadSource();

        int createSession = source.IndexOf("new SessionHudAdapter(sourceEpoch: 1)", StringComparison.Ordinal);
        int attachSession = source.IndexOf("_networkLoop.AttachHudSession(hudSession)", StringComparison.Ordinal);
        int mountHud = source.IndexOf("NativeGameplayHudHost.TryMount(", StringComparison.Ordinal);
        int startNetwork = source.IndexOf("_networkLoop.Start(", StringComparison.Ordinal);

        Assert.True(createSession >= 0);
        Assert.True(attachSession > createSession);
        Assert.True(mountHud > attachSession);
        Assert.True(startNetwork > mountHud);
        Assert.Contains("NativeHudContentPaths.Canonical()", source, StringComparison.Ordinal);
        Assert.Contains("GetNode<CanvasLayer>(\"Interface\")", source, StringComparison.Ordinal);
        Assert.Contains("_status.Text = hudError", source, StringComparison.Ordinal);
        Assert.Contains("return;", source[mountHud..startNetwork], StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneDoesNotMountOrDispatchThroughLegacyHud()
    {
        string source = ReadSource();

        Assert.DoesNotContain("new GameplayHudControl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_hudControl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleInventory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleQuestLog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Abilities.TryRequestUse", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "NativeGameplayHudHost.TryMount("));
        Assert.Contains("public override void _UnhandledInput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public override void _Input", source, StringComparison.Ordinal);
    }

    private static string ReadSource() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contract-source", "ZoneWalkabout.cs"));

    private static int Count(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }
}
