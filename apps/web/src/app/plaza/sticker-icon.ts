import { ChangeDetectionStrategy, Component, computed, input } from "@angular/core";

/**
 * The 12 plaza stickers (assets/manifests/quickchat.v1.json, protocol §13 question 3): flat 2D icons drawn as
 * SVG shapes in a 48 × 48 box. Static data only (no markup from outside), rendered with Angular bindings.
 */

interface Shape {
  d?: string;
  circle?: readonly [number, number, number];
  rect?: readonly [number, number, number, number, number];
  fill?: string;
  stroke?: string;
  width?: number;
  transform?: string;
}

const INK = "#3a2a10";
const GOLD = "#ffc83d";
const GOLD_EDGE = "#d9930b";
const FACE: Shape = { circle: [24, 24, 20], fill: GOLD, stroke: GOLD_EDGE, width: 2 };

export const STICKER_ART: Readonly<Record<string, readonly Shape[]>> = {
  heart: [
    { d: "M24 42C10 32 4 25 4 16.5C4 10 9 5 15 5C19 5 22 7 24 10C26 7 29 5 33 5C39 5 44 10 44 16.5C44 25 38 32 24 42Z", fill: "#ff4d6d", stroke: "#c9183d", width: 2 },
    { d: "M11 15.5C11 12 13 9.5 16 9.5", stroke: "#ffd0da", width: 3 },
  ],
  star: [
    { d: "M24 5L29 18.1L43 18.8L32.1 27.6L35.8 41.2L24 33.5L12.2 41.2L15.9 27.6L5 18.8L19 18.1Z", fill: GOLD, stroke: GOLD_EDGE, width: 2 },
  ],
  fire: [
    { d: "M24 4C27 12 36 16 36 28C36 37 30 44 24 44C18 44 12 37 12 28C12 21 16 17 18 12C19 17 21 19 23 20C22 14 22 9 24 4Z", fill: "#ff6a1a", stroke: "#d1440a", width: 2 },
    { d: "M24 22C26 27 30 29 30 34C30 39 27 42 24 42C21 42 18 39 18 35C18 31 21 30 22 26C23 28 24 28 24 22Z", fill: GOLD },
  ],
  laugh: [
    FACE,
    { d: "M13.5 19Q17 14.5 20.5 19", stroke: INK, width: 2.5 },
    { d: "M27.5 19Q31 14.5 34.5 19", stroke: INK, width: 2.5 },
    { d: "M13 25H35C35 32.5 30 37.5 24 37.5C18 37.5 13 32.5 13 25Z", fill: INK },
    { d: "M18.5 33.5C21 31 27 31 29.5 33.5C27.5 36.5 20.5 36.5 18.5 33.5Z", fill: "#ff6b6b" },
    { d: "M8 21C6 24.5 6 27 8 27C10 27 10 24.5 8 21Z", fill: "#6ec6ff" },
    { d: "M40 21C38 24.5 38 27 40 27C42 27 42 24.5 40 21Z", fill: "#6ec6ff" },
  ],
  clap: [
    { rect: [-6.5, -12, 13, 24, 6.5], fill: GOLD, stroke: GOLD_EDGE, width: 2, transform: "translate(19.5 28) rotate(-18)" },
    { rect: [-6.5, -12, 13, 24, 6.5], fill: GOLD, stroke: GOLD_EDGE, width: 2, transform: "translate(28.5 28) rotate(18)" },
    { d: "M8 8L11.5 13", stroke: "#ff6a1a", width: 2.5 },
    { d: "M24 3V9", stroke: "#ff6a1a", width: 2.5 },
    { d: "M40 8L36.5 13", stroke: "#ff6a1a", width: 2.5 },
  ],
  thumbs_up: [
    { rect: [6, 21, 10, 22, 2], fill: "#8fb3ff", stroke: "#4f78d6", width: 2 },
    { d: "M16 22H20L25.5 8.5C28.5 8 31 10.5 30 14L28 21H38.5C41.5 21 43.5 24 42.5 27L39.5 39C39 41 37.5 42 35.5 42H16Z", fill: GOLD, stroke: GOLD_EDGE, width: 2 },
    { d: "M30 28H40M30 34.5H38.5", stroke: GOLD_EDGE, width: 1.8 },
  ],
  basketball: [
    { circle: [24, 24, 20], fill: "#ff7a2e", stroke: "#2a1a10", width: 2 },
    { d: "M24 4V44M4 24H44M10 10C17 17 17 31 10 38M38 10C31 17 31 31 38 38", stroke: "#2a1a10", width: 2 },
  ],
  trophy: [
    { d: "M14.5 9H8V13C8 18 11 21 15 21M33.5 9H40V13C40 18 37 21 33 21", stroke: GOLD_EDGE, width: 3 },
    { d: "M14 6H34V16C34 23 30 28 24 28C18 28 14 23 14 16Z", fill: GOLD, stroke: GOLD_EDGE, width: 2 },
    { rect: [21, 27, 6, 8, 0], fill: GOLD_EDGE },
    { rect: [14, 35, 20, 7, 2], fill: "#8a5a2b" },
    { d: "M24 11L25.5 14.6L29 15L26.3 17.3L27.1 21L24 19L20.9 21L21.7 17.3L19 15L22.5 14.6Z", fill: "#fff3c4" },
  ],
  music: [
    { d: "M19 37V12L39 7.5V33", stroke: "#b48cff", width: 3.5 },
    { d: "M19 12L39 7.5V13L19 17.5Z", fill: "#b48cff" },
    { circle: [14.5, 37, 5.5], fill: "#b48cff" },
    { circle: [34.5, 33, 5.5], fill: "#b48cff" },
  ],
  wave: [
    {
      d: "M16 29V14A2 2 0 0 1 20 14V24V9A2 2 0 0 1 24 9V24V8A2 2 0 0 1 28 8V24V11A2 2 0 0 1 32 11V28C32 36 28 42 22 42C18 42 16 39.5 14.5 37L7.6 27.6A2.2 2.2 0 0 1 11 24.8L16 29Z",
      fill: GOLD,
      stroke: GOLD_EDGE,
      width: 2,
    },
    { d: "M36 10Q40 14 38 19M39.5 5.5Q45.5 12 42.5 20.5", stroke: "#8fb3ff", width: 2.5 },
  ],
  surprised: [
    FACE,
    { d: "M13 13.5Q17 10 21 13.5M27 13.5Q31 10 35 13.5", stroke: INK, width: 2.5 },
    { circle: [17, 20, 3], fill: INK },
    { circle: [31, 20, 3], fill: INK },
    { circle: [24, 32, 5], fill: INK },
  ],
  cool: [
    FACE,
    { d: "M8.5 17H39.5V20C39.5 25 36.5 27.5 32.5 27.5C29 27.5 26.5 25 26 21H22C21.5 25 19 27.5 15.5 27.5C11.5 27.5 8.5 25 8.5 20Z", fill: "#1c1c22" },
    { d: "M30 20.5H34", stroke: "#ffffff", width: 1.6 },
    { d: "M17 33.5Q25 38 32 31", stroke: INK, width: 2.5 },
  ],
};

