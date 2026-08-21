using Godot;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// Builds the scene node for an entity the registry has not seen before, and
/// parents it under the zone's entity root.
/// </summary>
public sealed class ZoneEntityVisualFactory(
    Node3D entityRoot,
    EntityModelCatalog catalog) : IEntityVisualFactory
{
    /// <summary>How many entities got a native model rather than a capsule.</summary>
    public int ModelCount { get; private set; }

    /// <summary>How many entities fell back to a labelled capsule.</summary>
    public int CapsuleCount { get; private set; }

    public IEntityVisual Create(SampledEntity sample)
    {
        var visual = new NetworkEntityVisual
        {
            Name = $"Entity_{sample.EntityId}",
            EntityId = sample.EntityId,
        };
        entityRoot.AddChild(visual);
        visual.Bind(sample, catalog);
        if (visual.HasModel)
        {
            ModelCount++;
        }
        else
        {
            CapsuleCount++;
        }

        return visual;
    }
}
