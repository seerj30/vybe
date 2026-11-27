// The routes import the landing component; JIT compiles it in this test.
import "@angular/compiler";
import type { Route } from "@angular/router";
import { firstValueFrom, of } from "rxjs";
import { describe, expect, it } from "vitest";
import { PreloadPages, routes } from "./app.routes";

const flatten = (list: readonly Route[]): Route[] => list.flatMap((route) => [route, ...flatten(route.children ?? [])]);

describe("route loading", () => {
  it("loads only the landing page in the first download", () => {
    const eager = flatten(routes).filter((route) => route.component !== undefined);
    expect(eager.map((route) => route.path)).toEqual([""]);
  });

  it("preloads the small app pages, never the 3D routes or admin", () => {
    const preloaded = flatten(routes)
      .filter((route) => route.data?.["preload"] === true)
      .map((route) => route.path);
    expect(preloaded).toContain("hub");
    expect(preloaded).toContain("play");
    for (const heavy of ["room", "plaza", "room/live", "visit/:handle", "admin", "practice", "arcade/street-hoops", "arcade/street-juggle"]) {
      expect(preloaded).not.toContain(heavy);
    }
  });

  it("is injectable (the router asks for the preloader as optional: else preloading is silently off)", () => {
    // `@Injectable` adds the injectable definition `ɵprov`; without it the injector cannot create the strategy.
    expect("ɵprov" in PreloadPages).toBe(true);
  });

  it("runs the loader only for a marked route", async () => {
    const strategy = new PreloadPages();
    let loads = 0;
    const load = () => {
      loads++;
      return of("chunk");
    };
    expect(await firstValueFrom(strategy.preload({ path: "hub", data: { preload: true } }, load))).toBe("chunk");
    expect(await firstValueFrom(strategy.preload({ path: "plaza", data: { layout: "fullscreen" } }, load))).toBeNull();
    expect(loads).toBe(1);
  });
});
