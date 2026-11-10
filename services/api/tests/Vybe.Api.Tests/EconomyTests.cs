using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Catalog, purchase, equipment, avatar and reward caps (spec §5, §7, §8).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class EconomyTests(ApiFactory api)
{
    [Fact]
    public async Task Purchase_ErrorCodes()
    {
        var player = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync();
        await player.PlayPerfectRunAsync(); // 400 coins, level 2

        await Contract.AssertProblemAsync(
            await player.PurchaseAsync(Guid.NewGuid()), HttpStatusCode.UnprocessableEntity, "item_unavailable");
        await Contract.AssertProblemAsync(
            await player.PurchaseAsync(await player.CatalogItemIdAsync("top_starter_tee")), HttpStatusCode.UnprocessableEntity, "item_not_purchasable");
        await Contract.AssertProblemAsync(
            await player.PurchaseAsync(await player.CatalogItemIdAsync("top_neon_hoodie")), HttpStatusCode.Forbidden, "level_too_low");

        var sweatband = await player.CatalogItemIdAsync("acc_sweatband");
        await Contract.AssertAsync(await player.PurchaseAsync(sweatband), HttpStatusCode.OK, "Purchase");
        await Contract.AssertProblemAsync(await player.PurchaseAsync(sweatband), HttpStatusCode.Conflict, "already_owned");

        await using var db = api.CreateDbContext();
        await db.Players.Where(p => p.Id == player.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlayerStatus.Suspended));
        await Contract.AssertProblemAsync(
            await player.PurchaseAsync(await player.CatalogItemIdAsync("emote_swish")), HttpStatusCode.Forbidden, "account_suspended");
    }

    [Fact]
    public async Task Equipment_ErrorCodes()
    {
        var player = await Player.CreateGuestAsync(api);
        var sweatband = await player.CatalogItemIdAsync("acc_sweatband");
        await GrantAsync(player.Id, sweatband);
        var tee = await player.CatalogItemIdAsync("top_starter_tee");
        var revision = await AvatarRevisionAsync(player);

        await Contract.AssertProblemAsync(
            await EquipAsync(player, new { slot = "hair", itemId = await player.CatalogItemIdAsync("hair_high_top"), expectedAvatarRevision = revision }),
            HttpStatusCode.Forbidden, "item_not_owned");
        await Contract.AssertProblemAsync(
            await EquipAsync(player, new { slot = "bottom", itemId = tee, expectedAvatarRevision = revision }),
            HttpStatusCode.UnprocessableEntity, "incompatible_item");
        await Contract.AssertProblemAsync(
            await EquipAsync(player, new { slot = "accessory", itemId = sweatband, expectedAvatarRevision = revision }),
            HttpStatusCode.UnprocessableEntity, "validation_failed");

        await Contract.AssertAsync(
            await EquipAsync(player, new { slot = "accessory", itemId = sweatband, attachIndex = 0, expectedAvatarRevision = revision }),
            HttpStatusCode.OK, "EquipmentUpdated");
        await Contract.AssertProblemAsync(
            await EquipAsync(player, new { slot = "accessory", itemId = sweatband, attachIndex = 1, expectedAvatarRevision = revision + 1 }),
            HttpStatusCode.UnprocessableEntity, "item_already_equipped");
        await Contract.AssertProblemAsync(
            await EquipAsync(player, new { slot = "top", itemId = (Guid?)null, expectedAvatarRevision = revision }),
            HttpStatusCode.Conflict, "revision_conflict");
    }

    [Fact]
    public async Task Avatar_InvalidPreset_StaleRevision_AndIncompatibleBodyChange()
    {
        var player = await Player.CreateGuestAsync(api);
        var hoodie = await player.CatalogItemIdAsync("top_neon_hoodie"); // body_a only
        await GrantAsync(player.Id, hoodie);
        var revision = await AvatarRevisionAsync(player);
        await Contract.AssertAsync(
            await EquipAsync(player, new { slot = "top", itemId = hoodie, expectedAvatarRevision = revision }), HttpStatusCode.OK, "EquipmentUpdated");

        object Avatar(string body, long expected) => new
        {
            bodyPresetId = body,
            skinToneId = "skin_01",
            facePresetId = "face_01",
            hairPresetId = "hair_short",
            hairColorId = "black",
            expectedRevision = expected,
        };

        await Contract.AssertProblemAsync(
            await player.Http.PutAsJsonAsync("/api/v1/me/avatar", Avatar("body_z", revision + 1)), HttpStatusCode.UnprocessableEntity, "invalid_preset");
        await Contract.AssertProblemAsync(
            await player.Http.PutAsJsonAsync("/api/v1/me/avatar", Avatar("body_a", revision)), HttpStatusCode.Conflict, "revision_conflict");
        var incompatible = await Contract.AssertProblemAsync(
            await player.Http.PutAsJsonAsync("/api/v1/me/avatar", Avatar("body_b", revision + 1)), HttpStatusCode.Conflict, "incompatible_equipment");
        Assert.Equal(hoodie, Assert.Single(incompatible.GetProperty("itemIds").EnumerateArray()).GetGuid());
    }

    [Fact]
    public async Task Catalog_PagesWithCursor_AndRejectsUnknownSlot()
    {
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var response = await anonymous.GetAsync($"/api/v1/catalog/items?limit=3{(cursor is null ? "" : $"&cursor={cursor}")}");
            Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString().Split(", ").Order()));
            var page = await Contract.AssertAsync(response, HttpStatusCode.OK, "CatalogPage");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        var priced = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot }).Catalog.Items.Count(i => !i.Starter);
        Assert.Equal(priced, seen.Count); // every catalog item except the free starters
        Assert.Equal(seen.Count, seen.Distinct().Count());
        await Contract.AssertProblemAsync(
            await anonymous.GetAsync("/api/v1/catalog/items?slot=cape"), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    [Fact]
    public async Task Rewards_MinimumPlay_AndDailyRunTiers()
    {
        var player = await Player.CreateGuestAsync(api);

        // 4 shots < minShotsForRewards (5): verified, no reward, not counted.
        var shortRun = await PlayAsync(player, shots: 4);
        Assert.Equal(0, shortRun.GetProperty("rewards").GetProperty("coins").GetInt64());

        // 5 perfect shots = 900 points → base 30 coins, 20 XP. Runs 1–10 pay 30, run 11 pays floor(30/4) = 7.
        for (var run = 1; run <= 10; run++)
        {
            Assert.Equal(30, (await PlayAsync(player, shots: 5)).GetProperty("rewards").GetProperty("coins").GetInt64());
        }

        Assert.Equal(7, (await PlayAsync(player, shots: 5)).GetProperty("rewards").GetProperty("coins").GetInt64());
    }

    private async Task<JsonElement> PlayAsync(Player player, int shots)
    {
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));
        return await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, shots)), HttpStatusCode.OK, "SubmitResult");
    }

    private static async Task<HttpResponseMessage> EquipAsync(Player player, object body) =>
        await player.Http.PostAsJsonAsync("/api/v1/me/equipment", body);

    private static async Task<long> AvatarRevisionAsync(Player player) =>
        (await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar"))
        .GetProperty("revision").GetInt64();

    private async Task GrantAsync(Guid playerId, Guid itemId)
    {
        await using var db = api.CreateDbContext();
        db.PlayerInventory.Add(new PlayerInventoryItem { PlayerId = playerId, ItemId = itemId, Source = InventorySource.AdminGrant });
        await db.SaveChangesAsync();
    }
}
