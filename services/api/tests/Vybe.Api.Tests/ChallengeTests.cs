using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Jobs;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Spec §14 E04, E07 and the challenge integration cases; every response is checked against the contract.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ChallengeTests(ApiFactory api)
{
    [Fact]
    public async Task E04_CreateShareAcceptVerifiedRunResult()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var run = await api.PlayRunAsync(creator, 10); // 2,150 points
        var (challengeId, token) = await CreateOkAsync(creator, run);

        // Anyone with the link sees a safe preview; the token never leaks through the Referer header.
        var anonymous = await api.CreateBrowser().GetAsync($"/api/v1/invites/{token}");
        Assert.Equal("no-referrer", anonymous.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("private, no-store", anonymous.Headers.NonValidated["Cache-Control"].ToString());
        var preview = await Contract.AssertAsync(anonymous, HttpStatusCode.OK, "ChallengePreview");
        Assert.Equal("open", preview.GetProperty("status").GetString());
        Assert.Equal(2_150, preview.GetProperty("creatorScore").GetInt32());
        Assert.Equal(await HandleAsync(creator), preview.GetProperty("creator").GetProperty("handle").GetString());
        await Contract.AssertAsync(await recipient.Http.GetAsync($"/api/v1/invites/{token}"), HttpStatusCode.OK, "ChallengePreview");

        var accepted = await Contract.AssertAsync(await AcceptAsync(recipient, token), HttpStatusCode.OK, "ChallengeAccepted");
        Assert.Equal(challengeId, accepted.GetProperty("challengeId").GetGuid());

        var result = await api.PlayRecipientRunAsync(recipient, challengeId, 93);
        Assert.Equal(22_900, result.GetProperty("score").GetInt32());
        Assert.Equal(150, result.GetProperty("rewards").GetProperty("coins").GetInt64()); // normal gameplay rewards

        foreach (var participant in new[] { creator, recipient })
        {
            var challenge = await GetAsync(participant, challengeId);
            Assert.Equal("completed", challenge.GetProperty("status").GetString());
            Assert.Equal("recipient_win", challenge.GetProperty("result").GetString());
            Assert.Equal(22_900, challenge.GetProperty("recipientScore").GetInt32());
            Assert.Equal(JsonValueKind.Null, challenge.GetProperty("shareUrl").ValueKind);
        }

        // The winner bonus comes from the outbox job, once.
        await RunOutboxAsync();
        Assert.Equal(1, await LedgerCountAsync(recipient.Id, $"challenge:{challengeId}"));
        Assert.Equal(0, await LedgerCountAsync(creator.Id, $"challenge:{challengeId}"));
        Assert.Equal(150 + 50, (await recipient.MeAsync()).GetProperty("walletBalance").GetInt64());

        var created = await ListAsync(creator, "?role=created");
        Assert.Contains(created.GetProperty("items").EnumerateArray(), c => c.GetProperty("challengeId").GetGuid() == challengeId);
        var received = await ListAsync(recipient, "?role=received&status=completed");
        Assert.Equal(challengeId, Assert.Single(received.GetProperty("items").EnumerateArray()).GetProperty("challengeId").GetGuid());

        // A recipient run cannot create a challenge.
        var recipientSession = result.GetProperty("sessionId").GetGuid();
        await Contract.AssertProblemAsync(await CreateAsync(recipient, recipientSession), HttpStatusCode.UnprocessableEntity, "session_not_eligible");
    }

    [Fact]
    public async Task E07_ExpiredInviteAndSelfAccept_AreRejected()
    {
        var creator = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));

        await Contract.AssertProblemAsync(await AcceptAsync(creator, token), HttpStatusCode.Forbidden, "self_accept");

        api.Clock.Advance(Features.ChallengeEndpoints.AcceptWindow);
        await Contract.AssertProblemAsync(await AcceptAsync(other, token), HttpStatusCode.Gone, "challenge_expired");
        var preview = await Contract.AssertAsync(await other.Http.GetAsync($"/api/v1/invites/{token}"), HttpStatusCode.OK, "ChallengePreview");
        Assert.Equal("expired", preview.GetProperty("status").GetString());
        Assert.Equal("expired", (await GetAsync(creator, challengeId)).GetProperty("status").GetString());

        await Contract.AssertProblemAsync(
            await other.Http.GetAsync("/api/v1/invites/not-a-real-token-at-all-0000000"), HttpStatusCode.NotFound, "challenge_unavailable");
    }

    [Fact]
    public async Task DoubleAccept_ConcurrentPlayers_OneWins()
    {
        var creator = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var c = await Player.CreateGuestAsync(api);
        var (_, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));

        var responses = await Task.WhenAll(AcceptAsync(b, token), AcceptAsync(c, token));

        await Contract.AssertAsync(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK, "ChallengeAccepted");
        await Contract.AssertProblemAsync(
            Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "challenge_already_accepted");
    }

    [Fact]
    public async Task CrossedChallenges_VerifiedConcurrently_BothResolve()
    {
        var a = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var (fromA, tokenA) = await CreateOkAsync(a, await api.PlayRunAsync(a, 10));
        var (fromB, tokenB) = await CreateOkAsync(b, await api.PlayRunAsync(b, 12));
        await Contract.AssertAsync(await AcceptAsync(b, tokenA), HttpStatusCode.OK, "ChallengeAccepted");
        await Contract.AssertAsync(await AcceptAsync(a, tokenB), HttpStatusCode.OK, "ChallengeAccepted");

        var runA = await a.StartRecipientRunAsync(fromB);
        var runB = await b.StartRecipientRunAsync(fromA);
        api.Clock.Advance(TimeSpan.FromSeconds(61));
        var submitted = await Task.WhenAll(
            a.SubmitAsync(runA.SessionId, runA.RulesetId, Player.PerfectEvents(runA.Config, runA.Seed, 93)),
            b.SubmitAsync(runB.SessionId, runB.RulesetId, Player.PerfectEvents(runB.Config, runB.Seed, 93)));

        foreach (var response in submitted)
        {
            var body = await Contract.AssertAsync(response, HttpStatusCode.OK, "SubmitResult");
            Assert.Equal("verified", body.GetProperty("status").GetString());
        }

        await RunOutboxAsync();
        Assert.Equal("recipient_win", (await GetAsync(a, fromA)).GetProperty("result").GetString());
        Assert.Equal("recipient_win", (await GetAsync(a, fromB)).GetProperty("result").GetString());
        Assert.Equal(1, await LedgerCountAsync(a.Id, $"challenge:{fromB}"));
        Assert.Equal(1, await LedgerCountAsync(b.Id, $"challenge:{fromA}"));
    }

    [Fact]
    public async Task OutboxRetries_NeverPayTwice_AndRespectTheDailyCap()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 93));
        await Contract.AssertAsync(await AcceptAsync(recipient, token), HttpStatusCode.OK, "ChallengeAccepted");
        await api.PlayRecipientRunAsync(recipient, challengeId, 10); // creator wins

        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        await Task.WhenAll(processor.RunOnceAsync(100, CancellationToken.None), processor.RunOnceAsync(100, CancellationToken.None));
        // A retried job (e.g. after a crash between the effect and the status write) finds the ledger row.
        await using (var db = api.CreateDbContext())
        {
            await db.OutboxJobs.Where(j => j.JobKey.StartsWith($"challenge-resolved:{challengeId}:"))
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, OutboxStatus.Pending));
        }

        await RunOutboxAsync();

        Assert.Equal(1, await LedgerCountAsync(creator.Id, $"challenge:{challengeId}"));
        await using (var db = api.CreateDbContext())
        {
            Assert.All(await db.OutboxJobs.Where(j => j.JobKey.StartsWith($"challenge-resolved:{challengeId}:")).ToListAsync(),
                j => Assert.Equal(OutboxStatus.Done, j.Status));
            var resolvedAt = (await db.Challenges.SingleAsync(c => c.Id == challengeId)).ResolvedAt!.Value;
            var counter = await db.PlayerDailyCounters.SingleAsync(c => c.PlayerId == creator.Id && c.Day == DateOnly.FromDateTime(resolvedAt));
            Assert.Equal(1, counter.ChallengeBonuses);
        }

        // Sixth win of the day: no bonus.
        var winner = await Player.CreateGuestAsync(api);
        var loser = await Player.CreateGuestAsync(api);
        var (second, secondToken) = await CreateOkAsync(winner, await api.PlayRunAsync(winner, 93));
        await AcceptAsync(loser, secondToken);
        await api.PlayRecipientRunAsync(loser, second, 5);
        await using (var db = api.CreateDbContext())
        {
            var day = DateOnly.FromDateTime((await db.Challenges.SingleAsync(c => c.Id == second)).ResolvedAt!.Value);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vybe.player_daily_counters (player_id, day, challenge_bonuses) VALUES ({winner.Id}, {day}, 5)
                ON CONFLICT (player_id, day) DO UPDATE SET challenge_bonuses = 5
                """);
        }

        await RunOutboxAsync();
        Assert.Equal(0, await LedgerCountAsync(winner.Id, $"challenge:{second}"));
    }

    [Fact]
    public async Task CreateChallenge_EligibilityAndRecipientErrors()
    {
        var creator = await Player.CreateGuestAsync(api);
        var tooShort = await api.PlayRunAsync(creator, 4);
        await Contract.AssertProblemAsync(await CreateAsync(creator, tooShort), HttpStatusCode.UnprocessableEntity, "session_not_eligible");
        await Contract.AssertProblemAsync(await CreateAsync(creator, Guid.NewGuid()), HttpStatusCode.UnprocessableEntity, "session_not_eligible");

        var run = await api.PlayRunAsync(creator, 10);
        await Contract.AssertProblemAsync(await CreateAsync(creator, run, "nobody_here"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(
            await CreateAsync(creator, run, (await HandleAsync(creator)).ToUpperInvariant()), HttpStatusCode.UnprocessableEntity, "self_challenge");
        await CreateOkAsync(creator, run);
        await Contract.AssertProblemAsync(await CreateAsync(creator, run), HttpStatusCode.Conflict, "session_already_used");
    }

    [Fact]
    public async Task CreateChallenge_Replay_RebuildsShareUrl_WithoutStoringTheToken()
    {
        var creator = await Player.CreateGuestAsync(api);
        var run = await api.PlayRunAsync(creator, 10);
        var key = Guid.NewGuid();

        var first = await Contract.AssertAsync(await CreateAsync(creator, run, key: key), HttpStatusCode.Created, "ChallengeCreated");
        var replay = await Contract.AssertAsync(await CreateAsync(creator, run, key: key), HttpStatusCode.Created, "ChallengeCreated");

        var shareUrl = first.GetProperty("shareUrl").GetString()!;
        Assert.Equal(shareUrl, replay.GetProperty("shareUrl").GetString());
        await using var db = api.CreateDbContext();
        var stored = await db.IdempotencyRecords.SingleAsync(r => r.ActorSubject == creator.Id.ToString() && r.Key == key);
        Assert.DoesNotContain("shareUrl", stored.ResponseBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(shareUrl.Split('/')[^1], stored.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocks_RecipientBlocked_Suppressed_AndBlockedAccept()
    {
        var creator = await Player.CreateGuestAsync(api);
        var blocksCreator = await Player.CreateGuestAsync(api);
        var blockedByCreator = await Player.CreateGuestAsync(api);
        var bystander = await Player.CreateGuestAsync(api);
        await BlockAsync(blocksCreator.Id, creator.Id);
        await BlockAsync(creator.Id, blockedByCreator.Id);

        await Contract.AssertProblemAsync(
            await CreateAsync(creator, await api.PlayRunAsync(creator, 10), await HandleAsync(blockedByCreator)),
            HttpStatusCode.UnprocessableEntity, "recipient_blocked");

        // The recipient blocked the caller: 201 as usual, but the recipient never sees it.
        var (suppressed, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10), await HandleAsync(blocksCreator));
        Assert.Empty((await ListAsync(blocksCreator, string.Empty)).GetProperty("items").EnumerateArray());
        await Contract.AssertProblemAsync(await blocksCreator.Http.GetAsync($"/api/v1/challenges/{suppressed}"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await blocksCreator.Http.GetAsync($"/api/v1/invites/{token}"), HttpStatusCode.NotFound, "challenge_unavailable");
        await Contract.AssertProblemAsync(await AcceptAsync(blocksCreator, token), HttpStatusCode.NotFound, "challenge_unavailable");
        await Contract.AssertProblemAsync(await AcceptAsync(bystander, token), HttpStatusCode.NotFound, "challenge_unavailable");
        Assert.Equal("open", (await GetAsync(creator, suppressed)).GetProperty("status").GetString());

        // A public invite cannot be accepted across a block (same answer as an invalid link).
        var (_, publicToken) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await Contract.AssertProblemAsync(await AcceptAsync(blockedByCreator, publicToken), HttpStatusCode.NotFound, "challenge_unavailable");
        await Contract.AssertAsync(await AcceptAsync(bystander, publicToken), HttpStatusCode.OK, "ChallengeAccepted");
    }

    [Fact]
    public async Task RecipientSession_Rules()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var stranger = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));

        await Contract.AssertProblemAsync(await CreateSessionAsync(recipient, challengeId), HttpStatusCode.NotFound, "challenge_unavailable");
        await AcceptAsync(recipient, token);
        await Contract.AssertProblemAsync(await CreateSessionAsync(stranger, challengeId), HttpStatusCode.NotFound, "challenge_unavailable");

        var run = await recipient.StartRecipientRunAsync(challengeId);
        await using (var db = api.CreateDbContext())
        {
            var creatorSeed = await db.Challenges.Where(c => c.Id == challengeId)
                .Join(db.GameSessions, c => c.CreatorSessionId, s => s.Id, (c, s) => s.Seed).SingleAsync();
            Assert.Equal(creatorSeed, run.Seed); // same conditions as the creator run
        }

        await Contract.AssertProblemAsync(await CreateSessionAsync(recipient, challengeId), HttpStatusCode.Conflict, "challenge_session_exists");

        // A rejected recipient run expires the challenge.
        api.Clock.Advance(TimeSpan.FromSeconds(61));
        var rejected = await Contract.AssertAsync(
            await recipient.SubmitAsync(run.SessionId, run.RulesetId, new[] { new { tMs = 900, aimX = 0, power = 500 }, new { tMs = 100, aimX = 0, power = 500 } }),
            HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("expired", (await GetAsync(creator, challengeId)).GetProperty("status").GetString());
        await using (var db = api.CreateDbContext())
        {
            Assert.Equal(ChallengeStatus.Expired, (await db.Challenges.SingleAsync(c => c.Id == challengeId)).Status);
        }
    }

    [Fact]
    public async Task RecipientSession_StartAtTheCompleteDeadline_IsGone_AndEmptyTraceCompletes()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (late, lateToken) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await AcceptAsync(recipient, lateToken);
        api.Clock.Advance(Features.ChallengeEndpoints.AcceptWindow - TimeSpan.FromSeconds(5));
        var session = (await Contract.AssertAsync(await CreateSessionAsync(recipient, late), HttpStatusCode.Created, "SessionCreated"))
            .GetProperty("sessionId").GetGuid();
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        await Contract.AssertProblemAsync(
            await recipient.Http.PostAsync($"/api/v1/games/sessions/{session}/start", null), HttpStatusCode.Gone, "challenge_expired");

        var (onTime, onTimeToken) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await AcceptAsync(recipient, onTimeToken);
        var empty = await api.PlayRecipientRunAsync(recipient, onTime, 0);
        Assert.Equal(0, empty.GetProperty("score").GetInt32());
        var challenge = await GetAsync(recipient, onTime);
        Assert.Equal("completed", challenge.GetProperty("status").GetString());
        Assert.Equal("creator_win", challenge.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ReplacingTheRecipientSession_ExpiresTheChallenge()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await AcceptAsync(recipient, token);
        await recipient.StartRecipientRunAsync(challengeId);

        await recipient.StartRunAsync(); // one open session per player: the recipient run ends expired

        Assert.Equal("expired", (await GetAsync(creator, challengeId)).GetProperty("status").GetString());
        await using var db = api.CreateDbContext();
        Assert.Equal(ChallengeStatus.Expired, (await db.Challenges.SingleAsync(c => c.Id == challengeId)).Status);
    }

    [Fact]
    public async Task Rematch_PresetsTheRecipient_OncePerParticipant()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var stranger = await Player.CreateGuestAsync(api);
        var (original, token) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await Contract.AssertProblemAsync(
            await RematchAsync(creator, original, await api.PlayRunAsync(creator, 10)), HttpStatusCode.Conflict, "challenge_not_completed");
        await AcceptAsync(recipient, token);
        await api.PlayRecipientRunAsync(recipient, original, 10); // tie

        Assert.Equal("tie", (await GetAsync(creator, original)).GetProperty("result").GetString());
        await Contract.AssertProblemAsync(
            await RematchAsync(stranger, original, await api.PlayRunAsync(stranger, 10)), HttpStatusCode.NotFound, "not_found");

        // The recipient of the original challenge creates the rematch; the creator becomes the designated recipient.
        var rematch = await Contract.AssertAsync(
            await RematchAsync(recipient, original, await api.PlayRunAsync(recipient, 10)), HttpStatusCode.Created, "ChallengeCreated");
        var rematchId = rematch.GetProperty("challengeId").GetGuid();
        var rematchToken = rematch.GetProperty("shareUrl").GetString()!.Split('/')[^1];
        await Contract.AssertProblemAsync(
            await RematchAsync(recipient, original, await api.PlayRunAsync(recipient, 10)), HttpStatusCode.Conflict, "rematch_already_created");

        var seen = await GetAsync(creator, rematchId);
        Assert.Equal(rematch.GetProperty("shareUrl").GetString(), seen.GetProperty("shareUrl").GetString());
        Assert.Equal(original, seen.GetProperty("rematchOfChallengeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await GetAsync(recipient, rematchId)).GetProperty("shareUrl").ValueKind);

        await Contract.AssertProblemAsync(await AcceptAsync(stranger, rematchToken), HttpStatusCode.NotFound, "challenge_unavailable");
        await Contract.AssertAsync(await AcceptAsync(creator, rematchToken), HttpStatusCode.OK, "ChallengeAccepted");
        Assert.Equal(JsonValueKind.Null, (await GetAsync(creator, rematchId)).GetProperty("shareUrl").ValueKind);
    }

    [Fact]
    public async Task VisibleStatus_NeverShowsCancelled_AndFiltersByIt()
    {
        var creator = await Player.CreateGuestAsync(api);
        var (cancelled, _) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        var (open, _) = await CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await using (var db = api.CreateDbContext())
        {
            await db.Challenges.Where(c => c.Id == cancelled)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChallengeStatus.Cancelled));
        }

        Assert.Equal("expired", (await GetAsync(creator, cancelled)).GetProperty("status").GetString());
        var expired = await ListAsync(creator, "?status=expired");
        Assert.Equal(cancelled, Assert.Single(expired.GetProperty("items").EnumerateArray()).GetProperty("challengeId").GetGuid());
        var openPage = await ListAsync(creator, "?status=open&limit=1");
        Assert.Equal(open, Assert.Single(openPage.GetProperty("items").EnumerateArray()).GetProperty("challengeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, openPage.GetProperty("nextCursor").ValueKind);

        var first = await ListAsync(creator, "?limit=1");
        var next = first.GetProperty("nextCursor").GetString()!;
        var second = await ListAsync(creator, $"?limit=1&cursor={next}");
        Assert.Equal(cancelled, Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("challengeId").GetGuid());
        await Contract.AssertProblemAsync(
            await creator.Http.GetAsync("/api/v1/me/challenges?status=cancelled"), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    private async Task RunOutboxAsync() =>
        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);

    private async Task<int> LedgerCountAsync(Guid playerId, string ledgerKey)
    {
        await using var db = api.CreateDbContext();
        return await db.WalletTransactions.CountAsync(t => t.PlayerId == playerId && t.LedgerKey == ledgerKey);
    }

    private async Task BlockAsync(Guid blocker, Guid blocked)
    {
        await using var db = api.CreateDbContext();
        db.PlayerBlocks.Add(new PlayerBlock { BlockerPlayerId = blocker, BlockedPlayerId = blocked, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<string> HandleAsync(Player player) => (await player.MeAsync()).GetProperty("handle").GetString()!;

    private static async Task<HttpResponseMessage> CreateAsync(Player player, Guid sessionId, string? recipientHandle = null, Guid? key = null)
    {
        object body = recipientHandle is null ? new { creatorSessionId = sessionId } : new { creatorSessionId = sessionId, recipientHandle };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await player.Http.SendAsync(request);
    }

    private static async Task<(Guid ChallengeId, string Token)> CreateOkAsync(Player player, Guid sessionId, string? recipientHandle = null)
    {
        var created = await Contract.AssertAsync(await CreateAsync(player, sessionId, recipientHandle), HttpStatusCode.Created, "ChallengeCreated");
        var shareUrl = new Uri(created.GetProperty("shareUrl").GetString()!);
        Assert.StartsWith("/challenge/", shareUrl.AbsolutePath, StringComparison.Ordinal);
        return (created.GetProperty("challengeId").GetGuid(), shareUrl.Segments[^1]);
    }

    private static async Task<HttpResponseMessage> RematchAsync(Player player, Guid challengeId, Guid sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/challenges/{challengeId}/rematch")
        {
            Content = JsonContent.Create(new { creatorSessionId = sessionId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AcceptAsync(Player player, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invites/{token}/accept");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }

    private static async Task<JsonElement> GetAsync(Player player, Guid challengeId) =>
        await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/challenges/{challengeId}"), HttpStatusCode.OK, "Challenge");

    private static async Task<JsonElement> ListAsync(Player player, string query) =>
        await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/me/challenges{query}"), HttpStatusCode.OK, "ChallengePage");

    private static async Task<HttpResponseMessage> CreateSessionAsync(Player player, Guid challengeId) =>
        await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { challengeId });
}

/// <summary>Runs for challenge tests: a verified solo run with a given number of perfect shots.</summary>
internal static class ChallengeRunExtensions
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static async Task<Guid> PlayRunAsync(this ApiFactory api, Player player, int shots)
    {
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        var body = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, shots)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        return sessionId;
    }

    /// <summary>Opens and starts the recipient session of an accepted challenge.</summary>
    public static async Task<(Guid SessionId, long Seed, StreetHoopsConfig Config, string RulesetId)> StartRecipientRunAsync(
        this Player player, Guid challengeId)
    {
        var created = await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { challengeId }), HttpStatusCode.Created, "SessionCreated");
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var rulesetId = created.GetProperty("rulesetId").GetString()!;
        var ruleset = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/rulesets/{rulesetId}"), HttpStatusCode.OK, "Ruleset");
        var config = ruleset.GetProperty("config").Deserialize<StreetHoopsConfig>(Web)!;
        var started = await Contract.AssertAsync(
            await player.Http.PostAsync($"/api/v1/games/sessions/{sessionId}/start", null), HttpStatusCode.OK, "SessionStarted");
        var session = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/sessions/{sessionId}"), HttpStatusCode.OK, "GameSession");
        Assert.Equal(challengeId, session.GetProperty("challengeId").GetGuid());
        return (sessionId, started.GetProperty("seed").GetInt64(), config, rulesetId);
    }

    /// <summary>Opens, starts and submits the recipient run; returns the verified submit body.</summary>
    public static async Task<JsonElement> PlayRecipientRunAsync(this ApiFactory api, Player player, Guid challengeId, int shots)
    {
        var (sessionId, seed, config, rulesetId) = await player.StartRecipientRunAsync(challengeId);
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        var body = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, shots)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        return body;
    }
}
