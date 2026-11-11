import type { JuggleTouchKind } from "@vybe/game-rules";
import { heightY, laneX, VIEW_HEIGHT } from "./juggle-view";

/** Colors of the player's avatar (from the avatar presets; fallbacks when no avatar is known). */
export interface AvatarColors {
  skin: string;
  hair: string;
  top: string;
}

export interface JuggleFrame {
  nowMs: number;
  lanes: number;
  /** Ball position as a fractional lane, its height in ruleset units, and whether it is in play. */
  ball: { lane: number; y: number; star: boolean; inPlay: boolean };
  /** The touch window around the ideal touch height, in ruleset units. */
  zone: { kickY: number; goodDy: number; perfectDy: number };
  avatarLane: number;
  /** Time of the last touch (the leg is up for a moment), or null. */
  kickAtMs: number | null;
  flash: { lane: number; kind: JuggleTouchKind; atMs: number } | null;
  reducedMotion: boolean;
}

const KEY_HINTS = ["←", "↓", "→"];
const FLASH_MS = 320;
const KICK_MS = 180;
const TRAIL = 6;
const FLASH_COLORS: Record<JuggleTouchKind, string> = { perfect: "#ffc83d", good: "#3ddc84", whiff: "#ff8a8a" };

/** Street Juggle on a 2D canvas: a night street, three chalk spots, the ball and the player's avatar. */
export class JuggleScene {
  private readonly ctx: CanvasRenderingContext2D;
  private backdrop: HTMLCanvasElement | null = null;
  private width = 0;
  private height = 0;
  private dpr = 1;
  private readonly trail: Array<{ x: number; y: number }> = [];

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private colors: AvatarColors,
  ) {
    const ctx = canvas.getContext("2d");
    if (ctx === null) throw new Error("2D canvas is not available");
    this.ctx = ctx;
  }

  setColors(colors: AvatarColors): void {
    this.colors = colors;
  }

  resize(width: number, height: number, devicePixelRatio: number): void {
    this.width = width;
    this.height = height;
    this.dpr = Math.min(2, Math.max(1, devicePixelRatio));
    this.canvas.width = Math.round(width * this.dpr);
    this.canvas.height = Math.round(height * this.dpr);
    this.backdrop = null;
  }

  clearTrail(): void {
    this.trail.length = 0;
  }

  render(frame: JuggleFrame): void {
    const { ctx, width, height } = this;
    if (width === 0 || height === 0) return;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    // The buildings end at the curb (horizonY); the ball, the feet and the spots share the floor line (groundY).
    const horizonY = height * 0.78;
    const groundY = height * 0.9;
    const topY = Math.min(height * 0.12, 72);
    ctx.drawImage(this.backdropFor(horizonY), 0, 0, width, height);

    this.drawZone(frame, groundY, topY);
    this.drawSpots(frame, groundY);
    this.drawAvatar(frame, groundY, topY);
    if (frame.ball.inPlay) this.drawBall(frame, groundY, topY);
    else this.trail.length = 0;
  }

  /** The static street (sky, buildings, lamps, asphalt), drawn once per size. */
  private backdropFor(groundY: number): HTMLCanvasElement {
    if (this.backdrop !== null) return this.backdrop;
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(this.width * this.dpr);
    canvas.height = Math.round(this.height * this.dpr);
    const ctx = canvas.getContext("2d");
    if (ctx === null) throw new Error("2D canvas is not available");
    ctx.scale(this.dpr, this.dpr);
    const { width, height } = this;

    const sky = ctx.createLinearGradient(0, 0, 0, groundY);
    sky.addColorStop(0, "#0d1020");
    sky.addColorStop(1, "#2a1d3a");
    ctx.fillStyle = sky;
    ctx.fillRect(0, 0, width, groundY);

    // Buildings from a fixed generator, so the street looks the same every run.
    let s = 20261111;
    const next = (): number => {
      s = (Math.imul(s, 1103515245) + 12345) & 0x7fffffff;
      return s / 0x7fffffff;
    };
    for (let x = -10; x < width; ) {
      const w = 50 + next() * 90;
      const h = groundY * (0.3 + next() * 0.45);
      ctx.fillStyle = next() < 0.5 ? "#181c2e" : "#1e2236";
      ctx.fillRect(x, groundY - h, w, h);
      ctx.fillStyle = "rgb(255 207 110 / 0.35)";
      for (let wy = groundY - h + 12; wy < groundY - 20; wy += 18) {
        for (let wx = x + 8; wx < x + w - 10; wx += 14) {
          if (next() < 0.28) ctx.fillRect(wx, wy, 6, 9);
        }
      }
      x += w + 4;
    }

    for (const lampX of [width * 0.12, width * 0.88]) {
      const glow = ctx.createRadialGradient(lampX, groundY * 0.38, 0, lampX, groundY * 0.38, groundY * 0.5);
      glow.addColorStop(0, "rgb(255 170 70 / 0.35)");
      glow.addColorStop(1, "rgb(255 170 70 / 0)");
      ctx.fillStyle = glow;
      ctx.fillRect(0, 0, width, groundY);
      ctx.fillStyle = "#3a3f55";
      ctx.fillRect(lampX - 2, groundY * 0.38, 4, groundY * 0.62);
      ctx.fillStyle = "#ffd28a";
      ctx.beginPath();
      ctx.arc(lampX, groundY * 0.38, 6, 0, Math.PI * 2);
      ctx.fill();
    }

    ctx.fillStyle = "#5a5f73";
    ctx.fillRect(0, groundY - 4, width, 4);
    const road = ctx.createLinearGradient(0, groundY, 0, height);
    road.addColorStop(0, "#2b2e3b");
    road.addColorStop(1, "#1b1d26");
    ctx.fillStyle = road;
    ctx.fillRect(0, groundY, width, height - groundY);

    this.backdrop = canvas;
    return canvas;
  }

  /** The touch window as a band, with the perfect band inside it: the player sees where to touch. */
  private drawZone(frame: JuggleFrame, groundY: number, topY: number): void {
    const { ctx, width } = this;
    const { kickY, goodDy, perfectDy } = frame.zone;
    const goodTop = heightY(kickY + goodDy, groundY, topY);
    const goodBottom = heightY(kickY - goodDy, groundY, topY);
    ctx.fillStyle = "rgb(61 220 132 / 0.08)";
    ctx.fillRect(0, goodTop, width, goodBottom - goodTop);
    const perfectTop = heightY(kickY + perfectDy, groundY, topY);
    const perfectBottom = heightY(kickY - perfectDy, groundY, topY);
    ctx.fillStyle = "rgb(255 200 61 / 0.14)";
    ctx.fillRect(0, perfectTop, width, perfectBottom - perfectTop);
    ctx.setLineDash([6, 6]);
    ctx.strokeStyle = "rgb(255 200 61 / 0.6)";
    ctx.lineWidth = 1;
    const ideal = heightY(kickY, groundY, topY);
    ctx.beginPath();
    ctx.moveTo(0, ideal);
    ctx.lineTo(width, ideal);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  private drawSpots(frame: JuggleFrame, groundY: number): void {
    const { ctx, width, height } = this;
    const rx = Math.min(width / frame.lanes / 2.6, 70);
    const ry = Math.max(6, Math.min(16, height * 0.025));
    const spotY = groundY;
    for (let lane = 0; lane < frame.lanes; lane++) {
      const x = laneX(lane, frame.lanes, width);
      ctx.strokeStyle = "rgb(244 246 255 / 0.55)";
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.ellipse(x, spotY, rx, ry, 0, 0, Math.PI * 2);
      ctx.stroke();
      const hint = frame.lanes === 3 ? KEY_HINTS[lane] : undefined;
      if (hint !== undefined) {
        ctx.fillStyle = "rgb(244 246 255 / 0.6)";
        ctx.font = "600 14px system-ui, sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(hint, x, spotY);
      }
    }

    const flash = frame.flash;
    if (flash !== null) {
      const age = frame.nowMs - flash.atMs;
      if (age >= 0 && age < FLASH_MS) {
        const x = laneX(flash.lane, frame.lanes, width);
        const grow = frame.reducedMotion ? 1 : 1 + age / FLASH_MS;
        ctx.globalAlpha = 1 - age / FLASH_MS;
        ctx.strokeStyle = FLASH_COLORS[flash.kind];
        ctx.lineWidth = 5;
        ctx.beginPath();
        ctx.ellipse(x, spotY, rx * grow, ry * grow, 0, 0, Math.PI * 2);
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
    }
  }

  /**
   * A flat 2D figure in the player's skin, hair and top colors. It stands on its spot, fits its column, and its
   * kicking foot rises to the touch height, so a kick meets the ball where the rules judge it.
   */
  private drawAvatar(frame: JuggleFrame, groundY: number, topY: number): void {
    const { ctx, width, colors } = this;
    const x = laneX(frame.avatarLane, frame.lanes, width);
    const footY = groundY;
    const touchY = heightY(frame.zone.kickY, groundY, topY);
    // Knee (0.27 of the figure) at the touch height when the column is wide enough; else as wide as the column.
    const h = Math.min((groundY - touchY) / 0.27, ((width / frame.lanes) * 0.85) / 0.4, (groundY - topY) * 0.8);
    const hipY = footY - h * 0.47;
    const shoulderY = footY - h * 0.8;
    const headR = h * 0.085;
    const limb = Math.max(4, h * 0.055);
    const kicking = frame.kickAtMs !== null && frame.nowMs - frame.kickAtMs < KICK_MS;

    ctx.lineCap = "round";
    ctx.strokeStyle = "#1c2033";
    ctx.lineWidth = limb;
    ctx.beginPath();
    ctx.moveTo(x - h * 0.05, hipY);
    ctx.lineTo(x - h * 0.06, footY);
    ctx.moveTo(x + h * 0.05, hipY);
    if (kicking) {
      // Knee forward, foot up at the touch height (a high kick when the touch height is above the hip).
      const kickFootY = Math.max(touchY, hipY - h * 0.15);
      ctx.lineTo(x + h * 0.15, (hipY + kickFootY) / 2 + h * 0.08);
      ctx.lineTo(x + h * 0.22, kickFootY);
    } else {
      ctx.lineTo(x + h * 0.06, footY);
    }
    ctx.stroke();

    ctx.fillStyle = "#f4f6ff";
    ctx.fillRect(x - h * 0.1, footY - limb / 2, h * 0.08, limb);
    if (!kicking) ctx.fillRect(x + h * 0.02, footY - limb / 2, h * 0.08, limb);

    ctx.fillStyle = colors.top;
    ctx.beginPath();
    ctx.roundRect(x - h * 0.1, shoulderY, h * 0.2, hipY - shoulderY + limb, h * 0.035);
    ctx.fill();

    ctx.strokeStyle = colors.skin;
    ctx.lineWidth = limb * 0.8;
    ctx.beginPath();
    ctx.moveTo(x - h * 0.09, shoulderY + limb);
    ctx.lineTo(x - h * 0.17, hipY - h * 0.04);
    ctx.moveTo(x + h * 0.09, shoulderY + limb);
    ctx.lineTo(x + h * 0.17, hipY - h * 0.04);
    ctx.stroke();

    const headY = shoulderY - headR * 1.15;
    ctx.fillStyle = colors.skin;
    ctx.beginPath();
    ctx.arc(x, headY, headR, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = colors.hair;
    ctx.beginPath();
    ctx.arc(x, headY, headR * 1.04, Math.PI * 1.05, Math.PI * 1.95);
    ctx.fill();
  }

  private drawBall(frame: JuggleFrame, groundY: number, topY: number): void {
    const { ctx, width, height } = this;
    const x = laneX(frame.ball.lane, frame.lanes, width);
    const y = heightY(frame.ball.y, groundY, topY);
    const r = Math.max(13, Math.min(width / frame.lanes, height) * 0.045);

    const lift = Math.min(1, frame.ball.y / VIEW_HEIGHT);
    ctx.fillStyle = `rgb(0 0 0 / ${0.35 - lift * 0.25})`;
    ctx.beginPath();
    ctx.ellipse(x, groundY + 2, r * (1.2 - lift * 0.6), r * 0.3, 0, 0, Math.PI * 2);
    ctx.fill();

    if (!frame.reducedMotion) {
      this.trail.push({ x, y });
      if (this.trail.length > TRAIL) this.trail.shift();
      this.trail.forEach((p, i) => {
        ctx.fillStyle = `rgb(244 246 255 / ${(0.12 * (i + 1)) / TRAIL})`;
        ctx.beginPath();
        ctx.arc(p.x, p.y, r * (0.5 + (0.5 * i) / TRAIL), 0, Math.PI * 2);
        ctx.fill();
      });
    }

    ctx.fillStyle = frame.ball.star ? "#ffc83d" : "#f4f6ff";
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#141826";
    ctx.lineWidth = 2;
    ctx.stroke();
    if (frame.ball.star) {
      // A star shape, so the gold ball does not depend on color alone.
      this.drawStar(x, y, r * 0.62, "#141826");
    } else {
      ctx.fillStyle = "#141826";
      ctx.beginPath();
      ctx.arc(x, y, r * 0.32, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  private drawStar(x: number, y: number, r: number, color: string): void {
    const { ctx } = this;
    ctx.fillStyle = color;
    ctx.beginPath();
    for (let i = 0; i < 10; i++) {
      const radius = i % 2 === 0 ? r : r * 0.45;
      const a = -Math.PI / 2 + (i * Math.PI) / 5;
      const px = x + Math.cos(a) * radius;
      const py = y + Math.sin(a) * radius;
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    }
    ctx.closePath();
    ctx.fill();
  }
}
