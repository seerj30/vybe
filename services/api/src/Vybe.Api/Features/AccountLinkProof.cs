using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Features;

/// <summary>A verified Supabase identity: the subject and the claims of the validated token.</summary>
public sealed record VerifiedIdentity(string Subject, ClaimsIdentity Claims);

/// <summary>Verifies the <c>linkProof</c> of <c>POST /accounts/link</c> (spec §2 J3, §5).</summary>
public interface ILinkProofVerifier
{
    /// <summary>The identity of a valid, non-anonymous proof with a confirmed email; otherwise null.</summary>
    Task<VerifiedIdentity?> VerifyAsync(string proof, CancellationToken cancellationToken);
}

/// <summary>
/// Validates the proof as a Supabase access token with the same issuer, audience, algorithms and
/// signing keys as the bearer scheme. Supabase writes <c>is_anonymous</c> for anonymous sign-ins and
/// <c>user_metadata.email_verified</c> once the email is confirmed (ADR 0005).
/// </summary>
public sealed class SupabaseLinkProofVerifier(IOptionsMonitor<JwtBearerOptions> options) : ILinkProofVerifier
{
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<VerifiedIdentity?> VerifyAsync(string proof, CancellationToken cancellationToken)
    {
        var bearer = options.Get(AuthSchemes.Supabase);
        var parameters = bearer.TokenValidationParameters.Clone();
        if (bearer.ConfigurationManager is { } manager)
        {
            var configuration = await manager.GetConfigurationAsync(cancellationToken);
            parameters.IssuerSigningKeys = [.. parameters.IssuerSigningKeys ?? [], .. configuration.SigningKeys];
        }

        var result = await _handler.ValidateTokenAsync(proof, parameters);
        if (!result.IsValid || result.ClaimsIdentity.FindFirst("sub")?.Value is not { Length: > 0 } subject)
        {
            return null;
        }

        var identity = result.ClaimsIdentity;
        var anonymous = identity.FindFirst("is_anonymous")?.Value;
        if (anonymous is null || !bool.TryParse(anonymous, out var isAnonymous) || isAnonymous)
        {
            return null;
        }

        return string.IsNullOrEmpty(identity.FindFirst("email")?.Value) || !EmailVerified(identity.FindFirst("user_metadata")?.Value)
            ? null
            : new VerifiedIdentity(subject, identity);
    }

    private static bool EmailVerified(string? userMetadata)
    {
        if (string.IsNullOrEmpty(userMetadata))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(userMetadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("email_verified", out var verified)
                && verified.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
