using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class GameplayFocusOwnerTests
{
    [Fact]
    public void Cancel_releases_mouse_before_leaving_walkabout()
    {
        var focus = new GameplayFocusOwner();

        Assert.True(focus.MouseCaptured);
        Assert.Equal(FocusCancelResult.MouseReleased, focus.Cancel());
        Assert.False(focus.MouseCaptured);
        Assert.Equal(FocusCancelResult.LeaveWalkabout, focus.Cancel());
    }

    [Fact]
    public void Window_owns_focus_until_the_top_window_closes()
    {
        var focus = new GameplayFocusOwner();

        focus.Open(GameplayWindow.Inventory);
        focus.Open(GameplayWindow.QuestLog);

        Assert.Equal(GameplayWindow.QuestLog, focus.FocusedWindow);
        Assert.False(focus.MouseCaptured);
        Assert.False(focus.WorldInputEnabled);
        Assert.Equal(FocusCancelResult.WindowClosed, focus.Cancel());
        Assert.Equal(GameplayWindow.Inventory, focus.FocusedWindow);
        Assert.Equal(FocusCancelResult.WindowClosed, focus.Cancel());
        Assert.Null(focus.FocusedWindow);
        Assert.True(focus.WorldInputEnabled);
    }

    [Fact]
    public void World_can_only_recapture_when_no_window_owns_focus()
    {
        var focus = new GameplayFocusOwner();
        focus.Cancel();
        focus.Open(GameplayWindow.Dialogue);

        Assert.False(focus.TryCaptureWorld());

        focus.Close(GameplayWindow.Dialogue);

        Assert.True(focus.TryCaptureWorld());
        Assert.True(focus.MouseCaptured);
    }
}
