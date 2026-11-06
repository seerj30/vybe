using System.Text.Json;

namespace Vybe.Api.Http;

internal enum BodyStatus
{
    Ok,
    TooLarge,
    Malformed,
}

internal sealed record JsonBodyResult(BodyStatus Status, byte[] Bytes, JsonDocument? Document);

/// <summary>Reads a request body with a size cap and parses it, without framework model binding.</summary>
internal static class JsonBody
{
    /// <summary>Spec §5: 413 above 16 KB.</summary>
    public const int MaxBytes = 16 * 1024;

    public static async Task<JsonBodyResult> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBytes)
        {
            return new JsonBodyResult(BodyStatus.TooLarge, [], null);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return new JsonBodyResult(BodyStatus.TooLarge, [], null);
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        try
        {
            return new JsonBodyResult(BodyStatus.Ok, bytes, JsonDocument.Parse(bytes));
        }
        catch (JsonException)
        {
            return new JsonBodyResult(BodyStatus.Malformed, bytes, null);
        }
    }

    /// <summary>RFC 6901 JSON Pointer to a top-level property.</summary>
    public static string Pointer(string property) => "/" + property.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
