using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Content;

public sealed class ContentOptions
{
    public const string Section = "Content";

    /// <summary>Repository root that holds <c>assets/</c> and <c>packages/game-rules/fixtures/rulesets/</c>.</summary>
    public required string Root { get; init; }

    public bool SeedOnStartup { get; init; }

    /// <summary>
    /// Public URL of the published art (<c>assets/published</c>, served by the web container at <c>/assets</c>), for example
    /// <c>https://vybe.example/assets</c>. Production requires an https URL (ProductionConfiguration).
    /// </summary>
    public string AssetBaseUrl { get; init; } = "https://cdn.vybe.invalid/assets";
}

public sealed record AvatarDefaults(string BodyPresetId, string SkinToneId, string FacePresetId, string HairPresetId, string HairColorId);

public sealed record AvatarManifest(
    int Version,
    string? Description,
    string[] BodyPresets,
    string[] SkinTones,
    string[] Faces,
    string[] Hairs,
    string[] HairColors,
    AvatarDefaults Defaults);

public sealed record CatalogEntry(
    string Sku,
    ItemSlot Slot,
    string Name,
    long Price,
    int MinLevel,
    string AssetKey,
    string[] CompatibleBodyPresets,
    Rarity Rarity,
    bool Starter = false,
    string? ThumbnailUrl = null);

public sealed record CatalogFile(int CatalogVersion, string? Description, CatalogEntry[] Items);

/// <summary>Footprint in cells: <c>W × D</c> on the floor, <c>W × H</c> on a wall (docs/design/p1-my-room.md).</summary>
public sealed record RoomFootprint(int W, int? D, int? H);

public sealed record RoomEntry(
    string Sku,
    ItemSlot Slot,
    string Name,
    long Price,
    int MinLevel,
    Rarity Rarity,
    string AssetKey,
    string[] Surfaces,
    RoomFootprint? Footprint = null,
    string? Layer = null,
    string? ThumbnailUrl = null);

public sealed record RoomGrid(int Columns, int Rows);

public sealed record RoomFile(int Version, string? Description, RoomGrid Floor, RoomGrid Wall, int MaxItems, RoomEntry[] Items);

public sealed record RulesetContent(string Id, string GameId, int Version, IRewardConfig Config, string ConfigJson, string Checksum);

/// <summary>Versioned static content (ADR 0001: avatar manifest; spec §8 catalog; spec §7 rulesets), loaded once.</summary>
public sealed class ContentCatalog
{
    private const string AssetsPrefix = "/assets";

