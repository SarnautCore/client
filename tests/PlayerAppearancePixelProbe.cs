using System;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>Renders the curated warrior under neutral light for pixel review.</summary>
public partial class PlayerAppearancePixelProbe : Node3D
{
    public override async void _Ready()
    {
        var world = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("596779"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("c7d4e8"),
                AmbientLightEnergy = 0.9f,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Bg,
            },
        };
        AddChild(world);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-35, -25, 0),
            LightColor = new Color("fff0dc"),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
        });

        var fill = new OmniLight3D
        {
            Position = new Vector3(-2, 2, -2),
            LightColor = new Color("b9d7ff"),
            LightEnergy = 4.0f,
            OmniRange = 8.0f,
        };
        AddChild(fill);

        var character = new CharacterRig
        {
            Name = "PlayerCharacter",
            AutoLoad = false,
            ShowPlaceholderOnFailure = false,
        };
        var catalog = new EntityModelCatalog();
        PlayerCharacterModel.Apply(character, catalog, CuratedOption());
        AddChild(character);
        bool loaded = character.Load();

        var camera = new Camera3D { Current = true, Fov = 34 };
        AddChild(camera);
        Aim(camera, new Vector3(2.4f, 1.35f, -3.2f));

        var floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(6, 6) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("38414b"),
                Roughness = 0.9f,
            },
        };
        AddChild(floor);

        for (int frame = 0; frame < 12; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        // The baked rig faces +Z, so the first camera (on -Z) sees the
        // model's back and the second (on +Z) its front.
        string prefix = System.Environment.GetEnvironmentVariable("SARNAUT_APPEARANCE_PROBE") ?? string.Empty;
        Error back = prefix.Length == 0
            ? Error.InvalidParameter
            : GetViewport().GetTexture().GetImage().SavePng($"{prefix}-back.png");

        Aim(camera, new Vector3(-2.4f, 1.35f, 3.2f));
        for (int frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        Error front = prefix.Length == 0
            ? Error.InvalidParameter
            : GetViewport().GetTexture().GetImage().SavePng($"{prefix}-front.png");

        bool hasMace = NativeCharacterLodContract.HasAttachment(
            character.Model,
            "Attach_Mace_1H_Club_A_01",
            "Slot_Hand_R");
        bool hasShield = NativeCharacterLodContract.HasAttachment(
            character.Model,
            "Attach_Shield_1H_Simple_A_01",
            "Slot_Shield_Hand");
        bool passed = loaded && hasMace && hasShield && front == Error.Ok && back == Error.Ok;
        GD.Print(
            $"PLAYER_APPEARANCE_PIXEL loaded={loaded} scene=\"{character.ScenePath}\" "
            + $"mace={hasMace} shield={hasShield} front={front} back={back} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static void Aim(Camera3D camera, Vector3 position)
    {
        camera.Position = position;
        camera.LookAtFromPosition(position, new Vector3(0, 1.05f, 0), Vector3.Up);
    }

    private static ChargenOption CuratedOption() => new(
        "chargen.league.warrior",
        "race.kania",
        "class.warrior",
        "female",
        "faction.league",
        "M2.Chargen.LeagueWarrior.Name",
        "M2.Chargen.LeagueWarrior.Description",
        "unused-by-native-runtime",
        "zone.inst-league1",
        1,
        113.0f,
        170.5f,
        156.293f);
}
