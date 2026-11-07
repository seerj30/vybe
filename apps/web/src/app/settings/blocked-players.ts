import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { errorMessage } from "../api/messages";

type BlockedPlayer = Schemas["BlockedPlayer"];

/** `GET /me/blocks` and unblock (spec §5 Safety). Never shows who blocked the caller. */
@Component({
  selector: "vybe-blocked-players",
  imports: [DatePipe],
  template: `
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
      <button type="button" class="button secondary" (click)="reload()">Try again</button>
    }
    @if (notice(); as notice) {
      <p class="notice" role="status">{{ notice }}</p>
    }
    @if (loaded() && players().length === 0) {
      <p class="muted">You have not blocked anyone.</p>
    }
    @if (players().length > 0) {
      <ul class="blocks" aria-label="Blocked players">
        @for (player of players(); track player.handle) {
          <li [attr.aria-label]="player.handle">
            <span>
              <strong>{{ player.displayName ?? player.handle }}</strong>
              <span class="muted">{{ player.handle }} · blocked {{ player.blockedAt | date: "mediumDate" }}</span>
            </span>
            <button type="button" class="button secondary" [disabled]="busy() === player.handle" (click)="unblock(player)">
              Unblock {{ player.handle }}
            </button>
          </li>
        }
      </ul>
    }
    @if (cursor()) {
      <button type="button" class="button secondary" (click)="load(cursor())">Show more</button>
    }
  `,
  styles: `
    .blocks { list-style: none; padding: 0; margin: 0; display: grid; gap: 8px; }
    .blocks li { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; }
    .blocks span { display: grid; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BlockedPlayers {
  private readonly api = inject(AccountApi);
  protected readonly players = signal<BlockedPlayer[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  constructor() {
    void this.load(null);
  }

  protected reload(): void {
    this.players.set([]);
    void this.load(null);
  }

  protected async load(cursor: string | null): Promise<void> {
    this.error.set(null);
    try {
      const page = await this.api.listBlocks(cursor ?? undefined);
      this.players.update((players) => [...players, ...page.items]);
      this.cursor.set(page.nextCursor);
      this.loaded.set(true);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    }
  }

  protected async unblock(player: BlockedPlayer): Promise<void> {
    this.busy.set(player.handle);
    this.error.set(null);
    this.notice.set(null);
    try {
      await this.api.unblock(player.handle);
      this.players.update((players) => players.filter((p) => p.handle !== player.handle));
      this.notice.set(`You unblocked ${player.handle}. Cancelled challenges do not come back.`);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(null);
    }
  }
}
