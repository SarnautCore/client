using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

public sealed record AbilityDefinition(string Id, string NameKey, string IconPath, double CooldownSeconds = 1);

public sealed record AbilityUseRequest(string AbilityId, ulong TargetEntityId, ulong ClientTick);

/// <summary>Known abilities, activation requests, and their shared M2 cooldown.</summary>
public sealed class AbilityBarViewModel
{
    private readonly AbilityDefinition[] _abilities;
    private double _cooldownDurationSeconds = 1;

    public AbilityBarViewModel(IEnumerable<AbilityDefinition> abilities)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        _abilities = abilities.Where(ability => !string.IsNullOrWhiteSpace(ability.Id)).ToArray();
    }

    public IReadOnlyList<AbilityDefinition> Abilities => _abilities;

    public double GlobalCooldownRemainingSeconds { get; private set; }

    public bool IsOnGlobalCooldown => GlobalCooldownRemainingSeconds > 0;

    public double CooldownFraction => _cooldownDurationSeconds <= 0
        ? 0
        : Math.Clamp(GlobalCooldownRemainingSeconds / _cooldownDurationSeconds, 0, 1);

    public event Action? Changed;

    public event Action<AbilityUseRequest>? AbilityRequested;

    public bool TryRequestUse(int slotIndex, ulong targetEntityId, ulong clientTick = 0)
    {
        if (slotIndex < 0 || slotIndex >= _abilities.Length || IsOnGlobalCooldown)
        {
            return false;
        }

        AbilityRequested?.Invoke(new AbilityUseRequest(_abilities[slotIndex].Id, targetEntityId, clientTick));
        return true;
    }

    public void Apply(CombatEvent combatEvent, ulong ownEntityId)
    {
        ArgumentNullException.ThrowIfNull(combatEvent);
        if (combatEvent.CasterId != ownEntityId || combatEvent.Rejection != AbilityRejection.None)
        {
            return;
        }

        AbilityDefinition? ability = string.IsNullOrWhiteSpace(combatEvent.AbilityId)
            ? _abilities.FirstOrDefault()
            : _abilities.FirstOrDefault(candidate => candidate.Id == combatEvent.AbilityId);
        if (ability is null)
        {
            return;
        }

        _cooldownDurationSeconds = Math.Max(0, ability.CooldownSeconds);
        GlobalCooldownRemainingSeconds = _cooldownDurationSeconds;
        Changed?.Invoke();
    }

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || !IsOnGlobalCooldown)
        {
            return;
        }

        GlobalCooldownRemainingSeconds = Math.Max(0, GlobalCooldownRemainingSeconds - deltaSeconds);
        Changed?.Invoke();
    }
}
