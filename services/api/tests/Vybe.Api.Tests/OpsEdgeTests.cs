using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>An API with a web origin for CORS and a low per-player write limit.</summary>
public sealed class OpsEdgeApiFactory : ApiFactory
{
    public const string WebOrigin = "https://web.vybe.test";
    public const int WritesPerMinute = 5;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"{EdgeSecurity.CorsSection}:0", WebOrigin);
        builder.UseSetting(EdgeSecurity.WritesPerPlayerKey, WritesPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Telemetry on, to unreachable endpoints: the API must start and serve without a collector or Sentry.
        builder.UseSetting(Telemetry.OtlpEndpointKey, "http://127.0.0.1:9");
        builder.UseSetting("Sentry:Dsn", "https://public@sentry.vybe.invalid/1");
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
        using var client = api.CreateClient();

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
        Assert.True(limited.Headers.RetryAfter is not null);
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
