import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { EMPTY_OUTFIT, type Outfit, outfitFromEquipped } from "../avatar/avatar-look";
import { type AvatarPresets, DEFAULT_PRESETS, pickPresets } from "../avatar/presets";
import { AvatarViewport } from "../avatar-viewport/avatar-viewport";
import { GAMES } from "../games/games";
import { GuestExpiryNotice } from "../player/guest-expiry";
import { PlayerStore } from "../player/player-store";
import { QuestsCard } from "../quests/quests-card";
import { CoinChange } from "../ui/coin-change";
import { LEDGER_LABELS } from "../ui/labels";

type LedgerEntry = Schemas["LedgerEntry"];

/** Wallet changes in the hub wallet card. */
const RECENT_CHANGES = 3;

/** `/hub` (spec §3): avatar, level, wallet, quests and the way to play. A failed avatar or quest load is a partial load. */
@Component({
  selector: "vybe-hub",
  imports: [AvatarViewport, CoinChange, GuestExpiryNotice, QuestsCard, RouterLink],
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
          <div class="card wallet">
            <h2>Wallet</h2>
            <p class="balance"><span data-testid="wallet-balance">{{ me.walletBalance }}</span> V-Coins</p>
            <h3 class="recent-title">Recent changes</h3>
            @if (recent(); as recent) {
              @if (recent.length === 0) {
                <p class="muted small" data-testid="recent-changes">No V-Coin changes yet. Play a game to earn coins.</p>
              } @else {
                <ul class="recent" aria-label="Recent V-Coin changes" data-testid="recent-changes">
                  @for (entry of recent; track entry.id) {
                    <li><span>{{ labels[entry.reason] }}</span> <vybe-coin-change [delta]="entry.delta" /></li>
                  }
                </ul>
              }
            } @else if (recentError()) {
              <p class="muted small">Your recent changes could not load. The wallet history shows them.</p>
            } @else {
              <p class="muted small" role="status">Loading…</p>
            }
            <div class="wallet-links">
              <a class="chip" routerLink="/wallet">History</a>
              <a class="chip" routerLink="/shop">Shop</a>
              <a class="chip" routerLink="/wardrobe">Wardrobe</a>
              <a class="chip" routerLink="/room">My Room</a>
            </div>
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
    .grid { display: grid; gap: var(--space-4); }
    .expiry { display: block; margin-bottom: var(--space-4); }
    .expiry:empty { display: none; }
    .profile { display: flex; gap: var(--space-4); align-items: center; }
    .avatar { width: 150px; flex: none; }
    .handle { font-size: var(--text-xl); font-weight: 700; margin: 0; }
    h2 { margin: 0 0 var(--space-2); }
    .picker { list-style: none; margin: 0 0 12px; padding: 0; display: grid; gap: var(--space-2); }
    .game { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; border: 1px solid var(--color-border-strong); border-radius: var(--radius-md); text-decoration: none; color: inherit; }
    .game:hover, .game:focus-visible { border-color: var(--color-game); }
    .game-name { font-weight: 700; color: var(--color-game); }
    .small { font-size: var(--text-xs); }
    @media (min-width: 768px) { .picker { grid-template-columns: 1fr 1fr; } }
    .balance { font-size: var(--text-2xl); font-weight: 800; margin: 0; color: var(--color-accent); font-variant-numeric: tabular-nums; }
    .wallet { display: flex; flex-direction: column; gap: var(--space-3); }
    .wallet h2, .wallet p { margin: 0; }
    .recent-title { margin: var(--space-1) 0 0; font-size: var(--text-xs); font-weight: 700; color: var(--color-text-muted); }
    .recent { list-style: none; margin: 0; padding: 0; display: grid; }
    .recent li { display: flex; justify-content: space-between; gap: var(--space-3); padding: var(--space-2) 0; font-size: var(--text-sm); }
    .recent li + li { border-top: 1px solid var(--color-border); }
    .wallet-links { display: grid; grid-template-columns: 1fr 1fr; gap: var(--space-2); margin-top: auto; }
    .wallet-links .chip { justify-content: center; padding: var(--space-2); font-size: var(--text-sm); }
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
  protected readonly labels = LEDGER_LABELS;
  /** The last wallet changes; `null` while loading or after a failed load (`recentError`). */
  protected readonly recent = signal<LedgerEntry[] | null>(null);
  protected readonly recentError = signal(false);

  protected readonly outfit = signal<Outfit>(EMPTY_OUTFIT);

  constructor() {
    void this.player.refresh().catch(() => undefined);
    // A failed load is a partial load: the balance and the history link still show.
    this.api.listWalletTransactions({ limit: RECENT_CHANGES }).then(
      (page) => this.recent.set(page.items.slice(0, RECENT_CHANGES)),
      () => this.recentError.set(true),
    );
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
