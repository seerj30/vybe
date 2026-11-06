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

    /// <summary>Test-only claim: answer with this HTTP status instead of the user record (Retry-After 7 s).</summary>
    public const string StatusClaim = "test_auth_status";

    /// <summary>Test-only claim: the user record carries this id instead of the token subject.</summary>
    public const string IdClaim = "test_auth_id";

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
        if (jwt.TryGetPayloadValue<int>(StatusClaim, out var status))
        {
            var failure = new HttpResponseMessage((HttpStatusCode)status);
            failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(failure);
        }

        var confirmed = jwt.TryGetPayloadValue<bool>(EmailConfirmedClaim, out var value) && value;
        var anonymous = jwt.TryGetPayloadValue<bool>("is_anonymous", out var isAnonymous) && isAnonymous;
        jwt.TryGetPayloadValue<string>("email", out var email);
        var user = new Dictionary<string, object?>
        {
            ["id"] = jwt.TryGetPayloadValue<string>(IdClaim, out var id) ? id : jwt.Subject,
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
