using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Vybe.Api.Tests;

/// <summary>
/// Stands in for Supabase Auth <c>GET {iss}/user</c>: answers with the user record of the bearer token.
/// The test-only claim <see cref="EmailConfirmedClaim"/> decides <c>email_confirmed_at</c>, as the server-side
/// record would. The API already validated the token, so this fake only decodes it.
/// </summary>
public sealed class FakeSupabaseAuth : HttpMessageHandler
{
    public const string ApiKey = "test-publishable-key";
    public const string EmailConfirmedClaim = "test_email_confirmed";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri?.AbsoluteUri != $"{ApiFactory.TokenIssuer}/user"
            || !request.Headers.TryGetValues("apikey", out var keys) || keys.Single() != ApiKey
            || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token })
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var jwt = new JsonWebToken(token);
        var confirmed = jwt.TryGetPayloadValue<bool>(EmailConfirmedClaim, out var value) && value;
        var anonymous = jwt.TryGetPayloadValue<bool>("is_anonymous", out var isAnonymous) && isAnonymous;
        jwt.TryGetPayloadValue<string>("email", out var email);
        var user = new Dictionary<string, object?>
        {
            ["id"] = jwt.Subject,
            ["email"] = email ?? string.Empty,
            ["is_anonymous"] = anonymous,
            ["email_confirmed_at"] = confirmed ? "2026-11-06T10:00:00Z" : null,
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json"),
        });
    }
}
