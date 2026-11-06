using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Features;

/// <summary>Supabase Auth settings used by account linking (ADR 0005).</summary>
public sealed class SupabaseAuthOptions
{
    public const string Section = "Auth:Supabase";

    /// <summary>The project's publishable (anon) API key, sent as <c>apikey</c> to the Auth user endpoint.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>Supabase Auth did not answer usefully (429, 5xx, timeout): the proof is neither valid nor invalid.</summary>
public sealed class AuthUnavailableException(TimeSpan? retryAfter, Exception? inner = null)
    : Exception("Supabase Auth is unavailable", inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Verifies the <c>linkProof</c> of <c>POST /accounts/link</c> (spec §2 J3, §5) in two steps.</summary>
public interface ILinkProofVerifier
{
    /// <summary>Local check only (signature, issuer, audience, lifetime): the token subject, or null.</summary>
    Task<string?> ReadSubjectAsync(string proof, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Supabase Auth whether the user of a locally valid proof is not anonymous and has a confirmed email.
    /// Throws <see cref="AuthUnavailableException"/> when Auth cannot answer.
    /// </summary>
    Task<bool> IsConfirmedAsync(string proof, string subject, CancellationToken cancellationToken);
}

/// <summary>
/// Validates the proof as a Supabase access token with the bearer scheme's issuer, audience, algorithms
/// and signing keys (one key refresh on an unknown key id). Then it reads the user from the issuer's Auth
/// endpoint (<c>{iss}/user</c>), because only the server-side user record says whether the email is
/// confirmed: token claims such as <c>user_metadata</c> can be changed by the user (ADR 0005).
/// </summary>
public sealed class SupabaseLinkProofVerifier(
    IOptionsMonitor<JwtBearerOptions> bearerOptions, IHttpClientFactory httpClients, IOptions<SupabaseAuthOptions> authOptions)
    : ILinkProofVerifier
{
    public const string HttpClientName = "supabase-auth";
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<string?> ReadSubjectAsync(string proof, CancellationToken cancellationToken)
    {
        var bearer = bearerOptions.Get(AuthSchemes.Supabase);
        var result = await ValidateAsync(proof, bearer, cancellationToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && bearer.ConfigurationManager is { } manager)
        {
            // Supabase rotated its signing keys: refresh the metadata (rate-limited by the manager) and try once more.
            manager.RequestRefresh();
            result = await ValidateAsync(proof, bearer, cancellationToken);
        }

        return result.IsValid && result.ClaimsIdentity.FindFirst("sub")?.Value is { Length: > 0 } subject ? subject : null;
    }

    public async Task<bool> IsConfirmedAsync(string proof, string subject, CancellationToken cancellationToken)
    {
        // The proof passed ReadSubjectAsync, so its issuer is the configured Supabase project.
        var issuer = new JsonWebToken(proof).Issuer;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{issuer.TrimEnd('/')}/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", proof);
        request.Headers.Add("apikey", authOptions.Value.ApiKey ?? string.Empty);
        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AuthUnavailableException(DefaultRetryAfter, e); // network error or timeout
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return false; // signed out, deleted or banned user
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new AuthUnavailableException(response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var user = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = user.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "id") == subject
                && !(root.TryGetProperty("is_anonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True)
                && Text(root, "email") is { Length: > 0 }
                && Text(root, "email_confirmed_at") is { Length: > 0 };
        }
    }

    private async Task<TokenValidationResult> ValidateAsync(string proof, JwtBearerOptions bearer, CancellationToken cancellationToken)
    {
        var parameters = bearer.TokenValidationParameters.Clone();
        if (bearer.ConfigurationManager is { } manager)
        {
            var configuration = await manager.GetConfigurationAsync(cancellationToken);
            parameters.IssuerSigningKeys = [.. parameters.IssuerSigningKeys ?? [], .. configuration.SigningKeys];
        }

        return await _handler.ValidateTokenAsync(proof, parameters);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
