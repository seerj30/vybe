import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { FRIENDS_API, type LeaderboardScope } from "../api/friends-api";
import { errorMessage } from "../api/messages";
import { Generations } from "../challenges/generations";
import { GAMES, gameInfo } from "../games/games";
import { previousWeek, weekRange } from "../friends/friend-view";
import { PlayerStore } from "../player/player-store";
import { playerName } from "../profiles/player-name";

type Load = { kind: "loading" } | { kind: "failed"; message: string } | { kind: "ready"; board: Schemas["WeeklyLeaderboard"] };

/**
 * `/leaderboard` (P1, docs/design/p1-friends-leaderboard.md): the weekly board of one game, global or friends,
 * this week or last week, with the caller's own rank. Query parameters `game`, `scope` and `week` keep the view in the URL.
 */
@Component({
  selector: "vybe-leaderboard",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Leaderboard</h1>
      <p class="muted">{{ activeGame().name }} · best verified score per player this week (Monday to Sunday, UTC). Full-length runs without a challenge count.</p>

      <nav class="tabs" aria-label="Game">
        @for (g of games; track g.id) {
          <a routerLink="/leaderboard" [queryParams]="{ game: g.id, scope: activeScope(), week: week() ?? null }" [class.active]="activeGame().id === g.id"
            [attr.aria-current]="activeGame().id === g.id ? 'page' : null">{{ g.name }}</a>
        }
      </nav>
      <nav class="tabs" aria-label="Leaderboard">
        <a routerLink="/leaderboard" [queryParams]="{ game: activeGame().id, scope: 'global', week: week() ?? null }" [class.active]="activeScope() === 'global'"
          [attr.aria-current]="activeScope() === 'global' ? 'page' : null">Global</a>
        <a routerLink="/leaderboard" [queryParams]="{ game: activeGame().id, scope: 'friends', week: week() ?? null }" [class.active]="activeScope() === 'friends'"
          [attr.aria-current]="activeScope() === 'friends' ? 'page' : null">Friends</a>
      </nav>

      @let l = state();
      @switch (l.kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading the leaderboard…</p>
        }
        @case ("failed") {
          <p class="error" role="alert">{{ l.message }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @case ("ready") {
          @let b = l.board;
          <div class="week">
            <h2>{{ week() ? "Week" : "This week" }}: {{ range(b) }}</h2>
            @if (week()) {
              <a routerLink="/leaderboard" [queryParams]="{ game: activeGame().id, scope: activeScope() }">This week</a>
            } @else if (b.weekStart > firstWeekStart) {
              <a routerLink="/leaderboard" [queryParams]="{ game: activeGame().id, scope: activeScope(), week: lastWeek(b) }">Last week</a>
            }
          </div>

          <div class="card me" data-testid="my-rank">
            @if (b.me; as me) {
              <p class="rank">Your rank: <strong>#{{ me.rank }}</strong> · {{ me.score }} points</p>
              @if (!me.listed) {
                <p class="muted small">
                  @if (b.scope === "global" && player.me()?.profileVisibility === "private") {
                    Your profile is private, so the global list does not show you.
                  } @else {
                    You are not in the top {{ b.entries.length }} yet.
                  }
                </p>
              }
            } @else {
              <p>You have no score this week. <a [routerLink]="activeGame().route">Play {{ activeGame().name }}</a></p>
            }
          </div>

          @if (b.entries.length === 0) {
            <p class="muted">
              @if (b.scope === "friends") {
                No scores from you or your friends this week. <a routerLink="/friends">Add friends</a>
              } @else {
                No scores this week yet.
              }
            </p>
          } @else {
            <table class="card board">
              <caption class="visually-hidden">{{ b.scope === "friends" ? "Friends" : "Global" }} leaderboard, {{ range(b) }}</caption>
              <thead>
                <tr><th scope="col">Rank</th><th scope="col">Player</th><th scope="col" class="num">Score</th></tr>
              </thead>
              <tbody>
                @for (e of b.entries; track e.handle) {
                  <tr [class.mine]="e.isMe" [attr.aria-current]="e.isMe ? 'true' : null">
                    <td>#{{ e.rank }}</td>
                    <td class="who">
                      @if (b.scope === "global" && !e.isMe) {
                        <a [routerLink]="['/profile', e.handle]">{{ name(e) }}</a>
                      } @else {
                        {{ name(e) }}
                      }
                      @if (e.isMe) { <span class="muted">(you)</span> }
                      <span class="muted small">Level {{ e.level }}</span>
                    </td>
                    <td class="num">{{ e.score }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        }
      }
    </section>
  `,
  styles: `
    .tabs { display: flex; gap: 8px; margin: 16px 0; }
    .tabs a { min-height: 44px; display: inline-flex; align-items: center; padding: 8px 16px; border-radius: 12px; background: #2b3460; color: #f4f6ff; text-decoration: none; font-weight: 700; }
    .tabs a.active { background: #ffc83d; color: #141826; }
    .week { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: baseline; gap: 8px; }
    h2 { margin: 0 0 8px; font-size: 18px; }
    .me { margin-bottom: 16px; }
    .me p { margin: 0; }
    .rank { font-size: 20px; }
    .small { font-size: 14px; }
    .board { width: 100%; border-collapse: collapse; padding: 0; }
    .board th, .board td { padding: 10px 12px; text-align: left; }
    .board tbody tr + tr td { border-top: 1px solid #2b3350; }
    .board .num { text-align: right; font-variant-numeric: tabular-nums; }
    .board tr.mine td { background: #23305a; }
    .who { overflow-wrap: anywhere; }
    .who .small { display: block; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Leaderboard {
  /** Query parameters (route input binding). */
  readonly game = input<string | undefined>();
  readonly scope = input<string | undefined>();
  readonly week = input<string | undefined>();

  private readonly friendsApi = inject(FRIENDS_API);
  private readonly loads = new Generations();
  protected readonly name = playerName;
  protected readonly player = inject(PlayerStore);
  /** The API serves weeks from 2026-W01 (Monday 2025-12-29). */
  protected readonly firstWeekStart = "2025-12-29";
  protected readonly state = signal<Load>({ kind: "loading" });
  protected readonly games = GAMES;
  /** An unknown or missing `game` shows the first game. */
  protected readonly activeGame = computed(() => gameInfo(this.game() ?? ""));
  protected readonly activeScope = computed<LeaderboardScope>(() => (this.scope() === "friends" ? "friends" : "global"));

  constructor() {
    effect(() => {
      this.activeGame();
      this.activeScope();
      this.week();
      untracked(() => void this.load());
    });
  }

  protected async load(): Promise<void> {
    const isCurrent = this.loads.next();
    this.state.set({ kind: "loading" });
    const week = this.week();
    let state: Load;
    try {
      const board = await this.friendsApi.weeklyLeaderboard(this.activeGame().id, { scope: this.activeScope(), ...(week ? { week } : {}) });
      state = { kind: "ready", board };
    } catch (error: unknown) {
      state = { kind: "failed", message: errorMessage(error) };
    }
    if (isCurrent()) this.state.set(state);
  }

  protected range(board: Schemas["WeeklyLeaderboard"]): string {
    return weekRange(board.weekStart, board.weekEnd);
  }

  protected lastWeek(board: Schemas["WeeklyLeaderboard"]): string {
    return previousWeek(board.weekStart);
  }
}
