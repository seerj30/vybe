using Vybe.Api.Content;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec v1.4 §8 (decision 5): every priced catalog item is inside the price band of its rarity.</summary>
public sealed class CatalogPriceTests
{
    [Fact]
    public void PricedItems_AreInsideTheirRarityBand()
    {
        var catalog = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot }).Catalog;

        Assert.All(catalog.Items.Where(i => !i.Starter), item =>
        {
            var (min, max) = item.Rarity switch
            {
                Rarity.Common => (300L, 800L),
                Rarity.Rare => (1_500L, 3_000L),
                Rarity.Epic => (6_000L, 10_000L),
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.Rarity, "unknown rarity"),
            };
            Assert.True(item.Price >= min && item.Price <= max, $"{item.Sku}: {item.Rarity} price {item.Price} not in {min}–{max}");
        });
        Assert.All(catalog.Items.Where(i => i.Starter), item => Assert.Equal(0, item.Price));
    }
}
