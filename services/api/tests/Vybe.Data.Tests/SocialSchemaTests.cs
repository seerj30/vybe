namespace Vybe.Data.Tests;

/// <summary>P1 friends and weekly leaderboard: database rules (docs/design/p1-friends-leaderboard.md).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SocialSchemaTests(PostgresFixture db)
{
    private const string InsertFriendship =
        "INSERT INTO vybe.friendships (id, requester_player_id, addressee_player_id, status, suppressed, responded_at) VALUES (gen_random_uuid(), $1, $2, $3, $4, $5)";

    [Fact]
    public async Task Friendships_OneRowPerPair_InEitherDirection_NoSelf()
    {
        var a = await Sql.PlayerAsync(db.DataSource);
        var b = await Sql.PlayerAsync(db.DataSource);
        await Sql.ExecuteAsync(db.DataSource, InsertFriendship, a, b, "pending", false, DBNull.Value);

        Assert.Equal(Sql.UniqueViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, b, a, "pending", false, DBNull.Value)).SqlState);
        Assert.Equal(Sql.UniqueViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, a, b, "pending", false, DBNull.Value)).SqlState);
        Assert.Equal(Sql.CheckViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, a, a, "pending", false, DBNull.Value)).SqlState);
    }

    [Fact]
    public async Task Friendships_OnlyPendingRowsAreSuppressed_AndAnsweredRowsHaveATime()
    {
        var a = await Sql.PlayerAsync(db.DataSource);
        var b = await Sql.PlayerAsync(db.DataSource);
        Assert.Equal(Sql.CheckViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, a, b, "accepted", true, DateTime.UtcNow)).SqlState);
        Assert.Equal(Sql.CheckViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, a, b, "accepted", false, DBNull.Value)).SqlState);
        Assert.Equal(Sql.CheckViolation, (await Sql.FailsAsync(db.DataSource, InsertFriendship, a, b, "blocked", false, DateTime.UtcNow)).SqlState);
    }

    [Fact]
    public async Task WeeklyScores_StartOnAMonday()
    {
        var a = await Sql.PlayerAsync(db.DataSource);
        var failure = await Sql.FailsAsync(
            db.DataSource,
            "INSERT INTO vybe.weekly_leaderboard_scores (game_id, week_start, player_id, best_score, session_id, achieved_at) VALUES ('street-hoops', $1, $2, 10, gen_random_uuid(), now())",
            new DateOnly(2026, 11, 10), a);
        Assert.Equal(Sql.CheckViolation, failure.SqlState);
    }
}
