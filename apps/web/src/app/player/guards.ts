import { inject } from "@angular/core";
import { type CanActivateFn, Router } from "@angular/router";
import { AuthSession } from "../auth/auth-session";
import { PlayerStore } from "./player-store";

/**
 * Screens that need a player: no session → onboarding, or the landing sign-in when this browser had a
 * signed-in account (its session lives in memory only); API unreachable → landing (unavailable state).
 */
export const requirePlayer: CanActivateFn = async () => {
  const router = inject(Router);
  const auth = inject(AuthSession);
  const player = inject(PlayerStore);
  try {
    if ((await player.ensure()) !== null) return true;
    return router.parseUrl(auth.hadAccount() ? "/" : "/play");
  } catch {
    return router.parseUrl("/");
  }
};
