import { ChangeDetectionStrategy, Component, computed, input } from "@angular/core";

export type CoinChangeKind = "earned" | "spent" | "none";

/** Earned (+), spent (−, U+2212) or no change. */
export function coinChangeKind(delta: number): CoinChangeKind {
  if (delta > 0) return "earned";
  if (delta < 0) return "spent";
  return "none";
}

/** The change with its sign: "+100", "−350", "0". The sign stays, so color is never the only signal. */
export function signedCoins(delta: number): string {
  const kind = coinChangeKind(delta);
  const amount = String(Math.abs(delta));
  if (kind === "earned") return `+${amount}`;
  if (kind === "spent") return `−${amount}`;
  return amount;
}

/** A V-Coin change in a list: green with a plus sign when earned, red with a minus sign when spent. */
@Component({
  selector: "vybe-coin-change",
  template: `{{ text() }}`,
  host: { class: "amount", "[class.earned]": "kind() === 'earned'", "[class.spent]": "kind() === 'spent'" },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CoinChange {
  readonly delta = input.required<number>();
  protected readonly kind = computed(() => coinChangeKind(this.delta()));
  protected readonly text = computed(() => signedCoins(this.delta()));
}
