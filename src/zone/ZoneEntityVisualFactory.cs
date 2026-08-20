using Godot;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// Builds the scene node for an entity the registry has not seen before, and
/// parents it under the zone's entity root.
/// </summary>
public sealed class ZoneEntityVisualFactory(
    Node3D entityRoot,
    EntityModelCatalog catalog,
    string convertedRoot) : IEntityVisualFactory
{
    /// <summary>How many entities got a converted model rather than a capsule.</summary>
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
        visual.Bind(sample, catalog, convertedRoot);
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
