using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Vybe.Api.Http;

/// <summary>Stable <c>code</c> values of the contract's <c>ProblemCode</c> enum that this API returns.</summary>
internal static class ProblemCodes
{
    public const string MalformedRequest = "malformed_request";
    public const string InvalidIdempotencyKey = "invalid_idempotency_key";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string AccountSuspended = "account_suspended";
    public const string CsrfCheckFailed = "csrf_check_failed";
    public const string NotFound = "not_found";
    public const string RequestInProgress = "request_in_progress";
    public const string PayloadTooLarge = "payload_too_large";
    public const string ValidationFailed = "validation_failed";
    public const string IdempotencyKeyMismatch = "idempotency_key_mismatch";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
    public const string AlreadyAuthenticated = "already_authenticated";
    public const string SessionExpired = "session_expired";
    public const string ChallengeUnavailable = "challenge_unavailable";
}

internal sealed record FieldError(string Field, string Code, string? Message = null);

/// <summary>RFC 9457 problem details with the VYBE <c>code</c> and <c>traceId</c> (spec §5). No exception details.</summary>
internal static class ApiProblem
{
    public const string ContentType = "application/problem+json";

    public static ProblemDetails Create(
        HttpContext http,
        int status,
        string code,
        string? detail = null,
        IReadOnlyList<FieldError>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = ReasonPhrases.GetReasonPhrase(status),
            Status = status,
            Detail = detail,
            Instance = http.Request.Path.Value,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToHexString() ?? http.TraceIdentifier;
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        return problem;
    }

    public static IResult Result(
        HttpContext http,
        int status,
        string code,
        string? detail = null,
        IReadOnlyList<FieldError>? errors = null) =>
        TypedResults.Problem(Create(http, status, code, detail, errors));

    /// <summary>For middleware and handlers that write the response directly.</summary>
    public static Task WriteAsync(HttpContext http, int status, string code, string? detail = null)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(Create(http, status, code, detail), JsonOptions(http), ContentType);
    }

    public static JsonSerializerOptions JsonOptions(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
}
