using System;
using System.Collections.Generic;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// One server entity in the scene: its model and the body a pick ray hits.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing that draws a replicated entity. The zone loader used
/// to spawn a second, offline copy of every NPC from source map
/// placements, so a mob the shard was simulating stood next to a mob that was
/// only a decoration and neither one could be told from the other. The loader
/// no longer does that; what a snapshot describes is what is on screen.
/// </para>
/// <para>
/// A capsule is not a failure state. Without a matching native scene, every
/// entity is still represented at the authoritative position and remains pickable.
/// The native ContextOvertip HUD is the sole owner of names and health presentation.
/// </para>
/// </remarks>
public sealed partial class NetworkEntityVisual : Node3D, IEntityVisual
{
    /// <summary>The physics layer entity bodies live on, so a pick ray can ask for only them.</summary>
    public const uint EntityCollisionLayer = 1 << 1;

    private const float CapsuleRadius = 0.42f;
    private const float CapsuleHeight = 1.8f;

    private static readonly Color PlayerColor = new("55c8e8");
    private static readonly Color NpcColor = new("e4a853");
    private static readonly Color DeadColor = new("6b6f75");
    private Area3D _body = null!;
    private CharacterRig? _character;
    private MeshInstance3D? _capsule;
    private StandardMaterial3D? _capsuleMaterial;

    private int _shownHealth = -1;
    private int _shownMaxHealth = -1;
    private bool _shownAlive = true;
    private bool _targeted;
    private float _height = CapsuleHeight;

    /// <summary>The entity this draws.</summary>
    public ulong EntityId { get; init; }

    /// <summary>True when a native model loaded, false when this is a capsule.</summary>
    public bool HasModel { get; private set; }

    /// <summary>True when the native model is actively evaluating a clip.</summary>
    public bool IsAnimationPlaying => _character?.IsAnimationPlaying == true;

    /// <summary>The native clip currently selected for this entity.</summary>
    public string ActiveClip => _character?.ActiveClip ?? string.Empty;

    /// <summary>The pick capsule's radius, height, and the height of its centre.</summary>
    public float PickRadius { get; private set; }

    public float PickHeight { get; private set; }

    public float PickCentreHeight { get; private set; }

    /// <summary>World anchor consumed by the native ContextOvertip projection adapter.</summary>
    public Vector3 HudAnchorPosition => GlobalPosition + (Vector3.Up * (_height + 0.45f));

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
        }
    }

    /// <summary>
    /// Fills in the visual for one entity: its model if the native content has
    /// one, and a capsule if it does not.
    /// </summary>
    /// <remarks>
    /// Called after the node is in the scene tree, because a character
    /// starts its idle animation as it loads and an <c>AnimationPlayer</c>
    /// outside the tree has nothing to play into.
    /// </remarks>
    public void Bind(SampledEntity sample, EntityModelCatalog catalog)
    {
        float scale = 1.0f;
        if (catalog.TryResolve(sample.ContentId, out EntityModel model))
        {
            var character = new CharacterRig
            {
                Name = "Model",
                AutoLoad = false,
                ShowPlaceholderOnFailure = false,
                ScenePath = model.ScenePath,
            };
            AddChild(character);
            if (character.Load())
            {
                _character = character;
                HasModel = true;
                scale = 1.0f;
            }
            else
            {
                // The manifest named a scene the mounted content cannot load.
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
        Apply(sample);
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
        // Capsules are dynamic entities too: the zone's runtime point lights
        // reach them through the receiver layer.
        DynamicEntityLighting.MarkReceiver(_capsule);
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

            if (node is not VisualInstance3D visual)
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

    public void Apply(SampledEntity sample)
    {
        Position = OnlineCoordinateFrame.ToGodot(sample);
        Rotation = new Vector3(0, sample.Heading, 0);
        _character?.SetMoving(sample.AnimationState == AnimationState.Moving);
        ApplyHealth(sample);
    }

    public bool PlayAttack() => _character?.PlayAttack() ?? false;

    public bool PlayHit() => _character?.PlayHit() ?? false;

    public bool PlayDeath() => _character?.PlayDeath() ?? false;

    private void ApplyHealth(SampledEntity sample)
    {
        if (_shownHealth == sample.Health && _shownMaxHealth == sample.MaxHealth && _shownAlive == sample.Alive)
        {
            return;
        }

        _shownHealth = sample.Health;
        _shownMaxHealth = sample.MaxHealth;
        _shownAlive = sample.Alive;

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
