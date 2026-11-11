/**
 * A scripted player for Street Juggle: test data, fixtures and tuning only (the web app does not import it).
 * The client gets the seed at start, so a bot can always play well; replay proves a trace is legal,
 * not that a human played it (design §4 "Limit").
 */
import { gravityAt, StreetJuggleSim } from "./juggle.ts";
import type { JuggleTouch, StreetJuggleConfig } from "./juggle-types.ts";

export interface AutoplayOptions {
  /** Timing error of planned touch i, in ms after the ideal tick (cycled; default 0). */
  offsetsMs?: readonly number[];
  /** Planned touch i goes to a wrong spot when this returns true. */
  wrongLane?: (i: number) => boolean;
  /** Stop after this many touches. */
  maxTouches?: number;
}

/** The tick, at or after `from`, where a falling ball is nearest to `kickY`; null if it drops first. */
function idealTick(config: StreetJuggleConfig, from: number, y0: number, vy0: number): number | null {
  let y = y0;
  let vy = vy0;
  let best: number | null = null;
  let bestDy = Number.POSITIVE_INFINITY;
  for (let j = from; j < from + 100_000 && y > 0; j++) {
    const dy = Math.abs(y - config.kickY);
    if (vy <= 0 && dy <= config.goodDy && dy < bestDy) {
      best = j;
      bestDy = dy;
    }
    vy -= gravityAt(config, j);
    y += vy;
  }
  return best;
}

/** Plays a whole run and returns its trace (always a legal trace: ranges, count and cadence hold). */
export function autoplay(config: StreetJuggleConfig, seed: number, options: AutoplayOptions = {}): JuggleTouch[] {
  const sim = new StreetJuggleSim(config, seed);
  const offsets = options.offsetsMs ?? [0];
  const maxTouches = Math.min(options.maxTouches ?? config.maxEvents, config.maxEvents);
  const events: JuggleTouch[] = [];
  let planned: { tick: number; lane: number } | null = null;
  let plans = 0;

  for (let k = 0; k < sim.totalTicks; k++) {
    const ball = sim.ball;
    if (!ball.inPlay) planned = null;
    if (planned === null && ball.inPlay && events.length < maxTouches) {
      const ideal = idealTick(config, k, ball.y, ball.vy);
      if (ideal !== null) {
        const i = plans++;
        const offsetTicks = Math.round((offsets[i % offsets.length] ?? 0) / config.tickMs);
        const last = events[events.length - 1];
        const earliest = last === undefined ? k : Math.max(k, Math.ceil((last.tMs + config.minGapMs) / config.tickMs));
        const wrong = options.wrongLane?.(i) ?? false;
        planned = { tick: Math.max(earliest, ideal + offsetTicks), lane: wrong ? (ball.lane + 1) % config.lanes : ball.lane };
      }
    }
    if (planned !== null && planned.tick === k) {
      const touch = { tMs: k * config.tickMs, lane: planned.lane };
      sim.touch(touch);
      events.push(touch);
      planned = null;
    } else {
      sim.advanceTo(k + 1);
    }
  }
  return events;
}
