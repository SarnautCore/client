using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

public enum DeathFeedbackKind
{
    None,
    TargetDefeated,
    PlayerDied,
    Respawned,
}

/// <summary>Short-lived death and respawn notices.</summary>
public sealed class DeathFeedbackViewModel
{
    private readonly HashSet<ulong> _deadEntities = [];
    private readonly double _lifetimeSeconds;

    public DeathFeedbackViewModel(double lifetimeSeconds = 2.5)
    {
        if (lifetimeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
        }

        _lifetimeSeconds = lifetimeSeconds;
    }

    public DeathFeedbackKind Kind { get; private set; }

    public ulong EntityId { get; private set; }

    public double RemainingSeconds { get; private set; }

    public bool Visible => Kind != DeathFeedbackKind.None && RemainingSeconds > 0;

    public event Action? Changed;

    public void Apply(DeathEvent deathEvent, ulong ownEntityId)
    {
        ArgumentNullException.ThrowIfNull(deathEvent);
        if (deathEvent.VictimEntityId == 0)
        {
            return;
        }

        _deadEntities.Add(deathEvent.VictimEntityId);
        EntityId = deathEvent.VictimEntityId;
        Kind = deathEvent.VictimEntityId == ownEntityId
            ? DeathFeedbackKind.PlayerDied
            : DeathFeedbackKind.TargetDefeated;
        RemainingSeconds = _lifetimeSeconds;
        Changed?.Invoke();
    }

    public void Observe(EntityHudSnapshot snapshot)
    {
        if (!snapshot.Alive || !_deadEntities.Remove(snapshot.EntityId))
        {
            return;
        }

        EntityId = snapshot.EntityId;
        Kind = DeathFeedbackKind.Respawned;
        RemainingSeconds = _lifetimeSeconds;
        Changed?.Invoke();
    }

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || !Visible)
        {
            return;
        }

        RemainingSeconds = Math.Max(0, RemainingSeconds - deltaSeconds);
        if (RemainingSeconds == 0)
        {
            Kind = DeathFeedbackKind.None;
        }

        Changed?.Invoke();
    }
}
