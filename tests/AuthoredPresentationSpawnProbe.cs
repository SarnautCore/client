using Godot;

namespace SarnautCore;

public partial class AuthoredPresentationSpawnProbe : Node
{
    private static readonly Vector3 ExpectedFloor6PlayerPosition = new(321.8298f, 156.142f, -5793.858f);

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Vector3 spawn = loader.SuggestedSpawnPosition;
        bool passed = spawn.IsEqualApprox(ExpectedFloor6PlayerPosition);

        GD.Print(
            $"AUTHORED_PRESENTATION_SPAWN spawn={spawn} expected={ExpectedFloor6PlayerPosition} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}

