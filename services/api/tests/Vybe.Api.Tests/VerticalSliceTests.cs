using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Vybe.Api.Tests;

/// <summary>Spec §14 E01, E02, E05, E06 at the API level; every response is checked against the contract.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class VerticalSliceTests(ApiFactory api)
{
    [Fact]
    public async Task E01_GuestAvatarVerifiedGameWallet()
    {
        var player = await Player.CreateGuestAsync(api);
        var me = await player.MeAsync();
        Assert.Equal("guest", me.GetProperty("kind").GetString());
        Assert.Equal(0, me.GetProperty("walletBalance").GetInt64());

        // Starter items are owned and equipped.
        var inventory = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/inventory"), HttpStatusCode.OK, "Inventory");
        Assert.Equal(3, inventory.GetProperty("items").GetArrayLength());
        var avatar = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, avatar.GetProperty("equipped").GetProperty("top").ValueKind);

        var updated = await Contract.AssertAsync(await player.Http.PutAsJsonAsync("/api/v1/me/avatar", new
        {
            bodyPresetId = "body_b",
            skinToneId = "skin_05",
            facePresetId = "face_02",
            hairPresetId = "hair_curly",
            hairColorId = "blue",
            expectedRevision = avatar.GetProperty("revision").GetInt64(),
        }), HttpStatusCode.OK, "AvatarUpdated");
        Assert.Equal(1, updated.GetProperty("revision").GetInt64());

        var run = await player.PlayPerfectRunAsync();
        Assert.Equal(22_900, run.GetProperty("score").GetInt32());
        var rewards = run.GetProperty("rewards");
        Assert.Equal(150, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(80, rewards.GetProperty("xp").GetInt32());

        me = await player.MeAsync();
        Assert.Equal(150, me.GetProperty("walletBalance").GetInt64());
        Assert.Equal(80, me.GetProperty("xpTotal").GetInt64());

        var ledger = await Contract.AssertAsync(
            await player.Http.GetAsync("/api/v1/me/wallet/transactions"), HttpStatusCode.OK, "LedgerPage");
        var entry = Assert.Single(ledger.GetProperty("items").EnumerateArray());
        Assert.Equal("game_reward", entry.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task E02_PurchaseEquipAndReloadPersists()
    {
        var player = await Player.CreateGuestAsync(api);
        for (var run = 0; run < 3; run++)
        {
            await player.PlayPerfectRunAsync(); // 3 × 150 coins, 240 XP → level 2 (+100 coins)
        }

        var me = await player.MeAsync();
        Assert.Equal(2, me.GetProperty("level").GetInt32());
        Assert.Equal(550, me.GetProperty("walletBalance").GetInt64());

        var catalog = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/catalog/items?slot=top"), HttpStatusCode.OK, "CatalogPage");
        Assert.All(catalog.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("price").GetInt64() > 0));

        var jersey = await player.CatalogItemIdAsync("top_court_jersey");
        var purchase = await Contract.AssertAsync(await player.PurchaseAsync(jersey), HttpStatusCode.OK, "Purchase");
        Assert.Equal(50, purchase.GetProperty("balanceAfter").GetInt64()); // jersey 500

        var avatar = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        await Contract.AssertAsync(await player.Http.PostAsJsonAsync("/api/v1/me/equipment", new
        {
            slot = "top",
            itemId = jersey,
            expectedAvatarRevision = avatar.GetProperty("revision").GetInt64(),
        }), HttpStatusCode.OK, "EquipmentUpdated");

        // "Reload": a fresh request reads the persisted state.
        var reloaded = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        Assert.Equal(jersey, reloaded.GetProperty("equipped").GetProperty("top").GetGuid());
        var catalogAfter = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/catalog/items?slot=top"), HttpStatusCode.OK, "CatalogPage");
        Assert.Contains(catalogAfter.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetGuid() == jersey && i.GetProperty("owned").GetBoolean());
    }

    [Fact]
    public async Task E05_DuplicateSubmit_NeverPaysTwice()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        var events = Player.PerfectEvents(config, seed, 20);
        var key = Guid.NewGuid();

        var first = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events, key), HttpStatusCode.OK, "SubmitResult");
        var sameKey = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events, key), HttpStatusCode.OK, "SubmitResult");
        var otherKey = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, events), HttpStatusCode.OK, "SubmitResult");

        Assert.True(JsonElement.DeepEquals(first, sameKey), "the stored response must be replayed"); // jsonb may reorder keys
        Assert.Equal("verified", otherKey.GetProperty("status").GetString()); // canonical result
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == player.Id && t.LedgerKey == $"game:{sessionId}"));
        var me = await player.MeAsync();
        Assert.Equal(otherKey.GetProperty("rewards").GetProperty("coins").GetInt64(), me.GetProperty("walletBalance").GetInt64());
    }

    [Fact]
    public async Task E06_ConcurrentPurchases_NeverOverdraw()
    {
        var player = await Player.CreateGuestAsync(api);
        for (var run = 0; run < 3; run++)
        {
            await player.PlayPerfectRunAsync(); // 550 coins with the level 2 bonus
        }

        // 500 + 450 > 550: at most one can succeed, whatever the interleaving.
        var jersey = await player.CatalogItemIdAsync("top_court_jersey");
        var hair = await player.CatalogItemIdAsync("hair_high_top");
        var responses = await Task.WhenAll(player.PurchaseAsync(jersey), player.PurchaseAsync(hair));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var failed = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
        await Contract.AssertProblemAsync(failed, HttpStatusCode.Conflict, "insufficient_funds");
        var balance = (await player.MeAsync()).GetProperty("walletBalance").GetInt64();
        Assert.True(balance is 50 or 100, $"unexpected balance {balance}");
    }
}
