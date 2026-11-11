import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { EMPTY_OUTFIT, type Outfit, outfitFromEquipped } from "../avatar/avatar-look";
import { type AvatarPresets, DEFAULT_PRESETS, pickPresets } from "../avatar/presets";
import { AvatarViewport } from "../avatar-viewport/avatar-viewport";
import { GAMES } from "../games/games";
import { GuestExpiryNotice } from "../player/guest-expiry";
import { PlayerStore } from "../player/player-store";
import { QuestsCard } from "../quests/quests-card";

/** `/hub` (spec §3): avatar, level, wallet, quests and the way to play. A failed avatar or quest load is a partial load. */
@Component({
  selector: "vybe-hub",
  imports: [AvatarViewport, GuestExpiryNotice, QuestsCard, RouterLink],
  template: `
    <section class="page">
      <h1>Hub</h1>
      @if (player.me(); as me) {
        <vybe-guest-expiry class="expiry" [me]="me" />
        <div class="grid">
          <div class="card profile">
            <vybe-avatar-viewport class="avatar" [presets]="presets()" [outfit]="outfit()" />
            <div>
              <p class="handle">{{ me.handle }}</p>
              <p class="muted">{{ me.kind === "guest" ? "Guest" : "Registered" }} · Level {{ me.level }} · {{ me.xpTotal }} XP</p>
              @if (avatarError(); as error) {
                <p class="notice" role="status">Your avatar could not load, so a default picture shows. {{ error }}</p>
              }
              <div class="links"><a routerLink="/avatar/create">Edit avatar</a> · <a routerLink="/achievements">Achievements</a></div>
            </div>
          </div>
          <div class="card">
            <h2>Wallet</h2>
            <p class="balance"><span data-testid="wallet-balance">{{ me.walletBalance }}</span> V-Coins</p>
            <div class="links"><a routerLink="/wallet">History</a> · <a routerLink="/shop">Shop</a> · <a routerLink="/wardrobe">Wardrobe</a> · <a routerLink="/room">My Room</a></div>
          </div>
          <div class="card play">
            <h2>Play</h2>
            <p class="muted">Sixty-second games. Score points to earn V-Coins and XP.</p>
            <ul class="picker" aria-label="Games">
              @for (game of games; track game.id) {
                <li>
                  <a class="game" [routerLink]="game.route">
                    <span class="game-name">Play {{ game.name }}</span>
                    <span class="muted small">{{ game.blurb }}</span>
                  </a>
                </li>
              }
            </ul>
            <div class="actions">
              <a class="button secondary" routerLink="/arcade">Arcade</a>
              <a class="button secondary" routerLink="/challenges">My challenges</a>
            </div>
          </div>
          <div class="card social">
            <h2>Friends and leaderboard</h2>
            <p class="muted">Add friends, challenge them, and compare your best score in each game this week.</p>
            <div class="actions">
              <a class="button secondary" routerLink="/friends">Friends</a>
              <a class="button secondary" routerLink="/leaderboard">Weekly leaderboard</a>
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
    .avatar { width: 150px; flex: none; }
    .handle { font-size: 20px; font-weight: 700; margin: 0; }
    h2 { margin: 0 0 8px; font-size: 18px; }
    .picker { list-style: none; margin: 0 0 12px; padding: 0; display: grid; gap: 8px; }
    .game { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; border: 1px solid #3a4466; border-radius: 10px; text-decoration: none; color: inherit; }
    .game:hover, .game:focus-visible { border-color: #ff6a1a; }
    .game-name { font-weight: 700; color: #ff6a1a; }
    .small { font-size: 13px; }
    @media (min-width: 768px) { .picker { grid-template-columns: 1fr 1fr; } }
    .balance { font-size: 28px; font-weight: 800; margin: 0; color: #ffc83d; font-variant-numeric: tabular-nums; }
    @media (min-width: 768px) { .grid { grid-template-columns: 2fr 1fr; } .play, .social, .quests { grid-column: 1 / -1; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Hub {
  private readonly api = inject(API);
  protected readonly player = inject(PlayerStore);
  protected readonly games = GAMES;
  protected readonly presets = signal<AvatarPresets>(DEFAULT_PRESETS);
  protected readonly avatarError = signal<string | null>(null);

  protected readonly outfit = signal<Outfit>(EMPTY_OUTFIT);

  constructor() {
    void this.player.refresh().catch(() => undefined);
    // The inventory names the equipped items; without it the avatar still shows (no items).
    Promise.all([this.api.getAvatar(), this.api.getInventory().catch(() => null)]).then(
      ([avatar, inventory]) => {
        this.presets.set(pickPresets(avatar));
        if (inventory !== null) this.outfit.set(outfitFromEquipped(avatar.equipped, inventory.items));
      },
      (error: unknown) => this.avatarError.set(errorMessage(error)),
    );
  }
}
