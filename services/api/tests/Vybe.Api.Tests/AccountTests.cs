using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec §2 J3, §14 E03 and "account link conflict"; every response is checked against the contract.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AccountTests(ApiFactory api)
{
    [Fact]
    public async Task E03_GuestLinksAVerifiedIdentity_AndKeepsPlayerIdWalletAndInventory()
    {
        var guest = await Player.CreateGuestAsync(api);
        await guest.PlayPerfectRunAsync();
        var before = await guest.MeAsync();
        Assert.Equal("guest", before.GetProperty("kind").GetString());
        var inventoryBefore = await guest.Http.GetStringAsync("/api/v1/me/inventory");
        var subject = Guid.NewGuid().ToString();

        var response = await LinkAsync(guest, ApiFactory.RegisteredToken(subject));
        var linked = await Contract.AssertAsync(response, HttpStatusCode.OK, "AccountLinked");
        Assert.Equal(guest.Id, linked.GetProperty("playerId").GetGuid());
        var cleared = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-vybe_guest=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);

        // The guest credentials are revoked: the cookie no longer authenticates.
        await Contract.AssertProblemAsync(await guest.Http.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await using (var db = api.CreateDbContext())
        {
            Assert.All(await db.GuestSessions.Where(s => s.PlayerId == guest.Id).ToListAsync(), s => Assert.NotNull(s.RevokedAt));
        }

        // The same player, wallet and inventory behind the Supabase identity.
        using var registered = Bearer(ApiFactory.RegisteredToken(subject));
        var after = await Contract.AssertAsync(await registered.GetAsync("/api/v1/me"), HttpStatusCode.OK, "Me");
        Assert.Equal(guest.Id, after.GetProperty("id").GetGuid());
        Assert.Equal("registered", after.GetProperty("kind").GetString());
        Assert.Equal(before.GetProperty("walletBalance").GetInt64(), after.GetProperty("walletBalance").GetInt64());
        Assert.Equal(before.GetProperty("xpTotal").GetInt64(), after.GetProperty("xpTotal").GetInt64());
        Assert.Equal(inventoryBefore, await registered.GetStringAsync("/api/v1/me/inventory"));
    }

    [Fact]
    public async Task LinkConflict_NeverMerges_AndTheSecondGuestStaysValid()
    {
        var first = await Player.CreateGuestAsync(api);
        var second = await Player.CreateGuestAsync(api);
        var subject = Guid.NewGuid().ToString();
        await Contract.AssertAsync(await LinkAsync(first, ApiFactory.RegisteredToken(subject)), HttpStatusCode.OK, "AccountLinked");

        await Contract.AssertProblemAsync(
            await LinkAsync(second, ApiFactory.RegisteredToken(subject)), HttpStatusCode.Conflict, "identity_already_linked");
        var me = await second.MeAsync();
        Assert.Equal(second.Id, me.GetProperty("id").GetGuid());
        Assert.Equal("guest", me.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ConcurrentLinksOfOneIdentity_OneWins()
    {
        var guests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Player.CreateGuestAsync(api)));
        var subject = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(guests.Select(g => LinkAsync(g, ApiFactory.RegisteredToken(subject))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var conflict in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await Contract.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "identity_already_linked");
        }

        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.Players.CountAsync(p => p.AuthSubject == subject && p.Kind == PlayerKind.Registered));
    }

    public static TheoryData<string> RejectedProofs => new()
    {
        "not-a-jwt",
        ApiFactory.RegisteredToken(Guid.NewGuid().ToString(), anonymous: true),
        ApiFactory.RegisteredToken(Guid.NewGuid().ToString(), emailVerified: false), // user_metadata.email_verified is true here
        ApiFactory.SupabaseToken(Guid.NewGuid().ToString()), // no is_anonymous, email or user_metadata
        ApiFactory.SupabaseToken(
            Guid.NewGuid().ToString(),
            new Dictionary<string, object> { ["is_anonymous"] = false, ["email"] = "x@vybe.test", ["user_metadata"] = new Dictionary<string, object> { ["email_verified"] = true } },
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-signing-key-0123456789abcdef"))),
    };

    [Theory]
    [MemberData(nameof(RejectedProofs))]
    public async Task RejectedProof_Is422_AndTheGuestSessionStaysValid(string proof)
    {
        var guest = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await LinkAsync(guest, proof), HttpStatusCode.UnprocessableEntity, "invalid_link_proof");
        Assert.Equal("guest", (await guest.MeAsync()).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Link_NeedsTheGuestCookieAndTheCsrfHeader()
    {
        var guest = await Player.CreateGuestAsync(api);
        var proof = ApiFactory.RegisteredToken(Guid.NewGuid().ToString());

        // A bearer header does not skip the CSRF check here: the endpoint authenticates with the cookie.
        guest.Http.DefaultRequestHeaders.Remove("X-Requested-With");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts/link") { Content = JsonContent.Create(new { linkProof = proof }) })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", proof);
            await Contract.AssertProblemAsync(await guest.Http.SendAsync(request), HttpStatusCode.Forbidden, "csrf_check_failed");
        }

        // Without a guest cookie, a bearer token alone is not a guest session.
        using var registered = Bearer(proof);
        registered.DefaultRequestHeaders.Add("X-Requested-With", "vybe-web");
        await Contract.AssertProblemAsync(
            await registered.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = proof }), HttpStatusCode.Unauthorized, "unauthenticated");
        await Contract.AssertProblemAsync(
            await LinkAsync(await Player.CreateGuestAsync(api), string.Empty), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    private static async Task<HttpResponseMessage> LinkAsync(Player guest, string proof) =>
        await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = proof });

    private HttpClient Bearer(string token)
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
