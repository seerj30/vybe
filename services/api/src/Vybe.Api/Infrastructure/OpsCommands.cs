using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Features;
using Vybe.Data;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Deploy steps that run as their own process, never at API start-up:
/// <c>migrate</c> applies the EF Core migrations; <c>seed</c> loads rulesets, catalog and quests
/// (idempotent). Both use <c>ConnectionStrings:Migrations</c> (a direct or session-mode connection with the
/// owner role), else <c>ConnectionStrings:Vybe</c>. Usage: <c>dotnet Vybe.Api.dll migrate|seed</c>.
/// </summary>
public static partial class OpsCommands
{
    public const string Migrate = "migrate";
    public const string Seed = "seed";

    public static bool IsCommand(string[] args) => args is [Migrate or Seed, ..];

    /// <summary>Entry point from <c>Program</c>: the same configuration sources as the API (appsettings, environment, arguments).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args[1..]);
        builder.AddVybeLogging();
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OpsCommands));
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
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
                throw new ArgumentException($"unknown command '{command}' (use {Migrate} or {Seed})", nameof(command));
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
