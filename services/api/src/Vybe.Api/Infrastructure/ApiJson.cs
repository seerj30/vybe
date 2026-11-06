using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vybe.Api.Infrastructure;

public static class ApiJson
{
    /// <summary>camelCase, snake_case enum values, nulls written (the contract requires nullable fields present).</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        // Unknown properties are rejected (contract: additionalProperties false) → 400 malformed_request.
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

/// <summary>Reads a capped request body; the bytes are also hashed for idempotency.</summary>
public static class RequestBody
{
    public const int MaxBytes = 16 * 1024;

    public static async Task<byte[]> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.ContentLength > MaxBytes)
        {
            throw Problem.Of(413, "payload_too_large");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw Problem.Of(413, "payload_too_large");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static T Deserialize<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, ApiJson.Options) ?? throw Problem.MalformedRequest();
        }
        catch (JsonException)
        {
            throw Problem.MalformedRequest();
        }
    }
}