    private readonly Dictionary<string, string> thumbnails;

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public ContentCatalog(ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Avatar = Read<AvatarManifest>(Path.Combine(options.Root, "assets", "manifests", "avatar.v1.json"));
        Catalog = Read<CatalogFile>(Path.Combine(options.Root, "assets", "manifests", "catalog.v1.json"));
        Room = Read<RoomFile>(Path.Combine(options.Root, "assets", "manifests", "room.v1.json"));
        RoomItems = Room.Items.ToDictionary(i => i.Sku, StringComparer.Ordinal);
        // Room skus never repeat avatar skus (check_assets.py), so one map holds both.
        thumbnails = Catalog.Items.Select(i => (i.Sku, i.ThumbnailUrl))
            .Concat(Room.Items.Select(i => (i.Sku, i.ThumbnailUrl)))
            .Where(i => !string.IsNullOrEmpty(i.ThumbnailUrl))
            .ToDictionary(i => i.Sku, i => i.ThumbnailUrl!, StringComparer.Ordinal);
        Rulesets = Directory
            .EnumerateFiles(Path.Combine(options.Root, "packages", "game-rules", "fixtures", "rulesets"), "*.json")
            .Select(LoadRuleset)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public ContentOptions Options { get; }

    public AvatarManifest Avatar { get; }

    public CatalogFile Catalog { get; }

    /// <summary>My Room grid and room items (footprints, layers, surfaces) by sku.</summary>
    public RoomFile Room { get; }

    public IReadOnlyDictionary<string, RoomEntry> RoomItems { get; }

    public IReadOnlyDictionary<string, RulesetContent> Rulesets { get; }

    public IEnumerable<string> StarterSkus => Catalog.Items.Where(i => i.Starter).Select(i => i.Sku);

    /// <summary>
    /// The item picture: the pipeline thumbnail (manifest <c>thumbnailUrl</c>, a <c>/assets/...</c> path) under
    /// <see cref="ContentOptions.AssetBaseUrl"/>; the old <c>{assetKey}/preview.png</c> path for an item without one.
    /// </summary>
    public string PreviewUrl(string sku, string assetKey)
    {
        var baseUrl = Options.AssetBaseUrl.TrimEnd('/');
        if (thumbnails.TryGetValue(sku, out var thumbnail))
        {
            return baseUrl + (thumbnail.StartsWith(AssetsPrefix, StringComparison.Ordinal) ? thumbnail[AssetsPrefix.Length..] : thumbnail);
        }

        return $"{baseUrl}/{assetKey}/preview.png";
    }

    public bool IsValidPresetSet(string body, string skin, string face, string hair, string hairColor) =>
        Avatar.BodyPresets.Contains(body) && Avatar.SkinTones.Contains(skin) && Avatar.Faces.Contains(face)
        && Avatar.Hairs.Contains(hair) && Avatar.HairColors.Contains(hairColor);

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), FileJson)
        ?? throw new InvalidDataException($"content file {path} is empty");

    private static RulesetContent LoadRuleset(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var config = root.GetProperty("config");
        var checksum = root.GetProperty("checksum").GetString()!;
        if (CanonicalJson.Checksum(config) != checksum)
        {
            throw new InvalidDataException($"ruleset {path}: checksum does not match its config");
        }

        var gameId = root.GetProperty("gameId").GetString()!;
        if (!Games.IsKnown(gameId))
        {
            throw new InvalidDataException($"ruleset {path}: unknown game {gameId}");
        }

        return new RulesetContent(
            root.GetProperty("id").GetString()!,
            gameId,
            root.GetProperty("version").GetInt32(),
            Games.For(gameId).ParseConfig(config.GetRawText()),
            config.GetRawText(),
            checksum);
    }
}

/// <summary>Idempotent seeding of rulesets and catalog items (Development and tests; production runs it as a deploy step).</summary>
public static class ContentSeeder
{
    public static async Task SeedAsync(VybeDbContext db, ContentCatalog content, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNowForDb();

        foreach (var ruleset in content.Rulesets.Values)
        {
            var existing = await db.GameRulesets.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleset.Id, cancellationToken);
            if (existing is null)
            {
                db.GameRulesets.Add(new GameRuleset
                {
                    Id = ruleset.Id,
                    GameId = ruleset.GameId,
                    Version = ruleset.Version,
                    Config = ruleset.ConfigJson,
                    Checksum = ruleset.Checksum,
                    ActiveFrom = now,
                });
            }
            else if (existing.Checksum != ruleset.Checksum)
            {
                throw new InvalidOperationException(
                    $"ruleset {ruleset.Id} changed after activation; publish a new ruleset version instead");
            }
        }

        var items = await db.CatalogItems.ToDictionaryAsync(i => i.Sku, StringComparer.Ordinal, cancellationToken);
        // Room items are catalog items in the room slots; the body preset column has no effect for them.
        var roomEntries = content.Room.Items.Select(r => new CatalogEntry(
            r.Sku, r.Slot, r.Name, r.Price, r.MinLevel, r.AssetKey, content.Avatar.BodyPresets, r.Rarity));
        foreach (var entry in content.Catalog.Items.Concat(roomEntries))
        {
            if (!items.TryGetValue(entry.Sku, out var item))
            {
                item = new CatalogItem { Sku = entry.Sku, Name = entry.Name, AssetKey = entry.AssetKey };
                db.CatalogItems.Add(item);
            }

            item.Slot = entry.Slot;
            item.Name = entry.Name;
            item.Price = entry.Price;
            item.MinLevel = entry.MinLevel;
            item.AssetKey = entry.AssetKey;
            item.CompatibleBodyPresets = entry.CompatibleBodyPresets;
            item.Rarity = entry.Rarity;
            item.IsActive = true;
            item.CatalogVersion = content.Catalog.CatalogVersion;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

public static class RulesetConfig
{
    /// <summary>The reward-relevant view of a stored ruleset config of game <paramref name="gameId"/>.</summary>
    public static IRewardConfig Parse(string gameId, string configJson) => Games.For(gameId).ParseConfig(configJson);
}
