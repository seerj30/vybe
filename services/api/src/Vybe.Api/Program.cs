using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vybe.Api;
using Vybe.Api.Auth;
using Vybe.Api.Gameplay;
using Vybe.Api.Guests;
using Vybe.Api.Http;
using Vybe.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<VybeApiOptions>()
    .Bind(builder.Configuration.GetSection(VybeApiOptions.SectionName))
    .Validate(o => o.IsValid(), "Vybe options must be positive")
    .ValidateOnStart();

// The connection string is a secret: environment variable ConnectionStrings__Vybe or a secret store.
builder.Services.AddDbContext<VybeDbContext>((services, options) => VybeDbContextOptions.Configure(
    (DbContextOptionsBuilder<VybeDbContext>)options,
    services.GetRequiredService<IConfiguration>().GetConnectionString("Vybe")
    ?? throw new InvalidOperationException("ConnectionStrings:Vybe is not configured.")));

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<RulesetStore>();
builder.Services.AddScoped<IdempotencyStore>();
builder.Services.AddScoped<SubmitGameSession>();

builder.Services.AddAuthentication(GuestTokens.Scheme)
    .AddScheme<AuthenticationSchemeOptions, GuestCookieHandler>(GuestTokens.Scheme, null);
builder.Services.AddAuthorization();

builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IOptions<VybeApiOptions>>((cors, vybe) =>
    cors.AddDefaultPolicy(policy => policy
        .WithOrigins(vybe.Value.CorsOrigins)
        .AllowCredentials()
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
        .WithHeaders("Content-Type", Csrf.HeaderName, SubmitGameSession.IdempotencyKeyHeader, "traceparent")));

builder.Services.AddRateLimiter(limiter =>
{
    limiter.AddPolicy(GuestEndpoints.RateLimitPolicy, http =>
    {
        var permits = http.RequestServices.GetRequiredService<IOptions<VybeApiOptions>>().Value.GuestCreationPermitsPerMinute;
        // Behind a proxy, configure forwarded headers so RemoteIpAddress is the client address.
        return RateLimitPartition.GetFixedWindowLimiter(
            GuestEndpoints.RateLimitPartition(http.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
    limiter.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return new ValueTask(ApiProblem.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited));
    };
});

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(http =>
{
    // Never leak exception details (spec §5); the middleware logs the exception.
    var (status, code) = http.Features.Get<IExceptionHandlerFeature>()?.Error switch
    {
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            (StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, ProblemCodes.MalformedRequest),
        _ => (StatusCodes.Status500InternalServerError, ProblemCodes.InternalError),
    };
    return ApiProblem.WriteAsync(http, status, code);
}));

app.UseCors();
// Before authentication: a limited request must not cost a guest-session lookup.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1");
GuestEndpoints.Map(api);
GameEndpoints.Map(api);
app.MapFallback(http => ApiProblem.WriteAsync(http, StatusCodes.Status404NotFound, ProblemCodes.NotFound));

await app.RunAsync();

/// <summary>Entry point; public for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program;
