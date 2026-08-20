using Godot;

namespace SarnautCore;

public partial class ZoneWalkaboutSmoke : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        ConvertedCharacter player = GetNode<ConvertedCharacter>("PlayerCharacter");
        player.SetMoving(true);
        bool runSelected = player.ActiveClip.Equals("run", System.StringComparison.OrdinalIgnoreCase);
        player.SetMoving(false);
        bool idleSelected = player.ActiveClip.Equals("idle", System.StringComparison.OrdinalIgnoreCase);
        bool passed = loader.TerrainTileCount > 0
            && loader.PlacedObjectCount > 0
            && loader.NpcVisualCount > 0
            && player.HasConvertedModel
            && player.SkeletonBoneCount > 0
            && player.ClipCount > 0
            && runSelected
            && idleSelected;
        GD.Print(
            $"ZONE_WALKABOUT_SMOKE terrain={loader.TerrainTileCount} vertices={loader.TerrainVertexCount} " +
            $"placed={loader.PlacedObjectCount} visual={loader.VisualObjectCount} unresolved={loader.UnresolvedObjectCount} " +
            $"server={loader.ServerObjectCount} npc={loader.NpcVisualCount}/{loader.NpcPlacementCount} " +
            $"npc_placeholders={loader.NpcPlaceholderCount} player_bones={player.SkeletonBoneCount} " +
            $"player_clips={player.ClipCount} idle_run={idleSelected && runSelected} " +
            $"result={(passed ? "PASS" : "FAIL")}");
        if (!passed && !string.IsNullOrEmpty(loader.LastError))
        {
            GD.PushError(loader.LastError);
        }

        if (!passed && !string.IsNullOrEmpty(player.LastError))
        {
            GD.PushError(player.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }
}
