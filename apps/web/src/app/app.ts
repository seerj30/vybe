import { ChangeDetectionStrategy, Component, type ElementRef, inject, viewChild } from "@angular/core";
import { toSignal } from "@angular/core/rxjs-interop";
import { type ActivatedRouteSnapshot, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from "@angular/router";
import { filter, map } from "rxjs";
import type { Layout } from "./app.routes";
import { PlayerStore } from "./player/player-store";

interface NavItem {
  path: string;
  label: string;
}

const NAV: readonly NavItem[] = [
  { path: "/hub", label: "Hub" },
  { path: "/arcade", label: "Arcade" },
  { path: "/shop", label: "Shop" },
  { path: "/wardrobe", label: "Wardrobe" },
  { path: "/wallet", label: "Wallet" },
  { path: "/settings", label: "Settings" },
];

function layoutOf(snapshot: ActivatedRouteSnapshot): Layout {
  let route: ActivatedRouteSnapshot | null = snapshot;
  let layout: Layout = "bare";
  while (route !== null) {
    layout = (route.data["layout"] as Layout | undefined) ?? layout;
    route = route.firstChild;
  }
  return layout;
}

/** App shell (spec §3 global UI): sidebar on desktop, bottom nav on mobile, none on bare and game screens. */
@Component({
  selector: "vybe-root",
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="shell" [attr.data-layout]="layout()">
      @if (layout() === "app") {
        <nav class="nav" aria-label="Main">
          <a class="brand" routerLink="/hub">VYBE</a>
          <ul>
            @for (item of nav; track item.path) {
              <li>
                <a [routerLink]="item.path" routerLinkActive="active" ariaCurrentWhenActive="page">{{ item.label }}</a>
              </li>
            }
          </ul>
          @if (player.me(); as me) {
            <p class="coins" aria-label="Wallet balance">{{ me.walletBalance }} V-Coins</p>
          }
        </nav>
      }
      <main #main tabindex="-1">
        <router-outlet />
      </main>
    </div>
  `,
  styleUrl: "./app.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly nav = NAV;
  protected readonly player = inject(PlayerStore);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>("main");
  private readonly router = inject(Router);
  private navigations = 0;

  protected readonly layout = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => {
        // Screen readers and keyboard users start at the new screen, not at the old focus position.
        if (this.navigations++ > 0) this.main().nativeElement.focus({ preventScroll: true });
        return layoutOf(this.router.routerState.snapshot.root);
      }),
    ),
    { initialValue: "bare" as Layout },
  );
}
