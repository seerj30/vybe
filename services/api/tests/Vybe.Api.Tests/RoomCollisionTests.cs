using System.Text.Json;
using Vybe.Api.Content;
using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary>
/// Furniture collision in open rooms (docs/design/p2-room-visits.md §4). The vectors in
/// <c>packages/contracts/fixtures/room-collision.v1.json</c> also run in the web predictor test
/// (<c>apps/web/src/app/plaza/room-collision.test.ts</c>), so both sides stop an avatar at the same place.
/// </summary>
public sealed class RoomCollisionTests
{
    private static readonly ContentCatalog Content = new(new ContentOptions { Root = ApiFactory.RepoRoot });

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly VectorFile Vectors = JsonSerializer.Deserialize<VectorFile>(
        File.ReadAllText(Path.Combine(ApiFactory.RepoRoot, "packages", "contracts", "fixtures", "room-collision.v1.json")), Json)!;

    public static TheoryData<string> CaseNames() => [.. Vectors.Cases.Select(c => c.Name)];

    [Fact]
    public void Vectors_UseTheRulesOfTheApi()
    {
        Assert.Equal(RoomCollision.AvatarRadiusCm, Vectors.AvatarRadiusCm);
        Assert.Equal(SpaceRules.RoomBoundCm, Vectors.BoundCm);
        Assert.Equal(SpaceRules.RoomSpeedCmPerS, Vectors.SpeedCmPerS);
        Assert.Equal(0.5, Content.Room.CellMeters);
    }

    /// <summary>Blocked moves, sliding, rotation, rugs, wall items, an avatar inside an obstacle, no furniture.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedVector(string name)
    {
        var vector = Vectors.Cases.Single(c => c.Name == name);
        var obstacles = RoomCollision.Obstacles(new SpaceRoom(null, null, vector.Items), Content.RoomItems, Content.Room);
        Assert.Equal(vector.Obstacles, obstacles.Select(o => new[] { o.MinX, o.MinZ, o.MaxX, o.MaxZ }));
        foreach (var walk in vector.Walks)
        {
            var (x, z) = (walk.From[0], walk.From[1]);
            for (var tick = 0; tick < walk.Ticks; tick++)
            {
                (x, z) = SpaceManager.Walk(x, z, walk.Intent[0], walk.Intent[1], SpaceRules.RoomSpeedCmPerS, SpaceRules.RoomBoundCm, obstacles);
            }

            Assert.Equal(walk.To[0], x, 0.01);
            Assert.Equal(walk.To[1], z, 0.01);
        }
    }

    [Theory]
    [InlineData(127, 0)]
    [InlineData(-90, 90)]
    [InlineData(30, -127)]
    [InlineData(64, 17)]
    public void Walk_WithoutObstacles_IsThePlazaRule(int dx, int dz)
    {
        // The movement rule before furniture collision (protocol doc §4): direction × speed × |intent| / 127, clamped.
        var (x, z) = (1590.0, -1590.0);
        for (var tick = 0; tick < 5; tick++)
        {
            var length = Math.Sqrt((dx * dx) + (dz * dz));
            var step = SpaceRules.SpeedCmPerS * Math.Min(length / SpaceRules.MaxIntent, 1) * 0.1;
            var expected = (Math.Clamp(x + (dx / length * step), -SpaceRules.BoundCm, SpaceRules.BoundCm),
                Math.Clamp(z + (dz / length * step), -SpaceRules.BoundCm, SpaceRules.BoundCm));
            (x, z) = SpaceManager.Walk(x, z, dx, dz, SpaceRules.SpeedCmPerS, SpaceRules.BoundCm, []);
            Assert.Equal(expected, (x, z));
        }
    }

    [Fact]
    public void FreeSpot_KeepsAFreePoint_AndMovesABlockedPointToTheNearestFreePoint()
    {
        // A bed (3 × 4 cells) over the room center: x −50…100, z −100…100, plus the avatar radius.
        var obstacles = Obstacles(("furn_bed", 3, 2, 0));
        Assert.Equal(new RoomObstacle(-70, -120, 120, 120), Assert.Single(obstacles));
        Assert.Equal((-70.0, 0.0), RoomCollision.FreeSpot(0, 0, obstacles, SpaceRules.RoomBoundCm));
        Assert.Equal((150.0, 30.0), RoomCollision.FreeSpot(150, 30, obstacles, SpaceRules.RoomBoundCm));
        Assert.Equal((120.0, 40.0), RoomCollision.FreeSpot(110, 43, obstacles, SpaceRules.RoomBoundCm));
    }

