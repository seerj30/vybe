using System.Net;
using System.Net.Http.Json;

namespace Vybe.Api.Tests;

/// <summary>Create-session and submit take the player row before the session row, so they never deadlock.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SessionLockTests(ApiFactory api)
{
    [Fact]
    public async Task CreateSession_AndSubmitOfTheOpenSession_Concurrently_NeverFail()
    {
        for (var round = 0; round < 5; round++)
        {
            var player = await Player.CreateGuestAsync(api);
            var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
            api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));

            var submit = player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 93));
            var create = player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { });
            await Task.WhenAll(submit, create);

            // Either order is valid: verified, or expired because the new session replaced it first.
            var result = await Contract.AssertAsync(await submit, HttpStatusCode.OK, "SubmitResult");
            Assert.True(result.GetProperty("status").GetString() is "verified" or "expired", result.ToString());
            await Contract.AssertAsync(await create, HttpStatusCode.Created, "SessionCreated");
        }
    }
}
