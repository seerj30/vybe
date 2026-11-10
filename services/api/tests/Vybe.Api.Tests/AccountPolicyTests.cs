using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Api.Jobs;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>
/// An own server and database: these tests move the clock by months, and they configure the Supabase Auth
/// admin API (service-role key) with a fake that records the deletions.
/// </summary>
public sealed class AccountPolicyApiFactory : ApiFactory
{
    public const string ServiceRoleKey = "test-service-role-key";

    public FakeSupabaseAdmin Admin { get; } = new();

    /// <summary>Every log line of the API (all categories, Debug and up), to prove what never reaches the logs.</summary>
    public LogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
        // Not through Auth:Supabase:Authority: that setting would also make the bearer scheme fetch metadata.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOptions<SupabaseAdminOptions>>();
            services.AddSingleton(Options.Create(new SupabaseAdminOptions { Authority = TokenIssuer, ServiceRoleKey = ServiceRoleKey }));
            services.AddHttpClient(SupabaseAuthUserAdmin.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Admin);
        });
    }
}

/// <summary>Keeps every formatted log message, its scopes and its exception text.</summary>
public sealed class LogSink : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class SinkLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => sink._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new StringBuilder();
            sink._scopes.ForEachScope((scope, text) => text.Append(scope).Append(' '), scopes);
            sink.Messages.Enqueue($"{category} {formatter(state, exception)} {scopes} {exception}");
        }
    }
}

/// <summary>Stands in for <c>DELETE {iss}/admin/users/{id}</c> of Supabase Auth; the next answers can be queued.</summary>
public sealed class FakeSupabaseAdmin : HttpMessageHandler
{
    public ConcurrentQueue<(HttpStatusCode Status, string Body)> Answers { get; } = new();

    public ConcurrentQueue<string> Deleted { get; } = new();

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(request);
        var prefix = $"{ApiFactory.TokenIssuer}/admin/users/";
        var url = request.RequestUri!.AbsoluteUri;
        if (request.Method != HttpMethod.Delete || !url.StartsWith(prefix, StringComparison.Ordinal)
            || request.Headers.GetValues("apikey").Single() != AccountPolicyApiFactory.ServiceRoleKey
            || request.Headers.Authorization?.ToString() != $"Bearer {AccountPolicyApiFactory.ServiceRoleKey}")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var (status, body) = Answers.TryDequeue(out var answer) ? answer : (HttpStatusCode.OK, "{}");
        if (status == HttpStatusCode.OK)
        {
            Deleted.Enqueue(url[prefix.Length..]);
        }

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Spec §17 decisions 1, 2, 6 and 8 (spec v1.4): age gate, guest lifetime and cleanup, profile visibility, Auth user deletion.</summary>
public sealed class AccountPolicyTests(AccountPolicyApiFactory api) : IClassFixture<AccountPolicyApiFactory>
{
    // ---------------------------------------------------------------- decision 1: age gate

