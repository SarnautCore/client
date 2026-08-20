// Ported from ao-godot-converter runtime templates (Apache-2.0).
using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds a Skeleton3D, skinned ArrayMesh, and Skin from the converter's
/// .skmesh artifact. Format version 4 is self-contained: it embeds the
/// Godot-converted skeleton and skinning palette and stores indices in
/// Godot's clockwise front-face winding. Versions 2 and 3 predate that:
/// they rely on a .skeleton.tscn instanced into the scene plus skeleton
/// metadata, and carry counter-clockwise indices, so their materials render
/// double-sided.
/// </summary>
[GlobalClass]
public partial class ConvertedSkinnedMesh : Node3D
{
    private static readonly Dictionary<string, LoadedSkinMesh?> MeshCache = new();
    private static readonly Dictionary<string, Texture2D?> TextureCache = new();

    public override void _EnterTree()
    {
        // The skeleton must exist before sibling AnimationPlayer nodes run
        // _Ready and resolve their "Skeleton3D:bone" track paths, and a
        // parent's _EnterTree runs before any child's, so it is built here.
        string skinMeshPath = (string)GetMeta("allods_skin_mesh", "");
        if (string.IsNullOrEmpty(skinMeshPath) || GetNodeOrNull<Skeleton3D>("Skeleton3D") != null)
        {
            return;
        }

        Skeleton3D? skeleton = BuildSkeletonFromFile(skinMeshPath);
        if (skeleton != null)
        {
            AddChild(skeleton);
        }
    }

    public override void _Ready()
    {
        string skinMeshPath = (string)GetMeta("allods_skin_mesh", "");
        if (string.IsNullOrEmpty(skinMeshPath))
        {
            return;
        }

        var loaded = LoadSkinMesh(skinMeshPath);
        if (loaded == null)
        {
            return;
        }

        var skeleton = GetNodeOrNull<Skeleton3D>("Skeleton3D");
        var instance = new MeshInstance3D { Mesh = loaded.DefaultMesh };
        instance.SetMeta("allods_source_mesh", loaded.FullMesh);
        instance.SetMeta("allods_surfaces", loaded.SurfacesJson);
        if (skeleton != null)
        {
            instance.Skin = BuildSkin(loaded, skeleton);
            skeleton.AddChild(instance);
        }
        else
        {
            AddChild(instance);
        }

        GetNodeOrNull<MeshInstance3D>("Mesh")?.Hide();
    }

    /// <summary>Loads the default-visible surfaces for tools such as the asset viewer.</summary>
    public static ArrayMesh? LoadPreviewMesh(string path)
    {
        return LoadSkinMesh(path)?.DefaultMesh;
    }

    /// <summary>Builds the Skeleton3D embedded in a version-4 .skmesh, or null.</summary>
    public static Skeleton3D? BuildSkeletonFromFile(string path)
    {
        var loaded = LoadSkinMesh(path);
        if (loaded == null || loaded.BoneNames.Length == 0)
        {
            return null;
        }

        var skeleton = new Skeleton3D { Name = "Skeleton3D" };
        for (int bone = 0; bone < loaded.BoneNames.Length; bone++)
        {
            skeleton.AddBone(loaded.BoneNames[bone]);
        }

        for (int bone = 0; bone < loaded.BoneNames.Length; bone++)
        {
            if (loaded.BoneParents[bone] >= 0)
            {
                skeleton.SetBoneParent(bone, loaded.BoneParents[bone]);
            }

            skeleton.SetBoneRest(bone, loaded.BoneRests[bone]);
        }

        skeleton.ResetBonePoses();
        return skeleton;
    }

    /// <summary>
    /// Builds the Skin from the version-4 embedded palette, falling back to the
    /// legacy skeleton metadata written next to version-2/3 artifacts.
    /// </summary>
    public static Skin BuildSkinForSkeleton(string path, Skeleton3D skeleton)
    {
        LoadedSkinMesh? loaded = string.IsNullOrEmpty(path) ? null : LoadSkinMesh(path);
        return loaded == null ? LegacySkinFromMetadata(skeleton) : BuildSkin(loaded, skeleton);
    }

    private sealed class LoadedSkinMesh
    {
        public required uint Version { get; init; }
        public required ArrayMesh FullMesh { get; init; }
        public required ArrayMesh DefaultMesh { get; init; }
        public required string SurfacesJson { get; init; }
        public required string[] BoneNames { get; init; }
        public required int[] BoneParents { get; init; }
        public required Transform3D[] BoneRests { get; init; }
        public required int[] SlotBones { get; init; }
        public required Transform3D[] SlotBinds { get; init; }
    }

