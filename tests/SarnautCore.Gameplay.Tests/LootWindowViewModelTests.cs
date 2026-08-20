using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class LootWindowViewModelTests
{
    [Fact]
    public void Offer_populates_take_removes_contents_and_empty_window_closes()
    {
        var loot = new LootWindowViewModel();
        ulong requestedCorpse = 0;
        loot.TakeRequested += corpse => requestedCorpse = corpse;

        var offer = new LootOffer { CorpseEntityId = 51, Money = 4 };
        offer.Items.Add(new LootItem { ItemId = "item.trash-hoof", Count = 2 });
        loot.Apply(offer);

        Assert.True(loot.IsOpen);
        Assert.Equal(4, loot.Money);
        Assert.Single(loot.Items);

        Assert.True(loot.RequestTake());
        Assert.Equal((ulong)51, requestedCorpse);

        var result = new LootResult
        {
            CorpseEntityId = 51,
            Money = 4,
            Refusal = LootRefusal.None,
        };
        result.Items.Add(new LootItem { ItemId = "item.trash-hoof", Count = 2 });
        loot.Apply(result);

        Assert.Empty(loot.Items);
        Assert.Equal(0, loot.Money);
        Assert.False(loot.IsOpen);
    }

    [Fact]
    public void Refused_take_keeps_the_offer_open()
    {
        var loot = new LootWindowViewModel();
        var offer = new LootOffer { CorpseEntityId = 51 };
        offer.Items.Add(new LootItem { ItemId = "item.heal-elixir", Count = 1 });
        loot.Apply(offer);

        loot.Apply(new LootResult
        {
            CorpseEntityId = 51,
            Refusal = LootRefusal.BagFull,
        });

        Assert.True(loot.IsOpen);
        Assert.Single(loot.Items);
        Assert.Equal(LootRefusal.BagFull, loot.LastRefusal);
    }
}
