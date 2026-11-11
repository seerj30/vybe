using System.Net;
using Vybe.Api.Content;

namespace Vybe.Api.Tests;

/// <summary>Spec §5 <c>previewUrl</c>, §9: item pictures are the pipeline thumbnails of the catalog manifest.</summary>
public sealed class CatalogPreviewUrlTests
{
    private static readonly ContentOptions Options = new() { Root = ApiFactory.RepoRoot };

    private static string Thumbnail(string sku) =>
        new ContentCatalog(Options).Catalog.Items.Single(i => i.Sku == sku).ThumbnailUrl!;

    [Fact]
    public void PreviewUrl_IsTheManifestThumbnailOnTheWebOrigin()
    {
        var content = new ContentCatalog(Options, "https://play.vybe.example/");

        var url = content.PreviewUrl("top_court_jersey", "tops/court_jersey");

        Assert.Equal($"https://play.vybe.example{Thumbnail("top_court_jersey")}", url);
        Assert.Matches(@"^https://play\.vybe\.example/assets/3d/thumbs/top_court_jersey\.[0-9a-f]{12}\.png$", url);
    }

    [Fact]
    public void AssetOrigin_OverridesTheWebOrigin()
    {
        var content = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot, AssetOrigin = "https://cdn.vybe.example" }, "https://play.vybe.example");

        Assert.StartsWith("https://cdn.vybe.example/assets/3d/thumbs/acc_chain.", content.PreviewUrl("acc_chain", "accessories/chain"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemThatIsNoLongerInTheFile_KeepsAStableAbsoluteUrl()
    {
        var content = new ContentCatalog(Options, "https://play.vybe.example");

        Assert.Equal("https://play.vybe.example/assets/tops/retired_tee/preview.png", content.PreviewUrl("top_retired_tee", "tops/retired_tee"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://assets.vybe.example")]
    public void InvalidAssetOrigin_StopsTheStart(string origin) =>
        Assert.Throws<InvalidOperationException>(() => new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot, AssetOrigin = origin }));
}

[Collection(ApiTestGroup.Name)]
public sealed class CatalogPreviewUrlApiTests(ApiFactory api)
{
    [Fact]
    public async Task CatalogAndInventory_ReturnTheManifestThumbnails()
    {
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var thumbnails = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot }).Catalog.Items
            .ToDictionary(i => i.Sku, i => i.ThumbnailUrl);

        var response = await anonymous.GetAsync("/api/v1/catalog/items?limit=100");
        var page = await Contract.AssertAsync(response, HttpStatusCode.OK, "CatalogPage");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(
            $"https://vybe.test{thumbnails[item.GetProperty("sku").GetString()!]}",
            item.GetProperty("previewUrl").GetString()));

        // The starter items of a new guest.
        var player = await Player.CreateGuestAsync(api);
        var inventory = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/inventory"), HttpStatusCode.OK, "Inventory");
        var owned = inventory.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, owned.Count);
        Assert.All(owned, item => Assert.Equal(
            $"https://vybe.test{thumbnails[item.GetProperty("sku").GetString()!]}",
            item.GetProperty("previewUrl").GetString()));
    }
}
