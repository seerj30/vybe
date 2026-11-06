import { inject } from "@angular/core";
import { type CanActivateFn, Router } from "@angular/router";
import { PlayerStore } from "./player-store";

/** Screens that need a player: no session → onboarding; API unreachable → landing (unavailable state). */
export const requirePlayer: CanActivateFn = async () => {
  const router = inject(Router);
  try {
    return (await inject(PlayerStore).ensure()) === null ? router.parseUrl("/play") : true;
  } catch {
    return router.parseUrl("/");
  }
};
