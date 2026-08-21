using Godot;

namespace SarnautCore;

/// <summary>Keeps native sky geometry centered on the active camera.</summary>
public partial class CameraCenteredSky : Node3D
{
    public override void _Process(double delta)
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera is not null)
        {
            GlobalPosition = camera.GlobalPosition;
        }
    }
}
