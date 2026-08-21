using Godot;

namespace SarnautCore;

/// <summary>
/// The render-layer contract that separates the zone's two lighting models.
/// </summary>
/// <remarks>
/// <para>
/// Native statics with unshaded baked materials stay on
/// <see cref="BakedOnlyLayers"/> and ignore runtime lights. Dynamic entities and
/// shaded props join <see cref="ReceiverLayerMask"/> so sampled entity lighting
/// can reach them without double-lighting baked surfaces.
/// </para>
/// <para>
/// Native scene setup chooses this membership from each mesh material's shading
/// mode. The camera's default cull mask sees all layers, so layer membership
/// never affects visibility, only which lights reach a surface.
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
