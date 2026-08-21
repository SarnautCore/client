using Godot;

namespace SarnautCore;

/// <summary>
/// The render-layer contract that separates the zone's two lighting models.
/// </summary>
/// <remarks>
/// <para>
/// Retail shades a zone with two disjoint models: statics carry their offline
/// bake (lightvrt vertex colors, terrain lightmaps) and ignore runtime lights,
/// while dynamic entities and unbaked props are lit at runtime by the zone's
/// ambient plus its point lights. Here the runtime point lights — authored
/// <c>client.Scene.LightComponent</c> placements and the sampled-bake fill
/// (<see cref="SampledEntityLight"/>) — carry a light cull mask of
/// <see cref="ReceiverLayerMask"/> only, and only runtime-lit meshes join that
/// layer. Baked statics are demoted back to <see cref="BakedOnlyLayers"/> when
/// their bake applies, so they can never be double-lit: their unshaded baked
/// materials already ignore lights, and the mask keeps the separation explicit
/// even for a static whose material later becomes shaded.
/// </para>
/// <para>
/// Every converted mesh starts as a receiver because at materialization time
/// nobody knows yet whether a bake covers it; <see cref="BakedStaticLighting"/>
/// owns the demotion. The camera's default cull mask sees all layers, so layer
/// membership never affects visibility, only which lights reach a surface.
/// </para>
/// </remarks>
public static class DynamicEntityLighting
{
    /// <summary>The render layer (bit 2) reserved for runtime-lit receivers.</summary>
    public const uint ReceiverLayerMask = 1u << 1;

    /// <summary>World layer plus the receiver layer: dynamic entities and unbaked props.</summary>
    public const uint ReceiverLayers = 1u | ReceiverLayerMask;

    /// <summary>The default world layer: baked statics that runtime lights must skip.</summary>
    public const uint BakedOnlyLayers = 1u;

    /// <summary>Marks one visual instance as a runtime-lit receiver.</summary>
    public static void MarkReceiver(VisualInstance3D instance)
    {
        instance.Layers |= ReceiverLayerMask;
    }

    /// <summary>Marks every mesh below <paramref name="root"/> as a runtime-lit receiver.</summary>
    public static void MarkReceivers(Node root)
    {
        if (root is VisualInstance3D instance)
        {
            MarkReceiver(instance);
        }

        foreach (Node child in root.GetChildren())
        {
            MarkReceivers(child);
        }
    }
}
