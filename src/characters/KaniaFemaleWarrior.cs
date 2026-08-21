using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>The authored appearance for the curated League warrior option.</summary>
public partial class KaniaFemaleWarrior : Node3D
{
    private const string ConvertedRoot = ConvertedCharacter.DefaultConvertedRoot;
    private const string BaseScene = "Characters/Kania_female/KaniaFemale.(VisObjectTemplate).scene.tscn";
    private const string AppearanceId = "kania-female-warrior-starting-kit";

    private static readonly string[] VisibleSurfaces =
    [
        "facial_6",
        "hair_3",
        "face_6",
        "teeth_0",
        "body_0",
        "hands_0",
        "ears_0",
        "legs_0",
        "boots_0",
        "arms_0",
        "torso_0",
        "skirt_0",
    ];

    private static readonly string[] BodyAtlasSurfaces =
    [
        "face_6",
        "teeth_0",
        "body_0",
        "hands_0",
        "ears_0",
        "legs_0",
        "boots_0",
        "arms_0",
        "torso_0",
    ];

    private static readonly (string Path, Vector2I Destination)[] AtlasPatches =
    [
        ("Items/TextureComponents/Foot/Leather_A_10ElfPaladinBoots_FT_U_D.png", new Vector2I(256, 0)),
        ("Items/TextureComponents/LegUpper/Leather_A_07KaniaWarriorArmor_LU_F_D.png", new Vector2I(256, 256)),
        ("Items/TextureComponents/Hand/Leather_A_07KaniaWarriorArmor_HN_F_U.png", new Vector2I(0, 288)),
        ("Items/TextureComponents/ArmLower/Leather_A_07KaniaWarriorArmor_AL_F_D.png", new Vector2I(0, 320)),
        ("Items/TextureComponents/TorsoLower/Leather_A_07KaniaWarriorArmor_TL_F_D.png", new Vector2I(256, 320)),
        ("Items/TextureComponents/ArmUpper/Leather_A_07KaniaWarriorArmor_AU_F_D.png", new Vector2I(0, 384)),
        ("Items/TextureComponents/TorsoUpper/Leather_A_07KaniaWarriorArmor_TU_F_D.png", new Vector2I(256, 384)),
    ];

    public override void _Ready()
    {
        PackedScene? scene = ConvertedSceneLoader.Load(
            ConvertedRoot,
            BaseScene,
            out string error,
            enableRuntimeSkinnedMesh: true);
        if (scene?.Instantiate<Node3D>() is not { } model)
        {
            GD.PushWarning($"KaniaFemaleWarrior: {error}");
            return;
        }

        model.Name = "KaniaFemaleBase";
        AddChild(model);

        var skinnedMesh = model as ConvertedSkinnedMesh;
        Texture2D? atlas = BuildStartingKitAtlas();
        Texture2D? skirt = LoadTexture("Items/ObjectComponents/Skirt/Armor_Leather_A_07KaniaWarrior.png");
        if (skinnedMesh == null || atlas == null || skirt == null)
        {
            GD.PushWarning("KaniaFemaleWarrior: converted starting-kit appearance is incomplete.");
            return;
        }

        var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        foreach (string surface in BodyAtlasSurfaces)
        {
            textures[surface] = atlas;
        }

        textures["skirt_0"] = skirt;
        if (!skinnedMesh.ApplyAppearance(VisibleSurfaces, textures, AppearanceId))
        {
            GD.PushWarning($"KaniaFemaleWarrior: appearance '{AppearanceId}' did not apply; the base body stays undressed.");
            return;
        }

        Skeleton3D? skeleton = FindDescendant<Skeleton3D>(model);
        if (skeleton == null
            || !Equip(skeleton, "Mainhand", "Slot_Hand_R",
                "Items/ObjectComponents/Weapon/Mace_1H_Club_A_01/Mace_1H_Club_A_01.(VisObjectTemplate).scene.tscn")
            || !Equip(skeleton, "Offhand", "Slot_Shield_Hand",
                "Items/ObjectComponents/Shield/Shield_1H_Simple_A_01/Shield_1H_Simple_A_01.(VisObjectTemplate).scene.tscn"))
        {
            GD.PushWarning("KaniaFemaleWarrior: converted starting weapons are incomplete.");
        }
    }

    private static Texture2D? BuildStartingKitAtlas()
    {
        Image? source = LoadBlittableImage("Characters/Kania_female/KaniaFemaleSkin00.png");
        if (source == null)
        {
            return null;
        }

        Image atlas = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            source.HasMipmaps(),
            source.GetFormat(),
            source.GetData());
        foreach ((string path, Vector2I destination) in AtlasPatches)
        {
            Image? patch = LoadBlittableImage(path);
            if (patch == null)
            {
                return null;
            }

            atlas.BlitRect(patch, new Rect2I(Vector2I.Zero, patch.GetSize()), destination);
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// Loads a texture as an image both sides of a blit can share. Editor
    /// imports arrive VRAM-compressed with mipmaps, and BlitRect refuses
    /// compressed or mismatched formats.
    /// </summary>
    private static Image? LoadBlittableImage(string relativePath)
    {
        Image? image = LoadTexture(relativePath)?.GetImage();
        if (image == null || image.IsEmpty())
        {
            return null;
        }

        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            return null;
        }

        image.Convert(Image.Format.Rgba8);
        image.ClearMipmaps();
        return image;
    }

    private static bool Equip(Skeleton3D skeleton, string name, string boneName, string scenePath)
    {
        PackedScene? scene = ConvertedSceneLoader.Load(ConvertedRoot, scenePath, out string error);
        if (scene?.Instantiate<Node3D>() is not { } item)
        {
            GD.PushWarning($"KaniaFemaleWarrior: {error}");
            return false;
        }

        var attachment = new BoneAttachment3D { Name = name, BoneName = boneName };
        skeleton.AddChild(attachment);
        attachment.AddChild(item);
        return true;
    }

    private static Texture2D? LoadTexture(string relativePath)
    {
        string path = $"{ConvertedRoot}/assets/{relativePath}";
        return UpscaledTextures.Load(path)
            ?? (ConvertedSceneLoader.IsLoadable(path, "Texture2D")
                ? ResourceLoader.Load<Texture2D>(path)
                : null);
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
}

