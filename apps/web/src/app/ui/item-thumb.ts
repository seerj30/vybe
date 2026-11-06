import { ChangeDetectionStrategy, Component, computed, input, output, signal } from "@angular/core";

/**
 * Item picture with a safe fallback (spec §9): when the preview does not load, a neutral tile with
 * the item name shows instead, and `failed` tells the screen to show a non-blocking warning.
 */
@Component({
  selector: "vybe-item-thumb",
  template: `
    @if (broken()) {
      <div class="fallback" role="img" [attr.aria-label]="name() + ' (picture not available)'">
        <span aria-hidden="true">{{ initials() }}</span>
      </div>
    } @else {
      <img [src]="src()" [alt]="name()" loading="lazy" decoding="async" (error)="fail()" />
    }
  `,
  styles: `
    :host { display: block; aspect-ratio: 1; border-radius: 12px; overflow: hidden; background: #232a40; }
    img { display: block; width: 100%; height: 100%; object-fit: cover; }
    .fallback { display: grid; place-items: center; width: 100%; height: 100%; font-size: 28px; font-weight: 800; color: #8a90a6; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ItemThumb {
  readonly src = input.required<string>();
  readonly name = input.required<string>();
  readonly failed = output<void>();

  protected readonly broken = signal(false);
  protected readonly initials = computed(() =>
    this.name()
      .split(/\s+/)
      .map((word) => word.charAt(0))
      .join("")
      .slice(0, 2)
      .toUpperCase(),
  );

  protected fail(): void {
    if (this.broken()) return;
    this.broken.set(true);
    this.failed.emit();
  }
}
