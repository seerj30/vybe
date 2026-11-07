import { DestroyRef, inject } from "@angular/core";

/**
 * Calls `callback` each time the tab becomes visible again, so a screen shows changes made elsewhere
 * (for example the other player's run). Call it in an injection context; it stops with the component.
 */
export function whenVisible(callback: () => void): void {
  const listener = (): void => {
    if (document.visibilityState === "visible") callback();
  };
  document.addEventListener("visibilitychange", listener);
  inject(DestroyRef).onDestroy(() => document.removeEventListener("visibilitychange", listener));
}
