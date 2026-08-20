using System;
using System.Collections.Generic;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// One server entity in the scene: its model, the body a pick ray hits, and the
/// nameplate and health bar above it.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing that draws a replicated entity. The zone loader used
/// to spawn a second, offline copy of every NPC from the converted map
/// placements, so a mob the shard was simulating stood next to a mob that was
/// only a decoration and neither one could be told from the other. The loader
/// no longer does that; what a snapshot describes is what is on screen.
/// </para>
/// <para>
/// A capsule is not a failure state. Without the converted assets — CI, a fresh
/// checkout — every entity is a labelled capsule at the right place with the
/// right name and health, which is a usable client.
/// </para>
/// </remarks>
public sealed partial class NetworkEntityVisual : Node3D, IEntityVisual
{
    /// <summary>The physics layer entity bodies live on, so a pick ray can ask for only them.</summary>
    public const uint EntityCollisionLayer = 1 << 1;

    private const float CapsuleRadius = 0.42f;
    private const float CapsuleHeight = 1.8f;
    private const float HealthBarWidth = 1.1f;
    private const float HealthBarHeight = 0.1f;

    private static readonly Color PlayerColor = new("55c8e8");
    private static readonly Color NpcColor = new("e4a853");
    private static readonly Color DeadColor = new("6b6f75");
    private static readonly Color HealthColor = new("cf4f3e");
    private static readonly Color HealthBackdrop = new("14181d");
    private static readonly Color NameColor = new("f2f4f8");
    private static readonly Color TargetColor = new("ffd479");

    /// <summary>
    /// The health bar is one quad, filled in the fragment shader and turned to
    /// face the camera in the vertex shader.
    /// </summary>
    /// <remarks>
    /// Two quads with <c>BaseMaterial3D</c> billboarding would have been simpler
    /// to write and wrong to look at: that billboard mode keeps the node's world
    /// translation and replaces its basis, so a fill anchored to the left edge
    /// would swing around the bar as the mob turned. One quad has no left edge to
    /// lose, and it is also one draw call per entity instead of two.
    /// </remarks>
    private const string HealthBarShaderCode = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix;

        uniform float fill : hint_range(0.0, 1.0) = 1.0;
        uniform vec4 fill_color : source_color;
        uniform vec4 back_color : source_color;

        void vertex() {
            MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
                INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
        }

