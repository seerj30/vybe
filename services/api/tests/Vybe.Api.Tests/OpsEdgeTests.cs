using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>
/// An API with a web origin for CORS, a low write limit, a trusted proxy network (10.0.0.0/8), Sentry and logs
/// captured in memory, a test header that sets the peer address, and a test header that fails database commands.
/// </summary>
public sealed class OpsEdgeApiFactory : ApiFactory
{
    public const string WebOrigin = "https://web.vybe.test";
    public const int WritesPerMinute = 5;
    public const string PeerHeader = "X-Test-Peer";
    public const string FailHeader = "X-Test-Fail";

    public CapturingSentryTransport Sentry { get; } = new();

    public CapturingLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"{EdgeSecurity.CorsSection}:0", WebOrigin);
        builder.UseSetting(EdgeSecurity.WritesPerPlayerKey, WritesPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Telemetry on, to unreachable endpoints: the API must start and serve without a collector or Sentry.
        builder.UseSetting(Telemetry.OtlpEndpointKey, "http://127.0.0.1:9");
        builder.UseSetting("Sentry:Dsn", "https://public@sentry.vybe.invalid/1");
        builder.UseSetting($"{EdgeSecurity.ProxySection}:0", "10.0.0.0/8");
        // Framework request logs on, so a query string would reach logs and Sentry breadcrumbs if not removed.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "Information");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerAddressStartupFilter>();
            services.PostConfigure<global::Sentry.AspNetCore.SentryAspNetCoreOptions>(options => options.Transport = Sentry);
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
            services.ConfigureDbContext<Vybe.Data.VybeDbContext>((provider, options) =>
                options.AddInterceptors(new FailingCommandInterceptor(provider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>())));
        });
    }

    /// <summary>TestServer has no client address; the test header sets the TCP peer before forwarded headers run.</summary>
    private sealed class PeerAddressStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(PeerHeader, out var peer))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class FailingCommandInterceptor(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var request = accessor.HttpContext?.Request;
            return request is not null && request.Headers.ContainsKey(FailHeader)
                ? throw new InvalidOperationException($"test failure on {request.Path}{request.QueryString}")
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

/// <summary>Keeps every Sentry envelope as text instead of sending it.</summary>
public sealed class CapturingSentryTransport : global::Sentry.Extensibility.ITransport
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Envelopes { get; } = new();

    public async Task SendEnvelopeAsync(global::Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null, cancellationToken);
        Envelopes.Enqueue(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}

/// <summary>Formats every log entry with <see cref="JsonLogFormatter"/>, like the console outside Development.</summary>
public sealed class CapturingLogs : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ISupportExternalScope
{
    private Microsoft.Extensions.Logging.IExternalScopeProvider? _scopes;

    public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(Microsoft.Extensions.Logging.IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLogs owner, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes?.Push(state);

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            using var writer = new StringWriter();
            new JsonLogFormatter().Write(
                new Microsoft.Extensions.Logging.Abstractions.LogEntry<TState>(logLevel, category, eventId, state, exception, formatter), owner._scopes, writer);
            owner.Lines.Enqueue(writer.ToString());
        }
    }
}

