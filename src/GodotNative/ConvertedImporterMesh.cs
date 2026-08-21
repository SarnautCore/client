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

            ArrayMesh runtimeMesh = importerMesh.GetMesh();
            // GetMesh may hand back copies of the surface materials, so the
            // substitution is reapplied to what actually renders. Cached, so
            // repeating it for an already-swapped material costs a lookup.
            SarnautCore.UpscaledTextures.Retexture(runtimeMesh);
            NormalizeConvertedMaterials(runtimeMesh);
            var instance = new MeshInstance3D
            {
                Name = level == 0 ? "Mesh" : $"MeshLOD{level}",
                Mesh = runtimeMesh,
                VisibilityRangeBegin = level > 0 && level - 1 < distances.Length
                    ? distances[level - 1]
                    : 0.0f,
                VisibilityRangeEnd = level < distances.Length ? distances[level] : 0.0f,
            };
            // Every converted mesh starts as a runtime-lit receiver; the zone
            // loader's baked pass demotes the meshes its bake covers, so only
            // dynamic entities and unbaked props stay on the receiver layer.
            SarnautCore.DynamicEntityLighting.MarkReceiver(instance);
            if (skeleton == null)
            {
                AddChild(instance);
            }
            else
            {
                // Code-built MeshInstance3D nodes start with an empty skeleton
                // NodePath in this Godot build; without ".." they never attach
                // to the skeleton and render the bind pose forever.
                instance.Skeleton = new NodePath("..");
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
                SarnautCore.DynamicEntityLighting.MarkReceiver(instance);
                if (skeleton == null)
                {
                    AddChild(instance);
                }
                else
                {
                    instance.Skeleton = new NodePath("..");
                    instance.Skin = skin;
                    skeleton.AddChild(instance);
                }
            }
        }
    }

    /// <summary>
    /// Repairs authored blend semantics on converted mesh materials. Older
    /// converter artifacts emit additive surfaces (BLEND_EFFECT_ADD on a
    /// transparent part: crystal glows, flares, wing glows) as alpha-blended
    /// SHADED materials, which the scene's dim ambient collapses to nothing.
    /// The source engine draws them as emissive additive geometry, so they
    /// render unshaded with additive blending here. Idempotent, and applied at
    /// the one place every converted static and character mesh materializes.
    /// </summary>
    public static void NormalizeConvertedMaterials(ArrayMesh mesh)
    {
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetMaterial(surface) is not BaseMaterial3D material)
            {
                continue;
            }

            string blendEffect = (string)material.GetMeta("allods_blend_effect", "");
            bool authoredTransparent =
                material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
                || material.BlendMode == BaseMaterial3D.BlendModeEnum.Add;
            if (authoredTransparent && blendEffect.Contains("ADD"))
            {
                material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
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
