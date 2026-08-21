using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>Exercises manifest lookup, plain scene loading, lighting, and clip control.</summary>
public partial class NativeCharacterRuntimeSmoke : Node
{
    private const string FixtureRoot = "res://tests/fixtures/native-character-content";
    private readonly List<string> _failures = [];

    public override void _Ready()
    {
        var catalog = new EntityModelCatalog(FixtureRoot);
        Expect(catalog.IsAvailable, $"fixture manifest loads: {catalog.LastError}");
        Expect(
            catalog.TryResolve("mob.inst-league1.rat.rat1-1", out EntityModel model),
            "server content id resolves");
        Expect(model.Scale == 1.0f, "baked scale is not applied a second time");

        var rig = new CharacterRig
        {
            Name = "FixtureRig",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
            ScenePath = model.ScenePath,
        };
        AddChild(rig);
        Expect(rig.Load(), $"PackedScene loads through ResourceLoader: {rig.LastError}");
        Expect(rig.HasModel, "rig reports its native model");
        Expect(rig.SkeletonBoneCount == 1, "fixture skeleton is present");
        Expect(rig.ClipCount == 5, "fixture animation library is present");

        MeshInstance3D? mesh = rig.Model?.FindChild("Mesh", recursive: true, owned: false) as MeshInstance3D;
        Expect(mesh != null, "fixture mesh is present");
        Expect(
            mesh != null && (mesh.Layers & DynamicEntityLighting.ReceiverLayerMask) != 0,
            "native character meshes receive dynamic lighting");

        rig.SetMoving(true);
        Expect(rig.ActiveClip.Equals("run", StringComparison.OrdinalIgnoreCase), "movement selects run");
        Expect(rig.PlayAttack(), "attack one-shot is available");
        Expect(rig.PlayHit(), "hit one-shot is available");
        Expect(rig.PlayDeath(), "death one-shot is available");

        bool passed = _failures.Count == 0;
        foreach (string failure in _failures)
        {
            GD.PushError($"NATIVE_CHARACTER_RUNTIME {failure}");
        }

        GD.Print($"NATIVE_CHARACTER_RUNTIME result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private void Expect(bool condition, string message)
    {
        if (!condition)
        {
            _failures.Add(message);
        }
    }
}
