using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Gameplay;

internal sealed record LoadedRuleset(
    string Id,
    string GameId,
    int Version,
    JsonElement ConfigJson,
    StreetHoopsConfig Config,
    string Checksum);

/// <summary>
/// Loads rulesets and checks the stored checksum against the RFC 8785 canonical JSON of the config.
/// Ruleset content is immutable (database guard), so loaded rulesets are cached for the process.
/// </summary>
internal sealed class RulesetStore(VybeDbContext db)
{
    public const string StreetHoopsGameId = "street-hoops";

    private static readonly ConcurrentDictionary<string, LoadedRuleset> Cache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<LoadedRuleset?> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var row = await db.GameRulesets.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null ? null : Cache.GetOrAdd(row.Id, _ => Load(row));
    }

    public async Task<LoadedRuleset> GetAsync(string id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException($"ruleset {id} referenced by a session does not exist");

    /// <summary>The newest active, not retired ruleset of a game (spec §4: retirement stops new solo sessions).</summary>
    public async Task<LoadedRuleset?> FindActiveAsync(string gameId, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.GameRulesets.AsNoTracking()
            .Where(r => r.GameId == gameId && r.ActiveFrom <= now && r.RetiredAt == null)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Cache.GetOrAdd(row.Id, _ => Load(row));
    }

    private static LoadedRuleset Load(GameRuleset row)
    {
        using var document = JsonDocument.Parse(row.Config);
        var configJson = document.RootElement.Clone();
        var checksum = CanonicalJson.Checksum(configJson);
        if (!string.Equals(checksum, row.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ruleset {row.Id}: stored checksum does not match its config");
        }

        var config = configJson.Deserialize<StreetHoopsConfig>(ConfigJsonOptions)
                     ?? throw new InvalidOperationException($"ruleset {row.Id}: config is null");
        StreetHoopsSimulator.ValidateConfig(config);
        return new LoadedRuleset(row.Id, row.GameId, row.Version, configJson, config, row.Checksum);
    }
}
