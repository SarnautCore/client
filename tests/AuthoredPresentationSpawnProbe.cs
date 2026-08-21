using System;
using Godot;

namespace SarnautCore;

public partial class AuthoredPresentationSpawnProbe : Node
{
    private static readonly Vector3 ExpectedFloor6PlayerPosition = new(321.8298f, 156.142f, -5793.858f);

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Vector3 spawn = loader.SuggestedSpawnPosition;
        var testWalker = new Node3D();
        AddChild(testWalker);
        var expectedRotation = new Quaternion(Vector3.Up, Mathf.DegToRad(73.0f));
        ZoneWalkabout.ApplyPresentationSpawn(
            testWalker,
            ExpectedFloor6PlayerPosition,
            expectedRotation);
        bool transformPassed = testWalker.Position.IsEqualApprox(ExpectedFloor6PlayerPosition)
            && MathF.Abs(testWalker.Quaternion.Dot(expectedRotation)) >= 0.99999f;
        RemoveChild(testWalker);
        testWalker.Free();

        bool passed = spawn.IsEqualApprox(ExpectedFloor6PlayerPosition) && transformPassed;

        GD.Print(
            $"AUTHORED_PRESENTATION_SPAWN spawn={spawn} expected={ExpectedFloor6PlayerPosition} "
            + $"non_identity_rotation={transformPassed} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
