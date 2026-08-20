using Godot;

namespace SarnautCore;

public partial class ZoneWalkaboutSmoke : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        bool passed = loader.TerrainTileCount > 0 && loader.PlacedObjectCount > 0;
        GD.Print(
            $"ZONE_WALKABOUT_SMOKE terrain={loader.TerrainTileCount} vertices={loader.TerrainVertexCount} " +
            $"placed={loader.PlacedObjectCount} visual={loader.VisualObjectCount} unresolved={loader.UnresolvedObjectCount} " +
            $"server={loader.ServerObjectCount} result={(passed ? "PASS" : "FAIL")}");
        if (!passed && !string.IsNullOrEmpty(loader.LastError))
        {
            GD.PushError(loader.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }
}
