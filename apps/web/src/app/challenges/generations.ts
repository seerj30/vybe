/**
 * Stale-response guard for screens that load again when a route value changes. `next()` starts new
 * work and makes all earlier work stale; `snapshot()` lets an action check that the screen it started
 * on is still the current one, without making other work stale.
 */
export class Generations {
  private current = 0;

  /** Starts new work. The returned check is true until the next `next()`. */
  next(): () => boolean {
    const generation = ++this.current;
    return () => generation === this.current;
  }

  /** A check for the current work: false after the next `next()`. */
  snapshot(): () => boolean {
    const generation = this.current;
    return () => generation === this.current;
  }
}
