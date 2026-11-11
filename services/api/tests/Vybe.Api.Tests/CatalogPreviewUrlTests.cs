using System.Net;
using Vybe.Api.Content;

namespace Vybe.Api.Tests;

/// <summary>
/// Spec §5 <c>previewUrl</c>, §9: through HTTP, the catalog and the inventory return the pipeline thumbnail of each
/// item (manifest <c>thumbnailUrl</c> under <c>Content:AssetBaseUrl</c>), never the old placeholder path.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CatalogPreviewUrlTests(ApiFactory api)
{
    private static readonly ContentCatalog Content = new(new ContentOptions { Root = ApiFactory.RepoRoot });

    // Avatar items and room items (docs/design/p1-my-room.md) both have pipeline thumbnails.
    private static readonly Dictionary<string, string?> Thumbnails =
        Content.Catalog.Items.Select(i => (i.Sku, i.ThumbnailUrl)).Concat(Content.Room.Items.Select(i => (i.Sku, i.ThumbnailUrl)))
            .ToDictionary(i => i.Sku, i => i.ThumbnailUrl);

    private static void AssertThumbnail(System.Text.Json.JsonElement item)
    {
        var thumbnail = Thumbnails[item.GetProperty("sku").GetString()!];
        Assert.NotNull(thumbnail);
        var url = item.GetProperty("previewUrl").GetString()!;
        Assert.EndsWith(thumbnail["/assets".Length..], url, StringComparison.Ordinal);
        Assert.DoesNotContain("/preview.png", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogAndInventory_ReturnThePipelineThumbnails()
    {
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var page = await Contract.AssertAsync(await anonymous.GetAsync("/api/v1/catalog/items?limit=100"), HttpStatusCode.OK, "CatalogPage");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 30, $"{items.Count} catalog items");
        Assert.All(items, AssertThumbnail);

        // The starter items of a new guest.
        var player = await Player.CreateGuestAsync(api);
        var inventory = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/inventory"), HttpStatusCode.OK, "Inventory");
        var owned = inventory.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, owned.Count);
        Assert.All(owned, AssertThumbnail);
    }
}
