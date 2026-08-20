namespace SarnautCore.Gameplay;

/// <summary>Interactive HUD windows that may own keyboard and pointer focus.</summary>
public enum GameplayWindow
{
    Loot,
    Inventory,
    QuestLog,
    Dialogue,
}

/// <summary>The action a caller must perform after the focus owner handles Escape.</summary>
public enum FocusCancelResult
{
    None,
    WindowClosed,
    MouseReleased,
    LeaveWalkabout,
}

/// <summary>
/// Owns walkabout pointer capture and the stack of interactive HUD windows.
/// </summary>
/// <remarks>
/// Godot mirrors this state into <c>Input.MouseMode</c>. No scene node is
/// allowed to change mouse mode independently, which keeps world picking,
/// camera look, and window input from racing each other.
/// </remarks>
public sealed class GameplayFocusOwner
{
    private readonly List<GameplayWindow> _windows = [];

    public bool MouseCaptured { get; private set; } = true;

    public GameplayWindow? FocusedWindow => _windows.Count == 0 ? null : _windows[^1];

    public bool IsOpen(GameplayWindow window) => _windows.Contains(window);

    public bool WorldInputEnabled => _windows.Count == 0;

    public bool WorldLookEnabled => WorldInputEnabled && MouseCaptured;

    public bool WorldPointerEnabled => WorldInputEnabled && !MouseCaptured;

    public event Action? Changed;

    public void Open(GameplayWindow window)
    {
        _windows.Remove(window);
        _windows.Add(window);
        MouseCaptured = false;
        Changed?.Invoke();
    }

    public bool Close(GameplayWindow window)
    {
        bool removed = _windows.Remove(window);
        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    public FocusCancelResult Cancel()
    {
        if (_windows.Count > 0)
        {
            _windows.RemoveAt(_windows.Count - 1);
            Changed?.Invoke();
            return FocusCancelResult.WindowClosed;
        }

        if (MouseCaptured)
        {
            MouseCaptured = false;
            Changed?.Invoke();
            return FocusCancelResult.MouseReleased;
        }

        return FocusCancelResult.LeaveWalkabout;
    }

    public bool TryCaptureWorld()
    {
        if (_windows.Count > 0 || MouseCaptured)
        {
            return false;
        }

        MouseCaptured = true;
        Changed?.Invoke();
        return true;
    }
}
