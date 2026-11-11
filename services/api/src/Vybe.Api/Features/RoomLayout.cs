using System.Text.Json;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

/// <summary>One stored placement (docs/design/p1-my-room.md): grid cell of the footprint corner, 90° rotation.</summary>
public sealed record StoredPlacement(Guid ItemId, string Surface, int X, int Y, int Rotation);

/// <summary>The item facts a placement check needs: slot and active flag from the database, footprint from the manifest.</summary>
public sealed record PlaceableItem(Guid Id, ItemSlot Slot, bool IsActive, RoomEntry? Entry);

/// <summary>
/// The My Room placement rules (docs/design/p1-my-room.md §1). The web editor has the same rules
/// (<c>apps/web/src/app/room/room-layout.ts</c>); this copy is the authority.
/// </summary>
public static class RoomLayout
{
    public const string Floor = "floor";
    public const string WallBack = "wall_back";
    public const string WallLeft = "wall_left";
    public const string Rug = "rug";

    /// <summary>No grid is this large; a larger x or y is out of bounds before any arithmetic.</summary>
    public const int MaxCoordinate = 64;

    public static readonly IReadOnlySet<string> Surfaces = new HashSet<string>(StringComparer.Ordinal) { Floor, WallBack, WallLeft };

    public static readonly IReadOnlySet<int> Rotations = new HashSet<int> { 0, 90, 180, 270 };

    public static bool IsRoomSlot(ItemSlot slot) =>
        slot is ItemSlot.Furniture or ItemSlot.WallDecor or ItemSlot.RoomWall or ItemSlot.RoomFloor;

    public static IReadOnlyList<StoredPlacement> Parse(string json) =>
        JsonSerializer.Deserialize<StoredPlacement[]>(json, ApiJson.Options) ?? [];

    public static string ToJson(IReadOnlyList<StoredPlacement> placements) => JsonSerializer.Serialize(placements, ApiJson.Options);

    /// <summary>
    /// Checks slots, surfaces, bounds and overlaps; throws the first problem (with the <c>itemId</c>).
    /// Ownership is checked before (every id must be in <paramref name="items"/>).
    /// </summary>
    public static void Validate(IReadOnlyList<StoredPlacement> placements, IReadOnlyDictionary<Guid, PlaceableItem> items, RoomFile room)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(room);
        var floorObjects = new HashSet<(int, int)>();
        var floorRugs = new HashSet<(int, int)>();
        var walls = new Dictionary<string, HashSet<(int, int)>>(StringComparer.Ordinal)
        {
            [WallBack] = [],
            [WallLeft] = [],
        };
        foreach (var p in placements)
        {
            var item = items[p.ItemId];
            var onFloor = p.Surface == Floor;
            var slotFits = onFloor ? item.Slot == ItemSlot.Furniture : item.Slot == ItemSlot.WallDecor;
            if (!slotFits || item.Entry?.Footprint is not { } footprint || !item.Entry.Surfaces.Contains(p.Surface))
            {
                throw ItemProblem(422, "incompatible_item", p.ItemId);
            }

            if (!item.IsActive)
            {
                throw ItemProblem(422, "item_inactive", p.ItemId);
            }

            if (!onFloor && p.Rotation != 0)
            {
                throw Problem.ValidationFailed("/items/rotation", "invalid", "a wall item always has rotation 0");
            }

            var (w, d) = Cells(footprint, p.Surface, p.Rotation);
            var grid = onFloor ? room.Floor : room.Wall;
            if (p.X < 0 || p.Y < 0 || p.X > grid.Columns - w || p.Y > grid.Rows - d)
            {
                throw ItemProblem(422, "placement_out_of_bounds", p.ItemId);
            }

            var taken = onFloor ? (item.Entry.Layer == Rug ? floorRugs : floorObjects) : walls[p.Surface];
            for (var x = p.X; x < p.X + w; x++)
            {
                for (var y = p.Y; y < p.Y + d; y++)
                {
                    if (!taken.Add((x, y)))
                    {
                        throw ItemProblem(422, "placement_overlap", p.ItemId);
                    }
                }
            }
        }
    }

    /// <summary>Cells covered along x and y: the floor footprint turns with the rotation; a wall footprint is w × h.</summary>
    public static (int W, int D) Cells(RoomFootprint footprint, string surface, int rotation)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        if (surface != Floor)
        {
            return (footprint.W, footprint.H ?? 0);
        }

        var depth = footprint.D ?? 0;
        return rotation is 90 or 270 ? (depth, footprint.W) : (footprint.W, depth);
    }

    public static ApiProblemException ItemProblem(int status, string code, Guid itemId) =>
        new(status, code, extensions: new Dictionary<string, object?> { ["itemId"] = itemId });
}
