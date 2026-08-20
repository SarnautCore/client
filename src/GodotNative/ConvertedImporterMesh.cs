// Ported from ao-godot-converter runtime templates (Apache-2.0).
using Godot;

/// <summary>
/// Materializes the converter's per-level ImporterMesh resources as runtime ArrayMeshes.
/// Visibility ranges preserve the authored Allods LOD distance thresholds. Skinned
/// scenes carry the skeleton inside the .skmesh referenced by allods_skin_mesh;
/// legacy scenes instead instanced a .skeleton.tscn child, which is still honored.
/// </summary>
[GlobalClass]
public partial class ConvertedImporterMesh : Node3D
{
    public override void _EnterTree()
    {
        // The skeleton must exist before sibling AnimationPlayer nodes run
        // _Ready and resolve their "Skeleton3D:bone" track paths, and a parent's
        // _EnterTree runs before any child's, so it is built here.
        string skinMeshPath = (string)GetMeta("allods_skin_mesh", "");
        if (!string.IsNullOrEmpty(skinMeshPath) && GetNodeOrNull<Skeleton3D>("Skeleton3D") == null)
        {
            Skeleton3D? skeleton = ConvertedSkinnedMesh.BuildSkeletonFromFile(skinMeshPath);
            if (skeleton != null)
            {
                AddChild(skeleton);
            }
        }
    }

    public override void _Ready()
    {
        var importerMeshPaths = (string[])GetMeta(
            "allods_lod_meshes", System.Array.Empty<string>());
        var distances = (float[])GetMeta(
            "allods_lod_distances", System.Array.Empty<float>());
        if (importerMeshPaths.Length == 0)
        {
            return;
        }

        string skinMeshPath = (string)GetMeta("allods_skin_mesh", "");
        var skeleton = GetNodeOrNull<Skeleton3D>("Skeleton3D");
        Skin? skin = skeleton == null
            ? null
            : ConvertedSkinnedMesh.BuildSkinForSkeleton(skinMeshPath, skeleton);
        int materialized = 0;
        for (int level = 0; level < importerMeshPaths.Length; level++)
        {
            string path = importerMeshPaths[level];
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            ImporterMesh? importerMesh = LoadImporterMesh(path);
            if (importerMesh == null)
            {
                continue;
            }

            var instance = new MeshInstance3D
            {
                Name = level == 0 ? "Mesh" : $"MeshLOD{level}",
                Mesh = importerMesh.GetMesh(),
                VisibilityRangeBegin = level > 0 && level - 1 < distances.Length
                    ? distances[level - 1]
                    : 0.0f,
                VisibilityRangeEnd = level < distances.Length ? distances[level] : 0.0f,
            };
            if (skeleton == null)
            {
                AddChild(instance);
            }
            else
            {
                instance.Skin = skin;
                skeleton.AddChild(instance);
            }

            materialized++;
        }

        // The ImporterMesh route needs the editor to have imported the tree's
        // textures; the .skmesh route reads everything through FileAccess. Fall
        // back to it so models render even on a freshly mounted converted tree.
        if (materialized == 0 && !string.IsNullOrEmpty(skinMeshPath))
        {
            ArrayMesh? fallback = ConvertedSkinnedMesh.LoadPreviewMesh(skinMeshPath);
            if (fallback != null)
            {
                var instance = new MeshInstance3D { Name = "Mesh", Mesh = fallback };
                if (skeleton == null)
                {
                    AddChild(instance);
                }
                else
                {
                    instance.Skin = skin;
                    skeleton.AddChild(instance);
                }
            }
        }
    }

    /// <summary>
    /// Loads a converted ImporterMesh .tres. The converter writes texture
    /// references relative to its own output tree (res://assets/...), so when
    /// the tree is mounted under converted/ the resource must be loaded through
    /// ConvertedSceneLoader, which patches those prefixes.
    /// </summary>
    private static ImporterMesh? LoadImporterMesh(string path)
    {
        int marker = path.LastIndexOf("/assets/", System.StringComparison.Ordinal);
        string root = marker > "res://".Length ? path[..marker] : string.Empty;
        if (root.Length > 0)
        {
            ImporterMesh? patched = SarnautCore.ConvertedSceneLoader.LoadResource<ImporterMesh>(
                root, path, out string error);
            if (patched == null)
            {
                GD.PushWarning($"ConvertedImporterMesh: {error}");
            }

            return patched;
        }

        var direct = ResourceLoader.Load<ImporterMesh>(path);
        if (direct == null)
        {
            GD.PushWarning($"ConvertedImporterMesh: cannot load {path}");
        }

        return direct;
    }
}
