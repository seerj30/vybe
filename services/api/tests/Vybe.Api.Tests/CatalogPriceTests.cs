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

    /// <summary>Spec v1.4 §8: at least 30 priced items at beta; the 3D asset fields of §9 do not break the loader.</summary>
    [Fact]
    public void Catalog_HasTheBetaItemCount_AndKeepsTheVersion1Skus()
    {
        var catalog = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot }).Catalog;

        Assert.True(catalog.CatalogVersion >= 2, $"catalog version {catalog.CatalogVersion}");
        Assert.True(catalog.Items.Count(i => !i.Starter) >= 30, "fewer than 30 priced items");
        Assert.Equal(catalog.Items.Length, catalog.Items.Select(i => i.Sku).Distinct(StringComparer.Ordinal).Count());
        string[] version1 =
        [
            "top_starter_tee", "bottom_starter_shorts", "shoes_starter_kicks", "top_court_jersey", "top_neon_hoodie",
            "hair_high_top", "shoes_gold_high", "acc_sweatband", "acc_chain", "emote_swish",
        ];
        Assert.All(version1, sku => Assert.Contains(catalog.Items, i => i.Sku == sku));
    }

    /// <summary>Spec §9: the item picture is the pipeline thumbnail under Content:AssetBaseUrl, not a placeholder path.</summary>
    [Fact]
    public void PreviewUrl_UsesThePipelineThumbnail()
    {
        var content = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot, AssetBaseUrl = "https://vybe.example/assets/" });
        var item = content.Catalog.Items.First(i => i.Sku == "hair_high_top");

        Assert.NotNull(item.ThumbnailUrl);
        Assert.StartsWith("/assets/3d/thumbs/", item.ThumbnailUrl, StringComparison.Ordinal);
        Assert.Equal("https://vybe.example" + item.ThumbnailUrl, content.PreviewUrl(item.Sku, item.AssetKey));
        Assert.Equal("https://vybe.example/assets/unknown/preview.png", content.PreviewUrl("no_such_sku", "unknown"));
    }
}
