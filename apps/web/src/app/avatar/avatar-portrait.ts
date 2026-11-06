import { ChangeDetectionStrategy, Component, computed, input } from "@angular/core";
import { type AvatarPresets, describeAvatar, hairColor, skinColor } from "./presets";

/**
 * 2D fallback portrait (spec §3, §9): used until the 3D avatar art exists, and later when WebGL or an
 * asset is not available. It reads only presets, so it never fails to render.
 */
@Component({
  selector: "vybe-avatar-portrait",
  template: `
    <svg viewBox="0 0 120 120" role="img" [attr.aria-label]="'Avatar: ' + description()">
      <circle cx="60" cy="60" r="58" class="backdrop" />
      <path [attr.d]="shoulders()" class="shirt" />
      <rect x="52" y="66" width="16" height="14" [attr.fill]="skin()" />
      @if (p().hairPresetId === "hair_long") {
        <path d="M34 50 Q34 22 60 22 Q86 22 86 50 L88 84 L32 84 Z" [attr.fill]="hair()" />
      }
      <ellipse cx="60" cy="50" rx="22" ry="25" [attr.fill]="skin()" />
      @switch (p().hairPresetId) {
        @case ("hair_short") {
          <path d="M38 46 Q38 24 60 24 Q82 24 82 46 Q74 34 60 34 Q46 34 38 46 Z" [attr.fill]="hair()" />
        }
        @case ("hair_long") {
          <path d="M37 50 Q37 24 60 24 Q83 24 83 50 Q76 32 60 32 Q44 32 37 50 Z" [attr.fill]="hair()" />
        }
        @case ("hair_curly") {
          <g [attr.fill]="hair()">
            <circle cx="42" cy="36" r="9" /><circle cx="52" cy="28" r="9" /><circle cx="64" cy="27" r="9" />
            <circle cx="75" cy="32" r="9" /><circle cx="81" cy="42" r="7" /><circle cx="38" cy="45" r="6" />
          </g>
        }
        @default {
          <path d="M39 42 Q40 26 60 26 Q80 26 81 42 Q70 33 60 33 Q50 33 39 42 Z" [attr.fill]="hair()" opacity="0.8" />
        }
      }
      <g class="face">
        @switch (p().facePresetId) {
          @case ("face_02") {
            <path d="M48 50 q4 -4 8 0 M64 50 q4 -4 8 0" class="line" />
            <path d="M52 62 q8 7 16 0" class="line" />
          }
          @case ("face_03") {
            <circle cx="52" cy="50" r="2.5" /><circle cx="68" cy="50" r="2.5" />
            <path d="M54 63 h12" class="line" />
          }
          @case ("face_04") {
            <ellipse cx="52" cy="50" rx="3" ry="4" /><ellipse cx="68" cy="50" rx="3" ry="4" />
            <circle cx="60" cy="63" r="3.5" />
          }
          @default {
            <circle cx="52" cy="50" r="3" /><circle cx="68" cy="50" r="3" />
            <path d="M52 61 q8 6 16 0" class="line" />
          }
        }
      </g>
    </svg>
  `,
  styles: `
    :host { display: inline-block; width: 120px; aspect-ratio: 1; }
    svg { display: block; width: 100%; height: 100%; }
    .backdrop { fill: #232a40; }
    .shirt { fill: #4f5b82; }
    .face { fill: #1c1c22; }
    .line { fill: none; stroke: #1c1c22; stroke-width: 2.5; stroke-linecap: round; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AvatarPortrait {
  readonly presets = input.required<AvatarPresets>();

  protected readonly p = this.presets;
  protected readonly skin = computed(() => skinColor(this.p().skinToneId));
  protected readonly hair = computed(() => hairColor(this.p().hairColorId));
  protected readonly description = computed(() => describeAvatar(this.p()));
  // Body B has broader shoulders.
  protected readonly shoulders = computed(() =>
    this.p().bodyPresetId === "body_b" ? "M18 118 Q20 80 60 78 Q100 80 102 118 Z" : "M28 118 Q30 82 60 80 Q90 82 92 118 Z",
  );
}
