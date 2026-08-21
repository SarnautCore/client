using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

/// <summary>Checks one baked native character against its manifest LOD contract.</summary>
public static class NativeCharacterLodContract
{
    private const float RangeTolerance = 0.0001f;

    public static IReadOnlyList<MeshInstance3D> Inspect(Node3D model, NativeCharacterLod lod)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lod);

        var levels = new List<MeshInstance3D>(lod.Levels);
        for (int level = 0; level < lod.Levels; level++)
        {
            string name = LevelName(level);
            if (model.FindChild(name, recursive: true, owned: false) is not MeshInstance3D mesh)
            {
                throw new InvalidDataException($"Native character LOD node '{name}' is missing.");
            }

            levels.Add(mesh);
        }

        int namedLevelCount = CountDirectBodyLevels(levels[0].GetParent());
        if (namedLevelCount != lod.Levels)
        {
            throw new InvalidDataException(
                $"Native character has {namedLevelCount} named LOD meshes; manifest declares {lod.Levels}.");
        }

        if (levels[0].GetParent() is not Skeleton3D skeleton)
        {
            throw new InvalidDataException("Native character LOD0 is not a direct Skeleton3D child.");
        }

        Skin? sharedSkin = levels[0].Skin;
        if (sharedSkin is null)
        {
            throw new InvalidDataException("Native character LOD0 has no Skin resource.");
        }

        var meshIds = new HashSet<ulong>();
        Transform3D intendedTransform = levels[0].Transform;
        for (int level = 0; level < levels.Count; level++)
        {
            MeshInstance3D instance = levels[level];
            if (!ReferenceEquals(instance.GetParent(), skeleton))
            {
                throw new InvalidDataException($"Native character LOD{level} does not share the LOD0 skeleton.");
            }

            if (!ReferenceEquals(instance.Skin, sharedSkin))
            {
                throw new InvalidDataException($"Native character LOD{level} does not share the LOD0 Skin.");
            }

            if (!instance.Transform.IsEqualApprox(intendedTransform))
            {
                throw new InvalidDataException(
                    $"Native character LOD{level} changes the body mesh transform.");
            }

            Mesh? mesh = instance.Mesh;
            if (mesh is null || mesh.GetSurfaceCount() == 0)
            {
                throw new InvalidDataException($"Native character LOD{level} has no mesh surfaces.");
            }

            if (!meshIds.Add(mesh.GetInstanceId()))
            {
                throw new InvalidDataException($"Native character LOD{level} reuses another level's mesh.");
            }

            if ((instance.Layers & DynamicEntityLighting.ReceiverLayerMask) == 0)
            {
                throw new InvalidDataException($"Native character LOD{level} is not a dynamic-light receiver.");
            }

            float expectedBegin = level == 0 ? 0.0f : lod.SwitchDistances[level - 1];
            float expectedEnd = level == lod.Levels - 1 ? 0.0f : lod.SwitchDistances[level];
            RequireRange(instance.VisibilityRangeBegin, expectedBegin, level, "begin");
            RequireRange(instance.VisibilityRangeEnd, expectedEnd, level, "end");
            RequireRange(instance.VisibilityRangeBeginMargin, 0.0f, level, "begin margin");
            RequireRange(instance.VisibilityRangeEndMargin, 0.0f, level, "end margin");
            if (instance.VisibilityRangeFadeMode
                != GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled)
            {
                throw new InvalidDataException($"Native character LOD{level} has range fading enabled.");
            }
        }

        InspectAttachments(model);
        EnsureAllMeshDescendantsReceiveDynamicLight(model);

        return levels.AsReadOnly();
    }

    public static MeshInstance3D SelectAtDistance(
        IReadOnlyList<MeshInstance3D> levels,
        float distance)
    {
        if (!float.IsFinite(distance) || distance < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        MeshInstance3D? selected = null;
        foreach (MeshInstance3D level in levels)
        {
            bool afterBegin = distance >= level.VisibilityRangeBegin;
            bool beforeEnd = level.VisibilityRangeEnd <= 0.0f
                || distance < level.VisibilityRangeEnd;
            if (!afterBegin || !beforeEnd)
            {
                continue;
            }

            if (selected is not null)
            {
                throw new InvalidDataException(
                    $"Native character LOD ranges overlap at distance {distance}.");
            }

            selected = level;
        }

        return selected ?? throw new InvalidDataException(
            $"Native character LOD ranges leave a gap at distance {distance}.");
    }

    public static float ProbeDistance(NativeCharacterLod lod, int level)
    {
        if (level < 0 || level >= lod.Levels)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (level == 0)
        {
            return lod.SwitchDistances[0] * 0.5f;
        }

        if (level == lod.Levels - 1)
        {
            float last = lod.SwitchDistances[^1];
            return last + MathF.Max(1.0f, last * 0.25f);
        }

        return (lod.SwitchDistances[level - 1] + lod.SwitchDistances[level]) * 0.5f;
    }

    private static int CountDirectBodyLevels(Node? parent)
    {
        if (parent is null)
        {
            return 0;
        }

        int count = 0;
        foreach (Node child in parent.GetChildren())
        {
            string name = child.Name.ToString();
            if (child is MeshInstance3D
                && (name == "Mesh" || name.StartsWith("MeshLOD", StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }

    private static void InspectAttachments(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is BoneAttachment3D attachment)
            {
                InspectAttachment(attachment);
            }

            InspectAttachments(child);
        }
    }

    private static void InspectAttachment(BoneAttachment3D attachment)
    {
        string baseName = $"{attachment.Name}Mesh";
        var levels = new List<MeshInstance3D>();
        for (int level = 0; ; level++)
        {
            string name = level == 0 ? baseName : $"{baseName}LOD{level}";
            MeshInstance3D? mesh = attachment.GetNodeOrNull<MeshInstance3D>(name);
            if (mesh is null)
            {
                break;
            }

            levels.Add(mesh);
        }

        int directMeshes = 0;
        foreach (Node child in attachment.GetChildren())
        {
            if (child is MeshInstance3D)
            {
                directMeshes++;
            }
        }

        if (levels.Count == 0 || levels.Count != directMeshes)
        {
            throw new InvalidDataException(
                $"Attachment '{attachment.Name}' has missing or unexpected LOD mesh names.");
        }

        var meshIds = new HashSet<ulong>();
        Transform3D intendedTransform = levels[0].Transform;
        for (int level = 0; level < levels.Count; level++)
        {
            MeshInstance3D instance = levels[level];
            if (!instance.Transform.IsEqualApprox(intendedTransform))
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} changes the attachment transform.");
            }

            if (instance.Skin is not null)
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} must remain rigid.");
            }

            Mesh? mesh = instance.Mesh;
            if (mesh is null || mesh.GetSurfaceCount() == 0)
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} has no mesh surfaces.");
            }

            if (!meshIds.Add(mesh.GetInstanceId()))
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} reuses another level's mesh.");
            }

            RequireRange(instance.VisibilityRangeBeginMargin, 0.0f, level, "begin margin");
            RequireRange(instance.VisibilityRangeEndMargin, 0.0f, level, "end margin");
            if (instance.VisibilityRangeFadeMode
                != GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled)
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} has range fading enabled.");
            }
        }

        RequireRange(levels[0].VisibilityRangeBegin, 0.0f, 0, "attachment begin");
        if (levels.Count == 1)
        {
            RequireRange(levels[0].VisibilityRangeEnd, 0.0f, 0, "attachment end");
            return;
        }

        for (int level = 0; level < levels.Count - 1; level++)
        {
            float end = levels[level].VisibilityRangeEnd;
            if (!float.IsFinite(end) || end <= levels[level].VisibilityRangeBegin)
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' LOD{level} has a non-increasing range.");
            }

            RequireRange(
                levels[level + 1].VisibilityRangeBegin,
                end,
                level + 1,
                "attachment begin");
        }

        RequireRange(
            levels[^1].VisibilityRangeEnd,
            0.0f,
            levels.Count - 1,
            "attachment end");

        for (int level = 0; level < levels.Count; level++)
        {
            float distance = ProbeDistance(levels, level);
            MeshInstance3D selected = SelectAtDistance(levels, distance);
            if (!ReferenceEquals(selected, levels[level]))
            {
                throw new InvalidDataException(
                    $"Attachment '{attachment.Name}' selects the wrong mesh at distance {distance}.");
            }
        }
    }

    private static void EnsureAllMeshDescendantsReceiveDynamicLight(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh
                && (mesh.Layers & DynamicEntityLighting.ReceiverLayerMask) == 0)
            {
                throw new InvalidDataException(
                    $"Native character mesh '{mesh.GetPath()}' is not a dynamic-light receiver.");
            }

            EnsureAllMeshDescendantsReceiveDynamicLight(child);
        }
    }

    private static float ProbeDistance(IReadOnlyList<MeshInstance3D> levels, int level)
    {
        if (level == 0)
        {
            return levels[0].VisibilityRangeEnd * 0.5f;
        }

        if (level == levels.Count - 1)
        {
            float begin = levels[level].VisibilityRangeBegin;
            return begin + MathF.Max(1.0f, begin * 0.25f);
        }

        return (levels[level].VisibilityRangeBegin + levels[level].VisibilityRangeEnd) * 0.5f;
    }

    private static string LevelName(int level) => level == 0 ? "Mesh" : $"MeshLOD{level}";

    private static void RequireRange(float actual, float expected, int level, string field)
    {
        if (!float.IsFinite(actual) || MathF.Abs(actual - expected) > RangeTolerance)
        {
            throw new InvalidDataException(
                $"Native character LOD{level} {field} is {actual}; expected {expected}.");
        }
    }
}
