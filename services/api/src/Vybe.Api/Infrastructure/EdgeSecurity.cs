using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Request edge (spec §11): forwarded headers from known proxies only, security headers on every response,
/// CORS for configured origins only, and rate limits per player (authenticated) or per address (anonymous):
/// one for writes and a more generous one for reads.
/// </summary>
public static class EdgeSecurity
{
    public const string CorsSection = "Cors:AllowedOrigins";
    public const string ProxySection = "ReverseProxy:KnownNetworks";
    public const string TrustAnyHopKey = "ReverseProxy:TrustAnyHop";
    public const string WritesPerPlayerKey = "RateLimits:WritesPerPlayerPerMinute";
    public const string ReadsPerMinuteKey = "RateLimits:ReadsPerMinute";

    public const string HstsSubDomainsKey = "Security:HstsIncludeSubDomains";

    /// <summary><c>HttpContext.Items</c> key of the matched route template, kept for spans of error responses.</summary>
    public const string RouteItem = "vybe.route";

    /// <summary>Headers that the web app sends cross-origin (only when <see cref="CorsSection"/> lists origins).</summary>
    public static readonly string[] CorsRequestHeaders =
        ["Authorization", "Content-Type", Idempotency.Header, CsrfMiddleware.Header, "traceparent", "tracestate"];

    public static void AddVybeEdge(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;

        var networks = ProxyNetworks(configuration);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = configuration.GetValue("ReverseProxy:ForwardLimit", 1);
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(network);
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

        var readsPerMinute = configuration.GetValue(ReadsPerMinuteKey, 600);
        if (readsPerMinute < 1)
        {
            throw new InvalidOperationException($"{ReadsPerMinuteKey} must be at least 1");
        }

        // The windows follow the application clock (TimeProvider), so tests control them.
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>().Configure<TimeProvider>((options, clock) =>
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PerMinute(WriterKey, writesPerMinute, clock),
                PerMinute(ReaderKey, readsPerMinute, clock)));
    }

    /// <summary>
    /// The proxy networks. A /0 network trusts every client's <c>X-Forwarded-For</c>, so it needs
    /// <see cref="TrustAnyHopKey"/> = true (only for a container that is reachable through the proxy alone).
    /// </summary>
    public static IReadOnlyList<System.Net.IPNetwork> ProxyNetworks(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var trustAnyHop = configuration.GetValue(TrustAnyHopKey, false);
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in configuration.GetSection(ProxySection).Get<string[]>() ?? [])
        {
            if (!System.Net.IPNetwork.TryParse(entry, out var network))
            {
                throw new InvalidOperationException($"{ProxySection} has an entry that is not a CIDR network");
            }

            if (network.PrefixLength == 0 && !trustAnyHop)
            {
                throw new InvalidOperationException(
                    $"{ProxySection} has a /0 network, which lets any client forge its address; set {TrustAnyHopKey}=true only if the container is reachable through the proxy alone");
            }

            networks.Add(network);
        }

        return networks;
    }

    private static PartitionedRateLimiter<HttpContext> PerMinute(Func<HttpContext, string?> partition, int permits, TimeProvider clock) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => partition(context) is { } key
            ? RateLimitPartition.Get(key, _ => new ClockRateLimiter(permits, TimeSpan.FromMinutes(1), clock))
            : RateLimitPartition.GetNoLimiter(string.Empty));

    /// <summary>The first middleware: client address and scheme, then headers and CORS for every later response.</summary>
    public static WebApplication UseVybeEdge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Configuration.GetSection(ProxySection).Get<string[]>() is { Length: > 0 })
        {
            app.UseForwardedHeaders();
        }

        var hsts = app.Environment.IsDevelopment()
            ? null
            : app.Configuration.GetValue(HstsSubDomainsKey, true) ? "max-age=31536000; includeSubDomains" : "max-age=31536000";
        app.Use((context, next) =>
        {
            // Routing has run (WebApplication adds it first); the exception handler clears the endpoint later.
            if (context.GetEndpoint() is RouteEndpoint endpoint)
            {
                context.Items[RouteItem] = endpoint.RoutePattern.RawText;
            }

            // OnStarting: the exception handler clears headers, so they are added when the response starts.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Resource-Policy"] = "same-site";
                // Personal data and presigned URLs must not stay in shared caches; endpoints that allow caching set their own value.
                if (string.IsNullOrEmpty(headers.CacheControl))
                {
                    headers.CacheControl = "no-store";
                }

                // HSTS only over https (after forwarded headers): browsers ignore it on http anyway.
                if (hsts is not null && context.Request.IsHttps)
                {
                    headers.StrictTransportSecurity = hsts;
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

    /// <summary>
    /// The write partition of an unsafe request: the guest's player id or the token subject when authenticated, else
    /// the client address (IPv6 per /64). Anonymous endpoints with their own policy (guest creation) are skipped.
    /// </summary>
    public static string? WriterKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsRead(context.Request.Method) || HasOwnPolicy(context) ? null : ClientKey(context);
    }

    /// <summary>The read partition (same keys as writes); probes under <c>/health</c> are not limited.</summary>
    public static string? ReaderKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !IsRead(context.Request.Method) || context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
            ? null
            : ClientKey(context);
    }

    private static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool HasOwnPolicy(HttpContext context) =>
        context.User.Identity?.IsAuthenticated != true
        && context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null;

    private static string ClientKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (context.User.FindFirstValue(AuthSchemes.AuthKindClaim) == AuthSchemes.Guest)
            {
                return "guest:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            }

            if (context.User.FindFirstValue("sub") is { Length: > 0 } subject)
            {
                return "sub:" + subject;
            }
        }

        return "ip:" + ClientAddress.RateLimitPartition(context.Connection.RemoteIpAddress);
    }
}
