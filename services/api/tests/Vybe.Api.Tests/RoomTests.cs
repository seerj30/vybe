using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Content;
using Vybe.Api.Features;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>My Room (docs/design/p1-my-room.md): layout validation, idempotent saves and room visibility.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RoomTests(ApiFactory api)
{
    [Fact]
    public async Task NewPlayer_HasTheEmptyDefaultRoom()
    {
        var player = await Player.CreateGuestAsync(api);
        var room = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/room"), HttpStatusCode.OK, "Room");
        Assert.Equal(0, room.GetProperty("revision").GetInt64());
        Assert.Empty(room.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, room.GetProperty("wallItem").ValueKind);
        Assert.Equal(JsonValueKind.Null, room.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task BoughtFurniture_CanBePlacedSavedAndReadBack()
    {
        var player = await Player.CreateGuestAsync(api);
        await SetBalanceAsync(player, 5_000);
        var sofa = await player.CatalogItemIdAsync("furn_armchair"); // level 1, 2 × 2 cells
        await Contract.AssertAsync(await player.PurchaseAsync(sofa), HttpStatusCode.OK, "Purchase");
        var (rug, poster, walls, floor) = await GrantAsync(player, "furn_round_rug", "decor_hoops_poster", "walls_brick", "floor_oak");

        var saved = await Contract.AssertAsync(await SaveAsync(player, 0, walls, floor,
            Place(sofa, "floor", 2, 3, 90), Place(rug, "floor", 2, 3, 0), Place(poster, "wall_left", 6, 2, 0)), HttpStatusCode.OK, "Room");
        Assert.Equal(1, saved.GetProperty("revision").GetInt64());
        Assert.Equal("walls_brick", saved.GetProperty("wallItem").GetProperty("sku").GetString());
        Assert.Equal("floor_oak", saved.GetProperty("floorItem").GetProperty("sku").GetString());

        var read = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/room"), HttpStatusCode.OK, "Room");
        var items = read.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("furn_armchair", items[0].GetProperty("sku").GetString());
        Assert.Equal(90, items[0].GetProperty("rotation").GetInt32());
        Assert.Equal("wall_left", items[2].GetProperty("surface").GetString());
    }

    [Fact]
    public async Task SameLayout_IsIdempotent_AndAnotherLayoutWithAnOldRevisionConflicts()
    {
        var player = await Player.CreateGuestAsync(api);
        var (lamp, plant) = await GrantAsync(player, "furn_floor_lamp", "furn_potted_plant");
        var first = await Contract.AssertAsync(await SaveAsync(player, 0, null, null, Place(lamp, "floor", 0, 0, 0)), HttpStatusCode.OK, "Room");

        // A retry after a lost answer: the same body with the old revision returns the stored room unchanged.
        var replay = await Contract.AssertAsync(await SaveAsync(player, 0, null, null, Place(lamp, "floor", 0, 0, 0)), HttpStatusCode.OK, "Room");
        Assert.Equal(first.GetProperty("revision").GetInt64(), replay.GetProperty("revision").GetInt64());

        await Contract.AssertProblemAsync(await SaveAsync(player, 0, null, null, Place(plant, "floor", 1, 1, 0)), HttpStatusCode.Conflict, "revision_conflict");
        var second = await Contract.AssertAsync(await SaveAsync(player, 1, null, null, Place(plant, "floor", 1, 1, 0)), HttpStatusCode.OK, "Room");
        Assert.Equal(2, second.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task ConcurrentFirstSaves_OnlyOneWins()
    {
        var player = await Player.CreateGuestAsync(api);
        var (lamp, plant) = await GrantAsync(player, "furn_floor_lamp", "furn_potted_plant");
        var responses = await Task.WhenAll(
            SaveAsync(player, 0, null, null, Place(lamp, "floor", 0, 0, 0)),
            SaveAsync(player, 0, null, null, Place(plant, "floor", 4, 4, 0)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await Contract.AssertProblemAsync(responses.Single(r => r.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "revision_conflict");
    }

    [Fact]
    public async Task InvalidLayouts_AreRefused_AndChangeNothing()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sofa, bed, rug, rug2, poster, clock, jersey, walls) = await GrantAsync(player,
            "furn_sofa", "furn_bed", "furn_round_rug", "furn_striped_rug", "decor_hoops_poster", "decor_wall_clock", "top_court_jersey", "walls_mint");

        async Task Refused(HttpStatusCode status, string code, Guid? itemId, Guid? wallItemId, params object[] items)
        {
            var problem = await Contract.AssertProblemAsync(await SaveAsync(player, 0, wallItemId, null, items), status, code);
            if (itemId is { } id)
            {
                Assert.Equal(id, problem.GetProperty("itemId").GetGuid());
            }
        }

        // Bounds: the sofa is 4 × 2 cells; turned 90° it is 2 × 4.
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", sofa, null, Place(sofa, "floor", 5, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", sofa, null, Place(sofa, "floor", 0, 5, 90));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", sofa, null, Place(sofa, "floor", -1, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", poster, null, Place(poster, "wall_back", 0, 4, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", sofa, null, Place(sofa, "floor", int.MaxValue, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_out_of_bounds", sofa, null, Place(sofa, "floor", 0, int.MinValue, 0));
        // Overlap on the same layer and on the same wall; a rug under an object is fine, two rugs are not.
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_overlap", bed, null, Place(sofa, "floor", 0, 0, 0), Place(bed, "floor", 3, 1, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_overlap", rug2, null, Place(rug, "floor", 0, 0, 0), Place(rug2, "floor", 2, 2, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "placement_overlap", clock, null, Place(poster, "wall_back", 0, 0, 0), Place(clock, "wall_back", 1, 1, 0));
        // Wrong surface, an avatar item in the room, a wall finish as furniture, a floor item as the wall finish.
        await Refused(HttpStatusCode.UnprocessableEntity, "incompatible_item", poster, null, Place(poster, "floor", 0, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "incompatible_item", sofa, null, Place(sofa, "wall_back", 0, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "incompatible_item", jersey, null, Place(jersey, "floor", 0, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "incompatible_item", walls, null, Place(walls, "floor", 0, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "incompatible_item", sofa, sofa);
        // Shape: rotations, wall rotations, surfaces, duplicates, missing fields, the item limit.
        await Refused(HttpStatusCode.UnprocessableEntity, "validation_failed", null, null, Place(sofa, "floor", 0, 0, 45));
        await Refused(HttpStatusCode.UnprocessableEntity, "validation_failed", null, null, Place(poster, "wall_back", 0, 0, 90));
        await Refused(HttpStatusCode.UnprocessableEntity, "validation_failed", null, null, Place(sofa, "ceiling", 0, 0, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "validation_failed", null, null, Place(sofa, "floor", 0, 0, 0), Place(sofa, "floor", 4, 4, 0));
        await Refused(HttpStatusCode.UnprocessableEntity, "validation_failed", null, null, new { itemId = sofa, surface = "floor", x = 0 });
        await Refused(HttpStatusCode.UnprocessableEntity, "room_item_limit", null, null,
            Enumerable.Range(0, 41).Select(_ => Place(Guid.NewGuid(), "floor", 0, 0, 0)).ToArray());
        await Contract.AssertProblemAsync(await Put(player, new { expectedRevision = 0, wallItemId = (Guid?)null, floorItemId = (Guid?)null }),
            HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await Put(player, new
        {
            expectedRevision = 0,
            wallItemId = (Guid?)null,
            floorItemId = (Guid?)null,
            items = new[] { new { itemId = sofa, surface = "floor", x = 1.5, y = 0, rotation = 0 } }
        }), HttpStatusCode.BadRequest, "malformed_request");
        await Contract.AssertProblemAsync(await Put(player, new
        {
            expectedRevision = 0,
            wallItemId = (Guid?)null,
            floorItemId = (Guid?)null,
            items = Array.Empty<object>(),
            owner = "someone"
        }), HttpStatusCode.BadRequest, "malformed_request");

        var room = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/room"), HttpStatusCode.OK, "Room");
        Assert.Equal(0, room.GetProperty("revision").GetInt64());
        Assert.Empty(room.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task UnownedOrInactiveItems_AreRefused()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var (desk, walls) = await GrantAsync(other, "furn_desk", "walls_stripes");
        var unowned = await Contract.AssertProblemAsync(await SaveAsync(player, 0, null, null, Place(desk, "floor", 0, 0, 0)),
            HttpStatusCode.Forbidden, "item_not_owned");
        Assert.Equal(desk, unowned.GetProperty("itemId").GetGuid());
        await Contract.AssertProblemAsync(await SaveAsync(player, 0, walls, null), HttpStatusCode.Forbidden, "item_not_owned");
        await Contract.AssertProblemAsync(await SaveAsync(player, 0, null, null, Place(Guid.NewGuid(), "floor", 0, 0, 0)),
            HttpStatusCode.Forbidden, "item_not_owned");

        var (cactus, _) = await GrantAsync(player, "furn_cactus", "furn_ball_rack");
        await using (var db = api.CreateDbContext())
        {
            await db.CatalogItems.Where(i => i.Id == cactus).ExecuteUpdateAsync(u => u.SetProperty(i => i.IsActive, false));
        }

        try
        {
            await Contract.AssertProblemAsync(await SaveAsync(player, 0, null, null, Place(cactus, "floor", 0, 0, 0)),
                HttpStatusCode.UnprocessableEntity, "item_inactive");
        }
        finally
        {
            await using var db = api.CreateDbContext();
            await db.CatalogItems.Where(i => i.Id == cactus).ExecuteUpdateAsync(u => u.SetProperty(i => i.IsActive, true));
        }
    }

    [Fact]
    public async Task Save_NeedsAuthenticationAndCsrf()
    {
        await Contract.AssertProblemAsync(await Put(api.CreateBrowser(), Body(0, null, null)), HttpStatusCode.Unauthorized, "unauthenticated");
        await Contract.AssertProblemAsync(await api.CreateBrowser().GetAsync("/api/v1/me/room"), HttpStatusCode.Unauthorized, "unauthenticated");
        var player = await Player.CreateGuestAsync(api);
        player.Http.DefaultRequestHeaders.Remove(Infrastructure.CsrfMiddleware.Header);
        await Contract.AssertProblemAsync(await SaveAsync(player, 0, null, null), HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    [Fact]
    public async Task RoomItems_AreNeverWorn_AndTheCatalogFiltersByCategory()
    {
        var player = await Player.CreateGuestAsync(api);
        var (beanbag, _) = await GrantAsync(player, "furn_beanbag", "furn_armchair");
        var revision = (await player.MeAsync()).GetProperty("avatarRevision").GetInt64();
        await Contract.AssertProblemAsync(
            await player.Http.PostAsJsonAsync("/api/v1/me/equipment", new { slot = "furniture", itemId = beanbag, expectedAvatarRevision = revision }),
            HttpStatusCode.UnprocessableEntity, "validation_failed");

        var room = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/catalog/items?category=room&limit=100"), HttpStatusCode.OK, "CatalogPage");
        var roomSlots = new[] { "furniture", "wall_decor", "room_wall", "room_floor" };
        var content = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot });
        Assert.Equal(content.Room.Items.Length, room.GetProperty("items").GetArrayLength());
        Assert.All(room.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("slot").GetString(), roomSlots));
        var avatar = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/catalog/items?category=avatar&limit=100"), HttpStatusCode.OK, "CatalogPage");
        Assert.All(avatar.GetProperty("items").EnumerateArray(), i => Assert.DoesNotContain(i.GetProperty("slot").GetString(), roomSlots));
        var walls = await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/catalog/items?slot=room_wall"), HttpStatusCode.OK, "CatalogPage");
        Assert.Equal(5, walls.GetProperty("items").GetArrayLength());
        await Contract.AssertProblemAsync(await player.Http.GetAsync("/api/v1/catalog/items?category=garden"), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    [Fact]
    public async Task ProfileRoom_FollowsProfileVisibility()
    {
        var owner = await Player.CreateGuestAsync(api);
        var visitor = await Player.CreateGuestAsync(api);
        var handle = (await owner.MeAsync()).GetProperty("handle").GetString()!;
        var (tv, _) = await GrantAsync(owner, "furn_tv_stand", "furn_speaker_tower");
        await Contract.AssertAsync(await SaveAsync(owner, 0, null, null, Place(tv, "floor", 0, 0, 0)), HttpStatusCode.OK, "Room");

        var seen = await Contract.AssertAsync(await visitor.Http.GetAsync($"/api/v1/profiles/{handle.ToUpperInvariant()}/room"), HttpStatusCode.OK, "Room");
        Assert.Equal("furn_tv_stand", seen.GetProperty("items")[0].GetProperty("sku").GetString());
        // Revision and time are the owner's: a viewer gets 0 and null.
        Assert.Equal(0, seen.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("updatedAt").ValueKind);
        var response = await visitor.Http.GetAsync($"/api/v1/profiles/{handle}/room");
        Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true }, response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());

        // Anonymous, unknown and private: the same 404 as the profile.
        var anonymous = await api.CreateBrowser().GetAsync($"/api/v1/profiles/{handle}/room");
        await Contract.AssertProblemAsync(anonymous, HttpStatusCode.NotFound, "not_found");
        Assert.Equal("noindex", anonymous.Headers.GetValues("X-Robots-Tag").Single());
        await Contract.AssertProblemAsync(await visitor.Http.GetAsync("/api/v1/profiles/no_such_room_x/room"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertAsync(await owner.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" }), HttpStatusCode.OK, "PrivacySettings");
        await Contract.AssertProblemAsync(await visitor.Http.GetAsync($"/api/v1/profiles/{handle}/room"), HttpStatusCode.NotFound, "not_found");
        var own = await Contract.AssertAsync(await owner.Http.GetAsync($"/api/v1/profiles/{handle}/room"), HttpStatusCode.OK, "Room");
        Assert.Single(own.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task BlockInEitherDirection_ShowsTheEmptyRoom_LikeAnUndecoratedRoom()
    {
        var owner = await Player.CreateGuestAsync(api);
        var blocked = await Player.CreateGuestAsync(api);
        var blocker = await Player.CreateGuestAsync(api);
        var handle = (await owner.MeAsync()).GetProperty("handle").GetString()!;
        var (bed, walls) = await GrantAsync(owner, "furn_bed", "walls_neon_night");
        await Contract.AssertAsync(await SaveAsync(owner, 0, walls, null, Place(bed, "floor", 0, 0, 0)), HttpStatusCode.OK, "Room");

        await BlockAsync(owner, blocked); // the owner blocked the visitor
        await BlockAsync(blocker, owner); // the visitor blocked the owner
        foreach (var visitor in new[] { blocked, blocker })
        {
            var text = await (await visitor.Http.GetAsync($"/api/v1/profiles/{handle}/room")).Content.ReadAsStringAsync();
            var room = JsonDocument.Parse(text).RootElement;
            Contract.Validate(room, "Room");
            Assert.Equal(JsonSerializer.Serialize(RoomResponse.Empty, Infrastructure.ApiJson.Options), text);
            Assert.Empty(room.GetProperty("items").EnumerateArray());
        }

        // The answer is the same as for a player who never decorated, and as for an emptied room.
        var undecorated = await Player.CreateGuestAsync(api);
        await Contract.AssertAsync(await SaveAsync(undecorated, 0, null, null), HttpStatusCode.OK, "Room"); // keeps no row
        var emptied = await Player.CreateGuestAsync(api);
        var (lamp, _) = await GrantAsync(emptied, "furn_floor_lamp", "furn_cactus");
        await Contract.AssertAsync(await SaveAsync(emptied, 0, null, null, Place(lamp, "floor", 0, 0, 0)), HttpStatusCode.OK, "Room");
        await Contract.AssertAsync(await SaveAsync(emptied, 1, null, null), HttpStatusCode.OK, "Room");
        var emptiedView = await (await blocked.Http.GetAsync($"/api/v1/profiles/{(await emptied.MeAsync()).GetProperty("handle").GetString()}/room"))
            .Content.ReadAsStringAsync();
        Assert.Equal(JsonSerializer.Serialize(RoomResponse.Empty, Infrastructure.ApiJson.Options), emptiedView);
        var plain = await (await blocked.Http.GetAsync($"/api/v1/profiles/{(await undecorated.MeAsync()).GetProperty("handle").GetString()}/room"))
            .Content.ReadAsStringAsync();
        Assert.Equal(plain, await (await blocked.Http.GetAsync($"/api/v1/profiles/{handle}/room")).Content.ReadAsStringAsync());
    }

    [Fact]
    public void LayoutRules_TurnTheFootprint_AndAllowARugUnderAnObject()
    {
        var content = new ContentCatalog(new ContentOptions { Root = ApiFactory.RepoRoot });
        var sofa = content.RoomItems["furn_sofa"];
        Assert.Equal((4, 2), RoomLayout.Cells(sofa.Footprint!, RoomLayout.Floor, 0));
        Assert.Equal((2, 4), RoomLayout.Cells(sofa.Footprint!, RoomLayout.Floor, 270));
        var items = new Dictionary<Guid, PlaceableItem>
        {
            [Guid.Parse("00000000-0000-0000-0000-000000000001")] = new(Guid.Empty, ItemSlot.Furniture, true, sofa),
            [Guid.Parse("00000000-0000-0000-0000-000000000002")] = new(Guid.Empty, ItemSlot.Furniture, true, content.RoomItems["furn_round_rug"]),
        };
        RoomLayout.Validate(
            [new(items.Keys.First(), "floor", 6, 0, 90), new(items.Keys.Last(), "floor", 5, 0, 0)], items, content.Room);
        var edge = Assert.Throws<Infrastructure.ApiProblemException>(() =>
            RoomLayout.Validate([new(items.Keys.First(), "floor", 7, 0, 90)], items, content.Room));
        Assert.Equal("placement_out_of_bounds", edge.Code);
    }

    [Fact]
    public async Task Export_ContainsTheRoom_AndDeletionRemovesIt()
    {
        var player = await Player.CreateGuestAsync(api);
        var (shelf, _) = await GrantAsync(player, "decor_wall_shelf", "decor_neon_bolt");
        await Contract.AssertAsync(await SaveAsync(player, 0, null, null, Place(shelf, "wall_back", 3, 2, 0)), HttpStatusCode.OK, "Room");

        await using var db = api.CreateDbContext();
        var export = JsonSerializer.SerializeToElement(
            await PlayerDataExport.BuildAsync(db, player.Id, DateTime.UtcNow, CancellationToken.None), Infrastructure.ApiJson.Options);
        Assert.Equal(shelf, export.GetProperty("room").GetProperty("items")[0].GetProperty("itemId").GetGuid());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await player.Http.SendAsync(request)).StatusCode);
        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);
        Assert.False(await db.PlayerRooms.AnyAsync(r => r.PlayerId == player.Id));
    }

    // ---------------------------------------------------------------- helpers

    private static object Place(Guid itemId, string surface, int x, int y, int rotation) => new { itemId, surface, x, y, rotation };

    private static object Body(long expectedRevision, Guid? wallItemId, Guid? floorItemId, params object[] items) =>
        new { expectedRevision, wallItemId, floorItemId, items };

    private static Task<HttpResponseMessage> SaveAsync(Player player, long expectedRevision, Guid? wallItemId, Guid? floorItemId, params object[] items) =>
        Put(player.Http, Body(expectedRevision, wallItemId, floorItemId, items));

    private static Task<HttpResponseMessage> Put(Player player, object body) => Put(player.Http, body);

    private static Task<HttpResponseMessage> Put(HttpClient http, object body) => http.PutAsJsonAsync("/api/v1/me/room", body);

    private async Task SetBalanceAsync(Player player, long balance)
    {
        await using var db = api.CreateDbContext();
        await db.Wallets.Where(w => w.PlayerId == player.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.Balance, balance));
    }

    private async Task<Guid[]> GrantManyAsync(Player player, params string[] skus)
    {
        await using var db = api.CreateDbContext();
        var items = await db.CatalogItems.Where(i => skus.Contains(i.Sku)).ToDictionaryAsync(i => i.Sku, i => i.Id);
        foreach (var sku in skus)
        {
            db.PlayerInventory.Add(new PlayerInventoryItem { PlayerId = player.Id, ItemId = items[sku], Source = InventorySource.AdminGrant });
        }

        await db.SaveChangesAsync();
        return skus.Select(s => items[s]).ToArray();
    }

    private async Task<(Guid, Guid)> GrantAsync(Player player, string a, string b)
    {
        var ids = await GrantManyAsync(player, a, b);
        return (ids[0], ids[1]);
    }

    private async Task<(Guid, Guid, Guid, Guid)> GrantAsync(Player player, string a, string b, string c, string d)
    {
        var ids = await GrantManyAsync(player, a, b, c, d);
        return (ids[0], ids[1], ids[2], ids[3]);
    }

    private async Task<(Guid, Guid, Guid, Guid, Guid, Guid, Guid, Guid)> GrantAsync(
        Player player, string a, string b, string c, string d, string e, string f, string g, string h)
    {
        var ids = await GrantManyAsync(player, a, b, c, d, e, f, g, h);
        return (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6], ids[7]);
    }

    private static async Task BlockAsync(Player blocker, Player target)
    {
        var handle = (await target.MeAsync()).GetProperty("handle").GetString();
        var response = await blocker.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
