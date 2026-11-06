using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Vybe.Api.Content;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Api.Jobs;
using Vybe.Data;

namespace Vybe.Api;

public static class ServiceSetup
{
    public const string GuestCreationPolicy = "guest-creation";

    public static IServiceCollection AddVybeApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var connectionString = configuration.GetConnectionString("Vybe")
            ?? throw new InvalidOperationException("ConnectionStrings:Vybe is not configured");

        services.AddDbContext<VybeDbContext>(options =>
            VybeDbContextOptions.Configure((DbContextOptionsBuilder<VybeDbContext>)options, connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ContentCatalog(
            configuration.GetSection(ContentOptions.Section).Get<ContentOptions>()
            ?? throw new InvalidOperationException("Content:Root is not configured")));
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentPlayer>();
        services.AddScoped<Idempotency>();
        services.AddSingleton(configuration.GetSection(ChallengeOptions.Section).Get<ChallengeOptions>() ?? new ChallengeOptions());
        services.AddSingleton<ChallengeInvites>();
        services.AddSingleton<ILinkProofVerifier, SupabaseLinkProofVerifier>();
        services.AddHttpClient(SupabaseLinkProofVerifier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        var supabaseAuth = services.AddOptions<SupabaseAuthOptions>().Bind(configuration.GetSection(SupabaseAuthOptions.Section));
        if (!environment.IsDevelopment())
        {
            supabaseAuth.Validate(o => !string.IsNullOrEmpty(o.ApiKey), "Auth:Supabase:ApiKey must be configured").ValidateOnStart();
        }
        services.AddSingleton(configuration.GetSection(PrivacyOptions.Section).Get<PrivacyOptions>() ?? new PrivacyOptions());
        services.AddSingleton<IExportStorage, LocalExportStorage>();
        services.AddSingleton<PrivacyProcessor>();
        if (configuration.GetValue("Jobs:Enabled", true))
        {
            services.AddHostedService<PrivacyWorker>();
        }

        services.AddVybeJobs(configuration);

        // Spec §11 payload caps: every request body, not only the endpoints that read it manually.
        services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = RequestBody.MaxBytes);
        services.Configure<JsonOptions>(options => ApiJson.Configure(options.SerializerOptions));
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.AddProblemDetails();

        AddAuthentication(services, configuration, environment);
        AddRateLimits(services, configuration);
        return services;
    }

    public static WebApplication UseVybeApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.Services.GetRequiredService<ChallengeInvites>(); // fail at start-up without an invite secret
        app.UseExceptionHandler();
        app.Use(LimitRequestBodyAsync);
        app.UseMiddleware<CsrfMiddleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        var api = app.MapGroup("/api/v1").RequireAuthorization();
        IdentityEndpoints.Map(api);
        GameplayEndpoints.Map(api);
        EconomyEndpoints.Map(api);
        ChallengeEndpoints.Map(api);
        QuestEndpoints.Map(api);
        AccountEndpoints.Map(api);
        ProfileEndpoints.Map(api);
        SafetyEndpoints.Map(api);
        OutfitEndpoints.Map(api);
        PrivacyEndpoints.Map(api);
        AdminEndpoints.Map(api);
        return app;
    }

    public static async Task SeedContentIfConfiguredAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var content = app.Services.GetRequiredService<ContentCatalog>();
        if (!content.Options.SeedOnStartup)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await ContentSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<VybeDbContext>(),
            content,
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            CancellationToken.None);
        await QuestSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<VybeDbContext>(), content.Options.Root, CancellationToken.None);
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddAuthentication(AuthSchemes.Player)
            .AddPolicyScheme(AuthSchemes.Player, "Guest cookie or Supabase bearer token", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.Any(h => h?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                        ? AuthSchemes.Supabase
                        : AuthSchemes.Guest)
            .AddScheme<AuthenticationSchemeOptions, GuestAuthenticationHandler>(AuthSchemes.Guest, null)
            .AddJwtBearer(AuthSchemes.Supabase, options =>
            {
                // Supabase Auth (spec §5, §11): issuer, audience, signature, algorithm and expiry are validated;
                // signing keys come from the issuer's metadata over HTTPS.
                var section = configuration.GetSection("Auth:Supabase");
                options.Authority = section["Authority"];
                options.MetadataAddress = section["MetadataAddress"] ?? string.Empty;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = section["Issuer"] ?? section["Authority"],
                    ValidAudience = section["Audience"] ?? "authenticated",
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        if (!environment.IsDevelopment())
        {
            services.AddOptions<JwtBearerOptions>(AuthSchemes.Supabase)
                .Validate(
                    o => !string.IsNullOrEmpty(o.Authority) || !string.IsNullOrEmpty(o.MetadataAddress)
                        || o.TokenValidationParameters.IssuerSigningKey is not null,
                    "Auth:Supabase:Authority (or MetadataAddress) must be configured")
                .ValidateOnStart();
        }

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
    }

    private static void AddRateLimits(IServiceCollection services, IConfiguration configuration)
    {
        // createGuest is anonymous; without a limit every new guest would also reset the per-player daily caps.
        // Behind a proxy, ForwardedHeaders must be configured so RemoteIpAddress is the client's address.
        var guestsPerMinute = configuration.GetValue("RateLimits:GuestsPerIpPerMinute", 10);
        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ProblemWriter.WriteAsync(context.HttpContext, 429, "rate_limited");
            };
            options.AddPolicy(GuestCreationPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.RateLimitPartition(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = guestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    /// <summary>Rejects bodies above the cap early, and caps streamed bodies where the server supports it.</summary>
    private static async Task LimitRequestBodyAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = RequestBody.MaxBytes;
        }

        if (context.Request.ContentLength > RequestBody.MaxBytes)
        {
            await ProblemWriter.WriteAsync(context, 413, "payload_too_large");
            return;
        }

        await next(context);
    }
}