/// <summary>Security headers, CORS and the per-player write limit (spec §11).</summary>
public sealed class OpsEdgeTests(OpsEdgeApiFactory api) : IClassFixture<OpsEdgeApiFactory>
{
    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/api/v1/me", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/no-such-route", HttpStatusCode.NotFound)]
    public async Task EveryResponse_HasTheSecurityHeaders(string path, HttpStatusCode status)
    {
        using var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'", Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
        Assert.Contains("no-store", Header(response, "Cache-Control"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_OnlyOverHttps()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(string.Empty, Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task ForwardedFor_FromTheTrustedProxy_SetsTheClient_FromOthersIsIgnored()
    {
        // Trusted proxy: two clients behind it get two buckets.
        for (var i = 0; i < OpsEdgeApiFactory.WritesPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AnonymousWriteAsync("10.0.0.9", "203.0.113.10")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AnonymousWriteAsync("10.0.0.9", "203.0.113.10")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AnonymousWriteAsync("10.0.0.9", "203.0.113.11")).StatusCode);

        // Untrusted peer: a forged X-Forwarded-For does not change the bucket.
        for (var i = 0; i < OpsEdgeApiFactory.WritesPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AnonymousWriteAsync("198.51.100.20", $"203.0.113.{30 + i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AnonymousWriteAsync("198.51.100.20", "203.0.113.99")).StatusCode);
    }

    [Fact]
    public async Task FailingRequest_SentryAndLogs_HaveNoQueryCookieBearerOrToken()
    {
        const string token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_SECRET";
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invites/{token}?code=QUERYSECRET");
        request.Headers.Add(OpsEdgeApiFactory.FailHeader, "1");
        request.Headers.Add("Authorization", "Bearer BEARERSECRET");
        request.Headers.Add("Cookie", "session=COOKIESECRET");

        using var response = await client.SendAsync(request);
        await global::Sentry.SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        await Contract.AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.NotEmpty(api.Sentry.Envelopes);
        Assert.Contains(api.Logs.Lines, line => line.Contains("[redacted]", StringComparison.Ordinal));
        foreach (var text in api.Sentry.Envelopes.Concat(api.Logs.Lines))
        {
            foreach (var secret in new[] { "QUERYSECRET", "BEARERSECRET", "COOKIESECRET", token })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task EndpointCacheControl_IsKept()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/api/v1/games/rulesets/street-hoops-v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task GuestCreation_IsNotCountedTwice()
    {
        for (var i = 0; i < OpsEdgeApiFactory.WritesPerMinute + 1; i++)
        {
            await Player.CreateGuestAsync(api);
        }
    }

    [Fact]
    public async Task Preflight_FromTheWebOrigin_AllowsCredentialsAndTheCsrfHeader()
    {
        using var client = api.CreateClient();
        using var request = Preflight(OpsEdgeApiFactory.WebOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(OpsEdgeApiFactory.WebOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains(CsrfMiddleware.Header, Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherOrigin_GetsNoCorsHeaders()
    {
        using var client = api.CreateClient();
        using var preflight = Preflight("https://evil.example");
        using var get = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        get.Headers.Add("Origin", "https://evil.example");

        using var preflightResponse = await client.SendAsync(preflight);
        using var getResponse = await client.SendAsync(get);

        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(getResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AuthenticatedWrites_AreLimitedPerPlayer_ReadsAndOtherPlayersAreNot()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);

        for (var i = 0; i < OpsEdgeApiFactory.WritesPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await CreateSessionAsync(player)).StatusCode);
        }

        using var limited = await CreateSessionAsync(player);
        await Contract.AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.InRange(limited.Headers.RetryAfter?.Delta ?? TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.OK, (await player.Http.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionAsync(other)).StatusCode);
    }

    [Fact]
    public void Telemetry_IsOn_WhenConfigured()
    {
        Assert.NotNull(api.Services.GetService<OpenTelemetry.Trace.TracerProvider>());
        Assert.NotNull(api.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
        Assert.True(Sentry.SentrySdk.IsEnabled);
    }

    [Fact]
    public void InviteRoute_TemplateHidesTheToken()
    {
        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'));

        Assert.Contains("/api/v1/invites/{token}", routes);
    }

    [Theory]
    [InlineData("https://web.vybe.example/")]
    [InlineData("https://web.vybe.example/app")]
    [InlineData("https://*.vybe.example")]
    [InlineData("http://web.vybe.example")]
    [InlineData("*")]
    public void CorsOrigins_MustBeHttpsOrigins(string origin)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"{EdgeSecurity.CorsSection}:0"] = origin }).Build();

        Assert.Throws<InvalidOperationException>(() => EdgeSecurity.CorsOrigins(configuration, new HostingEnvironment { EnvironmentName = Environments.Production }));
    }

    private async Task<HttpResponseMessage> AnonymousWriteAsync(string peer, string forwardedFor)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invites/{Guid.NewGuid():N}/accept");
        request.Headers.Add(OpsEdgeApiFactory.PeerHeader, peer);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> CreateSessionAsync(Player player) =>
        player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { });

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/purchases");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key,x-requested-with");
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : string.Empty;
}
