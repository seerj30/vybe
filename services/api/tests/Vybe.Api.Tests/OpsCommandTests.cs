using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>The deploy steps <c>migrate</c> and <c>seed</c> on an empty database, and readiness before and after.</summary>
public sealed class OpsCommandTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_ThenSeedTwice_IsReadyAndIdempotent()
    {
        await using (var empty = CreateDb())
        {
            Assert.Equal(HealthStatus.Unhealthy, (await ReadyAsync(empty)).Status);
        }

        await RunAsync(OpsCommands.Migrate);
        await RunAsync(OpsCommands.Migrate);
        await RunAsync(OpsCommands.Seed);
        var first = await CountsAsync();
        await RunAsync(OpsCommands.Seed);

        Assert.Equal(first, await CountsAsync());
        Assert.True(first.Rulesets > 0 && first.Items > 0 && first.Quests > 0);
        await using var db = CreateDb();
        Assert.Equal(HealthStatus.Healthy, (await ReadyAsync(db)).Status);
    }

    [Fact]
    public async Task MigrationsConnection_IsPreferredOverTheApiConnection()
    {
        var configuration = Config(new() { ["ConnectionStrings:Vybe"] = "Host=127.0.0.1;Port=1;Timeout=1", ["ConnectionStrings:Migrations"] = _postgres.GetConnectionString() });

        await OpsCommands.RunAsync(OpsCommands.Migrate, configuration, NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task UnknownCommand_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OpsCommands.RunAsync("drop", Config(new() { ["ConnectionStrings:Vybe"] = _postgres.GetConnectionString() }), NullLogger.Instance, CancellationToken.None));

    [Theory]
    [InlineData(new[] { "migrate" }, true)]
    [InlineData(new[] { "seed", "--environment", "Production" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--urls", "http://+:8080" }, false)]
    public void IsCommand_OnlyForTheFirstArgument(string[] args, bool expected) => Assert.Equal(expected, OpsCommands.IsCommand(args));

    private Task RunAsync(string command) =>
        OpsCommands.RunAsync(
            command,
            Config(new() { ["ConnectionStrings:Vybe"] = _postgres.GetConnectionString(), ["Content:Root"] = ApiFactory.RepoRoot }),
            NullLogger.Instance,
            CancellationToken.None);

    private async Task<(int Rulesets, int Items, int Quests)> CountsAsync()
    {
        await using var db = CreateDb();
        return (await db.GameRulesets.CountAsync(), await db.CatalogItems.CountAsync(), await db.QuestDefinitions.CountAsync());
    }

    private static Task<HealthCheckResult> ReadyAsync(VybeDbContext db) =>
        new DatabaseReadyCheck(db, NullLogger<DatabaseReadyCheck>.Instance).CheckHealthAsync(new HealthCheckContext());

    private VybeDbContext CreateDb() =>
        new(VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), _postgres.GetConnectionString()).Options);

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
