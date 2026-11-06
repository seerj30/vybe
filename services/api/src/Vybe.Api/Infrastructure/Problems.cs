using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace Vybe.Api.Infrastructure;

/// <summary>An expected API error with a stable spec §5 <c>code</c> (RFC 9457 problem details).</summary>
public sealed class ApiProblemException : Exception
{
    public ApiProblemException(int status, string code, string? detail = null, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(detail ?? code)
    {
        Status = status;
        Code = code;
        Detail = detail;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public int Status { get; }

    public string Code { get; }

    public string? Detail { get; }

    public IReadOnlyDictionary<string, object?> Extensions { get; }
}

/// <summary>Factories for the codes of spec §5 and the OpenAPI <c>ProblemCode</c> enum.</summary>
public static class Problem
{
    public static ApiProblemException MalformedRequest(string? detail = null) => new(400, "malformed_request", detail);

    public static ApiProblemException Unauthenticated() => new(401, "unauthenticated");

    public static ApiProblemException Forbidden() => new(403, "forbidden");

    public static ApiProblemException Of(int status, string code, string? detail = null) => new(status, code, detail);

    public static ApiProblemException WithItems(int status, string code, IEnumerable<Guid> itemIds) =>
        new(status, code, extensions: new Dictionary<string, object?> { ["itemIds"] = itemIds.ToArray() });

    public static ApiProblemException ValidationFailed(string field, string code, string message) =>
        new(422, "validation_failed", message, new Dictionary<string, object?>
        {
            ["errors"] = new[] { new { field, code, message } },
        });
}

public static class ProblemWriter
{
    public const string ContentType = "application/problem+json";

    public static Dictionary<string, object?> Body(HttpContext context, int status, string code, string? detail,
        IReadOnlyDictionary<string, object?>? extensions)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"/problems/{code}",
            ["title"] = code.Replace('_', ' '),
            ["status"] = status,
            ["instance"] = context.Request.Path.Value,
            ["code"] = code,
            ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        };
        if (detail is not null)
        {
            body["detail"] = detail;
        }

        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            body[key] = value;
        }

        return body;
    }

    public static async Task WriteAsync(HttpContext context, int status, string code, string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(Body(context, status, code, detail, extensions), ApiJson.Options),
            context.RequestAborted);
    }
}

/// <summary>Turns every exception into problem details; unknown errors become 500 <c>internal_error</c>.</summary>
internal sealed partial class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail, extensions) = exception switch
        {
            ApiProblemException p => (p.Status, p.Code, p.Detail, p.Extensions),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (413, "payload_too_large", (string?)null, (IReadOnlyDictionary<string, object?>?)null),
            BadHttpRequestException or JsonException => (400, "malformed_request", null, null),
            _ => (500, "internal_error", null, null),
        };

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.Request.Path);
        }

        await ProblemWriter.WriteAsync(httpContext, status, code, detail, extensions);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error on {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string path);
}
