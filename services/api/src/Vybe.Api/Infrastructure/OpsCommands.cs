using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Features;
using Vybe.Data;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Deploy steps that run as their own process, never at API start-up:
/// <c>migrate</c> applies the EF Core migrations; <c>seed</c> loads rulesets, catalog and quests
/// (idempotent); <c>release</c> runs <c>migrate</c> then <c>seed</c> in one process (one platform release
/// command, for example Fly.io <c>release_command</c>). They use <c>ConnectionStrings:Migrations</c> (a direct
/// or session-mode connection with the owner role), else <c>ConnectionStrings:Vybe</c>. <c>replay-deletions</c> runs after a database restore and
/// before the API takes traffic: it applies the deletion log again (<see cref="DeletionReplay"/>) with the
/// API's own settings. Usage: <c>dotnet Vybe.Api.dll migrate|seed|release|replay-deletions</c>.
/// </summary>
public static partial class OpsCommands
{
    public const string Migrate = "migrate";
    public const string Seed = "seed";
    public const string Release = "release";
    public const string ReplayDeletions = "replay-deletions";

    public static bool IsCommand(string[] args) => args is [Migrate or Seed or Release or ReplayDeletions, ..];

    /// <summary>Entry point from <c>Program</c>: the same configuration sources as the API (appsettings, environment, arguments).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        WebApplication app;
        WebApplicationBuilder builder;
        var replay = args[0] == ReplayDeletions;
        try
        {
            builder = WebApplication.CreateBuilder(args[1..]);
            if (replay)
            {
                // The full API services (privacy processor, export storage, deletion log); the host never starts.
                builder.Services.AddVybeApi(builder.Configuration, builder.Environment);
                builder.AddVybeOperations();
            }
            else
            {
                builder.AddVybeLogging();
            }

            app = builder.Build();
        }
        catch (Exception exception) when (exception is not HostAbortedException)
        {
            await Console.Error.WriteLineAsync($"The {args[0]} command could not start: {exception.Message}");
            return 1;
        }

        await using var disposeApp = app;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OpsCommands));
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            if (replay)
            {
                _ = app.Services.GetService<IDeletionLog>()
                    ?? throw new InvalidOperationException($"{S3ExportStorageOptions.Section} is not configured: the deletion log is in the export bucket");
                var result = await app.Services.GetRequiredService<DeletionReplay>().RunAsync(stop.Token);
                return result.StillQueued == 0 ? 0 : 1;
            }

            await RunAsync(args[0], builder.Configuration, logger, stop.Token);
            return 0;
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception, args[0]);
            return 1;
        }
    }

    public static async Task RunAsync(string command, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (command == Release)
        {
            // Seed after migrate: the seed writes into the new schema. A failed migrate stops before the seed.
            await RunAsync(Migrate, configuration, logger, cancellationToken);
            await RunAsync(Seed, configuration, logger, cancellationToken);
            return;
        }

        var connectionString = configuration.GetConnectionString("Migrations") is { Length: > 0 } migrations
            ? migrations
            : configuration.GetConnectionString("Vybe")
              ?? throw new InvalidOperationException("ConnectionStrings:Migrations (or ConnectionStrings:Vybe) is not configured");
        var options = VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), connectionString).Options;
        await using var db = new VybeDbContext(options);

        switch (command)
        {
            case Migrate:
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                var names = string.Join(", ", pending);
                LogMigrating(logger, pending.Count, names);
                await db.Database.MigrateAsync(cancellationToken);
                LogMigrated(logger);
                break;

            case Seed:
                var content = new ContentCatalog(
                    configuration.GetSection(ContentOptions.Section).Get<ContentOptions>()
                    ?? throw new InvalidOperationException("Content:Root is not configured"));
                await ContentSeeder.SeedAsync(db, content, TimeProvider.System, cancellationToken);
                await QuestSeeder.SeedAsync(db, content.Options.Root, cancellationToken);
                LogSeeded(logger, content.Rulesets.Count, content.Catalog.Items.Length, content.Catalog.CatalogVersion);
                break;

            default:
                throw new ArgumentException($"unknown command '{command}' (use {Migrate}, {Seed} or {Release})", nameof(command));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} migrations: {Migrations}")]
    private static partial void LogMigrating(ILogger logger, int count, string migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "The database schema is up to date")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Rulesets} rulesets, {Items} catalog items (catalog version {CatalogVersion}) and the quests")]
    private static partial void LogSeeded(ILogger logger, int rulesets, int items, int catalogVersion);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The {Command} command failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string command);
}