    private static Skin BuildSkin(LoadedSkinMesh loaded, Skeleton3D skeleton)
    {
        if (loaded.SlotBones.Length == 0)
        {
            return LegacySkinFromMetadata(skeleton);
        }

        var skin = new Skin();
        for (int slot = 0; slot < loaded.SlotBones.Length; slot++)
        {
            skin.AddBind(slot, loaded.SlotBinds[slot]);
            skin.SetBindBone(skin.GetBindCount() - 1, loaded.SlotBones[slot]);
        }

        return skin;
    }

    private static Skin LegacySkinFromMetadata(Skeleton3D skeleton)
    {
        var visual = (int[])skeleton.GetMeta("allods_visual_bones", System.Array.Empty<int>());
        var binds = (float[])skeleton.GetMeta("allods_skin_binds", System.Array.Empty<float>());
        var skin = new Skin();
        for (int slot = 0; slot < visual.Length; slot++)
        {
            Transform3D bind = binds.Length == visual.Length * 12
                ? ReadTransform(binds, slot * 12)
                : skeleton.GetBoneGlobalRest(visual[slot]).AffineInverse();
            skin.AddBind(slot, bind);
            skin.SetBindBone(skin.GetBindCount() - 1, visual[slot]);
        }

        return skin;
    }

    private static LoadedSkinMesh? LoadSkinMesh(string path)
    {
        if (MeshCache.TryGetValue(path, out LoadedSkinMesh? cached))
        {
            return cached;
        }

        LoadedSkinMesh? loaded = LoadSkinMeshUncached(path);
        MeshCache[path] = loaded;
        return loaded;
    }

    private static LoadedSkinMesh? LoadSkinMeshUncached(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PushWarning($"ConvertedSkinnedMesh: cannot open {path}");
            return null;
        }

        byte[] magic = file.GetBuffer(4);
        if (magic.Length != 4 || magic[0] != 'A' || magic[1] != 'O' || magic[2] != 'S' || magic[3] != 'K')
        {
            GD.PushWarning($"ConvertedSkinnedMesh: bad magic in {path}");
            return null;
        }

        uint version = file.Get32();
        if (version < 2 || version > 4)
        {
            GD.PushWarning($"ConvertedSkinnedMesh: unsupported version {version} in {path}");
            return null;
        }

        uint partCount = file.Get32();
        Texture2D? bodyAtlas = null;
        if (version >= 3)
        {
            string atlasPath = ResolveAssetPath(ReadString(file), path);
            bodyAtlas = LoadTexture(atlasPath);
        }

        string[] boneNames = System.Array.Empty<string>();
        int[] boneParents = System.Array.Empty<int>();
        Transform3D[] boneRests = System.Array.Empty<Transform3D>();
        int[] slotBones = System.Array.Empty<int>();
        Transform3D[] slotBinds = System.Array.Empty<Transform3D>();
        if (version >= 4)
        {
            uint boneCount = file.Get32();
            boneNames = new string[boneCount];
            boneParents = new int[boneCount];
            boneRests = new Transform3D[boneCount];
            for (uint bone = 0; bone < boneCount; bone++)
            {
                boneNames[bone] = ReadString(file);
                boneParents[bone] = (int)file.Get32();
                boneRests[bone] = ReadTransform(file);
            }

            uint slotCount = file.Get32();
            slotBones = new int[slotCount];
            slotBinds = new Transform3D[slotCount];
            for (uint slot = 0; slot < slotCount; slot++)
            {
                slotBones[slot] = (int)file.Get32();
                slotBinds[slot] = ReadTransform(file);
            }
        }

