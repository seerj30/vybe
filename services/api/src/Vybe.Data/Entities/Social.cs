namespace Vybe.Data.Entities;

/// <summary>Spec §4 <c>friendships</c> (P1): one row per player pair, whatever the state.</summary>
public enum FriendshipStatus { Pending, Accepted, Declined }

/// <summary>
/// Spec §4 <c>friendships</c> (P1, docs/design/p1-friends-leaderboard.md). The canonical pair is unique (an expression
/// index in the migration). <c>Suppressed</c>: a request to a player who blocked the requester; never shown to that player.
/// </summary>
public sealed class Friendship
{
    public Guid Id { get; set; }
    public Guid RequesterPlayerId { get; set; }
    public Guid AddresseePlayerId { get; set; }
    public FriendshipStatus Status { get; set; }
    public bool Suppressed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>New friend requests a player sent on one UTC day (abuse limit; a cancel does not give one back).</summary>
public sealed class FriendRequestCounter
{
    public Guid PlayerId { get; set; }
    public DateOnly Day { get; set; }
    public int Sent { get; set; }
}

/// <summary>
/// Weekly leaderboard summary (P1): the best counted score of one player in one game and ISO week (UTC). Written in the
/// run verification transaction, so the board never scans <c>game_sessions</c>.
/// </summary>
public sealed class WeeklyLeaderboardScore
{
    public required string GameId { get; set; }
    public DateOnly WeekStart { get; set; }
    public Guid PlayerId { get; set; }
    public int BestScore { get; set; }
    public Guid SessionId { get; set; }
    public DateTime AchievedAt { get; set; }
}
