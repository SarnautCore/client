using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class DamageNumberPoolViewModelTests
{
    [Fact]
    public void Expired_damage_number_returns_to_the_pool()
    {
        var pool = new DamageNumberPoolViewModel(capacity: 2, lifetimeSeconds: 1.2);

        DamageNumberViewModel first = pool.Spawn(42, 20, critical: false);
        pool.Spawn(43, 7, critical: true);

        Assert.Equal(2, pool.ActiveCount);

        pool.Advance(1.21);

        Assert.Equal(0, pool.ActiveCount);
        Assert.False(first.Active);

        DamageNumberViewModel reused = pool.Spawn(99, 11, critical: false);

        Assert.Same(first, reused);
        Assert.Equal((ulong)99, reused.EntityId);
        Assert.Equal(11, reused.Amount);
    }

    [Fact]
    public void Full_pool_recycles_the_oldest_number()
    {
        var pool = new DamageNumberPoolViewModel(capacity: 1, lifetimeSeconds: 1);
        DamageNumberViewModel first = pool.Spawn(1, 3, critical: false);

        pool.Advance(0.2);
        DamageNumberViewModel recycled = pool.Spawn(2, 5, critical: true);

        Assert.Same(first, recycled);
        Assert.Equal((ulong)2, recycled.EntityId);
        Assert.True(recycled.Critical);
        Assert.Equal(1, pool.ActiveCount);
    }
}
