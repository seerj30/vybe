using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Vybe.Data.Tests;

/// <summary>One PostgreSQL container per test run, migrated once with the real migrations.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Same major version as Supabase's managed Postgres.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public VybeDbContext CreateContext() =>
        new(VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
