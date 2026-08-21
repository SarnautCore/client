using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>Checks natural animation playback in the real walkabout scene.</summary>
public partial class LiveZonePlayerAnimationProbe : Node
{
    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node zone = scene.Instantiate();
        AddChild(zone);

        CharacterRig character = zone.GetNode<CharacterRig>("Walker/Character");
        Skeleton3D? skeleton = character.Model == null ? null : FindDescendant<Skeleton3D>(character.Model);
        AnimationPlayer? player = character.Model == null ? null : FindDescendant<AnimationPlayer>(character.Model);
        Expect(character.HasModel, $"live player model loads: {character.LastError}");
        Expect(skeleton != null, "live player has a skeleton");
        Expect(player != null, "live player has an AnimationPlayer");

        double firstTime = player?.CurrentAnimationPosition ?? 0;
        Transform3D[] firstPose = skeleton == null ? [] : CapturePose(skeleton);
        string capturePrefix = System.Environment.GetEnvironmentVariable("SARNAUT_ANIMATION_PROBE_FRAMES") ?? string.Empty;
        for (int frame = 0; frame < 12; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (capturePrefix.Length > 0)
            {
                Error saved = GetViewport().GetTexture().GetImage().SavePng($"{capturePrefix}{frame:D3}.png");
                Expect(saved == Error.Ok, $"frame {frame} screenshot saves: {saved}");
            }
        }

        double lastTime = player?.CurrentAnimationPosition ?? 0;
        Transform3D[] lastPose = skeleton == null ? [] : CapturePose(skeleton);
        bool poseAdvanced = PoseChanged(firstPose, lastPose);
        Vector3 rightArm = skeleton == null ? Vector3.Zero : BoneDirection(skeleton, "RightArm", "RightForeArm");
        Vector3 leftArm = skeleton == null ? Vector3.Zero : BoneDirection(skeleton, "LeftArm", "LeftForeArm");
        MeshInstance3D? skinnedMesh = skeleton == null ? null : FindSkinnedMesh(skeleton);
        // The stripped OBJ fallback node is the "Mesh" sibling of the body's
        // Skeleton3D (under the converted base scene root). A recursive search
        // from Model would instead find the equipped weapons' render meshes,
        // which are legitimately visible and also named "Mesh".
        MeshInstance3D? fallbackMesh = skeleton?.GetParent()?.GetNodeOrNull<MeshInstance3D>("Mesh");
        Node? boundSkeleton = skinnedMesh == null || skinnedMesh.Skeleton.IsEmpty
            ? null
            : skinnedMesh.GetNodeOrNull(skinnedMesh.Skeleton);

        Expect(player?.IsPlaying() == true, "live idle reports active playback");
        Expect(lastTime > firstTime + 0.05, $"live idle time advances, saw {firstTime:F3} -> {lastTime:F3}");
        Expect(poseAdvanced, "live idle changes skeleton poses without a manual Seek");
        Expect(Mathf.Abs(rightArm.Y) > 0.25f && Mathf.Abs(leftArm.Y) > 0.25f,
            $"live idle lowers both arms out of bind pose, right={rightArm} left={leftArm}");
        Expect(skinnedMesh != null, "live player has a runtime skinned mesh");
        Expect(boundSkeleton == skeleton,
            $"runtime mesh resolves its skeleton path '{skinnedMesh?.Skeleton}' to the animated skeleton");
        Expect(fallbackMesh == null || !fallbackMesh.Visible, "static OBJ fallback is hidden");

        bool passed = _failures.Count == 0;
        GD.Print(
            $"LIVE_ZONE_PLAYER_ANIMATION clip=\"{character.ActiveClip}\" playing={player?.IsPlaying() == true} "
            + $"time={firstTime:F3}->{lastTime:F3} pose_advanced={poseAdvanced} "
            + $"right_arm={rightArm} left_arm={leftArm} skin_path=\"{skinnedMesh?.Skeleton}\" "
            + $"skin_resolved={boundSkeleton == skeleton} fallback_visible={fallbackMesh?.Visible} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"LIVE_ZONE_PLAYER_ANIMATION {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private static Vector3 BoneDirection(Skeleton3D skeleton, string parentName, string childName)
    {
        int parent = skeleton.FindBone(parentName);
        int child = skeleton.FindBone(childName);
        if (parent < 0 || child < 0)
        {
            return Vector3.Zero;
        }

        return (skeleton.GetBoneGlobalPose(child).Origin - skeleton.GetBoneGlobalPose(parent).Origin).Normalized();
    }

    private static Transform3D[] CapturePose(Skeleton3D skeleton)
    {
        var poses = new Transform3D[skeleton.GetBoneCount()];
        for (int bone = 0; bone < poses.Length; bone++)
        {
            poses[bone] = skeleton.GetBonePose(bone);
        }

        return poses;
    }

    private static bool PoseChanged(IReadOnlyList<Transform3D> first, IReadOnlyList<Transform3D> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (int bone = 0; bone < first.Count; bone++)
        {
            if (!first[bone].IsEqualApprox(second[bone]))
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindDescendant<T>(Node parent) where T : Node
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static MeshInstance3D? FindSkinnedMesh(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is MeshInstance3D { Skin: not null } mesh)
            {
                return mesh;
            }

            MeshInstance3D? descendant = FindSkinnedMesh(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }
}
