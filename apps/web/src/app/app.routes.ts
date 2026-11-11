import type { Routes } from "@angular/router";
import { AvatarCreator } from "./pages/avatar-creator";
import { Arcade } from "./pages/arcade";
import { DeletionStatus } from "./pages/deletion-status";
import { Challenges } from "./pages/challenges";
import { Hub } from "./pages/hub";
import { Invite } from "./pages/invite";
import { Landing } from "./pages/landing";
import { Onboarding } from "./pages/onboarding";
import { Settings } from "./pages/settings";
import { Profile } from "./pages/profile";
import { Shop } from "./pages/shop";
import { Wallet } from "./pages/wallet";
import { Wardrobe } from "./pages/wardrobe";
import { requirePlayer } from "./player/guards";

/** Shell layout per route: `bare` (no nav), `app` (nav), `fullscreen` (game canvas). */
export type Layout = "bare" | "app" | "fullscreen";

// Spec §3 screen inventory. Gameplay routes load Three.js and the simulator as lazy chunks (spec §10).
export const routes: Routes = [
  { path: "", pathMatch: "full", component: Landing, title: "VYBE", data: { layout: "bare" } },
  { path: "play", component: Onboarding, title: "Start · VYBE", data: { layout: "bare" } },
  // Staff may have no player, and a deletion status must open after the sessions are revoked.
  {
    path: "admin",
    loadComponent: () => import("./pages/admin").then((m) => m.Admin),
    title: "Admin · VYBE",
    data: { layout: "bare" },
  },
  { path: "deletion/:requestId", component: DeletionStatus, title: "Account deletion · VYBE", data: { layout: "bare" } },
  {
    path: "practice",
    loadComponent: () => import("./street-hoops/street-hoops").then((m) => m.StreetHoops),
    title: "Street Hoops practice · VYBE",
    // Route input binding sets inputs missing from the data to undefined, so the mode is always explicit.
    data: { layout: "fullscreen", mode: "practice" },
  },
  {
    path: "arcade/street-hoops",
    canActivate: [requirePlayer],
    loadComponent: () => import("./street-hoops/street-hoops").then((m) => m.StreetHoops),
    title: "Street Hoops · VYBE",
    data: { layout: "fullscreen", mode: "online" },
  },
  // The invite preview works without a player; accepting it can create a guest (spec §2 J2).
  { path: "challenge/:token", component: Invite, title: "Challenge · VYBE", data: { layout: "app" } },
  {
    path: "",
    canActivate: [requirePlayer],
    data: { layout: "app" },
    children: [
      { path: "avatar/create", component: AvatarCreator, title: "Avatar · VYBE" },
      { path: "hub", component: Hub, title: "Hub · VYBE" },
      { path: "arcade", component: Arcade, title: "Arcade · VYBE" },
      { path: "shop", component: Shop, title: "Shop · VYBE" },
      { path: "wardrobe", component: Wardrobe, title: "Wardrobe · VYBE" },
      // My Room: the editor and its Three.js scene load as a lazy chunk (spec §10).
      { path: "room", loadComponent: () => import("./pages/room").then((m) => m.Room), title: "My Room · VYBE" },
      { path: "wallet", component: Wallet, title: "Wallet · VYBE" },
      { path: "settings", component: Settings, title: "Settings · VYBE" },
      { path: "challenges", component: Challenges, title: "Challenges · VYBE" },
      { path: "profile/:handle", component: Profile, title: "Profile · VYBE" },
    ],
  },
  { path: "**", redirectTo: "" },
];
