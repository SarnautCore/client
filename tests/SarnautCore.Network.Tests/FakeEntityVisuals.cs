using SarnautCore.Networking;

namespace SarnautCore.Network.Tests;

/// <summary>
/// A stand-in for the scene nodes the zone builds, so the registry can be tested
/// without a Godot runtime.
/// </summary>
internal sealed class FakeEntityVisual(ulong pickKey) : IEntityVisual
{
    public ulong PickKey { get; } = pickKey;

    public SampledEntity Applied { get; private set; }

    public int ApplyCount { get; private set; }

    public bool Retired { get; private set; }

    public void Apply(SampledEntity sample)
    {
        Applied = sample;
        ApplyCount++;
    }

    public void Retire() => Retired = true;
}

internal sealed class FakeEntityVisualFactory : IEntityVisualFactory
{
    private readonly Dictionary<ulong, FakeEntityVisual> _created = [];

    public int CreateCount { get; private set; }

    /// <summary>
    /// Pick keys are the entity id shifted, so a test can tell the two id spaces
    /// apart the way the scene's node ids are told apart from entity ids.
    /// </summary>
    public static ulong PickKeyOf(ulong entityId) => entityId + 10_000;

    public FakeEntityVisual this[ulong entityId] => _created[entityId];

    public IEntityVisual Create(SampledEntity sample)
    {
        CreateCount++;
        var visual = new FakeEntityVisual(PickKeyOf(sample.EntityId));
        _created[sample.EntityId] = visual;
        return visual;
    }
}
