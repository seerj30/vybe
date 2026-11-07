using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>One write per player per minute; the test moves the fake clock to the next window.</summary>
public sealed class OpsRetryApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(EdgeSecurity.WritesPerPlayerKey, "1");
    }
}

/// <summary>A 429 stores nothing: the retry with the same Idempotency-Key after Retry-After runs the purchase once.</summary>
public sealed class OpsRetryTests(OpsRetryApiFactory api) : IClassFixture<OpsRetryApiFactory>
{
    [Fact]
    public async Task RetryAfter429_WithTheSameKey_RunsOnceAndThenReplays()
    {
        var player = await Player.CreateGuestAsync(api);
        var catalog = await player.Http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/catalog/items");
        // A priced item that a new player may buy: the catalog is ordered by UUIDv7 id, so the first priced
        // item can need a higher level (403 level_too_low) depending on the seed order.
        var itemId = catalog.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("price").GetInt64() > 0 && i.GetProperty("minLevel").GetInt32() <= 1)
            .GetProperty("id").GetGuid();
        var key = Guid.NewGuid();
        api.Clock.Advance(TimeSpan.FromMinutes(1)); // a new window for this player
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await player.PurchaseAsync(itemId)).StatusCode); // uses the window's write

        using var limited = await player.PurchaseAsync(itemId, key);
        await Contract.AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        var retryAfter = limited.Headers.RetryAfter?.Delta ?? throw new InvalidOperationException("no Retry-After");
        Assert.Equal(TimeSpan.FromSeconds(60), retryAfter);

        api.Clock.Advance(retryAfter);
        using var retried = await player.PurchaseAsync(itemId, key);
        await Contract.AssertProblemAsync(retried, HttpStatusCode.Conflict, "insufficient_funds");

        api.Clock.Advance(retryAfter);
        using var replayed = await player.PurchaseAsync(itemId, key);
        await Contract.AssertProblemAsync(replayed, HttpStatusCode.Conflict, "insufficient_funds");
        await using var db = api.CreateDbContext();
        Assert.Equal(1, db.IdempotencyRecords.Count(r => r.Key == key));
    }

    [Fact]
    public void ClockLimiter_FollowsTheClock()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new ClockRateLimiter(2, TimeSpan.FromMinutes(1), clock);

        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        clock.Advance(TimeSpan.FromSeconds(45));
        using var denied = limiter.AttemptAcquire();
        Assert.False(denied.IsAcquired);
        Assert.True(denied.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(15), retryAfter);
        Assert.Null(limiter.IdleDuration);

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(limiter.AttemptAcquire().IsAcquired);
    }
}
