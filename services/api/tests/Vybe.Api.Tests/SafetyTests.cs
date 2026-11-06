using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec §5 Safety and profiles: blocks (invariants 8, 12 and block privacy), reports, public profiles.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SafetyTests(ApiFactory api)
{
    [Fact]
    public async Task Profile_PublicFieldsOnly_CaseInsensitive_SameForEveryCaller()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var handle = await HandleAsync(player);

        var anonymous = await api.CreateBrowser().GetAsync($"/api/v1/profiles/{handle.ToUpperInvariant()}");
        Assert.Equal("private, no-store", anonymous.Headers.NonValidated["Cache-Control"].ToString());
        var profile = await Contract.AssertAsync(anonymous, HttpStatusCode.OK, "PublicProfile");
        Assert.Equal(handle, profile.GetProperty("handle").GetString());
        Assert.Equal(["avatar", "displayName", "handle", "level"], profile.EnumerateObject().Select(p => p.Name).Order());

        // A block never changes the answer, so the blocked player cannot detect it.
        await BlockOkAsync(player, await HandleAsync(other));
        var seenByBlocked = await Contract.AssertAsync(await other.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.OK, "PublicProfile");
        Assert.Equal(profile.ToString(), seenByBlocked.ToString());

        await Contract.AssertProblemAsync(await other.Http.GetAsync("/api/v1/profiles/no_such_player_x"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await other.Http.GetAsync("/api/v1/profiles/a-b"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Blocks_AreIdempotent_ListedForTheBlockerOnly_AndRemovable()
    {
        var blocker = await Player.CreateGuestAsync(api);
        var blocked = await Player.CreateGuestAsync(api);
        var blockedHandle = await HandleAsync(blocked);

        await BlockOkAsync(blocker, blockedHandle.ToUpperInvariant());
        await BlockOkAsync(blocker, blockedHandle); // idempotent
        var list = await ListAsync(blocker, string.Empty);
        var entry = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(blockedHandle, entry.GetProperty("handle").GetString());
        Assert.Empty((await ListAsync(blocked, string.Empty)).GetProperty("items").EnumerateArray()); // never who blocked the caller

        await Contract.AssertProblemAsync(await BlockAsync(blocker, await HandleAsync(blocker)), HttpStatusCode.UnprocessableEntity, "self_block");
        await Contract.AssertProblemAsync(await BlockAsync(blocker, "no_such_player_x"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await BlockAsync(blocker, "a-b"), HttpStatusCode.UnprocessableEntity, "validation_failed");

        Assert.Equal(HttpStatusCode.NoContent, (await blocker.Http.DeleteAsync($"/api/v1/me/blocks/{blockedHandle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await blocker.Http.DeleteAsync($"/api/v1/me/blocks/{blockedHandle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await blocker.Http.DeleteAsync("/api/v1/me/blocks/no_such_player_x")).StatusCode);
        Assert.Empty((await ListAsync(blocker, string.Empty)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ListBlocks_PagesNewestFirst()
    {
        var blocker = await Player.CreateGuestAsync(api);
        var handles = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var target = await Player.CreateGuestAsync(api);
            handles.Add(await HandleAsync(target));
            await BlockOkAsync(blocker, handles[^1]);
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(blocker, cursor is null ? "?limit=2" : $"?limit=2&cursor={cursor}");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("handle").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(Enumerable.Reverse(handles), seen);
    }

    [Fact]
    public async Task Block_CancelsThePairsPendingChallenges_ShownAsExpired_AndUnblockDoesNotRestore()
    {
        var a = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var bystander = await Player.CreateGuestAsync(api);
        var (open, _) = await ChallengeTests.CreateOkAsync(a, await api.PlayRunAsync(a, 10), await HandleAsync(b));
        var (accepted, token) = await ChallengeTests.CreateOkAsync(b, await api.PlayRunAsync(b, 10));
        await Contract.AssertAsync(await ChallengeTests.AcceptAsync(a, token), HttpStatusCode.OK, "ChallengeAccepted");
        var (unrelated, _) = await ChallengeTests.CreateOkAsync(a, await api.PlayRunAsync(a, 10), await HandleAsync(bystander));

        await BlockOkAsync(b, await HandleAsync(a)); // the blocked player is a; either direction cancels

        foreach (var (player, id) in new[] { (a, open), (b, open), (a, accepted), (b, accepted) })
        {
            var challenge = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/challenges/{id}"), HttpStatusCode.OK, "Challenge");
            Assert.Equal("expired", challenge.GetProperty("status").GetString()); // never "cancelled"
        }

        var other = await Contract.AssertAsync(await a.Http.GetAsync($"/api/v1/challenges/{unrelated}"), HttpStatusCode.OK, "Challenge");
        Assert.Equal("open", other.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.DeleteAsync($"/api/v1/me/blocks/{await HandleAsync(a)}")).StatusCode);
        await using var db = api.CreateDbContext();
        Assert.All(await db.Challenges.Where(c => c.Id == open || c.Id == accepted).ToListAsync(), c => Assert.Equal(ChallengeStatus.Cancelled, c.Status));
    }

    [Fact]
    public async Task BlockRacingWithAccept_NoLiveChallengeSurvivesBetweenThePair()
    {
        for (var i = 0; i < 4; i++)
        {
            var creator = await Player.CreateGuestAsync(api);
            var recipient = await Player.CreateGuestAsync(api);
            var (_, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));

            var accept = ChallengeTests.AcceptAsync(recipient, token);
            var block = BlockAsync(creator, await HandleAsync(recipient));
            var (accepted, blocked) = (await accept, await block);

            Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
            if (accepted.StatusCode != HttpStatusCode.OK)
            {
                await Contract.AssertProblemAsync(accepted, HttpStatusCode.NotFound, "challenge_unavailable");
            }

            await AssertNoLiveChallengeAsync(creator.Id, recipient.Id);
        }
    }

    [Fact]
    public async Task BlockRacingWithCreate_NoLiveChallengeSurvivesBetweenThePair()
    {
        for (var i = 0; i < 4; i++)
        {
            var creator = await Player.CreateGuestAsync(api);
            var recipient = await Player.CreateGuestAsync(api);
            var session = await api.PlayRunAsync(creator, 10);
            var recipientHandle = await HandleAsync(recipient);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges")
            {
                Content = JsonContent.Create(new { creatorSessionId = session, recipientHandle }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var create = creator.Http.SendAsync(request);
            var block = BlockAsync(recipient, await HandleAsync(creator));

            // Block first: the challenge is stored suppressed and answered as usual (the creator cannot tell).
            await Contract.AssertAsync(await create, HttpStatusCode.Created, "ChallengeCreated");
            Assert.Equal(HttpStatusCode.NoContent, (await block).StatusCode);
            await AssertNoLiveChallengeAsync(creator.Id, recipient.Id);
            // Suppressed: not listed; created first and then cancelled: listed as expired.
            var received = await recipient.Http.GetFromJsonAsync<JsonElement>("/api/v1/me/challenges?role=received");
            Assert.All(received.GetProperty("items").EnumerateArray(), c => Assert.Equal("expired", c.GetProperty("status").GetString()));
        }
    }

    [Fact]
    public async Task Reports_Accepted_Validated_AndRateLimitedPerReporter()
    {
        var reporter = await Player.CreateGuestAsync(api);
        var target = await HandleAsync(await Player.CreateGuestAsync(api));

        var accepted = await Contract.AssertAsync(
            await ReportAsync(reporter, new { targetHandle = target.ToUpperInvariant(), reason = "offensive_name", details = "rude handle" }),
            HttpStatusCode.Accepted, "ReportAccepted");
        await using (var db = api.CreateDbContext())
        {
            var report = await db.PlayerReports.SingleAsync(r => r.Id == accepted.GetProperty("reportId").GetGuid());
            Assert.Equal((ReportReason.OffensiveName, ReportStatus.Open, "rude handle"), (report.Reason, report.Status, report.Details));
        }

        await Contract.AssertProblemAsync(
            await ReportAsync(reporter, new { targetHandle = await HandleAsync(reporter), reason = "other" }), HttpStatusCode.UnprocessableEntity, "self_report");
        await Contract.AssertProblemAsync(
            await ReportAsync(reporter, new { targetHandle = "no_such_player_x", reason = "other" }), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(
            await ReportAsync(reporter, new { targetHandle = target, reason = "other", details = new string('x', 501) }),
            HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(
            await ReportAsync(reporter, new { targetHandle = target, reason = "spam" }), HttpStatusCode.BadRequest, "malformed_request");

        // A repeat against the same target returns the open report instead of a new one.
        var repeat = await Contract.AssertAsync(
            await ReportAsync(reporter, new { targetHandle = target, reason = "cheating" }), HttpStatusCode.Accepted, "ReportAccepted");
        Assert.Equal(accepted.GetProperty("reportId").GetGuid(), repeat.GetProperty("reportId").GetGuid());

        for (var i = 1; i < 10; i++)
        {
            var other = await HandleAsync(await Player.CreateGuestAsync(api));
            Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(reporter, new { targetHandle = other, reason = "cheating" })).StatusCode);
        }

        var eleventh = await HandleAsync(await Player.CreateGuestAsync(api));
        await Contract.AssertProblemAsync(
            await ReportAsync(reporter, new { targetHandle = eleventh, reason = "cheating" }), HttpStatusCode.TooManyRequests, "rate_limited");
        api.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(reporter, new { targetHandle = eleventh, reason = "harassment" })).StatusCode);
    }

    private async Task AssertNoLiveChallengeAsync(Guid a, Guid b)
    {
        await using var db = api.CreateDbContext();
        var live = await db.Challenges
            .Where(c => ((c.CreatorPlayerId == a && c.RecipientPlayerId == b) || (c.CreatorPlayerId == b && c.RecipientPlayerId == a))
                && (c.Status == ChallengeStatus.Open || c.Status == ChallengeStatus.Accepted)
                && !c.Suppressed)
            .CountAsync();
        Assert.Equal(0, live);
    }

    private static async Task<string> HandleAsync(Player player) => (await player.MeAsync()).GetProperty("handle").GetString()!;

    private static async Task<HttpResponseMessage> BlockAsync(Player player, string handle) =>
        await player.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle });

    private static async Task BlockOkAsync(Player player, string handle) =>
        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(player, handle)).StatusCode);

    private static async Task<JsonElement> ListAsync(Player player, string query) =>
        await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/me/blocks{query}"), HttpStatusCode.OK, "BlockedPlayerPage");

    private static async Task<HttpResponseMessage> ReportAsync(Player player, object body) =>
        await player.Http.PostAsJsonAsync("/api/v1/reports", body);
}
