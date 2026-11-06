using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Features;

/// <summary>Supabase Auth settings used by account linking (ADR 0005).</summary>
public sealed class SupabaseAuthOptions
{
    public const string Section = "Auth:Supabase";

    /// <summary>The project's publishable (anon) API key, sent as <c>apikey</c> to the Auth user endpoint.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>Verifies the <c>linkProof</c> of <c>POST /accounts/link</c> (spec §2 J3, §5).</summary>
public interface ILinkProofVerifier
{
    /// <summary>The Supabase subject of a valid, non-anonymous proof with a confirmed email; otherwise null.</summary>
    Task<string?> VerifyAsync(string proof, CancellationToken cancellationToken);
}

/// <summary>
/// Validates the proof as a Supabase access token with the bearer scheme's issuer, audience, algorithms
/// and signing keys. Then it reads the user from the issuer's Auth endpoint (<c>{iss}/user</c>), because
/// only the server-side user record says whether the email is confirmed: token claims such as
/// <c>user_metadata</c> can be changed by the user (ADR 0005).
/// </summary>
public sealed class SupabaseLinkProofVerifier(
    IOptionsMonitor<JwtBearerOptions> bearerOptions, IHttpClientFactory httpClients, IOptions<SupabaseAuthOptions> authOptions)
    : ILinkProofVerifier
{
    public const string HttpClientName = "supabase-auth";
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<string?> VerifyAsync(string proof, CancellationToken cancellationToken)
    {
        var bearer = bearerOptions.Get(AuthSchemes.Supabase);
        var parameters = bearer.TokenValidationParameters.Clone();
        if (bearer.ConfigurationManager is { } manager)
        {
            var configuration = await manager.GetConfigurationAsync(cancellationToken);
            parameters.IssuerSigningKeys = [.. parameters.IssuerSigningKeys ?? [], .. configuration.SigningKeys];
        }

        var result = await _handler.ValidateTokenAsync(proof, parameters);
        if (!result.IsValid || result.ClaimsIdentity.FindFirst("sub")?.Value is not { Length: > 0 } subject
            || result.SecurityToken.Issuer is not { Length: > 0 } issuer)
        {
            return null;
        }

        // The issuer is validated above, so this request only goes to the configured Supabase project.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{issuer.TrimEnd('/')}/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", proof);
        request.Headers.Add("apikey", authOptions.Value.ApiKey ?? string.Empty);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null; // signed out, deleted or banned user
        }

        response.EnsureSuccessStatusCode(); // an Auth outage is a 500, not a rejected proof
        using var user = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = user.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && Text(root, "id") == subject
            && !(root.TryGetProperty("is_anonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True)
            && Text(root, "email") is { Length: > 0 }
            && Text(root, "email_confirmed_at") is { Length: > 0 }
            ? subject
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
