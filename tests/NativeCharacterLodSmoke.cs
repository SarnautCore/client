using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

/// <summary>Instantiates one native character and proves its authored LOD switches.</summary>
public partial class NativeCharacterLodSmoke : Node3D
{
    private const string FixtureRoot = "res://tests/fixtures/native-character-content";
    private const string FixtureKey = "mob.inst-league1.rat.rat1-1";
    private readonly List<string> _failures = [];

    public override void _Ready()
    {
        string nativeRoot = ReadSetting("SARNAUT_NATIVE_CHARACTER_LOD_ROOT", FixtureRoot);
        string characterKey = ReadSetting("SARNAUT_NATIVE_CHARACTER_LOD_KEY", FixtureKey);
        var manifest = new NativeCharacterManifestReader(nativeRoot);
        Expect(manifest.Manifest is not null, $"manifest loads from {nativeRoot}: {manifest.LastError}");
        if (manifest.Manifest is null)
        {
            Finish();
            return;
        }

        if (characterKey == "*")
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NativeCharacterModel identityModel in manifest.Manifest.Models
                .OrderBy(model => model.IdentityId, StringComparer.Ordinal))
            {
                if (seen.Add(identityModel.IdentityId))
                {
                    ValidateCharacter(manifest, identityModel);
                }
            }

            Expect(seen.Count == manifest.Manifest.IdentityCount,
                $"validated {seen.Count}/{manifest.Manifest.IdentityCount} identities");
            GD.Print($"NATIVE_CHARACTER_LOD identities={seen.Count}/{manifest.Manifest.IdentityCount}");
            Finish();
            return;
        }

        if (!manifest.TryResolve(characterKey, out NativeCharacterModel model))
        {
            Fail($"character key '{characterKey}' resolves: {manifest.LastError}");
            Finish();
            return;
        }

        ValidateCharacter(manifest, model);
        Finish();
    }

    private void ValidateCharacter(
        NativeCharacterManifestReader manifest,
        NativeCharacterModel model)
    {
        NativeCharacterLod? lod = model.Lod;
        if (lod is null)
        {
            Fail($"identity '{model.IdentityId}' declares native LOD metadata");
            return;
        }

        var rig = new CharacterRig
        {
            Name = "LodProbeRig",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
            ScenePath = manifest.ResolveScenePath(model),
        };
        AddChild(rig);
        try
        {
            if (!rig.Load() || rig.Model is null)
            {
                Fail($"identity '{model.IdentityId}' scene loads: {rig.LastError}");
                return;
            }

            IReadOnlyList<MeshInstance3D> levels;
            try
            {
                levels = NativeCharacterLodContract.Inspect(rig.Model, lod);
            }
            catch (Exception exception)
            {
                Fail($"identity '{model.IdentityId}': {exception.Message}");
                return;
            }

            var camera = new Camera3D { Name = "ControlledLodCamera", Current = true };
            AddChild(camera);
            try
            {
                Vector3 meshOrigin = levels[0].GlobalPosition;
                for (int level = 0; level < lod.Levels; level++)
                {
                    float requestedDistance = NativeCharacterLodContract.ProbeDistance(lod, level);
                    camera.GlobalPosition = meshOrigin + (Vector3.Back * requestedDistance);
                    float actualDistance = camera.GlobalPosition.DistanceTo(meshOrigin);
                    try
                    {
                        MeshInstance3D selected = NativeCharacterLodContract.SelectAtDistance(
                            levels,
                            actualDistance);
                        string expectedName = level == 0 ? "Mesh" : $"MeshLOD{level}";
                        Expect(selected.Name == expectedName,
                            $"identity '{model.IdentityId}' distance {actualDistance} selects {expectedName}");
                        if (level > 0)
                        {
                            Expect(!ReferenceEquals(selected, levels[0]),
                                $"identity '{model.IdentityId}' LOD0 does not overlap level {level} at distance {actualDistance}");
                        }
                    }
                    catch (Exception exception)
                    {
                        Fail($"identity '{model.IdentityId}': {exception.Message}");
                    }
                }
            }
            finally
            {
                RemoveChild(camera);
                camera.Free();
            }
        }
        finally
        {
            RemoveChild(rig);
            rig.Free();
        }
    }

    private static string ReadSetting(string name, string fallback)
    {
        string value = OS.GetEnvironment(name).Trim();
        return value.Length == 0 ? fallback : value;
    }

    private void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Fail(message);
        }
    }

    private void Fail(string message) => _failures.Add(message);

    private void Finish()
    {
        bool passed = _failures.Count == 0;
        foreach (string failure in _failures)
        {
            GD.PushError($"NATIVE_CHARACTER_LOD {failure}");
        }

        GD.Print($"NATIVE_CHARACTER_LOD result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