    [Fact]
    public async Task CreateGuest_ValidatesTheBirthMonth()
    {
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var browser = api.CreateBrowser();
        await Contract.AssertProblemAsync(await browser.PostAsync("/api/v1/guests", null), HttpStatusCode.UnprocessableEntity, "validation_failed");
        foreach (var body in new object[]
        {
            new { },
            new { birthYear = 1990 },
            new { birthMonth = 6 },
            new { birthYear = 1990, birthMonth = 0 },
            new { birthYear = 1990, birthMonth = 13 },
            new { birthYear = now.Year - AgePolicy.MaxAge - 1, birthMonth = 12 },
            new { birthYear = now.AddMonths(1).Year, birthMonth = now.AddMonths(1).Month }, // next month: not born yet
        })
        {
            await Contract.AssertProblemAsync(await browser.PostAsJsonAsync("/api/v1/guests", body), HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        await Contract.AssertProblemAsync(
            await browser.PostAsJsonAsync("/api/v1/guests", new { birthYear = "nineteen", birthMonth = 6 }), HttpStatusCode.BadRequest, "malformed_request");
    }

    /// <summary>The 18th birthday month must be before the current UTC month: the month before passes, the same month and the next do not.</summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task CreateGuest_AgeBoundary_IsTheMonthOfThe18thBirthday(int monthsFromNow, bool accepted)
    {
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var birthday18 = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(monthsFromNow);
        var born = birthday18.AddYears(-AgePolicy.MinimumAge);
        var browser = api.CreateBrowser();
        await using var db = api.CreateDbContext();
        var playersBefore = await db.Players.CountAsync();

        var response = await browser.PostAsJsonAsync("/api/v1/guests", new { birthYear = born.Year, birthMonth = born.Month });

        if (accepted)
        {
            var created = await Contract.AssertAsync(response, HttpStatusCode.Created, "GuestCreated");
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == created.GetProperty("playerId").GetGuid());
            Assert.Equal((AgeBand.Adult, AgePolicy.Version), (player.AgeBand, player.AgePolicyVersion));
            Assert.NotNull(player.AgeConfirmedAt);
            return;
        }

        // Under age: no player, no cookie, nothing stored.
        await Contract.AssertProblemAsync(response, HttpStatusCode.Forbidden, "age_not_eligible");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(playersBefore, await db.Players.CountAsync());
        await Contract.AssertProblemAsync(await browser.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task CreateGuest_NeverStoresTheBirthMonth_AndAllowsAnotherAnswerAfterAnUnderAgeOne()
    {
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var browser = api.CreateBrowser();
        // The gate is a self-declaration: nothing remembers the first answer, so a second answer is not blocked.
        await Contract.AssertProblemAsync(
            await browser.PostAsJsonAsync("/api/v1/guests", new { birthYear = now.Year - 10, birthMonth = 1 }), HttpStatusCode.Forbidden, "age_not_eligible");
        await Contract.AssertAsync(await browser.PostAsync("/api/v1/guests", Player.AdultGuest()), HttpStatusCode.Created, "GuestCreated");

        await using var db = api.CreateDbContext();
        var columns = await db.Database.SqlQuery<string>(
            $"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'vybe'").ToListAsync();
        Assert.DoesNotContain(columns, c => c.Contains("birth", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Logs.Messages, m => m.Contains("birth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateGuest_UnderAge_WithASignedInIdentityWithoutPlayer_QueuesTheAuthUserForDeletion()
    {
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var subject = Guid.NewGuid().ToString();
        using var signedIn = Bearer(ApiFactory.RegisteredToken(subject));
        signedIn.DefaultRequestHeaders.Add(CsrfMiddleware.Header, CsrfMiddleware.Value);

        var response = await signedIn.PostAsJsonAsync("/api/v1/guests", new { birthYear = now.Year - 12, birthMonth = 3 });

        await Contract.AssertProblemAsync(response, HttpStatusCode.Forbidden, "age_not_eligible");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var db = api.CreateDbContext();
        Assert.True(await db.AuthUserDeletions.AnyAsync(d => d.AuthSubject == subject));
        Assert.False(await db.Players.AnyAsync(p => p.AuthSubject == subject));
        await RunPrivacyAsync();
        Assert.Contains(subject, api.Admin.Deleted);
    }

    [Fact]
    public async Task AccountLinking_KeepsTheAgeGateResult()
    {
        var guest = await Player.CreateGuestAsync(api);
        await using var db = api.CreateDbContext();
        var before = await db.Players.AsNoTracking().SingleAsync(p => p.Id == guest.Id);

        await Contract.AssertAsync(
            await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(Guid.NewGuid().ToString()) }),
            HttpStatusCode.OK, "AccountLinked");

        var after = await db.Players.AsNoTracking().SingleAsync(p => p.Id == guest.Id);
        Assert.Equal(PlayerKind.Registered, after.Kind);
        Assert.Equal((before.AgeConfirmedAt, before.AgePolicyVersion, before.AgeBand), (after.AgeConfirmedAt, after.AgePolicyVersion, after.AgeBand));
    }

    // ---------------------------------------------------------------- decision 2: guest lifetime

    [Fact]
    public async Task GuestSession_SlidesOncePerUtcDay_AndEndsAtTheCap()
    {
        MoveToUtcHour(1);
        var guest = await Player.CreateGuestAsync(api);
        var created = api.Clock.GetUtcNow().UtcDateTime;
        Assert.Equal(created + GuestTokens.IdleLifetime, await GuestExpiresAtAsync(guest), TimeSpan.FromSeconds(1));

        // Same UTC day: no renewal, no cookie.
        api.Clock.Advance(TimeSpan.FromHours(20));
        var sameDay = await guest.Http.GetAsync("/api/v1/me");
        Assert.False(sameDay.Headers.Contains("Set-Cookie"));
        Assert.Equal(created + GuestTokens.IdleLifetime, await GuestExpiresAtAsync(guest), TimeSpan.FromSeconds(1));

        // Next UTC day: one renewal, sent as a new cookie; the second request that day writes nothing.
        api.Clock.Advance(TimeSpan.FromHours(4));
        var renewed = await guest.Http.GetAsync("/api/v1/me/avatar");
        var cookie = Assert.Single(renewed.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-vybe_guest=", cookie, StringComparison.Ordinal);
        Assert.Equal(api.Clock.GetUtcNow().UtcDateTime + GuestTokens.IdleLifetime, await GuestExpiresAtAsync(guest), TimeSpan.FromSeconds(1));
        Assert.False((await guest.Http.GetAsync("/api/v1/me")).Headers.Contains("Set-Cookie"));

        // Active every 29 days: the session never passes 90 days after creation.
        for (var i = 0; i < 3; i++)
        {
            api.Clock.Advance(TimeSpan.FromDays(29));
            await guest.MeAsync();
        }

        Assert.Equal(created + GuestTokens.MaxLifetime, await GuestExpiresAtAsync(guest), TimeSpan.FromSeconds(1));
        api.Clock.Advance(created + GuestTokens.MaxLifetime - api.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1));
        await Contract.AssertProblemAsync(await guest.Http.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task GuestSession_IdleFor30Days_Ends()
    {
        var guest = await Player.CreateGuestAsync(api);
        api.Clock.Advance(GuestTokens.IdleLifetime + TimeSpan.FromSeconds(1));
        await Contract.AssertProblemAsync(await guest.Http.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Me_GuestExpiresAt_IsNullForRegisteredPlayers()
    {
        var guest = await Player.CreateGuestAsync(api);
        Assert.Equal(JsonValueKind.String, (await guest.MeAsync()).GetProperty("guestExpiresAt").ValueKind);
        var subject = Guid.NewGuid().ToString();
        await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(subject) });

        using var registered = Bearer(ApiFactory.RegisteredToken(subject));
        var me = await Contract.AssertAsync(await registered.GetAsync("/api/v1/me"), HttpStatusCode.OK, "Me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("guestExpiresAt").ValueKind);
    }

    [Fact]
    public async Task GuestCleanup_Anonymizes30DaysAfterTheLastSessionEnded_AndKeepsActivePlayers()
    {
        var idle = await Player.CreateGuestAsync(api);
        var active = await Player.CreateGuestAsync(api);
        var linked = await Player.CreateGuestAsync(api);
        await linked.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(Guid.NewGuid().ToString()) });
        var cleanup = api.Services.GetRequiredService<GuestCleanup>();

        // The idle guest's session ends after 30 days; the active guest keeps using its session (8 weeks).
        for (var week = 0; week < 8; week++)
        {
            api.Clock.Advance(TimeSpan.FromDays(7));
            await active.MeAsync();
            await cleanup.RunOnceAsync(CancellationToken.None);
        }

        await using var db = api.CreateDbContext();
        Assert.Equal(PlayerStatus.Active, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == idle.Id)).Status);

        api.Clock.Advance(TimeSpan.FromDays(7)); // 63 days: the idle session ended 33 days ago
        await active.MeAsync();
        Assert.True(await cleanup.RunOnceAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await cleanup.RunOnceAsync(CancellationToken.None)); // a repeated run changes nothing
        Assert.Equal(PlayerStatus.Deleting, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == idle.Id)).Status);
        Assert.Equal(DeletionStatus.Queued, (await db.DeletionRequests.AsNoTracking().SingleAsync(r => r.PlayerId == idle.Id)).Status);

        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);
        var anonymized = await db.Players.AsNoTracking().SingleAsync(p => p.Id == idle.Id);
        Assert.Equal(PlayerStatus.Deleted, anonymized.Status);
        Assert.Matches("^d_[a-z0-9]{18}$", anonymized.Handle);
        Assert.Null(anonymized.AgeConfirmedAt);
        Assert.Equal(PlayerStatus.Active, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == active.Id)).Status);
        Assert.Equal(PlayerStatus.Active, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == linked.Id)).Status);
    }

    // ---------------------------------------------------------------- decision 6: profiles

    [Fact]
    public async Task Profile_AnonymousCallers_GetTheUnknownHandle404_WithNoIndexHeaders()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var handle = (await player.MeAsync()).GetProperty("handle").GetString()!;

        var anonymous = await api.CreateBrowser().GetAsync($"/api/v1/profiles/{handle}");
        var unknown = await other.Http.GetAsync("/api/v1/profiles/no_such_player_x");
        foreach (var response in new[] { anonymous, unknown })
        {
            AssertNoIndex(response);
            await Contract.AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        var visible = await other.Http.GetAsync($"/api/v1/profiles/{handle}");
        AssertNoIndex(visible);
        await Contract.AssertAsync(visible, HttpStatusCode.OK, "PublicProfile");
    }

    [Fact]
    public async Task PrivateProfile_IsVisibleOnlyToItsOwner_AndGetsNoProfileChallenges()
    {
        var owner = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var handle = (await owner.MeAsync()).GetProperty("handle").GetString()!;
        Assert.Equal("players", (await owner.MeAsync()).GetProperty("profileVisibility").GetString());

        var set = await Contract.AssertAsync(await SetVisibilityAsync(owner, "private"), HttpStatusCode.OK, "PrivacySettings");
        Assert.Equal("private", set.GetProperty("profileVisibility").GetString());
        Assert.Equal("private", (await owner.MeAsync()).GetProperty("profileVisibility").GetString());
        await Contract.AssertAsync(await SetVisibilityAsync(owner, "private"), HttpStatusCode.OK, "PrivacySettings"); // idempotent

        await Contract.AssertAsync(await owner.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.OK, "PublicProfile");
        await Contract.AssertProblemAsync(await other.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.NotFound, "not_found");
        var run = await api.PlayRunAsync(other, 10);
        await Contract.AssertProblemAsync(await ChallengeAsync(other, run, handle), HttpStatusCode.NotFound, "not_found");

        await Contract.AssertAsync(await SetVisibilityAsync(owner, "players"), HttpStatusCode.OK, "PrivacySettings");
        await Contract.AssertAsync(await other.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.OK, "PublicProfile");
        await Contract.AssertAsync(await ChallengeAsync(other, run, handle), HttpStatusCode.Created, "ChallengeCreated");
    }

    [Fact]
    public async Task PrivacySettings_RejectUnknownValues_AndNeedAuthenticationAndCsrf()
    {
        var player = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await SetVisibilityAsync(player, "public"), HttpStatusCode.BadRequest, "malformed_request");
        await Contract.AssertProblemAsync(
            await player.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = 7 }), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(
            await player.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { }), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await SetVisibilityAsync(api.CreateBrowser(), "private"), HttpStatusCode.Unauthorized, "unauthenticated");

        player.Http.DefaultRequestHeaders.Remove(CsrfMiddleware.Header);
        await Contract.AssertProblemAsync(await SetVisibilityAsync(player, "private"), HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    // ---------------------------------------------------------------- decision 8: Auth user deletion

    [Fact]
    public async Task Deletion_DeletesTheSupabaseAuthUser_AndRetriesWithoutBlockingTheAnonymization()
    {
        var subject = Guid.NewGuid();
        var player = await RegisteredAsync(subject);
        api.Admin.Answers.Enqueue((HttpStatusCode.ServiceUnavailable, "{}"));
        await RequestDeletionAsync(subject);

        await RunPrivacyAsync();
        await using var db = api.CreateDbContext();
        var deleted = await db.Players.AsNoTracking().SingleAsync(p => p.Id == player);
        Assert.Equal((PlayerStatus.Deleted, null), (deleted.Status, deleted.AuthSubject)); // anonymized although Auth failed
        var queued = await db.AuthUserDeletions.AsNoTracking().SingleAsync(d => d.AuthSubject == subject.ToString());
        Assert.Equal(1, queued.Attempts);
        Assert.DoesNotContain(subject.ToString(), api.Admin.Deleted);

        await RunPrivacyAsync(); // not due yet: no second call
        Assert.Equal(1, (await db.AuthUserDeletions.AsNoTracking().SingleAsync(d => d.AuthSubject == subject.ToString())).Attempts);

        api.Clock.Advance(PrivacyProcessor.AuthRetryDelay(1));
        await RunPrivacyAsync();
        Assert.Contains(subject.ToString(), api.Admin.Deleted);
        Assert.False(await db.AuthUserDeletions.AnyAsync(d => d.AuthSubject == subject.ToString()));
    }

    [Fact]
    public async Task AuthUserDeletion_UserNotFound_CountsAsDeleted_ButAnyOther404IsRetried()
    {
        var gone = Guid.NewGuid();
        var wrongPath = Guid.NewGuid();
        await RegisteredAsync(gone);
        await RegisteredAsync(wrongPath);
        await RequestDeletionAsync(gone);
        await RunPrivacyAsync(); // deletes the first Auth user before the second request exists
        Assert.Contains(gone.ToString(), api.Admin.Deleted);

        api.Admin.Answers.Enqueue((HttpStatusCode.NotFound, """{"code":404,"error_code":"user_not_found","msg":"User not found"}"""));
        await using var db = api.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO vybe.auth_user_deletions (auth_subject) VALUES ({Guid.NewGuid().ToString()})");
        await db.Database.ExecuteSqlAsync($"UPDATE vybe.auth_user_deletions SET next_attempt_at = {api.Clock.GetUtcNow().UtcDateTime.AddDays(-1)}");
        await RunPrivacyAsync();
        Assert.False(await db.AuthUserDeletions.AnyAsync());

        api.Admin.Answers.Enqueue((HttpStatusCode.NotFound, "<html>not here</html>"));
        await RequestDeletionAsync(wrongPath);
        await RunPrivacyAsync();
        Assert.Equal(1, (await db.AuthUserDeletions.AsNoTracking().SingleAsync(d => d.AuthSubject == wrongPath.ToString())).Attempts);
    }

    [Fact]
    public void AuthRetryDelay_GrowsAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), PrivacyProcessor.AuthRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(8), PrivacyProcessor.AuthRetryDelay(4));
        Assert.Equal(PrivacyProcessor.MaxAuthRetryDelay, PrivacyProcessor.AuthRetryDelay(500));
    }

    // ---------------------------------------------------------------- review follow-ups

    [Fact]
    public async Task Renewal_TwoParallelRequests_WriteOnce_AndSendOneCookie()
    {
        MoveToUtcHour(2);
        var guest = await Player.CreateGuestAsync(api);
        api.Clock.Advance(TimeSpan.FromDays(1));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => guest.Http.GetAsync("/api/v1/me/avatar")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(responses, r => r.Headers.Contains("Set-Cookie"));
        Assert.Equal(api.Clock.GetUtcNow().UtcDateTime + GuestTokens.IdleLifetime, await GuestExpiresAtAsync(guest), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Renewal_NeverRevivesARevokedSession()
    {
        var guest = await Player.CreateGuestAsync(api);
        await using var db = api.CreateDbContext();
        var revokedAt = api.Clock.GetUtcNow().UtcDateTime;
        await db.GuestSessions.Where(s => s.PlayerId == guest.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, revokedAt));
        var before = await db.GuestSessions.AsNoTracking().SingleAsync(s => s.PlayerId == guest.Id);
        api.Clock.Advance(TimeSpan.FromDays(1));

        var response = await guest.Http.GetAsync("/api/v1/me");

        await Contract.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var after = await db.GuestSessions.AsNoTracking().SingleAsync(s => s.PlayerId == guest.Id);
        Assert.Equal((before.ExpiresAt, before.RevokedAt), (after.ExpiresAt, after.RevokedAt));
    }

    [Fact]
    public async Task GuestCleanup_EdgeCases_AndBatchesOver500()
    {
        var reporter = await Player.CreateGuestAsync(api); // also gives InsertGuestAsync an avatar row to copy
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var expired = now - GuestCleanup.Grace - TimeSpan.FromDays(1);
        await using var db = api.CreateDbContext();
        var noSession = await InsertGuestAsync(db, "active", sessionEndsAt: null);
        var recentlyRevoked = await InsertGuestAsync(db, "active", sessionEndsAt: now + TimeSpan.FromDays(20), revokedAt: now - TimeSpan.FromDays(5));
        var oldRevoked = await InsertGuestAsync(db, "active", sessionEndsAt: now + TimeSpan.FromDays(20), revokedAt: expired);
        var suspendedReported = await InsertGuestAsync(db, "suspended", sessionEndsAt: expired);
        var suspended = await InsertGuestAsync(db, "suspended", sessionEndsAt: expired);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_reports (id, reporter_player_id, target_player_id, reason, status, created_at)
            VALUES ({Guid.NewGuid()}, {reporter.Id}, {suspendedReported}, 'cheating', 'open', {now})
            """);
        var bulk = new List<Guid>();
        for (var i = 0; i < GuestCleanup.BatchSize + 1; i++)
        {
            bulk.Add(await InsertGuestAsync(db, "active", sessionEndsAt: expired));
        }

        var queued = await api.Services.GetRequiredService<GuestCleanup>().RunOnceAsync(CancellationToken.None);

        Assert.True(queued >= GuestCleanup.BatchSize + 3, $"queued {queued}"); // bulk, oldRevoked, suspended (+ guests of other tests)
        var statuses = await db.Players.AsNoTracking()
            .Where(p => bulk.Contains(p.Id) || new[] { noSession, recentlyRevoked, oldRevoked, suspendedReported, suspended }.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Status);
        Assert.All(bulk, id => Assert.Equal(PlayerStatus.Deleting, statuses[id]));
        Assert.Equal(PlayerStatus.Active, statuses[noSession]);
        Assert.Equal(PlayerStatus.Active, statuses[recentlyRevoked]);
        Assert.Equal(PlayerStatus.Deleting, statuses[oldRevoked]);
        Assert.Equal(PlayerStatus.Suspended, statuses[suspendedReported]);
        Assert.Equal(PlayerStatus.Deleting, statuses[suspended]);

        await RunPrivacyAsync(); // anonymizes them, so later passes of other tests stay small
        var last = bulk[^1];
        Assert.Equal(PlayerStatus.Deleted, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == last)).Status);
    }

    [Fact]
    public async Task PrivateProfile_CanStillBeBlockedAndReported()
    {
        var target = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var handle = (await target.MeAsync()).GetProperty("handle").GetString()!;
        await Contract.AssertAsync(await SetVisibilityAsync(target, "private"), HttpStatusCode.OK, "PrivacySettings");

        // Accepted risk (ADR 0005, decision 6): these answers tell that the handle exists.
        Assert.Equal(HttpStatusCode.NoContent, (await other.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle })).StatusCode);
        await Contract.AssertAsync(
            await other.Http.PostAsJsonAsync("/api/v1/reports", new { targetHandle = handle, reason = "offensive_name" }), HttpStatusCode.Accepted, "ReportAccepted");
    }

    [Fact]
    public async Task AuthDeletion_LogsNeitherTheUserIdNorTheKey_AndAFailureEndsThePass()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await RegisteredAsync(first);
        await RegisteredAsync(second);
        api.Admin.Answers.Enqueue((HttpStatusCode.BadGateway, "{}"));
        var callsBefore = api.Admin.Requests.Count;
        await RequestDeletionAsync(first);
        await RequestDeletionAsync(second);

        await RunPrivacyAsync();

        Assert.Equal(callsBefore + 1, api.Admin.Requests.Count); // the failure ended the Auth step of this pass
        await using var db = api.CreateDbContext();
        Assert.Equal(2, await db.AuthUserDeletions.CountAsync(d => d.AuthSubject == first.ToString() || d.AuthSubject == second.ToString()));

        api.Clock.Advance(PrivacyProcessor.AuthRetryDelay(1));
        await RunPrivacyAsync();
        Assert.Contains(first.ToString(), api.Admin.Deleted);
        Assert.Contains(second.ToString(), api.Admin.Deleted);
        Assert.Contains(api.Logs.Messages, m => m.Contains("Supabase Auth user failed", StringComparison.Ordinal)); // the sink works
        foreach (var secret in new[] { first.ToString(), second.ToString(), AccountPolicyApiFactory.ServiceRoleKey })
        {
            Assert.DoesNotContain(api.Logs.Messages, m => m.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A guest row written directly (no API): optional one session that ends at <paramref name="sessionEndsAt"/>.</summary>
    private static async Task<Guid> InsertGuestAsync(VybeDbContext db, string status, DateTime? sessionEndsAt, DateTime? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        var handle = "edge_" + Guid.NewGuid().ToString("N")[..12];
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES ({id}, {handle}, {handle}, 'guest', {status});
            INSERT INTO vybe.avatar_configs (player_id, body_preset_id, skin_tone_id, face_preset_id, hair_preset_id, hair_color_id, equipped_json)
            SELECT {id}, body_preset_id, skin_tone_id, face_preset_id, hair_preset_id, hair_color_id, equipped_json FROM vybe.avatar_configs LIMIT 1;
            """);
        if (sessionEndsAt is { } ends)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vybe.guest_sessions (id, player_id, token_hash, expires_at, revoked_at)
                VALUES ({Guid.NewGuid()}, {id}, {System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)}, {ends}, {revokedAt})
                """);
        }

        return id;
    }


    private void MoveToUtcHour(int hour)
    {
        var now = api.Clock.GetUtcNow();
        var target = now.Date.AddDays(1).AddHours(hour);
        api.Clock.Advance(target - now.UtcDateTime);
    }

    private static async Task<DateTime> GuestExpiresAtAsync(Player guest) =>
        (await guest.MeAsync()).GetProperty("guestExpiresAt").GetDateTime();

    private static void AssertNoIndex(HttpResponseMessage response)
    {
        Assert.Equal("noindex", response.Headers.NonValidated["X-Robots-Tag"].ToString());
        Assert.Equal("private, no-store", response.Headers.NonValidated["Cache-Control"].ToString());
    }

    private static Task<HttpResponseMessage> SetVisibilityAsync(Player player, string visibility) =>
        SetVisibilityAsync(player.Http, visibility);

    private static Task<HttpResponseMessage> SetVisibilityAsync(HttpClient http, string visibility) =>
        http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = visibility });

    private static async Task<HttpResponseMessage> ChallengeAsync(Player player, Guid sessionId, string recipientHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges")
        {
            Content = JsonContent.Create(new { creatorSessionId = sessionId, recipientHandle }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }

    private async Task<Guid> RegisteredAsync(Guid subject)
    {
        var guest = await Player.CreateGuestAsync(api);
        await Contract.AssertAsync(
            await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(subject.ToString()) }),
            HttpStatusCode.OK, "AccountLinked");
        return guest.Id;
    }

    private async Task RequestDeletionAsync(Guid subject)
    {
        using var registered = Bearer(ApiFactory.RegisteredToken(subject.ToString(), authenticatedAt: api.Clock.GetUtcNow()));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await Contract.AssertAsync(await registered.SendAsync(request), HttpStatusCode.Accepted, "DeletionRequested");
    }

    private Task RunPrivacyAsync() => api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);

    private HttpClient Bearer(string token)
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