        void fragment() {
            vec4 colour = UV.x <= fill ? fill_color : back_color;
            ALBEDO = colour.rgb;
            ALPHA = colour.a;
        }
        """;

    /// <summary>One shader for every entity; only the uniforms differ.</summary>
    private static readonly Shader HealthBarShader = new() { Code = HealthBarShaderCode };

    private Area3D _body = null!;
    private Label3D _nameplate = null!;
    private MeshInstance3D _healthBar = null!;
    private ShaderMaterial _healthMaterial = null!;
    private ConvertedCharacter? _character;
    private MeshInstance3D? _capsule;
    private StandardMaterial3D? _capsuleMaterial;

    private string _nameText = string.Empty;
    private uint _shownLevel;
    private int _shownHealth = -1;
    private int _shownMaxHealth = -1;
    private bool _shownAlive = true;
    private bool _targeted;
    private float _height = CapsuleHeight;

    /// <summary>The entity this draws.</summary>
    public ulong EntityId { get; init; }

    /// <summary>True when a converted model loaded, false when this is a capsule.</summary>
    public bool HasModel { get; private set; }

    /// <summary>The pick capsule's radius, height, and the height of its centre.</summary>
    public float PickRadius { get; private set; }

    public float PickHeight { get; private set; }

    public float PickCentreHeight { get; private set; }

    /// <summary>
    /// The pick ray reports the <see cref="Area3D"/> it hit, so that is what the
    /// registry indexes this visual by.
    /// </summary>
    public ulong PickKey => _body.GetInstanceId();

    /// <summary>Whether this entity is the player's current target.</summary>
    public bool Targeted
    {
        get => _targeted;
        set
        {
            if (_targeted == value)
            {
                return;
            }

            _targeted = value;
            _nameplate.Modulate = value ? TargetColor : NameColor;
            _nameplate.OutlineSize = value ? 20 : 12;
        }
    }

    /// <summary>
    /// Fills in the visual for one entity: its model if the converted tree has
    /// one, and a capsule if it does not.
    /// </summary>
    /// <remarks>
    /// Called after the node is in the scene tree, because a converted character
    /// starts its idle animation as it loads and an <c>AnimationPlayer</c>
    /// outside the tree has nothing to play into.
    /// </remarks>
    public void Bind(SampledEntity sample, EntityModelCatalog catalog, string convertedRoot)
    {
        float scale = 1.0f;
        if (catalog.TryResolve(sample.ContentId, out EntityModel model))
        {
            var character = new ConvertedCharacter
            {
                Name = "Model",
                AutoLoad = false,
                LocomotionOnly = true,
                ShowPlaceholderOnFailure = false,
                ConvertedRoot = convertedRoot,
                CharacterScene = model.ScenePath,
                Scale = Vector3.One * model.Scale,
            };
            AddChild(character);
            if (character.LoadCharacter())
            {
                _character = character;
                HasModel = true;
                scale = model.Scale;
            }
            else
            {
                // The manifest named a scene the converted tree cannot build.
                // A capsule with the right name over it beats an empty patch of
                // ground, and the loader's failure list already names it.
                RemoveChild(character);
                character.QueueFree();
            }
        }

        if (!HasModel)
        {
            AddCapsule(sample);
        }

        AddBody(scale);
        AddOverhead(sample);
    }

    private void AddCapsule(SampledEntity sample)
    {
        _capsuleMaterial = new StandardMaterial3D
        {
            AlbedoColor = sample.Kind == EntityKind.Npc ? NpcColor : PlayerColor,
            Roughness = 0.75f,
        };
        _capsule = new MeshInstance3D
        {
            Name = "Capsule",
            Mesh = new CapsuleMesh
            {
                Radius = CapsuleRadius,
                Height = CapsuleHeight,
                Material = _capsuleMaterial,
            },
            Position = new Vector3(0, CapsuleHeight * 0.5f, 0),
        };
        AddChild(_capsule);
    }

    private void AddBody(float scale)
    {
        MeasurePickShape(scale, out float radius, out float height, out float centre);
        PickRadius = radius;
        PickHeight = height;
        PickCentreHeight = centre;
        _height = centre + (height * 0.5f);

        _body = new Area3D
        {
            Name = "PickBody",
            // Nothing collides with entities yet — movement is the shard's — so
            // this is an Area3D that only exists to be hit by a pick ray.
            CollisionLayer = EntityCollisionLayer,
            CollisionMask = 0,
            Monitoring = false,
            Monitorable = false,
            InputRayPickable = true,
        };
        _body.AddChild(new CollisionShape3D
        {
            Name = "Shape",
            Shape = new CapsuleShape3D { Radius = radius, Height = height },
            Position = new Vector3(0, centre, 0),
        });
        AddChild(_body);
    }

    /// <summary>
    /// Sizes the capsule a pick ray has to hit.
    /// </summary>
    /// <remarks>
    /// It is measured from the model rather than assumed, because creature
    /// scales in the source tree run from a rat at a fifth of a man to a boss
    /// several times his size: one nominal capsule would be unclickable on one
    /// and a wall in front of the other. The floors keep the smallest creature
    /// worth aiming at with a mouse.
    /// </remarks>
    private void MeasurePickShape(float scale, out float radius, out float height, out float centre)
    {
        const float MinimumRadius = 0.3f;
        const float MinimumHeight = 0.9f;

        radius = CapsuleRadius * scale;
        height = CapsuleHeight * scale;
        centre = height * 0.5f;
        if (_character?.Model is Node3D model && TryMeasureBounds(model, out Aabb bounds))
        {
            radius = Math.Max(Math.Max(bounds.Size.X, bounds.Size.Z) * 0.5f, MinimumRadius);
            height = Math.Max(bounds.Size.Y, MinimumHeight);
            centre = Math.Max(bounds.Position.Y + (bounds.Size.Y * 0.5f), height * 0.5f);
        }
        else
        {
            radius = Math.Max(radius, MinimumRadius);
            height = Math.Max(height, MinimumHeight);
            centre = Math.Max(centre, height * 0.5f);
        }

        // A capsule is two hemispheres and a cylinder; a radius past half its
        // height is not a shape Godot can build.
        radius = Math.Min(radius, height * 0.5f);
    }

    private bool TryMeasureBounds(Node3D model, out Aabb bounds)
    {
        bounds = default;
        bool any = false;
        Transform3D toLocal = GlobalTransform.AffineInverse();
        var pending = new Stack<Node>();
        pending.Push(model);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            foreach (Node child in node.GetChildren())
            {
                pending.Push(child);
            }

            if (node is not VisualInstance3D visual || node is Label3D)
            {
                continue;
            }

            Aabb local = TransformAabb(toLocal * visual.GlobalTransform, visual.GetAabb());
            bounds = any ? bounds.Merge(local) : local;
            any = true;
        }

        return any && bounds.Size.Y > 0.05f;
    }

    /// <summary>
    /// The axis-aligned box around a transformed box, corner by corner. Godot
    /// has this for the editor but not on the scripting side.
    /// </summary>
    private static Aabb TransformAabb(Transform3D transform, Aabb box)
    {
        Vector3 min = transform * box.Position;
        Vector3 max = min;
        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 point = transform * (box.Position + new Vector3(
                (corner & 1) == 0 ? 0 : box.Size.X,
                (corner & 2) == 0 ? 0 : box.Size.Y,
                (corner & 4) == 0 ? 0 : box.Size.Z));
            min = min.Min(point);
            max = max.Max(point);
        }

        return new Aabb(min, max - min);
    }

    private void AddOverhead(SampledEntity sample)
    {
        var overhead = new Node3D { Name = "Overhead", Position = new Vector3(0, _height + 0.45f, 0) };
        AddChild(overhead);

        _nameplate = new Label3D
        {
            Name = "Nameplate",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            PixelSize = 0.0045f,
            FontSize = 28,
            OutlineSize = 8,
            Modulate = NameColor,
            OutlineModulate = new Color(0, 0, 0, 0.85f),
            RenderPriority = 2,
            Position = new Vector3(0, 0.18f, 0),
        };
        FadeOutBeyondNameplateRange(_nameplate);
        overhead.AddChild(_nameplate);

        _healthMaterial = new ShaderMaterial { Shader = HealthBarShader };
        _healthMaterial.SetShaderParameter("fill_color", HealthColor);
        _healthMaterial.SetShaderParameter("back_color", HealthBackdrop);
        _healthBar = new MeshInstance3D
        {
            Name = "HealthBar",
            Mesh = new QuadMesh { Size = new Vector2(HealthBarWidth, HealthBarHeight) },
            MaterialOverride = _healthMaterial,
        };
        FadeOutBeyondNameplateRange(_healthBar);
        overhead.AddChild(_healthBar);

        Apply(sample);
    }

    /// <summary>
    /// Nameplates are for the crowd around the player, not for the far side of
    /// the instance: at a few hundred subscribed entities the far ones are noise
    /// and overdraw.
    /// </summary>
    private static void FadeOutBeyondNameplateRange(GeometryInstance3D instance)
    {
        instance.VisibilityRangeEnd = 55.0f;
        instance.VisibilityRangeEndMargin = 8.0f;
        instance.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;
    }

    public void Apply(SampledEntity sample)
    {
        Position = new Vector3(sample.X, sample.Z, sample.Y);
        Rotation = new Vector3(0, sample.Heading, 0);
        _character?.SetMoving(sample.AnimationState == AnimationState.Moving);
        ApplyIdentity(sample);
        ApplyHealth(sample);
    }

    private void ApplyIdentity(SampledEntity sample)
    {
        if (_nameText.Length > 0 && _shownLevel == sample.Level)
        {
            return;
        }

        string resolved = EntityNaming.Resolve(sample.NameKey, TranslationServer.Singleton.Translate(sample.NameKey));
        if (resolved.Length == 0)
        {
            // No name key at all: an entity id is still an answer, and a blank
            // plate over a capsule is not.
            resolved = $"Entity {sample.EntityId}";
        }

        _nameText = resolved;
        _shownLevel = sample.Level;
        _nameplate.Text = sample.Level > 0 ? $"{resolved}  ({sample.Level})" : resolved;
    }

    private void ApplyHealth(SampledEntity sample)
    {
        if (_shownHealth == sample.Health && _shownMaxHealth == sample.MaxHealth && _shownAlive == sample.Alive)
        {
            return;
        }

        _shownHealth = sample.Health;
        _shownMaxHealth = sample.MaxHealth;
        _shownAlive = sample.Alive;

        float fraction = sample.MaxHealth <= 0
            ? 0
            : Math.Clamp((float)sample.Health / sample.MaxHealth, 0, 1);
        _healthBar.Visible = sample.MaxHealth > 0 && sample.Alive;
        _healthMaterial.SetShaderParameter("fill", fraction);

        if (_capsuleMaterial != null)
        {
            _capsuleMaterial.AlbedoColor = sample.Alive
                ? (sample.Kind == EntityKind.Npc ? NpcColor : PlayerColor)
                : DeadColor;
        }

        // A corpse lies where it fell rather than standing there dead.
        if (_character?.Model != null)
        {
            _character.Model.RotationDegrees = new Vector3(sample.Alive ? 0 : -80, _character.ModelYawDegrees, 0);
        }
    }

    public void Retire() => QueueFree();
}
