// Ported from ao-godot-converter runtime templates (Apache-2.0).
using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds a skinned ArrayMesh from the converter's .skmesh artifact and binds
/// it to the exported Skeleton3D using the authored inverse-bind palette.
/// </summary>
[GlobalClass]
public partial class ConvertedSkinnedMesh : Node3D
{
    private static readonly Dictionary<string, LoadedSkinMesh> MeshCache = new();
    private static readonly Dictionary<string, Texture2D?> TextureCache = new();

    public override void _Ready()
    {
        string skinMeshPath = (string)GetMeta("allods_skin_mesh", "");
        if (string.IsNullOrEmpty(skinMeshPath))
        {
            return;
        }

        var skeleton = GetNodeOrNull<Skeleton3D>("Skeleton3D");
        if (skeleton == null)
        {
            GD.PushWarning($"ConvertedSkinnedMesh: no Skeleton3D under {GetPath()}");
            return;
        }

        var loaded = LoadSkinMesh(skinMeshPath);
        if (loaded == null)
        {
            return;
        }

        var instance = new MeshInstance3D
        {
            Mesh = loaded.DefaultMesh,
            Skin = BuildSkin(skeleton),
        };
        instance.SetMeta("allods_source_mesh", loaded.FullMesh);
        instance.SetMeta("allods_surfaces", loaded.SurfacesJson);
        skeleton.AddChild(instance);
        GetNodeOrNull<MeshInstance3D>("Mesh")?.Hide();
    }

    /// <summary>Loads the default-visible surfaces for tools such as the asset viewer.</summary>
    public static ArrayMesh? LoadPreviewMesh(string path)
    {
        return LoadSkinMesh(path)?.DefaultMesh;
    }

    private sealed class LoadedSkinMesh
    {
        public required ArrayMesh FullMesh { get; init; }
        public required ArrayMesh DefaultMesh { get; init; }
        public required string SurfacesJson { get; init; }
    }

    private static Skin BuildSkin(Skeleton3D skeleton)
    {
        var visual = (int[])skeleton.GetMeta("allods_visual_bones", System.Array.Empty<int>());
        var binds = (float[])skeleton.GetMeta("allods_skin_binds", System.Array.Empty<float>());
        var skin = new Skin();
        for (int slot = 0; slot < visual.Length; slot++)
        {
            Transform3D bind;
            if (binds.Length == visual.Length * 12)
            {
                int offset = slot * 12;
                bind = new Transform3D(
                    new Vector3(binds[offset], binds[offset + 3], binds[offset + 6]),
                    new Vector3(binds[offset + 1], binds[offset + 4], binds[offset + 7]),
                    new Vector3(binds[offset + 2], binds[offset + 5], binds[offset + 8]),
                    new Vector3(binds[offset + 9], binds[offset + 10], binds[offset + 11]));
            }
            else
            {
                bind = skeleton.GetBoneGlobalRest(visual[slot]).AffineInverse();
            }

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
        if (version != 2 && version != 3)
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

            var material = BuildMaterial(texturePath, blendEffect, transparent);
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

        var loaded = new LoadedSkinMesh
        {
            FullMesh = full,
            DefaultMesh = defaults.GetSurfaceCount() > 0 ? defaults : full,
            SurfacesJson = Json.Stringify(surfaces),
        };
        MeshCache[path] = loaded;
        return loaded;
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

    private static Material BuildMaterial(string texturePath, string blendEffect, bool transparent)
    {
        var material = new StandardMaterial3D
        {
            CullMode = BaseMaterial3D.CullModeEnum.Back,
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