    [Fact]
    public void FreeSpot_InAFullRoom_KeepsThePoint_AndFindsTheLastFreeCorner()
    {
        // Sofas (4 × 2 cells) on every floor cell: no free point is left.
        var crowded = Obstacles(
            ("furn_sofa", 0, 0, 0), ("furn_sofa", 4, 0, 0), ("furn_sofa", 0, 2, 0), ("furn_sofa", 4, 2, 0),
            ("furn_sofa", 0, 4, 0), ("furn_sofa", 4, 4, 0), ("furn_sofa", 0, 6, 0), ("furn_sofa", 4, 6, 0));
        for (var x = -170; x <= 170; x += 17)
        {
            for (var z = -170; z <= 170; z += 17)
            {
                var (fx, fz) = RoomCollision.FreeSpot(x, z, crowded, SpaceRules.RoomBoundCm);
                Assert.Contains(crowded, o => o.Contains(fx, fz)); // every cell is blocked, so the point stays
                Assert.Equal((x, z), ((int)fx, (int)fz));
            }
        }

        // Two free cells at the front right (x 150…200, z 100…200): the nearest free point is on the right bound.
        var almost = Obstacles(
            ("furn_sofa", 0, 0, 0), ("furn_sofa", 4, 0, 0), ("furn_sofa", 0, 2, 0), ("furn_sofa", 4, 2, 0),
            ("furn_sofa", 0, 4, 0), ("furn_sofa", 4, 4, 0), ("furn_sofa", 0, 6, 0), ("furn_bookshelf", 4, 6, 0),
            ("furn_floor_lamp", 6, 6, 0), ("furn_ball_rack", 4, 7, 0), ("furn_cactus", 6, 7, 0));
        Assert.Equal((170.0, 120.0), RoomCollision.FreeSpot(0, 120, almost, SpaceRules.RoomBoundCm));
    }

    [Fact]
    public void Obstacles_IgnoreRugsWallItemsAndUnknownSkus_AndTurnWithTheRotation()
    {
        var layout = new SpaceRoom(null, null,
        [
            new SpaceRoomItem("furn_round_rug", RoomLayout.Floor, 3, 3, 0),
            new SpaceRoomItem("decor_hoops_poster", RoomLayout.WallBack, 3, 0, 0),
            new SpaceRoomItem("decor_wall_clock", RoomLayout.WallLeft, 3, 2, 0),
            new SpaceRoomItem("furn_no_such_item", RoomLayout.Floor, 0, 0, 0),
            new SpaceRoomItem("furn_desk", RoomLayout.Floor, 0, 0, 90),
        ]);
        // A desk is 3 × 2 cells; turned 90° it covers 2 × 3: x −200…−100, z −200…−50.
        Assert.Equal(
            [new RoomObstacle(-220, -220, -80, -30)],
            RoomCollision.Obstacles(layout, Content.RoomItems, Content.Room));
    }

    private static IReadOnlyList<RoomObstacle> Obstacles(params (string Sku, int X, int Y, int Rotation)[] items) =>
        RoomCollision.Obstacles(
            new SpaceRoom(null, null, [.. items.Select(i => new SpaceRoomItem(i.Sku, RoomLayout.Floor, i.X, i.Y, i.Rotation))]),
            Content.RoomItems,
            Content.Room);

    private sealed record VectorFile(int AvatarRadiusCm, int BoundCm, int SpeedCmPerS, IReadOnlyList<VectorCase> Cases);

    private sealed record VectorCase(string Name, IReadOnlyList<SpaceRoomItem> Items, IReadOnlyList<int[]> Obstacles, IReadOnlyList<VectorWalk> Walks);

    private sealed record VectorWalk(double[] From, int[] Intent, int Ticks, double[] To);
}
