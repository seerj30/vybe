using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Features;

public sealed class ChallengeOptions
{
    public const string Section = "Challenges";

    /// <summary>Base64 HMAC key for invite tokens (at least 32 bytes). Required outside Development.</summary>
    public string? InviteSecret { get; init; }

    /// <summary>Web origin for share links: <c>{ShareBaseUrl}/challenge/{token}</c> (spec §3 route).</summary>
    public string ShareBaseUrl { get; init; } = "https://vybe.invalid";
}

/// <summary>
/// Invite tokens (spec §4, §11): token = base64url(HMAC-SHA256(server secret, challenge id)), 256 bits.
/// Only SHA-256(token) is stored; the token itself is rebuilt from the challenge id when needed.
/// </summary>
public sealed partial class ChallengeInvites
{
    private readonly byte[] _secret;
    private readonly string _shareBaseUrl;

    public ChallengeInvites(ChallengeOptions options, IHostEnvironment environment, ILogger<ChallengeInvites> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _shareBaseUrl = options.ShareBaseUrl.TrimEnd('/');
        if (!string.IsNullOrEmpty(options.InviteSecret))
        {
            _secret = Convert.FromBase64String(options.InviteSecret);
            if (_secret.Length < 32)
            {
                throw new InvalidOperationException("Challenges:InviteSecret must be at least 32 bytes (base64)");
            }
        }
        else if (environment.IsDevelopment())
        {
            // Development only: invite links stop working when the process restarts.
            _secret = RandomNumberGenerator.GetBytes(32);
            LogEphemeralSecret(logger);
        }
        else
        {
            throw new InvalidOperationException("Challenges:InviteSecret is not configured");
        }
    }

    public string Token(Guid challengeId) =>
        GuestTokens.Base64Url(HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(challengeId.ToString("D"))));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>The invite path pattern of the contract: <c>^[A-Za-z0-9_-]{22,128}$</c>.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: >= 22 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public string ShareUrl(Guid challengeId) => $"{_shareBaseUrl}/challenge/{Token(challengeId)}";

    /// <summary>Adds <c>shareUrl</c> to a stored <c>ChallengeCreated</c> body (fresh response and idempotent replay).</summary>
    public string AddShareUrl(string storedBody)
    {
        var node = JsonNode.Parse(storedBody)!.AsObject();
        node["shareUrl"] = ShareUrl(Guid.Parse(node["challengeId"]!.GetValue<string>()));
        return node.ToJsonString();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Challenges:InviteSecret is not configured; using a random per-process secret (Development only)")]
    private static partial void LogEphemeralSecret(ILogger logger);
}
