using Vybe.Api.Content;

namespace Vybe.Api.Features;

/// <summary>
/// One blocked area of an open room in space cm (x right, z toward the open front): a floor item's footprint grown
/// by <see cref="RoomCollision.AvatarRadiusCm"/>. A point blocks only strictly inside; the edges are free.
/// </summary>
public readonly record struct RoomObstacle(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public bool Contains(double x, double z) => x > MinX && x < MaxX && z > MinZ && z < MaxZ;
}

/// <summary>
/// Furniture collision in open rooms (docs/design/p2-room-visits.md §4). The web predictor has the same rules
/// (<c>apps/web/src/app/plaza/room-collision.ts</c>); shared vectors in <c>packages/contracts/fixtures</c> keep the
/// two copies equal. The plaza has no obstacles, so its movement is a plain clamp.
/// </summary>
public static class RoomCollision
{
    /// <summary>The avatar is a circle of this radius: about the shoulder width of the toon body.</summary>
    public const int AvatarRadiusCm = 20;

    /// <summary>Spawn search grid (cm): a blocked spawn point moves to the nearest free point of this grid.</summary>
    public const int SpawnGridCm = 10;

    /// <summary>
    /// The obstacles of a layout: floor items on the <c>object</c> layer, with the rotated footprint. Rugs, wall items
    /// and SKUs that the manifest does not know never block.
    /// </summary>
    public static IReadOnlyList<RoomObstacle> Obstacles(SpaceRoom layout, IReadOnlyDictionary<string, RoomEntry> items, RoomFile room)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(room);
        var cell = (int)Math.Round(room.CellMeters * 100);
        var half = room.Floor.Columns * cell / 2;
        var obstacles = new List<RoomObstacle>();
        foreach (var p in layout.Items)
        {
            if (p.Surface != RoomLayout.Floor || !items.TryGetValue(p.Sku, out var entry)
                || entry.Layer == RoomLayout.Rug || entry.Footprint is not { } footprint)
            {
                continue;
            }

            var (w, d) = RoomLayout.Cells(footprint, p.Surface, p.Rotation);
            obstacles.Add(new RoomObstacle(
                -half + (p.X * cell) - AvatarRadiusCm,
                -half + (p.Y * cell) - AvatarRadiusCm,
                -half + ((p.X + w) * cell) + AvatarRadiusCm,
                -half + ((p.Y + d) * cell) + AvatarRadiusCm));
        }

        return obstacles;
    }

    /// <summary>
    /// Moves by (<paramref name="sx"/>, <paramref name="sz"/>) inside ±<paramref name="bound"/>: first along x, then
    /// along z, so a diagonal move into an obstacle slides along its edge. An obstacle stops a move that starts
    /// outside it at its edge; a point already inside an obstacle moves freely, so nobody gets stuck.
    /// </summary>
    public static (double X, double Z) Move(double x, double z, double sx, double sz, IReadOnlyList<RoomObstacle> obstacles, int bound)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        var nx = Math.Clamp(x + sx, -bound, bound);
        foreach (var o in obstacles)
        {
            if (z > o.MinZ && z < o.MaxZ)
            {
                if (x <= o.MinX && nx > o.MinX)
                {
                    nx = o.MinX;
                }
                else if (x >= o.MaxX && nx < o.MaxX)
                {
                    nx = o.MaxX;
                }
            }
        }

        var nz = Math.Clamp(z + sz, -bound, bound);
        foreach (var o in obstacles)
        {
            if (nx > o.MinX && nx < o.MaxX)
            {
                if (z <= o.MinZ && nz > o.MinZ)
                {
                    nz = o.MinZ;
                }
                else if (z >= o.MaxZ && nz < o.MaxZ)
                {
                    nz = o.MaxZ;
                }
            }
        }

        return (nx, nz);
    }

    /// <summary>
    /// A spawn point outside every obstacle: the point itself when it is free, else the nearest free point of a
    /// <see cref="SpawnGridCm"/> grid inside ±<paramref name="bound"/>. With no free point at all the point stays (the
    /// avatar can still walk out, see <see cref="Move"/>).
    /// </summary>
    public static (double X, double Z) FreeSpot(double x, double z, IReadOnlyList<RoomObstacle> obstacles, int bound)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        if (!obstacles.Any(o => o.Contains(x, z)))
        {
            return (x, z);
        }

        var best = (X: x, Z: z);
        var bestDistance = double.PositiveInfinity;
        for (var cz = -bound; cz <= bound; cz += SpawnGridCm)
        {
            for (var cx = -bound; cx <= bound; cx += SpawnGridCm)
            {
                var distance = ((cx - x) * (cx - x)) + ((cz - z) * (cz - z));
                if (distance < bestDistance && !obstacles.Any(o => o.Contains(cx, cz)))
                {
                    (best, bestDistance) = ((cx, cz), distance);
                }
            }
        }

        return best;
    }
}
