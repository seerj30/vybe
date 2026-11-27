import type { PreloadingStrategy, Route, Routes } from "@angular/router";
import { type Observable, of } from "rxjs";
import { Landing } from "./pages/landing";
import { requirePlayer } from "./player/guards";

/** Shell layout per route: `bare` (no nav), `app` (nav), `fullscreen` (game canvas). */
export type Layout = "bare" | "app" | "fullscreen";

/** Small app pages: loaded in the background after the first screen, so a first visit is instant. */
const PRELOAD = { preload: true } as const;

/**
 * Background loading for the routes marked with {@link PRELOAD} only. The 3D routes (games, plaza, My Room) and admin
 * stay on demand: they are large, and many players never open them.
 */
export class PreloadPages implements PreloadingStrategy {
  preload(route: Route, load: () => Observable<unknown>): Observable<unknown> {
    return route.data?.["preload"] === true ? load() : of(null);
  }
}

// Spec §3 screen inventory. Every page except the landing page is a lazy chunk, so the first download stays small
// (budget warning 400 kB); gameplay routes also load Three.js and the simulator lazily (spec §10).
export const routes: Routes = [
  { path: "", pathMatch: "full", component: Landing, title: "VYBE", data: { layout: "bare" } },
  {
    path: "play",
    loadComponent: () => import("./pages/onboarding").then((m) => m.Onboarding),
    title: "Start · VYBE",
    data: { layout: "bare", ...PRELOAD },
  },
  // Staff may have no player, and a deletion status must open after the sessions are revoked.
  {
    path: "admin",
    loadComponent: () => import("./pages/admin").then((m) => m.Admin),
    title: "Admin · VYBE",
    data: { layout: "bare" },
  },
  {
    path: "deletion/:requestId",
    loadComponent: () => import("./pages/deletion-status").then((m) => m.DeletionStatus),
    title: "Account deletion · VYBE",
    data: { layout: "bare" },
  },
  {
    path: "practice",
    loadComponent: () => import("./street-hoops/street-hoops").then((m) => m.StreetHoops),
    title: "Street Hoops practice · VYBE",
    // Route input binding sets inputs missing from the data to undefined, so the mode is always explicit.
    data: { layout: "fullscreen", mode: "practice" },
  },
  {
    path: "practice/street-juggle",
    loadComponent: () => import("./street-juggle/street-juggle").then((m) => m.StreetJuggle),
    title: "Street Juggle practice · VYBE",
    data: { layout: "fullscreen", mode: "practice" },
  },
  {
    path: "arcade/street-juggle",
    canActivate: [requirePlayer],
    loadComponent: () => import("./street-juggle/street-juggle").then((m) => m.StreetJuggle),
    title: "Street Juggle · VYBE",
    data: { layout: "fullscreen", mode: "online" },
  },
  {
    path: "arcade/street-hoops",
    canActivate: [requirePlayer],
    loadComponent: () => import("./street-hoops/street-hoops").then((m) => m.StreetHoops),
    title: "Street Hoops · VYBE",
    data: { layout: "fullscreen", mode: "online" },
  },
  {
    // P2 public plaza (docs/design/p2-spaces-protocol.md): the scene, SignalR and MessagePack load as a lazy chunk.
    path: "plaza",
    canActivate: [requirePlayer],
    loadComponent: () => import("./plaza/plaza").then((m) => m.Plaza),
    title: "Plaza · VYBE",
    data: { layout: "fullscreen" },
  },
  // P2 live friend room visits (docs/design/p2-room-visits.md): the plaza page with a room scene, same lazy chunk.
  {
    path: "room/live",
    canActivate: [requirePlayer],
    loadComponent: () => import("./plaza/plaza").then((m) => m.Plaza),
    title: "Your room · VYBE",
    data: { layout: "fullscreen", space: "room-host" },
  },
  {
    path: "visit/:handle",
    canActivate: [requirePlayer],
    loadComponent: () => import("./plaza/plaza").then((m) => m.Plaza),
    title: "Room visit · VYBE",
    data: { layout: "fullscreen", space: "room-visit" },
  },
  // The invite preview works without a player; accepting it can create a guest (spec §2 J2).
  {
    path: "challenge/:token",
    loadComponent: () => import("./pages/invite").then((m) => m.Invite),
    title: "Challenge · VYBE",
    data: { layout: "app" },
  },
  {
    path: "",
    canActivate: [requirePlayer],
    data: { layout: "app" },
    children: [
      {
        path: "avatar/create",
        loadComponent: () => import("./pages/avatar-creator").then((m) => m.AvatarCreator),
        title: "Avatar · VYBE",
        data: PRELOAD,
      },
      {
        path: "hub",
        loadComponent: () => import("./pages/hub").then((m) => m.Hub),
        title: "Hub · VYBE",
        data: PRELOAD,
      },
      {
        path: "arcade",
        loadComponent: () => import("./pages/arcade").then((m) => m.Arcade),
        title: "Arcade · VYBE",
        data: PRELOAD,
      },
      {
        path: "shop",
        loadComponent: () => import("./pages/shop").then((m) => m.Shop),
        title: "Shop · VYBE",
        data: PRELOAD,
      },
      {
        path: "wardrobe",
        loadComponent: () => import("./pages/wardrobe").then((m) => m.Wardrobe),
        title: "Wardrobe · VYBE",
        data: PRELOAD,
      },
      // My Room: the editor and its Three.js scene load as a lazy chunk (spec §10).
      { path: "room", loadComponent: () => import("./pages/room").then((m) => m.Room), title: "My Room · VYBE" },
      {
        path: "wallet",
        loadComponent: () => import("./pages/wallet").then((m) => m.Wallet),
        title: "Wallet · VYBE",
        data: PRELOAD,
      },
      {
        path: "settings",
        loadComponent: () => import("./pages/settings").then((m) => m.Settings),
        title: "Settings · VYBE",
        data: PRELOAD,
      },
      {
        path: "challenges",
        loadComponent: () => import("./pages/challenges").then((m) => m.Challenges),
        title: "Challenges · VYBE",
        data: PRELOAD,
      },
      {
        path: "profile/:handle",
        loadComponent: () => import("./pages/profile").then((m) => m.Profile),
        title: "Profile · VYBE",
        data: PRELOAD,
      },
      {
        path: "friends",
        loadComponent: () => import("./pages/friends").then((m) => m.Friends),
        title: "Friends · VYBE",
        data: PRELOAD,
      },
      {
        path: "leaderboard",
        loadComponent: () => import("./pages/leaderboard").then((m) => m.Leaderboard),
        title: "Leaderboard · VYBE",
        data: PRELOAD,
      },
      {
        path: "achievements",
        loadComponent: () => import("./pages/achievements").then((m) => m.Achievements),
        title: "Achievements · VYBE",
        data: PRELOAD,
      },
    ],
  },
  { path: "**", redirectTo: "" },
];
