using Vybe.Api.Http;

namespace Vybe.Api.Auth;

/// <summary>
/// CSRF check for cookie-authenticated unsafe requests (spec §5, §11; ADR 0001 change 3):
/// <c>X-Requested-With: vybe-web</c> forces a CORS preflight, which only the VYBE origin passes.
/// </summary>
internal static class Csrf
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "vybe-web";

    /// <summary>
    /// Requires the header on this endpoint. Every endpoint that uses it today is cookie-authenticated
    /// (or is <c>createGuest</c>, which always requires it); bearer-only requests will skip the check
    /// when Supabase bearer authentication is added.
    /// </summary>
    public static TBuilder RequireCsrfHeader<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            string.Equals(context.HttpContext.Request.Headers[HeaderName], HeaderValue, StringComparison.Ordinal)
                ? await next(context)
                : ApiProblem.Result(context.HttpContext, StatusCodes.Status403Forbidden, ProblemCodes.CsrfCheckFailed));
}
