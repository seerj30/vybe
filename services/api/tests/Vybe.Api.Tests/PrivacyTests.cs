using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec §2 J4, §14 E12; data export; E08 for exports.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PrivacyTests(ApiFactory api)
{
    [Fact]
    public async Task DataExport_OnePending_ThenReadyWithTheOwnersData_OnlyForTheOwner()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync();

        var exportId = (await Contract.AssertAsync(await player.Http.PostAsync("/api/v1/me/data-export", null), HttpStatusCode.Accepted, "ExportRequested"))
            .GetProperty("exportId").GetGuid();
        var busy = await Contract.AssertProblemAsync(await player.Http.PostAsync("/api/v1/me/data-export", null), HttpStatusCode.Conflict, "export_in_progress");
        Assert.Equal(exportId, busy.GetProperty("exportId").GetGuid());

        var queued = await GetExportAsync(player, exportId);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("downloadUrl").ValueKind);
        await Contract.AssertProblemAsync(await other.Http.GetAsync($"/api/v1/me/data-export/{exportId}"), HttpStatusCode.NotFound, "not_found");

        await RunPrivacyJobsAsync();
        var ready = await GetExportAsync(player, exportId);
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        var file = new Uri(ready.GetProperty("downloadUrl").GetString()!).LocalPath;
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
        {
            var root = document.RootElement;
            Assert.Equal(player.Id, root.GetProperty("player").GetProperty("id").GetGuid());
            Assert.Equal((await player.MeAsync()).GetProperty("handle").GetString(), root.GetProperty("player").GetProperty("handle").GetString());
            Assert.Equal("game_reward", root.GetProperty("ledger")[0].GetProperty("reason").GetString());
            Assert.Single(root.GetProperty("gameSessions").EnumerateArray());
        }

        // A new export is possible once the previous one is finished; the link stops after its expiry.
        await Contract.AssertAsync(await player.Http.PostAsync("/api/v1/me/data-export", null), HttpStatusCode.Accepted, "ExportRequested");
        api.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(JsonValueKind.Null, (await GetExportAsync(player, exportId)).GetProperty("downloadUrl").ValueKind);
    }

    [Fact]
    public async Task E12_GuestDeletion_StatusWithoutAuthentication_AndAnonymization()
    {
        var player = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        var handle = (await player.MeAsync()).GetProperty("handle").GetString()!;
        var friendHandle = (await friend.MeAsync()).GetProperty("handle").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await friend.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await player.Http.PostAsJsonAsync("/api/v1/me/outfits", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await player.Http.PostAsync("/api/v1/me/data-export", null)).StatusCode);
        await RunPrivacyJobsAsync();
        var (challengeId, _) = await ChallengeTests.CreateOkAsync(player, await api.PlayRunAsync(player, 10), friendHandle);

        await Contract.AssertProblemAsync(
            await player.Http.PostAsync("/api/v1/me/deletion-requests", null), HttpStatusCode.PreconditionRequired, "idempotency_key_required");
        var key = Guid.NewGuid();
        var response = await RequestDeletionAsync(player.Http, key);
        var requested = await Contract.AssertAsync(response, HttpStatusCode.Accepted, "DeletionRequested");
        Assert.Equal(key, requested.GetProperty("requestId").GetGuid());
        Assert.StartsWith("__Host-vybe_guest=;", Assert.Single(response.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);
        await Contract.AssertProblemAsync(await player.Http.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");

        // The request id is the capability: no authentication, no personal data.
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var queued = await Contract.AssertAsync(await anonymous.GetAsync($"/api/v1/deletion-requests/{key}"), HttpStatusCode.OK, "DeletionRequest");
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("completedAt").ValueKind);
        await Contract.AssertProblemAsync(await anonymous.GetAsync($"/api/v1/deletion-requests/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found");

        await RunPrivacyJobsAsync();
        var completed = await Contract.AssertAsync(await anonymous.GetAsync($"/api/v1/deletion-requests/{key}"), HttpStatusCode.OK, "DeletionRequest");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);

        await using var db = api.CreateDbContext();
        var deleted = await db.Players.SingleAsync(p => p.Id == player.Id);
        Assert.Equal(PlayerStatus.Deleted, deleted.Status);
        Assert.Matches("^d_[a-z0-9]{18}$", deleted.Handle);
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(await db.SavedOutfits.AnyAsync(o => o.PlayerId == player.Id));
        Assert.False(await db.PlayerBlocks.AnyAsync(b => b.BlockerPlayerId == player.Id || b.BlockedPlayerId == player.Id));
        Assert.Equal(ChallengeStatus.Cancelled, (await db.Challenges.SingleAsync(c => c.Id == challengeId)).Status);
        Assert.True(await db.WalletTransactions.AnyAsync(t => t.PlayerId == player.Id)); // the ledger stays, pseudonymous
        var export = await db.DataExportRequests.SingleAsync(x => x.PlayerId == player.Id);
        Assert.False(File.Exists(Path.Combine(api.Services.GetRequiredService<PrivacyOptions>().ExportDirectory, export.StorageKey!)));
        await Contract.AssertProblemAsync(await anonymous.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task E12_RegisteredDeletion_NeedsARecentSignIn_AndFreesTheIdentity()
    {
        var guest = await Player.CreateGuestAsync(api);
        var subject = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(subject) })).StatusCode);

        using var stale = Bearer(ApiFactory.RegisteredToken(subject, authenticatedAt: api.Clock.GetUtcNow().AddMinutes(-6)));
        await Contract.AssertProblemAsync(await RequestDeletionAsync(stale, Guid.NewGuid()), HttpStatusCode.Forbidden, "reauthentication_required");
        using var noAmr = Bearer(ApiFactory.SupabaseToken(subject));
        await Contract.AssertProblemAsync(await RequestDeletionAsync(noAmr, Guid.NewGuid()), HttpStatusCode.Forbidden, "reauthentication_required");

        using var fresh = Bearer(ApiFactory.RegisteredToken(subject, authenticatedAt: api.Clock.GetUtcNow().AddMinutes(-4)));
        var key = Guid.NewGuid();
        await Contract.AssertAsync(await RequestDeletionAsync(fresh, key), HttpStatusCode.Accepted, "DeletionRequested");
        var replay = await Contract.AssertAsync(await RequestDeletionAsync(fresh, key), HttpStatusCode.Accepted, "DeletionRequested");
        Assert.Equal(key, replay.GetProperty("requestId").GetGuid());
        var other = await Contract.AssertAsync(await RequestDeletionAsync(fresh, Guid.NewGuid()), HttpStatusCode.Accepted, "DeletionRequested");
        Assert.Equal(key, other.GetProperty("requestId").GetGuid()); // the pending request, not a second one
        Assert.Equal("deleting", (await Contract.AssertAsync(await fresh.GetAsync("/api/v1/me"), HttpStatusCode.OK, "Me")).GetProperty("status").GetString());

        await RunPrivacyJobsAsync();
        await Contract.AssertProblemAsync(await fresh.GetAsync("/api/v1/me"), HttpStatusCode.NotFound, "player_not_found");
        var newGuest = await Player.CreateGuestAsync(api);
        await Contract.AssertAsync(
            await newGuest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(subject) }), HttpStatusCode.OK, "AccountLinked");
    }

    private async Task RunPrivacyJobsAsync() =>
        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);

    private static async Task<JsonElement> GetExportAsync(Player player, Guid exportId) =>
        await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/me/data-export/{exportId}"), HttpStatusCode.OK, "DataExport");

    private static async Task<HttpResponseMessage> RequestDeletionAsync(HttpClient http, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await http.SendAsync(request);
    }

    private HttpClient Bearer(string token)
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
