import { AIM_X_MAX, POWER_MAX, type ShotResultKind } from "@vybe/game-rules";

/** Court layout in Three.js world units. aimX ±1000 spans ±COURT_HALF_WIDTH; power 0…1000 spans NEAR_Z…FAR_Z. */
export const COURT_HALF_WIDTH = 4;
export const NEAR_Z = -2;
export const FAR_Z = -12;
export const RIM_HEIGHT = 3.05;
export const RIM_RADIUS = 0.45;
export const BALL_RADIUS = 0.24;

/** Power values marked by floor lines and by matching ticks on the power meter. */
export const POWER_GUIDES: readonly number[] = [200, 400, 600, 800];

export interface CourtPoint {
  x: number;
  z: number;
}

export const X_PER_UNIT = COURT_HALF_WIDTH / AIM_X_MAX;
export const Z_PER_UNIT = (NEAR_Z - FAR_Z) / POWER_MAX;

/** Court point for an aim and a power value (a shot's landing point, or the basket at targetX/idealPower). */
export function courtPoint(aimX: number, power: number): CourtPoint {
  return { x: aimX * X_PER_UNIT, z: NEAR_Z - power * Z_PER_UNIT };
}

/**
 * Where the ball crosses rim height. The simulator result decides; geometry only follows it:
 * perfect = rim centre, hit = inside the rim toward the shot, miss = clear of the rim.
 */
export function ballEndPoint(result: ShotResultKind, landing: CourtPoint, hoop: CourtPoint): CourtPoint {
  const dx = landing.x - hoop.x;
  const dz = landing.z - hoop.z;
  const distance = Math.hypot(dx, dz);
  const dirX = distance === 0 ? 1 : dx / distance;
  const dirZ = distance === 0 ? 0 : dz / distance;
  if (result === "perfect") return hoop;
  if (result === "hit") {
    const inside = 0.5 * (RIM_RADIUS - BALL_RADIUS);
    return { x: hoop.x + dirX * inside, z: hoop.z + dirZ * inside };
  }
  const clear = RIM_RADIUS + BALL_RADIUS + 0.05;
  if (distance >= clear) return landing;
  return { x: hoop.x + dirX * clear, z: hoop.z + dirZ * clear };
}
