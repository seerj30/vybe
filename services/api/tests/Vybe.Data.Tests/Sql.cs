using Npgsql;

namespace Vybe.Data.Tests;

/// <summary>Raw SQL helpers: the tests exercise the database rules directly, not through EF.</summary>
internal static class Sql
{
    public const string CheckViolation = "23514";
    public const string UniqueViolation = "23505";
    public const string IntegrityViolation = "23000";

    public static async Task ExecuteAsync(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        var value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    /// <summary>Runs <paramref name="sql"/> and returns the PostgreSQL error it must raise.</summary>
    public static async Task<PostgresException> FailsAsync(NpgsqlDataSource db, string sql, params object[] args) =>
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(db, sql, args));

    /// <summary>Inserts a player with a unique handle and returns its id.</summary>
    public static async Task<Guid> PlayerAsync(NpgsqlDataSource db)
    {
        var id = Guid.CreateVersion7();
        var handle = "p_" + id.ToString("N")[^12..];
        await ExecuteAsync(
            db,
            "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES ($1, $2, $2, 'guest', 'active')",
            id,
            handle);
        return id;
    }

    public static async Task<string> RulesetAsync(NpgsqlDataSource db)
    {
        var id = "test-" + Guid.CreateVersion7().ToString("N")[^12..];
        await ExecuteAsync(
            db,
            """
            INSERT INTO vybe.game_rulesets (id, game_id, version, config, checksum, active_from)
            VALUES ($1, $1, 1, '{"durationMs": 60000}', repeat('a', 64), now())
            """,
            id);
        return id;
    }

    public static async Task<Guid> SessionAsync(NpgsqlDataSource db, Guid playerId, string rulesetId, string status = "created")
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            db,
            """
            INSERT INTO vybe.game_sessions (id, player_id, game_id, ruleset_id, seed, status, start_deadline)
            VALUES ($1, $2, 'street-hoops', $3, 42, $4, now() + interval '10 minutes')
            """,
            id,
            playerId,
            rulesetId,
            status);
        return id;
    }
}
