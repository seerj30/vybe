using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>GET /admin/flagged-runs: review flags for moderators (spec §17 decision 4), a read without audit rows.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReviewFlagEndpointTests(ApiFactory api)
{
    private const string Path = "/api/v1/admin/flagged-runs";

    [Fact]
    public async Task FlaggedRuns_NeedTheModeratorRole()
    {
        var guest = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await guest.Http.GetAsync(Path), HttpStatusCode.Unauthorized, "unauthenticated");

        using var player = Bearer(ApiFactory.SupabaseToken(Guid.NewGuid().ToString()));
        await Contract.AssertProblemAsync(await player.GetAsync(Path), HttpStatusCode.Forbidden, "forbidden");

        // user_metadata is writable by the user, so a role there grants nothing.
        using var forged = Bearer(ApiFactory.SupabaseToken(Guid.NewGuid().ToString(), new Dictionary<string, object>
        {
            ["user_metadata"] = new Dictionary<string, object> { ["vybe_role"] = "moderator" },
        }));
        await Contract.AssertProblemAsync(await forged.GetAsync(Path), HttpStatusCode.Forbidden, "forbidden");

        using var moderator = Admin("moderator");
        await Contract.AssertAsync(await moderator.GetAsync(Path), HttpStatusCode.OK, "FlaggedRunPage");
        using var administrator = Admin("administrator");
        await Contract.AssertAsync(await administrator.GetAsync(Path), HttpStatusCode.OK, "FlaggedRunPage");
    }

    [Fact]
    public async Task FlaggedRuns_ListOnlyFlaggedRuns_NewestFirst_WithPlayerScoreAndRunTime()
    {
        var bot = await Player.CreateGuestAsync(api);
        var human = await Player.CreateGuestAsync(api);
        // 40 perfect shots, 650 ms apart, always on target: more than 60 % perfect and no variance.
        var older = await api.PlayRunAsync(bot, 40);
        var clean = await api.PlayRunAsync(human, 10); // fewer than 20 shots: no flag can apply
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = await api.PlayRunAsync(bot, 40);
        using var moderator = Admin("moderator");
        await using var db = api.CreateDbContext();
        var auditRows = await db.AdminAuditLogs.CountAsync();

        var listed = await ListAllAsync(moderator, "?limit=1");
        var ids = listed.Select(r => r.GetProperty("sessionId").GetGuid()).ToList();
        Assert.DoesNotContain(clean, ids);
        Assert.True(ids.IndexOf(newer) >= 0 && ids.IndexOf(newer) < ids.IndexOf(older), "newest first");

        var run = listed.Single(r => r.GetProperty("sessionId").GetGuid() == newer);
        var session = await db.GameSessions.AsNoTracking().SingleAsync(s => s.Id == newer);
        Assert.Equal(bot.Id, run.GetProperty("player").GetProperty("playerId").GetGuid());
        Assert.Equal(await db.Players.Where(p => p.Id == bot.Id).Select(p => p.Handle).SingleAsync(),
            run.GetProperty("player").GetProperty("handle").GetString());
        Assert.Equal(session.ReviewFlags, run.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.Contains("perfect_rate_high", session.ReviewFlags);
        Assert.Equal(session.Score, run.GetProperty("score").GetInt32());
        Assert.Equal(40, run.GetProperty("eventCount").GetInt32());
        Assert.Equal((long)(session.SubmittedAt!.Value - session.StartedAt!.Value).TotalMilliseconds, run.GetProperty("runMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("challengeId").ValueKind);

        Assert.Equal(auditRows, await db.AdminAuditLogs.CountAsync()); // reads write no audit rows
    }

    [Fact]
    public async Task FlaggedRuns_FilterByFlag_AndValidateTheQuery()
    {
        var bot = await Player.CreateGuestAsync(api);
        var run = await api.PlayRunAsync(bot, 40);
        using var moderator = Admin("moderator");

        var perfect = await ListAllAsync(moderator, "?flag=perfect_rate_high&limit=100");
        Assert.Contains(perfect, r => r.GetProperty("sessionId").GetGuid() == run);
        Assert.All(perfect, r => Assert.Contains("perfect_rate_high", r.GetProperty("flags").EnumerateArray().Select(f => f.GetString())));
        var streak = await ListAllAsync(moderator, "?flag=daily_cap_streak&limit=100");
        Assert.DoesNotContain(streak, r => r.GetProperty("sessionId").GetGuid() == run);

        await Contract.AssertProblemAsync(await moderator.GetAsync($"{Path}?flag=cheater"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await moderator.GetAsync($"{Path}?cursor=bm90LWEtY3Vyc29y"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await moderator.GetAsync($"{Path}?limit=0"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await moderator.GetAsync($"{Path}?limit=101"), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    [Fact]
    public async Task FlaggedRuns_EqualVerificationTimes_AreEachListedOnce_InIdOrder()
    {
        // Two players submit at the same (fake) time, so both runs have the same verified_at: the cursor must use the id.
        var first = await Player.CreateGuestAsync(api);
        var second = await Player.CreateGuestAsync(api);
        var a = await first.StartRunAsync();
        var b = await second.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(a.Config.DurationMs + 100));
        foreach (var (player, run) in new[] { (first, a), (second, b) })
        {
            var body = await Contract.AssertAsync(
                await player.SubmitAsync(run.SessionId, run.RulesetId, Player.PerfectEvents(run.Config, run.Seed, 40)), HttpStatusCode.OK, "SubmitResult");
            Assert.Equal("verified", body.GetProperty("status").GetString());
        }

        await using (var db = api.CreateDbContext())
        {
            var times = await db.GameSessions.Where(s => s.Id == a.SessionId || s.Id == b.SessionId).Select(s => s.VerifiedAt).Distinct().ToListAsync();
            Assert.Single(times);
        }

        using var moderator = Admin("moderator");
        var ids = (await ListAllAsync(moderator, "?limit=1")).Select(r => r.GetProperty("sessionId").GetGuid()).ToList();
        Assert.Single(ids, id => id == a.SessionId);
        Assert.Single(ids, id => id == b.SessionId);
        // PostgreSQL orders uuid values byte by byte, which is the order of the canonical text form (not Guid.CompareTo).
        var expected = new[] { a.SessionId, b.SessionId }.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal).ToList();
        Assert.Equal(expected, ids.Where(id => id == a.SessionId || id == b.SessionId).ToList());
    }

    [Fact]
    public async Task FlaggedRuns_LeaveOutDeletingAndDeletedPlayers()
    {
        var deleted = await Player.CreateGuestAsync(api);
        var run = await api.PlayRunAsync(deleted, 40);
        await using (var db = api.CreateDbContext())
        {
            await db.Players.Where(p => p.Id == deleted.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlayerStatus.Deleting));
        }

        using var moderator = Admin("moderator");
        Assert.DoesNotContain(await ListAllAsync(moderator, "?limit=100"), r => r.GetProperty("sessionId").GetGuid() == run);
    }

    /// <summary>Every page of the list, following <c>nextCursor</c>.</summary>
    private static async Task<List<JsonElement>> ListAllAsync(HttpClient moderator, string query)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var url = cursor is null ? $"{Path}{query}" : $"{Path}{query}&cursor={Uri.EscapeDataString(cursor)}";
            var page = await Contract.AssertAsync(await moderator.GetAsync(url), HttpStatusCode.OK, "FlaggedRunPage");
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        return items;
    }

    private HttpClient Admin(string role) => Bearer(ApiFactory.SupabaseToken(Guid.NewGuid().ToString(), new Dictionary<string, object>
    {
        ["app_metadata"] = new Dictionary<string, object> { ["vybe_role"] = role },
    }));

    private HttpClient Bearer(string token)
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
