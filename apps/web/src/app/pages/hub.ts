import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { AvatarPortrait } from "../avatar/avatar-portrait";
import { type AvatarPresets, DEFAULT_PRESETS, pickPresets } from "../avatar/presets";
import { GuestExpiryNotice } from "../player/guest-expiry";
import { PlayerStore } from "../player/player-store";
import { QuestsCard } from "../quests/quests-card";

/** `/hub` (spec §3): avatar, level, wallet, quests and the way to play. A failed avatar or quest load is a partial load. */
@Component({
  selector: "vybe-hub",
  imports: [AvatarPortrait, GuestExpiryNotice, QuestsCard, RouterLink],
  template: `
    <section class="page">
      <h1>Hub</h1>
      @if (player.me(); as me) {
        <vybe-guest-expiry class="expiry" [me]="me" />
        <div class="grid">
          <div class="card profile">
            <vybe-avatar-portrait [presets]="presets()" />
            <div>
              <p class="handle">{{ me.handle }}</p>
              <p class="muted">{{ me.kind === "guest" ? "Guest" : "Registered" }} · Level {{ me.level }} · {{ me.xpTotal }} XP</p>
              @if (avatarError(); as error) {
                <p class="notice" role="status">Your avatar could not load, so a default picture shows. {{ error }}</p>
              }
              <a routerLink="/avatar/create">Edit avatar</a>
            </div>
          </div>
          <div class="card">
            <h2>Wallet</h2>
            <p class="balance"><span data-testid="wallet-balance">{{ me.walletBalance }}</span> V-Coins</p>
            <div class="links"><a routerLink="/wallet">History</a> · <a routerLink="/shop">Shop</a> · <a routerLink="/wardrobe">Wardrobe</a></div>
          </div>
          <div class="card play">
            <h2>Street Hoops</h2>
            <p class="muted">Sixty seconds, one moving basket. Score points to earn V-Coins and XP.</p>
            <div class="actions">
              <a class="button" routerLink="/arcade/street-hoops">Play Street Hoops</a>
              <a class="button secondary" routerLink="/challenges">My challenges</a>
            </div>
          </div>
          <vybe-quests-card class="quests" />
        </div>
      }
    </section>
  `,
  styles: `
    .grid { display: grid; gap: 16px; }
    .expiry { display: block; margin-bottom: 16px; }
    .expiry:empty { display: none; }
    .profile { display: flex; gap: 16px; align-items: center; }
    .handle { font-size: 20px; font-weight: 700; margin: 0; }
    h2 { margin: 0 0 8px; font-size: 18px; }
    .balance { font-size: 28px; font-weight: 800; margin: 0; color: #ffc83d; font-variant-numeric: tabular-nums; }
    @media (min-width: 768px) { .grid { grid-template-columns: 2fr 1fr; } .play, .quests { grid-column: 1 / -1; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Hub {
  private readonly api = inject(API);
  protected readonly player = inject(PlayerStore);
  protected readonly presets = signal<AvatarPresets>(DEFAULT_PRESETS);
  protected readonly avatarError = signal<string | null>(null);

  constructor() {
    void this.player.refresh().catch(() => undefined);
    this.api.getAvatar().then(
      (avatar) => this.presets.set(pickPresets(avatar)),
      (error: unknown) => this.avatarError.set(errorMessage(error)),
    );
  }
}
