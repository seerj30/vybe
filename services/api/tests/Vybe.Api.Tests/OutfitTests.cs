using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Spec §4 saved_outfits, §5 outfits, §15 Equip ("saved outfit fails gracefully"); E08 for outfits.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OutfitTests(ApiFactory api)
{
    [Fact]
    public async Task SaveListRenameApplyDelete()
    {
        var player = await Player.CreateGuestAsync(api);
        var saved = await SaveAsync(player, "Court day");
        var id = (await Contract.AssertAsync(saved, HttpStatusCode.Created, "OutfitCreated")).GetProperty("id").GetGuid();
        var starterTop = await player.CatalogItemIdAsync("top_starter_tee");

        var list = await ListAsync(player);
        var outfit = Assert.Single(list.GetProperty("outfits").EnumerateArray());
        Assert.Equal("Court day", outfit.GetProperty("name").GetString());
        Assert.True(outfit.GetProperty("valid").GetBoolean());
        Assert.Equal(starterTop, outfit.GetProperty("equipped").GetProperty("top").GetGuid());

        var renamed = await Contract.AssertAsync(
            await player.Http.PutAsJsonAsync($"/api/v1/me/outfits/{id}", new { name = "Game night" }), HttpStatusCode.OK, "Outfit");
        Assert.Equal("Game night", renamed.GetProperty("name").GetString());

        // Unequip the top, then apply the outfit: the top is back, with a new revision.
        var revision = (await player.MeAsync()).GetProperty("avatarRevision").GetInt64();
        var unequipped = await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/me/equipment", new { slot = "top", itemId = (Guid?)null, expectedAvatarRevision = revision }),
            HttpStatusCode.OK, "EquipmentUpdated");
        revision = unequipped.GetProperty("revision").GetInt64();
        await Contract.AssertProblemAsync(await ApplyAsync(player, id, revision - 1), HttpStatusCode.Conflict, "revision_conflict");
        var applied = await Contract.AssertAsync(await ApplyAsync(player, id, revision), HttpStatusCode.OK, "EquipmentUpdated");
        Assert.Equal(starterTop, applied.GetProperty("equipped").GetProperty("top").GetGuid());
        Assert.True(applied.GetProperty("revision").GetInt64() > revision);
        var avatar = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        Assert.Equal(applied.GetProperty("revision").GetInt64(), avatar.GetProperty("revision").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent, (await player.Http.DeleteAsync($"/api/v1/me/outfits/{id}")).StatusCode);
        await Contract.AssertProblemAsync(await player.Http.DeleteAsync($"/api/v1/me/outfits/{id}"), HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await ListAsync(player)).GetProperty("outfits").EnumerateArray());
    }

    [Fact]
    public async Task Limit_IsThree_EvenForConcurrentSaves_AndAFreedSlotIsReused()
    {
        var player = await Player.CreateGuestAsync(api);
        var responses = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => SaveAsync(player, $"Outfit {i}")));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await Contract.AssertProblemAsync(rejected, HttpStatusCode.Conflict, "outfit_limit_reached");
        }

        var first = (await ListAsync(player)).GetProperty("outfits")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await player.Http.DeleteAsync($"/api/v1/me/outfits/{first}")).StatusCode);
        await Contract.AssertAsync(await SaveAsync(player, "Again"), HttpStatusCode.Created, "OutfitCreated");
        Assert.Equal(3, (await ListAsync(player)).GetProperty("outfits").GetArrayLength());
    }

    [Fact]
    public async Task InvalidItems_AreListed_AndApplyChangesNothing()
    {
        var player = await Player.CreateGuestAsync(api);
        var notOwned = await player.CatalogItemIdAsync("top_court_jersey");
        var wrongSlot = await player.CatalogItemIdAsync("bottom_starter_shorts");
        var shoes = await player.CatalogItemIdAsync("shoes_starter_kicks");
        var outfitId = Guid.NewGuid();
        await using (var db = api.CreateDbContext())
        {
            db.SavedOutfits.Add(new SavedOutfit
            {
                Id = outfitId,
                PlayerId = player.Id,
                Slot = 1,
                Name = "Broken",
                EquippedJson = JsonSerializer.Serialize(new
                {
                    hair = (Guid?)null,
                    top = notOwned,
                    bottom = (Guid?)null,
                    shoes = wrongSlot,
                    emote = (Guid?)null,
                    accessories = new Guid?[] { null, null },
                }),
            });
            await db.SaveChangesAsync();
        }

        var outfit = Assert.Single((await ListAsync(player)).GetProperty("outfits").EnumerateArray());
        Assert.False(outfit.GetProperty("valid").GetBoolean());
        Assert.Equal(new[] { notOwned, wrongSlot }.Order(), outfit.GetProperty("invalidItemIds").EnumerateArray().Select(e => e.GetGuid()).Order());

        var before = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        var problem = await Contract.AssertProblemAsync(
            await ApplyAsync(player, outfitId, before.GetProperty("revision").GetInt64()), HttpStatusCode.UnprocessableEntity, "outfit_items_invalid");
        Assert.Equal(new[] { notOwned, wrongSlot }.Order(), problem.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).Order());
        var after = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/avatar"), HttpStatusCode.OK, "Avatar");
        Assert.Equal(before.ToString(), after.ToString());
        Assert.Equal(shoes, after.GetProperty("equipped").GetProperty("shoes").GetGuid());
    }

    [Fact]
    public async Task E08_AnotherPlayersOutfit_IsNotFound()
    {
        var owner = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var id = (await Contract.AssertAsync(await SaveAsync(owner, "Mine"), HttpStatusCode.Created, "OutfitCreated")).GetProperty("id").GetGuid();
        var revision = (await other.MeAsync()).GetProperty("avatarRevision").GetInt64();

        await Contract.AssertProblemAsync(
            await other.Http.PutAsJsonAsync($"/api/v1/me/outfits/{id}", new { name = "Stolen" }), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await other.Http.DeleteAsync($"/api/v1/me/outfits/{id}"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await ApplyAsync(other, id, revision), HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await ListAsync(other)).GetProperty("outfits").EnumerateArray());
        Assert.Equal("Mine", Assert.Single((await ListAsync(owner)).GetProperty("outfits").EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890123456789x")]
    public async Task InvalidName_IsValidationFailed(string name)
    {
        var player = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await SaveAsync(player, name), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await using var db = api.CreateDbContext();
        Assert.False(await db.SavedOutfits.AnyAsync(o => o.PlayerId == player.Id));
    }

    private static async Task<HttpResponseMessage> SaveAsync(Player player, string name) =>
        await player.Http.PostAsJsonAsync("/api/v1/me/outfits", new { name });

    private static async Task<HttpResponseMessage> ApplyAsync(Player player, Guid id, long revision) =>
        await player.Http.PostAsJsonAsync($"/api/v1/me/outfits/{id}/apply", new { expectedAvatarRevision = revision });

    private static async Task<JsonElement> ListAsync(Player player) =>
        await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/outfits"), HttpStatusCode.OK, "OutfitList");
}
