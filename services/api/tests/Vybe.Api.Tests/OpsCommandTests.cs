using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>The deploy steps <c>migrate</c>, <c>seed</c> and <c>release</c> on an empty database, and readiness before and after.</summary>
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
    public async Task GrantsScript_GivesTheApiRoleDataAccessOnly_AndNothingToTheDataApiRoles()
    {
        await RunAsync(OpsCommands.Migrate);
        await PsqlAsync("CREATE ROLE vybe_api LOGIN PASSWORD 'api-local-test'; CREATE ROLE anon NOLOGIN; GRANT USAGE ON SCHEMA vybe TO anon;");
        await _postgres.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(ApiFactory.RepoRoot, "infra", "db", "grants.sql")), "/tmp/grants.sql");

        await PsqlAsync(null, "-v", "api_role=vybe_api", "-f", "/tmp/grants.sql");
        await PsqlAsync(null, "-v", "api_role=vybe_api", "-f", "/tmp/grants.sql"); // idempotent

        var api = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Username = "vybe_api", Password = "api-local-test" };
        await using var connection = new Npgsql.NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using (var read = new Npgsql.NpgsqlCommand("SELECT count(*) FROM vybe.players", connection))
        {
            Assert.Equal(0L, await read.ExecuteScalarAsync());
        }

        await using (var ddl = new Npgsql.NpgsqlCommand("CREATE TABLE vybe.not_allowed (id int)", connection))
        {
            Assert.Equal("42501", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ddl.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var anon = new Npgsql.NpgsqlCommand(
            "SELECT has_schema_privilege('anon', 'vybe', 'USAGE') OR has_table_privilege('anon', 'vybe.players', 'SELECT')", connection))
        {
            Assert.Equal(false, await anon.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Release_MigratesThenSeeds_AndIsIdempotent()
    {
        await RunAsync(OpsCommands.Release);
        var first = await CountsAsync();
        await RunAsync(OpsCommands.Release);

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
    [InlineData(new[] { "replay-deletions" }, true)]
    [InlineData(new[] { "release" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--urls", "http://+:8080" }, false)]
    public void IsCommand_OnlyForTheFirstArgument(string[] args, bool expected) => Assert.Equal(expected, OpsCommands.IsCommand(args));

    private async Task PsqlAsync(string? sql, params string[] arguments)
    {
        string[] command = ["psql", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", .. arguments];
        var result = await _postgres.ExecAsync(sql is null ? command : [.. command, "-c", sql]);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

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
