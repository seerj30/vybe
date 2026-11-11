import { describe, expect, it } from "vitest";
import { cells, describePlacement, firstFree, otherWall, type Placement, problemOf, rotated, toRequest, withPlacement } from "./room-layout";
import { roomItem } from "./room-manifest";

const at = (itemId: string, sku: string, x: number, y: number, rotation: Placement["rotation"] = 0, surface: Placement["surface"] = "floor"): Placement =>
  ({ itemId, sku, surface, x, y, rotation });

describe("room layout rules (docs/design/p1-my-room.md)", () => {
  it("turns the floor footprint with the rotation; a wall footprint does not turn", () => {
    expect(cells(roomItem("furn_sofa")!, "floor", 0)).toEqual([4, 2]);
    expect(cells(roomItem("furn_sofa")!, "floor", 90)).toEqual([2, 4]);
    expect(cells(roomItem("decor_wall_shelf")!, "wall_back", 0)).toEqual([2, 1]);
  });

  it("refuses items outside the grid", () => {
    expect(problemOf([at("a", "furn_sofa", 5, 0)])?.code).toBe("placement_out_of_bounds");
    expect(problemOf([at("a", "furn_sofa", 0, 5, 90)])?.code).toBe("placement_out_of_bounds");
    expect(problemOf([at("a", "furn_sofa", 6, 4, 90)])).toBeNull();
    expect(problemOf([at("a", "decor_hoops_poster", 0, 4, 0, "wall_back")])?.code).toBe("placement_out_of_bounds");
  });

  it("refuses overlaps on one layer or wall, and allows a rug under an object", () => {
    expect(problemOf([at("a", "furn_sofa", 0, 0), at("b", "furn_bed", 3, 1)])).toEqual({ itemId: "b", code: "placement_overlap" });
    expect(problemOf([at("a", "furn_round_rug", 0, 0), at("b", "furn_sofa", 0, 0)])).toBeNull();
    expect(problemOf([at("a", "furn_round_rug", 0, 0), at("b", "furn_striped_rug", 2, 2)])?.code).toBe("placement_overlap");
    expect(problemOf([at("a", "decor_hoops_poster", 0, 0, 0, "wall_back"), at("b", "decor_wall_clock", 0, 0, 0, "wall_left")])).toBeNull();
  });

  it("refuses wrong surfaces, finishes and turned wall items", () => {
    expect(problemOf([at("a", "decor_wall_clock", 0, 0)])?.code).toBe("incompatible_item");
    expect(problemOf([at("a", "furn_sofa", 0, 0, 0, "wall_back")])?.code).toBe("incompatible_item");
    expect(problemOf([at("a", "walls_mint", 0, 0)])?.code).toBe("incompatible_item");
    expect(problemOf([at("a", "decor_wall_clock", 0, 0, 90, "wall_back")])?.code).toBe("incompatible_item");
  });

  it("refuses more than 40 items", () => {
    const many = Array.from({ length: 41 }, (_, i) => at(`i${i}`, "furn_potted_plant", i % 8, Math.floor(i / 8) % 8));
    expect(problemOf(many)?.code).toBe("room_item_limit");
  });

  it("keeps the order when it replaces a placement, and refuses an invalid move", () => {
    const layout = [at("a", "furn_beanbag", 0, 0), at("b", "furn_cactus", 4, 4)];
    expect(withPlacement(layout, at("a", "furn_beanbag", 2, 0))).toEqual([at("a", "furn_beanbag", 2, 0), layout[1]]);
    expect(withPlacement(layout, at("a", "furn_beanbag", 3, 3))).toBe("placement_overlap");
  });

  it("finds the first free place on the floor, and at eye height on a wall", () => {
    expect(firstFree([at("a", "furn_beanbag", 0, 0)], { itemId: "b", sku: "furn_beanbag" })).toMatchObject({ x: 2, y: 0 });
    expect(firstFree([], { itemId: "p", sku: "decor_hoops_poster" })).toMatchObject({ surface: "wall_back", x: 0, y: 2 });
    expect(firstFree([], { itemId: "w", sku: "walls_mint" })).toBeNull();
  });

  it("rotates in 90° steps and moves wall items to the other wall", () => {
    expect(rotated(at("a", "furn_sofa", 0, 0, 270)).rotation).toBe(0);
    expect(otherWall(at("a", "decor_wall_clock", 1, 2, 0, "wall_back")).surface).toBe("wall_left");
    expect(describePlacement(at("a", "furn_sofa", 2, 3, 90))).toBe("floor, column 3, row 4, turned 90°");
  });

  it("builds the save request without skus", () => {
    const request = toRequest({ wallItem: { itemId: "w", sku: "walls_mint" }, floorItem: null, items: [at("a", "furn_sofa", 1, 2, 180)] }, 3);
    expect(request).toEqual({
      expectedRevision: 3,
      wallItemId: "w",
      floorItemId: null,
      items: [{ itemId: "a", surface: "floor", x: 1, y: 2, rotation: 180 }],
    });
  });
});
