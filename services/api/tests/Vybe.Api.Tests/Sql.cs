using Npgsql;

namespace Vybe.Api.Tests;

/// <summary>Raw SQL for arranging state and checking what the API wrote.</summary>
internal static class Sql
{
    public static async Task ExecuteAsync(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var command = Command(db, sql, args);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var command = Command(db, sql, args);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static NpgsqlCommand Command(NpgsqlDataSource db, string sql, object[] args)
    {
        var command = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return command;
    }
}
