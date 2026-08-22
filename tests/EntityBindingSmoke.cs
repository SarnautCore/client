using System;
using System.Collections.Generic;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// Drives the server-entity path end to end without a shard: bind snapshots to
/// visuals, pick one with a ray, and retire one the snapshot stopped carrying.
/// </summary>
/// <remarks>
/// It passes with or without the converted assets, and says which it ran with.
/// That is the point of the check: the capsule fallback is a supported way to
/// run the client and has to keep working, and the only way to know it does is
/// to run the same scene both ways.
/// </remarks>
public partial class EntityBindingSmoke : Node3D
{
    private const ulong LocalEntityId = 1;
    private const ulong NearEntityId = 2;
    private const ulong FarEntityId = 3;

    private readonly List<string> _failures = [];

    public override async void _Ready()
    {
        var catalog = new EntityModelCatalog();
        string contentId = "mob.inst-league1.rat.rat1-1";
        var entityRoot = new Node3D { Name = "NetworkEntities" };
        AddChild(entityRoot);

        var factory = new ZoneEntityVisualFactory(entityRoot, catalog);
        var registry = new EntityRegistry(factory);
        var timeline = new SnapshotTimeline();

        timeline.Add(Batch(10, contentId), 1.0);
        timeline.Add(Batch(11, contentId), 1.05);
        registry.Reconcile(timeline.OpenWindow(1.1, 0.05), LocalEntityId);
        registry.Reconcile(timeline.OpenWindow(1.12, 0.05), LocalEntityId);

        Expect(registry.Count == 2, $"registry tracks 2 entities, not {registry.Count}");
        Expect(entityRoot.GetChildCount() == 2, $"one visual per entity, not {entityRoot.GetChildCount()}");
        Expect(!registry.TryGet(LocalEntityId, out _), "the local player has no registry visual");
        Expect(registry.HasLocalSample, "the local player's sample is published");

        Expect(
            HasNoLegacyOverhead(registry, NearEntityId),
            "replicated entity has no legacy world-space name or health presentation");

        // Aim at the middle of the near entity's pick capsule. Creature scales
        // in the source tree vary by an order of magnitude, so a fixed eye
        // height would be a test of how tall a rat is.
        float eyeHeight = registry.TryGet(NearEntityId, out TrackedEntity? near)
            && near.Visual is NetworkEntityVisual nearVisual
            ? nearVisual.PickCentreHeight
            : 1.0f;
        var camera = new Camera3D { Position = new Vector3(0, eyeHeight, 20), Current = true };
        AddChild(camera);
        var picker = new ZoneEntityPicker();
        AddChild(picker);

        // The bodies only exist in the physics space once a physics frame has
        // run, so a ray cast before one hits nothing and proves nothing.
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        Vector2 centre = GetViewport().GetVisibleRect().Size * 0.5f;
        bool hit = picker.TryPick(camera, centre, out ulong pickKey)
            && registry.TryGetByPickKey(pickKey, out TrackedEntity? picked)
            && picked.EntityId == NearEntityId;
        Expect(
            hit,
            $"a ray through the middle of the screen picks entity {NearEntityId} "
            + $"(viewport {GetViewport().GetVisibleRect().Size}, origin {camera.ProjectRayOrigin(centre)}, "
            + $"normal {camera.ProjectRayNormal(centre)}, collided {picker.IsColliding()})");

        // The far entity leaves the snapshot: its visual has to go with it.
        timeline.Add(Batch(12, contentId, dropEntityId: FarEntityId), 1.15);
        registry.Reconcile(timeline.OpenWindow(1.2, 0.05), LocalEntityId);
        Expect(registry.Count == 1, $"a dropped entity is retired, {registry.Count} left");
        Expect(!registry.TryGet(FarEntityId, out _), "the dropped entity is gone from the registry");

        bool passed = _failures.Count == 0;
        GD.Print(
            $"ENTITY_BINDING_SMOKE manifest={(catalog.IsAvailable ? catalog.EntryCount : 0)} "
            + $"models={factory.ModelCount} capsules={factory.CapsuleCount} "
            + $"native_overtip_only=1 picked={hit} result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"ENTITY_BINDING_SMOKE {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private static bool HasNoLegacyOverhead(EntityRegistry registry, ulong entityId)
    {
        if (!registry.TryGet(entityId, out TrackedEntity? tracked)
            || tracked.Visual is not NetworkEntityVisual visual)
        {
            return false;
        }

        return visual.GetNodeOrNull<Node3D>("Overhead") is null
            && visual.GetNodeOrNull<Label3D>("Overhead/Nameplate") is null
            && visual.GetNodeOrNull<MeshInstance3D>("Overhead/HealthBar") is null;
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }

    private static SnapshotBatch Batch(ulong tick, string contentId, ulong dropEntityId = 0)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkCount = 1 };
        batch.Entities.Add(Entity(LocalEntityId, contentId, 0, 0, EntityKind.Player));
        batch.Entities.Add(Entity(NearEntityId, contentId, 0, 10, EntityKind.Npc));
        if (dropEntityId != FarEntityId)
        {
            batch.Entities.Add(Entity(FarEntityId, contentId, 50, 10, EntityKind.Npc));
        }

        return batch;
    }

    private static EntitySnapshot Entity(ulong entityId, string contentId, float x, float y, EntityKind kind)
    {
        return new EntitySnapshot
        {
            EntityId = entityId,
            Kind = kind,
            // The shard's axes: x and y span the ground, z is up.
            Position = new Vec3 { X = x, Y = y, Z = 0 },
            Velocity = new Vec3(),
            Heading = 0,
            AnimationState = AnimationState.Idle,
            ContentId = contentId,
            NameKey = "Rat1_1_Name.txt",
            Level = 2,
            Faction = "faction.wild",
            Health = 30,
            MaxHealth = 60,
            Alive = true,
        };
    }
}
