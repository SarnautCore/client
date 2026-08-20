using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class InventoryViewModelTests
{
    [Fact]
    public void Add_merges_stacks_and_slot_move_preserves_the_stack()
    {
        var inventory = new InventoryViewModel(3, _ => 20);

        Assert.True(inventory.TryAdd("item.trash-hoof", 12, out _));
        Assert.True(inventory.TryAdd("item.trash-hoof", 25, out _));

        Assert.Equal(20, inventory.Slots[0]!.Count);
        Assert.Equal(17, inventory.Slots[1]!.Count);

        Assert.True(inventory.TryMove(1, 2));

        Assert.Null(inventory.Slots[1]);
        Assert.Equal(17, inventory.Slots[2]!.Count);
    }

    [Fact]
    public void Capacity_rejection_does_not_partially_mutate_the_bag()
    {
        var inventory = new InventoryViewModel(2, _ => 20);
        inventory.TryAdd("item.trash-hoof", 20, out _);
        inventory.TryAdd("item.trash-hoof", 17, out _);

        bool added = inventory.TryAdd("item.trash-hoof", 4, out InventoryRejection rejection);

        Assert.False(added);
        Assert.Equal(InventoryRejection.Capacity, rejection);
        Assert.Equal(20, inventory.Slots[0]!.Count);
        Assert.Equal(17, inventory.Slots[1]!.Count);
    }
}
