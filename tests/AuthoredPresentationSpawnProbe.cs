using System;
using Godot;

namespace SarnautCore;

public partial class AuthoredPresentationSpawnProbe : Node
{
    private static readonly Vector3 ExpectedFloor6PlayerPosition = new(321.8298f, 156.142f, -5793.858f);
    private static readonly Quaternion ExpectedFloor6PlayerRotation = Quaternion.Identity;

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Vector3 spawn = loader.SuggestedSpawnPosition;
        Quaternion spawnRotation = loader.SuggestedSpawnRotation;
        var testWalker = new Node3D();
        AddChild(testWalker);
        ZoneWalkabout.ApplyPresentationSpawn(
            testWalker,
            spawn,
            spawnRotation);
        bool transformPassed = testWalker.Position.IsEqualApprox(ExpectedFloor6PlayerPosition)
            && MathF.Abs(testWalker.Quaternion.Dot(spawnRotation)) >= 0.99999f;
        RemoveChild(testWalker);
        testWalker.Free();

        bool rotationPassed = MathF.Abs(spawnRotation.Dot(ExpectedFloor6PlayerRotation)) >= 0.99999f;
        bool countersPassed = loader.NativeTerrainTileCount == 4
            && loader.NativeStaticPlacementCount == 41
            && loader.NativeStaticVisualCount == 36
            && loader.NativeStaticNonVisualCount == 5
            && loader.NativeCharacterPlacementCount == 24;
        bool passed = loader.IsFullyResolved
            && spawn.IsEqualApprox(ExpectedFloor6PlayerPosition)
            && rotationPassed
            && countersPassed
            && transformPassed;

        GD.Print(
            $"AUTHORED_PRESENTATION_SPAWN spawn={spawn} expected={ExpectedFloor6PlayerPosition} "
            + $"rotation={spawnRotation} fully_resolved={loader.IsFullyResolved} "
            + $"terrain={loader.NativeTerrainTileCount}/4 "
            + $"statics={loader.NativeStaticPlacementCount}/41 "
            + $"visual={loader.NativeStaticVisualCount}/36 "
            + $"non_visual={loader.NativeStaticNonVisualCount}/5 "
            + $"characters={loader.NativeCharacterPlacementCount}/24 "
            + $"authored_rotation={rotationPassed} transform={transformPassed} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        if (!passed && loader.LastError.Length > 0)
        {
            GD.PushError(loader.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }
}
