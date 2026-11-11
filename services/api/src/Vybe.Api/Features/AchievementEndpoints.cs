using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

public sealed record AchievementResponse(
    string Code, string Name, string Description, string Category, AchievementTier Tier, int RewardXp, int Target, int Progress,
    DateTime? UnlockedAt);

public sealed record AchievementList(int UnlockedCount, int TotalCount, int EarnedXp, IReadOnlyList<AchievementResponse> Achievements);

/// <summary>An unlocked achievement on the public profile: no unlock time (data minimization).</summary>
public sealed record AchievementBadge(string Code, string Name, AchievementTier Tier);

/// <summary>P1 achievements (docs/design/p1-achievements.md): the caller's own list. Read only.</summary>
public static class AchievementEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/me/achievements", ListAsync);
    }

    private static async Task<AchievementList> ListAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, AchievementCatalog catalog, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var playerId = await current.GetIdAsync(cancellationToken);
        var stats = await db.PlayerStats.AsNoTracking().Where(s => s.PlayerId == playerId)
            .ToDictionaryAsync(s => s.Stat, s => s.Value, StringComparer.Ordinal, cancellationToken);
        var unlocked = await UnlockedAsync(db, playerId, cancellationToken);

        var items = catalog.All.Select(a =>
        {
            var at = unlocked.TryGetValue(a.Code, out var time) ? time : (DateTime?)null;
            var progress = at is null ? (int)Math.Min(stats.GetValueOrDefault(a.Stat), a.Target) : a.Target;
            return new AchievementResponse(a.Code, a.Name, a.Description, a.Category, a.Tier, a.RewardXp, a.Target, progress, at);
        }).ToList();
        var done = items.Where(i => i.UnlockedAt is not null).ToList();
        return new AchievementList(done.Count, items.Count, done.Sum(i => i.RewardXp), items);
    }

    /// <summary>Unlocked badges of a player for the public profile: gold first, then the manifest order.</summary>
    public static async Task<IReadOnlyList<AchievementBadge>> BadgesAsync(
        VybeDbContext db, AchievementCatalog catalog, Guid playerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var unlocked = await UnlockedAsync(db, playerId, cancellationToken);
        return catalog.All.Where(a => unlocked.ContainsKey(a.Code))
            .OrderByDescending(a => a.Tier).ThenBy(a => catalog.Order(a.Code))
            .Select(a => new AchievementBadge(a.Code, a.Name, a.Tier))
            .ToList();
    }

    private static async Task<Dictionary<string, DateTime>> UnlockedAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.PlayerAchievements.AsNoTracking().Where(p => p.PlayerId == playerId)
            .Join(db.Achievements.Where(a => a.IsActive), p => p.AchievementId, a => a.Id, (p, a) => new { a.Code, p.UnlockedAt })
            .ToDictionaryAsync(x => x.Code, x => x.UnlockedAt, StringComparer.Ordinal, cancellationToken);
    }
}
