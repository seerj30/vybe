using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Request edge (spec §11): forwarded headers from known proxies only, security headers on every response,
/// CORS for configured origins only, and a per-player limit for authenticated writes.
/// </summary>
public static class EdgeSecurity
{
    public const string CorsSection = "Cors:AllowedOrigins";
    public const string ProxySection = "ReverseProxy:KnownNetworks";
    public const string WritesPerPlayerKey = "RateLimits:WritesPerPlayerPerMinute";

    /// <summary>Headers that the web app sends cross-origin (only when <see cref="CorsSection"/> lists origins).</summary>
    public static readonly string[] CorsRequestHeaders =
        ["Authorization", "Content-Type", Idempotency.Header, CsrfMiddleware.Header, "traceparent", "tracestate"];

    public static void AddVybeEdge(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;

        var networks = configuration.GetSection(ProxySection).Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = configuration.GetValue("ReverseProxy:ForwardLimit", 1);
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException($"{ProxySection} has an entry that is not a CIDR network"));
            }
        });

        var origins = CorsOrigins(configuration, builder.Environment);
        if (origins.Length > 0)
        {
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(origins)
                .AllowCredentials()
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                .WithHeaders(CorsRequestHeaders)
                .WithExposedHeaders("Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        }

        var writesPerMinute = configuration.GetValue(WritesPerPlayerKey, 120);
        if (writesPerMinute < 1)
        {
            throw new InvalidOperationException($"{WritesPerPlayerKey} must be at least 1");
        }

        builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            WriterKey(context) is { } key
                ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = writesPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter(string.Empty)));
    }

    /// <summary>The first middleware: client address and scheme, then headers and CORS for every later response.</summary>
    public static WebApplication UseVybeEdge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Configuration.GetSection(ProxySection).Get<string[]>() is { Length: > 0 })
        {
            app.UseForwardedHeaders();
        }

        var hsts = !app.Environment.IsDevelopment();
        app.Use((context, next) =>
        {
            // OnStarting: the exception handler clears headers, so they are added when the response starts.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Resource-Policy"] = "same-site";
                if (hsts)
                {
                    headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
                }

                return Task.CompletedTask;
            });
            return next(context);
        });

        if (CorsOrigins(app.Configuration, app.Environment).Length > 0)
        {
            app.UseCors();
        }

        return app;
    }

    /// <summary>
    /// Configured web origins: absolute https origins without path (http only in Development). Never a wildcard,
    /// because the policy allows credentials.
    /// </summary>
    public static string[] CorsOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var origins = configuration.GetSection(CorsSection).Get<string[]>() ?? [];
        foreach (var origin in origins)
        {
            var valid = Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp))
                && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
                && !origin.Contains('*', StringComparison.Ordinal) && !origin.EndsWith('/');
            if (!valid)
            {
                throw new InvalidOperationException($"{CorsSection} entries must be origins such as https://vybe.example (no path, no wildcard)");
            }
        }

        return origins;
    }

    /// <summary>The partition of an authenticated unsafe request: the guest's player id or the token subject.</summary>
    public static string? WriterKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            || context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return context.User.FindFirstValue(AuthSchemes.AuthKindClaim) == AuthSchemes.Guest
            ? "guest:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : context.User.FindFirstValue("sub") is { Length: > 0 } subject ? "sub:" + subject : null;
    }
}
