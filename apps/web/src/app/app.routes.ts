import type { Routes } from "@angular/router";
import { AvatarCreator } from "./pages/avatar-creator";
import { Arcade } from "./pages/arcade";
import { Hub } from "./pages/hub";
import { Landing } from "./pages/landing";
import { Onboarding } from "./pages/onboarding";
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
      { path: "wallet", component: Wallet, title: "Wallet · VYBE" },
    ],
  },
  { path: "**", redirectTo: "" },
];
