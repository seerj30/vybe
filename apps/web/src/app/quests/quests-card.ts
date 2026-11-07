import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { ActionKeys, isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import { PlayerStore } from "../player/player-store";

type PlayerQuest = Schemas["PlayerQuest"];

/** Spec §8 daily quests; an unknown code (new content) shows its code. */
const QUEST_LABELS: Record<string, string> = {
  play_3: "Finish 3 runs",
  score_1500: "Score 1,500 points in one run",
  challenge_or_streak: "Finish a challenge, or hit a streak of 5 in one run",
};

export function questLabel(code: string): string {
  return QUEST_LABELS[code] ?? code;
}

/** Claim before this time; quests of earlier days stay claimable for a while (spec §6 Quest). */
function claimBy(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/**
 * Daily quests on `/hub` (spec §3, §8): progress and claim. Rewards and the new balance come from the
 * server; the player store refreshes after a claim (spec §10).
 */
@Component({
  selector: "vybe-quests-card",
  template: `
    <section class="card" aria-labelledby="quests-title">
      <h2 id="quests-title">Daily quests</h2>
      <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>
      @if (loadError(); as error) {
        <p class="error" role="alert">Quests could not load. {{ error }}</p>
        <button type="button" class="button secondary" (click)="load()">Try again</button>
      } @else if (quests() === null) {
        <p class="muted" role="status">Loading quests…</p>
      } @else if (quests()!.length === 0) {
        <p class="muted">No quests today. Come back tomorrow.</p>
      } @else {
        <ul>
          @for (quest of quests()!; track quest.playerQuestId) {
            <li [attr.data-quest]="quest.code">
              <div class="top">
                <h3>{{ label(quest.code) }}</h3>
                <span class="reward">{{ quest.rewardCoins }} V-Coins · {{ quest.rewardXp }} XP</span>
              </div>
              <progress [max]="quest.target" [value]="quest.progress" [attr.aria-label]="label(quest.code) + ' progress'"></progress>
              <div class="bottom">
                <span class="muted">{{ quest.progress }} / {{ quest.target }}</span>
                @switch (quest.status) {
                  @case ("completed") {
                    <button type="button" class="button" [disabled]="busy() !== null" (click)="claim(quest)">
                      {{ busy() === quest.playerQuestId ? "Claiming…" : "Claim" }}
                      <span class="visually-hidden">{{ label(quest.code) }}</span>
                    </button>
                  }
                  @case ("claimed") {
                    <span class="done">Claimed</span>
                  }
                }
              </div>
              @if (quest.status === "completed") {
                <p class="muted small">Claim by {{ claimBy(quest.claimableUntil) }}</p>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: `
    h2 { margin: 0 0 8px; font-size: 18px; }
    ul { display: grid; gap: 14px; padding: 0; margin: 0; list-style: none; }
    h3 { margin: 0; font-size: 15px; }
    .top, .bottom { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; }
    .reward { color: #ffc83d; font-size: 13px; font-weight: 600; }
    progress { width: 100%; height: 10px; margin: 6px 0; accent-color: #ffc83d; }
    .done { color: #7be3a1; font-weight: 700; }
    .small { font-size: 13px; margin: 4px 0 0; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuestsCard {
  private readonly social = inject(SOCIAL_API);
  private readonly player = inject(PlayerStore);
  private readonly keys = new ActionKeys<string>();
  protected readonly label = questLabel;
  protected readonly claimBy = claimBy;

  protected readonly quests = signal<PlayerQuest[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  /** The quest being claimed. */
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.quests.set((await this.social.listQuests()).quests);
    } catch (error: unknown) {
      this.loadError.set(errorMessage(error));
    }
  }

  protected async claim(quest: PlayerQuest): Promise<void> {
    this.busy.set(quest.playerQuestId);
    this.message.set(null);
    try {
      const claimed = await this.social.claimQuest(quest.playerQuestId, this.keys.keyFor(quest.playerQuestId));
      this.keys.settle(quest.playerQuestId);
      this.message.set({ text: `Quest claimed: +${claimed.coinsGranted} V-Coins, +${claimed.xpGranted} XP.`, error: false });
      void this.player.refresh().catch(() => undefined);
    } catch (error: unknown) {
      this.keys.settle(quest.playerQuestId, error);
      this.message.set({ text: errorMessage(error), error: true });
      if (isApiError(error) && error.retryable) {
        this.busy.set(null);
        return;
      }
    }
    // The server state decides what the list shows (claimed, expired or still active).
    await this.load();
    this.busy.set(null);
  }
}
