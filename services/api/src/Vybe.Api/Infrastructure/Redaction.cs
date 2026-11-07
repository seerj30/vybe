using System.Text.RegularExpressions;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Removes secrets that can appear in request paths before text leaves the process (logs, traces, Sentry).
/// Invite tokens are bearer secrets in <c>/api/v1/invites/{token}</c> (spec §11); a deletion request id is a
/// capability for the anonymous status poll <c>/api/v1/deletion-requests/{id}</c>. Query strings after a path
/// are removed, because clients can put anything there.
/// </summary>
public static partial class Redaction
{
    public const string Replacement = "[redacted]";

    public static string? Redact(string? text) =>
        string.IsNullOrEmpty(text)
            ? text
            : SecretSegment().Replace(PathQuery().Replace(text, "${path}"), "${prefix}" + Replacement);

    [GeneratedRegex(@"(?<path>/[^\s?#""]*)\?[^\s#""]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathQuery();

    [GeneratedRegex(@"(?<prefix>/(?:invites|deletion-requests)/)[^/?#\s""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegment();
}