@Component({
  selector: "vybe-sticker-icon",
  template: `
    <svg viewBox="0 0 48 48" [attr.width]="size()" [attr.height]="size()" aria-hidden="true" focusable="false">
      @for (shape of shapes(); track $index) {
        @if (shape.circle; as c) {
          <circle [attr.cx]="c[0]" [attr.cy]="c[1]" [attr.r]="c[2]" [attr.fill]="shape.fill ?? 'none'" [attr.stroke]="shape.stroke ?? null" [attr.stroke-width]="shape.width ?? null" />
        } @else if (shape.rect; as r) {
          <rect [attr.x]="r[0]" [attr.y]="r[1]" [attr.width]="r[2]" [attr.height]="r[3]" [attr.rx]="r[4]" [attr.fill]="shape.fill ?? 'none'" [attr.stroke]="shape.stroke ?? null" [attr.stroke-width]="shape.width ?? null" [attr.transform]="shape.transform ?? null" />
        } @else {
          <path [attr.d]="shape.d" [attr.fill]="shape.fill ?? 'none'" [attr.stroke]="shape.stroke ?? null" [attr.stroke-width]="shape.width ?? null" stroke-linecap="round" stroke-linejoin="round" />
        }
      }
    </svg>
  `,
  styles: `:host { display: inline-flex; } svg { display: block; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StickerIcon {
  readonly sticker = input.required<string>();
  readonly size = input(40);
  protected readonly shapes = computed(() => STICKER_ART[this.sticker()] ?? []);
}
