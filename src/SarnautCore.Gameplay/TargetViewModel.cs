using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

/// <summary>The entity fields the target frame needs, independent of Godot.</summary>
public readonly record struct EntityHudSnapshot(
    ulong EntityId,
    string NameKey,
    string ContentId,
    int Level,
    int Health,
    int MaxHealth,
    bool Alive);

/// <summary>Selected-target state for the target frame and addon event source.</summary>
public sealed class TargetViewModel
{
    public ulong EntityId { get; private set; }

    public string NameKey { get; private set; } = string.Empty;

    public string ContentId { get; private set; } = string.Empty;

    public int Level { get; private set; }

    public int Health { get; private set; }

    public int MaxHealth { get; private set; }

    public bool Alive { get; private set; }

    public bool HasTarget => EntityId != 0;

    public double HealthFraction => MaxHealth <= 0 ? 0 : Math.Clamp((double)Health / MaxHealth, 0, 1);

    public event Action? Changed;

    public event Action<ulong>? TargetDied;

    public void Select(EntityHudSnapshot snapshot)
    {
        int level = Math.Max(0, snapshot.Level);
        int health = Math.Max(0, snapshot.Health);
        int maxHealth = Math.Max(0, snapshot.MaxHealth);
        bool alive = snapshot.Alive && health > 0;
        if (EntityId == snapshot.EntityId
            && NameKey == snapshot.NameKey
            && ContentId == snapshot.ContentId
            && Level == level
            && Health == health
            && MaxHealth == maxHealth
            && Alive == alive)
        {
            return;
        }

        EntityId = snapshot.EntityId;
        NameKey = snapshot.NameKey ?? string.Empty;
        ContentId = snapshot.ContentId ?? string.Empty;
        Level = level;
        Health = health;
        MaxHealth = maxHealth;
        Alive = alive;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (!HasTarget)
        {
            return;
        }

        EntityId = 0;
        NameKey = string.Empty;
        ContentId = string.Empty;
        Level = 0;
        Health = 0;
        MaxHealth = 0;
        Alive = false;
        Changed?.Invoke();
    }

    public void Apply(CombatEvent combatEvent)
    {
        ArgumentNullException.ThrowIfNull(combatEvent);
        if (!HasTarget || combatEvent.TargetId != EntityId || combatEvent.Rejection != AbilityRejection.None)
        {
            return;
        }

        bool wasAlive = Alive;
        Health = Math.Max(0, combatEvent.TargetHealth);
        MaxHealth = Math.Max(0, combatEvent.TargetMaxHealth);
        Alive = Health > 0 && !combatEvent.KillingBlow;
        Changed?.Invoke();
        if (wasAlive && !Alive)
        {
            TargetDied?.Invoke(EntityId);
        }
    }

    public void Apply(DeathEvent deathEvent)
    {
        ArgumentNullException.ThrowIfNull(deathEvent);
        if (!HasTarget || deathEvent.VictimEntityId != EntityId || !Alive)
        {
            return;
        }

        Health = 0;
        Alive = false;
        Changed?.Invoke();
        TargetDied?.Invoke(EntityId);
    }
}
