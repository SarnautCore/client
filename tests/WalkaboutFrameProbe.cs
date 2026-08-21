using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// Captures the offline walkabout exactly as a player reaches it from the boot
/// hub: default session, no character selected, real renderer. Saves one PNG
/// so a visual change has before/after evidence from the user's own scenario.
/// </summary>
public partial class WalkaboutFrameProbe : Node
{
    public override async void _Ready()
    {
        PackedScene packed = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn");
        Node3D zone = packed.Instantiate<Node3D>();
        AddChild(zone);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        ZoneLoader loader = zone.GetNode<ZoneLoader>("ZoneLoader");
        WalkaboutController walker = zone.GetNode<WalkaboutController>("Walker");
        string positionOverride = System.Environment.GetEnvironmentVariable("SARNAUT_FRAME_POSITION") ?? string.Empty;
        if (positionOverride.Split(',') is { Length: 3 } parts
            && float.TryParse(parts[0], out float x)
            && float.TryParse(parts[1], out float y)
            && float.TryParse(parts[2], out float z))
        {
            // Hold the overridden vantage instead of falling under gravity.
            walker.NetworkControlled = true;
            walker.Position = new Vector3(x, y, z);
        }

        string yawOverride = System.Environment.GetEnvironmentVariable("SARNAUT_FRAME_YAW") ?? string.Empty;
        if (float.TryParse(yawOverride, out float yawDegrees))
        {
            walker.RotationDegrees = new Vector3(0, yawDegrees, 0);
        }

        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);
        CharacterRig character = zone.GetNode<CharacterRig>("Walker/Character");
        Image image = GetViewport().GetTexture().GetImage();
        string proofPath = System.Environment.GetEnvironmentVariable("SARNAUT_FRAME_PROOF")
            is { Length: > 0 } custom
            ? custom
            : ProjectSettings.GlobalizePath("user://walkabout-frame.png");
        Error saveError = image.SavePng(proofPath);

        GD.Print(
            $"WALKABOUT_FRAME_PROBE spawn={walker.Position} terrain={loader.TerrainTileCount} "
            + $"visual={loader.VisualObjectCount} appearance=\"{character.ScenePath}\" "
            + $"active_clip=\"{character.ActiveClip}\" animating={character.IsAnimationPlaying} "
            + $"proof={proofPath} save={saveError}");
        ReportPose(character);
        GetTree().Quit(saveError == Error.Ok ? 0 : 1);
    }

    private static void ReportPose(CharacterRig character)
    {
        if (character.Model == null)
        {
            GD.Print("WALKABOUT_FRAME_PROBE pose: no model");
            return;
        }

        Skeleton3D? skeleton = Find<Skeleton3D>(character.Model);
        if (skeleton == null)
        {
            GD.Print("WALKABOUT_FRAME_PROBE pose: no skeleton");
            return;
        }

        int arm = skeleton.FindBone("LeftArm");
        if (arm < 0)
        {
            GD.Print("WALKABOUT_FRAME_PROBE pose: no LeftArm bone");
            return;
        }

        Quaternion rest = skeleton.GetBoneRest(arm).Basis.GetRotationQuaternion();
        Quaternion pose = skeleton.GetBonePoseRotation(arm);
        GD.Print($"WALKABOUT_FRAME_PROBE pose arm_drift_from_rest={rest.AngleTo(pose):F4}");
    }

    private static T? Find<T>(Node parent) where T : Node
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? found = Find<T>(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
