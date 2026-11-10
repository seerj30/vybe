using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Keeps the text and parameters of every SQL query that reads <c>review_flags</c>.</summary>
public sealed class FlaggedRunSqlRecorder : DbCommandInterceptor
{
    public ConcurrentQueue<(string Text, NpgsqlParameter[] Parameters)> Commands { get; } = new();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandText.Contains("review_flags", StringComparison.Ordinal))
        {
            Commands.Enqueue((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class FlaggedRunSqlApiFactory : ApiFactory
{
    public FlaggedRunSqlRecorder Recorder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.ConfigureDbContext<VybeDbContext>(options => options.AddInterceptors(Recorder)));
    }
}

/// <summary>The SQL that GET /admin/flagged-runs really sends uses the partial index ix_game_sessions_flagged_verified_at.</summary>
public sealed class ReviewFlagQueryPlanTests(FlaggedRunSqlApiFactory api) : IClassFixture<FlaggedRunSqlApiFactory>
{
    [Theory]
    [InlineData("?limit=20")]
    [InlineData("?flag=gap_variance_low&limit=20")]
    public async Task FlaggedRuns_UseThePartialIndex(string query)
    {
        var bot = await Player.CreateGuestAsync(api);
        await api.PlayRunAsync(bot, 40); // flagged
        using var moderator = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        moderator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.SupabaseToken(
            Guid.NewGuid().ToString(), new Dictionary<string, object> { ["app_metadata"] = new Dictionary<string, object> { ["vybe_role"] = "moderator" } }));

        api.Recorder.Commands.Clear();
        var page = await Contract.AssertAsync(await moderator.GetAsync($"/api/v1/admin/flagged-runs{query}"), HttpStatusCode.OK, "FlaggedRunPage");
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.Null(cursor); // one flagged run: no second page
        var (text, parameters) = Assert.Single(api.Recorder.Commands);

        // The table is tiny in a test, so a sequential scan would be cheaper; turning it off shows which index the
        // planner can use for this exact statement and these parameters.
        await using var db = api.CreateDbContext();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand($"EXPLAIN {text}", connection, transaction);
        explain.Parameters.AddRange(parameters);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains(plan, line => line.Contains("ix_game_sessions_flagged_verified_at", StringComparison.Ordinal));
    }
}
