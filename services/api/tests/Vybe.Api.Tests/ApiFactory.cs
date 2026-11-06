using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>The real API against a migrated PostgreSQL container, with a controllable clock.</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TokenIssuer = "https://auth.vybe.test/auth/v1";
    private static readonly SymmetricSecurityKey TokenKey = new(Encoding.UTF8.GetBytes("vybe-test-signing-key-0123456789abcdef"));

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6-alpine").Build();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Tests create many guests from one address; a dedicated test lowers this.</summary>
    public int GuestsPerMinute { get; protected set; } = 10_000;

    public static string RepoRoot { get; } = FindRepoRoot();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), _postgres.GetConnectionString());
        await using var db = new VybeDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public VybeDbContext CreateDbContext() =>
        new(VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), _postgres.GetConnectionString()).Options);

    /// <summary>A browser-like client: HTTPS (so Secure cookies flow), cookies kept, CSRF header sent.</summary>
    public HttpClient CreateBrowser()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(CsrfMiddleware.Header, CsrfMiddleware.Value);
        return client;
    }

    public static string SupabaseToken(string subject) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TokenIssuer,
            Audience = "authenticated",
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(TokenKey, SecurityAlgorithms.HmacSha256),
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Vybe", _postgres.GetConnectionString());
        builder.UseSetting("Content:Root", RepoRoot);
        builder.UseSetting("Content:SeedOnStartup", "true");
        builder.UseSetting("Challenges:InviteSecret", Convert.ToBase64String(Encoding.UTF8.GetBytes("vybe-test-invite-secret-0123456789abcdef")));
        builder.UseSetting("Jobs:Enabled", "false"); // tests run the jobs directly
        builder.UseSetting("RateLimits:GuestsPerIpPerMinute", GuestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<JwtBearerOptions>(AuthSchemes.Supabase, options =>
            {
                options.Authority = null;
                options.MetadataAddress = string.Empty;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenIssuer,
                    ValidAudience = "authenticated",
                    IssuerSigningKey = TokenKey,
                };
            });
        });
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "packages", "game-rules")) && Directory.Exists(Path.Combine(dir.FullName, "assets")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root not found above the test output directory");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