        var full = new ArrayMesh();
        var defaults = new ArrayMesh();
        var surfaces = new Godot.Collections.Array();
        for (uint part = 0; part < partCount; part++)
        {
            uint vertexCount = file.Get32();
            uint indexCount = file.Get32();
            uint flags = file.Get32();
            bool skinned = (flags & 0xff) != 0;
            bool transparent = ((flags >> 8) & 0xff) != 0;
            bool defaultVisible = version < 3 || ((flags >> 16) & 0xff) != 0;
            bool usesBodyAtlas = version >= 3 && ((flags >> 24) & 0xff) != 0;
            string partName = ReadString(file);
            string texturePath = ResolveAssetPath(ReadString(file), path);
            string blendEffect = ReadString(file);
            string slot = partName;
            int variant = -1;
            if (version >= 3)
            {
                slot = ReadString(file);
                variant = (int)file.Get32();
            }

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var colors = new Color[vertexCount];
            var uvs = new Vector2[vertexCount];
            var bones = new int[vertexCount * 4];
            var weights = new float[vertexCount * 4];
            for (uint vertex = 0; vertex < vertexCount; vertex++)
            {
                positions[vertex] = new Vector3(file.GetFloat(), file.GetFloat(), file.GetFloat());
                normals[vertex] = new Vector3(file.GetFloat(), file.GetFloat(), file.GetFloat());
                colors[vertex] = new Color(file.GetFloat(), file.GetFloat(), file.GetFloat(), file.GetFloat());
                uvs[vertex] = new Vector2(file.GetFloat(), file.GetFloat());
                for (int bone = 0; bone < 4; bone++)
                {
                    bones[vertex * 4 + bone] = file.Get8();
                }

                for (int weight = 0; weight < 4; weight++)
                {
                    weights[vertex * 4 + weight] = file.GetFloat();
                }
            }

            var indices = new int[indexCount];
            for (uint index = 0; index < indexCount; index++)
            {
                indices[index] = (int)file.Get32();
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            if (skinned)
            {
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }

            var material = BuildMaterial(texturePath, blendEffect, transparent, version);
            if (usesBodyAtlas && bodyAtlas != null && material is BaseMaterial3D baseMaterial)
            {
                baseMaterial.AlbedoTexture = bodyAtlas;
            }

            full.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            int surface = full.GetSurfaceCount() - 1;
            full.SurfaceSetName(surface, partName);
            full.SurfaceSetMaterial(surface, material);
            if (defaultVisible)
            {
                defaults.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                int visibleSurface = defaults.GetSurfaceCount() - 1;
                defaults.SurfaceSetName(visibleSurface, partName);
                defaults.SurfaceSetMaterial(visibleSurface, material);
            }

            surfaces.Add(new Godot.Collections.Dictionary
            {
                ["name"] = partName,
                ["slot"] = slot,
                ["variant"] = variant,
                ["default_visible"] = defaultVisible,
                ["uses_body_atlas"] = usesBodyAtlas,
            });
        }

        return new LoadedSkinMesh
        {
            Version = version,
            FullMesh = full,
            DefaultMesh = defaults.GetSurfaceCount() > 0 ? defaults : full,
            SurfacesJson = Json.Stringify(surfaces),
            BoneNames = boneNames,
            BoneParents = boneParents,
            BoneRests = boneRests,
            SlotBones = slotBones,
            SlotBinds = slotBinds,
        };
    }

    private static Transform3D ReadTransform(FileAccess file)
    {
        var values = new float[12];
        for (int i = 0; i < 12; i++)
        {
            values[i] = file.GetFloat();
        }

        return ReadTransform(values, 0);
    }

    private static Transform3D ReadTransform(float[] values, int offset)
    {
        // The 12 floats use Transform3D text order: 9 basis floats row-major
        // followed by the origin. Axis N is basis column N.
        return new Transform3D(
            new Vector3(values[offset], values[offset + 3], values[offset + 6]),
            new Vector3(values[offset + 1], values[offset + 4], values[offset + 7]),
            new Vector3(values[offset + 2], values[offset + 5], values[offset + 8]),
            new Vector3(values[offset + 9], values[offset + 10], values[offset + 11]));
    }

    private static string ReadString(FileAccess file)
    {
        uint length = file.Get32();
        if (length == 0)
        {
            return string.Empty;
        }

        return System.Text.Encoding.UTF8.GetString(file.GetBuffer(length));
    }

    private static Material BuildMaterial(string texturePath, string blendEffect, bool transparent, uint version)
    {
        var material = new StandardMaterial3D
        {
            // Version-4 indices wind clockwise for Godot; older artifacts kept
            // the source's counter-clockwise order and must render double-sided.
            CullMode = version >= 4
                ? BaseMaterial3D.CullModeEnum.Back
                : BaseMaterial3D.CullModeEnum.Disabled,
        };
        Texture2D? texture = LoadTexture(texturePath);
        if (texture != null)
        {
            material.AlbedoTexture = texture;
        }

        if (transparent && blendEffect.Contains("ADD"))
        {
            material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
        else if (transparent)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
        else
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.5f;
        }

        return material;
    }

    private static string ResolveAssetPath(string assetPath, string skinMeshPath)
    {
        const string originalRoot = "res://assets/";
        if (!assetPath.StartsWith(originalRoot, System.StringComparison.Ordinal))
        {
            return assetPath;
        }

        int assetsMarker = skinMeshPath.LastIndexOf("/assets/", System.StringComparison.Ordinal);
        return assetsMarker < 0
            ? assetPath
            : skinMeshPath[..(assetsMarker + "/assets/".Length)] + assetPath[originalRoot.Length..];
    }

    private static Texture2D? LoadTexture(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (TextureCache.TryGetValue(path, out Texture2D? cached))
        {
            return cached;
        }

        if (SarnautCore.ConvertedSceneLoader.IsLoadable(path))
        {
            Texture2D? imported = ResourceLoader.Load<Texture2D>(path);
            TextureCache[path] = imported;
            return imported;
        }

        if (!FileAccess.FileExists(path))
        {
            TextureCache[path] = null;
            return null;
        }

        string absolutePath = ProjectSettings.GlobalizePath(path);
        Image? image = Image.LoadFromFile(absolutePath);
        Texture2D? texture = image == null || image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
        TextureCache[path] = texture;
        return texture;
    }
}
