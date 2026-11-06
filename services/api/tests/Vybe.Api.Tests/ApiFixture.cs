using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>
/// One PostgreSQL container (real migrations) and one API host per test run. The API clock is a
/// <see cref="FakeTimeProvider"/>; tests in the collection run one at a time, so advancing it is safe.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    // Same major version as Supabase's managed Postgres.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using (var context = new VybeDbContext(VybeDbContextOptions
                         .Configure(new DbContextOptionsBuilder<VybeDbContext>(), _container.GetConnectionString()).Options))
        {
            await context.Database.MigrateAsync();
        }

        Factory = CreateFactory(guestPermitsPerMinute: 10_000);
    }

    /// <summary>A separate host on the same database and clock, for tests that need other settings.</summary>
    public WebApplicationFactory<Program> CreateFactory(int guestPermitsPerMinute) =>
        new VybeApiFactory(_container.GetConnectionString(), Clock, guestPermitsPerMinute);

    public HttpClient CreateClient(WebApplicationFactory<Program>? factory = null) =>
        (factory ?? Factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class VybeApiFactory(string connectionString, TimeProvider clock, int guestPermitsPerMinute)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Vybe", connectionString);
            builder.UseSetting(
                "Vybe:GuestCreationPermitsPerMinute",
                guestPermitsPerMinute.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
