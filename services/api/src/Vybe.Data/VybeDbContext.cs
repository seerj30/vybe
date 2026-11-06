using Microsoft.EntityFrameworkCore;
using Vybe.Data.Entities;

namespace Vybe.Data;

public sealed class VybeDbContext(DbContextOptions<VybeDbContext> options) : DbContext(options)
{
    public const string Schema = "vybe";

    public DbSet<Player> Players => Set<Player>();
    public DbSet<GuestSession> GuestSessions => Set<GuestSession>();
    public DbSet<AvatarConfig> AvatarConfigs => Set<AvatarConfig>();
    public DbSet<PlayerBlock> PlayerBlocks => Set<PlayerBlock>();
    public DbSet<PlayerReport> PlayerReports => Set<PlayerReport>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<PlayerInventoryItem> PlayerInventory => Set<PlayerInventoryItem>();
    public DbSet<SavedOutfit> SavedOutfits => Set<SavedOutfit>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<GameRuleset> GameRulesets => Set<GameRuleset>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<GameInputTrace> GameInputTraces => Set<GameInputTrace>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<QuestDefinition> QuestDefinitions => Set<QuestDefinition>();
    public DbSet<PlayerQuest> PlayerQuests => Set<PlayerQuest>();
    public DbSet<PlayerDailyCounter> PlayerDailyCounters => Set<PlayerDailyCounter>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<PlayerAchievement> PlayerAchievements => Set<PlayerAchievement>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<DataExportRequest> DataExportRequests => Set<DataExportRequest>();
    public DbSet<DeletionRequest> DeletionRequests => Set<DeletionRequest>();
    public DbSet<OutboxJob> OutboxJobs => Set<OutboxJob>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VybeDbContext).Assembly);

        // No cascades: deletion and retention are explicit workflows (spec §4 invariant 7, §2 J4).
        foreach (var foreignKey in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }
}

public static class VybeDbContextOptions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>The one place that configures the provider, used by the API, tests and design-time tools.</summary>
    public static DbContextOptionsBuilder<VybeDbContext> Configure(
        DbContextOptionsBuilder<VybeDbContext> builder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, VybeDbContext.Schema))
            .UseSnakeCaseNamingConvention();
    }
}
