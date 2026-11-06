import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import { challengeKey, forgetChallengeKey } from "../challenges/challenge-keys";
import {
  type Challenge,
  inviteToken,
  opponentOf,
  OUTCOME_LABELS,
  outcomeFor,
  shortTime,
  type Side,
  sideOf,
  STATUS_LABELS,
} from "../challenges/challenge-view";
import { PlayerStore } from "../player/player-store";
import { playerName } from "../profiles/player-name";

interface Row {
  challenge: Challenge;
  side: Side;
  opponent: Schemas["PlayerSummary"] | null;
  myScore: number | null;
  theirScore: number | null;
  outcome: string | null;
  /** In-app link to the invite, for the designated recipient of an open challenge. */
  token: string | null;
  /** One rematch per participant (spec §5): false once the caller sent it. */
  canRematch: boolean;
}

type CreateState =
  | { kind: "creating" }
  | { kind: "created"; created: Schemas["ChallengeCreated"] }
  | { kind: "failed"; message: string; retry: boolean; asNewChallenge: string | null };

const PAGE_SIZE = 20;

/**
 * `/challenges` (spec §3, §2 J2): my challenges, newest first. With `?session=` (a verified run from the
 * gameplay screen) it creates the challenge, or the rematch with `rematchOf`, and shows the share link.
 */
@Component({
  selector: "vybe-challenges",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Challenges</h1>

      @if (createState(); as state) {
        <div class="card create" aria-live="polite">
          @switch (state.kind) {
            @case ("creating") {
              <p role="status">Creating your challenge…</p>
            }
            @case ("created") {
              <h2>Your challenge is ready</h2>
              <p class="muted">
                @if (to(); as handle) {
                  Only {{ handle }} can accept it. They also find it in their challenges.
                } @else if (rematchOf()) {
                  Only your opponent can accept the rematch. They also find it in their challenges.
                } @else {
                  Send the link to one friend. The first player who accepts it plays your run.
                }
                Accept by {{ time(state.created.acceptDeadline) }}.
              </p>
              <label class="link">
                <span>Invite link</span>
                <input type="text" readonly [value]="state.created.shareUrl" (focus)="selectAll($event)" />
              </label>
              <div class="actions">
                <button type="button" class="button" (click)="copy(state.created.shareUrl)">Copy link</button>
              </div>
              <p role="status" class="muted small">{{ copyStatus() }}</p>
            }
            @case ("failed") {
              <p class="error" role="alert">{{ state.message }}</p>
              @if (state.retry) {
                <button type="button" class="button" (click)="create()">Try again</button>
              }
              @if (state.asNewChallenge; as sessionId) {
                <a class="button secondary" routerLink="/challenges" [queryParams]="{ session: sessionId }">Send this run as a new challenge</a>
              }
            }
          }
        </div>
      }

      <p class="muted">
        Finish a verified run, then challenge a friend with it. The friend plays the same court and the higher score wins.
      </p>
      <div class="actions top">
        <a class="button" routerLink="/arcade/street-hoops">Play a run to challenge</a>
      </div>

      @if (listError(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button secondary" (click)="load()">Try again</button>
      } @else if (rows() === null) {
        <p class="muted" role="status">Loading your challenges…</p>
      } @else if (rows()!.length === 0) {
        <p class="notice">You have no challenges yet. Play a run, then send it to a friend.</p>
      } @else {
        <ul class="list" aria-label="My challenges">
          @for (row of rows()!; track row.challenge.challengeId) {
            <li class="card item" [attr.data-status]="row.challenge.status">
              <h2>
                @if (row.opponent; as opponent) {
                  {{ row.side === "creator" ? "You challenged" : "Challenge from" }}
                  <a [routerLink]="['/profile', opponent.handle]">{{ name(opponent) }}</a>
                } @else {
                  Open invite
                }
              </h2>
              <p class="status">
                <span class="badge">{{ statusLabels[row.challenge.status] }}</span>
                @if (row.outcome; as outcome) {
                  <strong class="outcome">{{ outcome }}</strong>
                }
              </p>
              <dl class="scores">
                <div><dt>You</dt><dd>{{ row.myScore ?? "—" }}</dd></div>
                <div><dt>{{ row.opponent ? name(row.opponent) : "Opponent" }}</dt><dd>{{ row.theirScore ?? "—" }}</dd></div>
              </dl>
              @switch (row.challenge.status) {
                @case ("open") {
                  <p class="muted small">Accept by {{ time(row.challenge.acceptDeadline) }}</p>
                }
                @case ("accepted") {
                  @if (row.challenge.completeDeadline; as deadline) {
                    <p class="muted small">Play by {{ time(deadline) }}</p>
                  }
                }
              }
              <div class="row">
                @if (row.token; as token) {
                  <a class="button" [routerLink]="['/challenge', token]">Open invite</a>
                }
                @if (row.challenge.status === "accepted" && row.side === "recipient" && row.myScore === null) {
                  <a class="button" routerLink="/arcade/street-hoops" [queryParams]="{ challenge: row.challenge.challengeId }">Play your run</a>
                }
                @if (row.challenge.status === "completed" && row.canRematch) {
                  <a class="button secondary" routerLink="/arcade/street-hoops" [queryParams]="{ rematchOf: row.challenge.challengeId }">
                    Rematch<span class="visually-hidden"> {{ row.opponent ? name(row.opponent) : "" }}</span>
                  </a>
                }
              </div>
            </li>
          }
        </ul>
        @if (nextCursor()) {
          <button type="button" class="button secondary" [disabled]="loadingMore()" (click)="loadMore()">Show more</button>
        }
      }
    </section>
  `,
  styles: `
    .create { margin-bottom: 16px; }
    .create h2 { margin: 0 0 8px; font-size: 20px; }
    .link { display: grid; gap: 4px; font-weight: 600; }
    .link input { min-height: 44px; padding: 8px 12px; border-radius: 10px; border: 1px solid #2b3350; background: #141826; color: inherit; font: inherit; }
    .small { font-size: 13px; }
    .top { margin: 0 0 16px; }
    .list { display: grid; gap: 12px; padding: 0; margin: 0 0 16px; list-style: none; }
    .item h2 { margin: 0 0 6px; font-size: 17px; }
    .status { display: flex; gap: 10px; align-items: center; margin: 0 0 8px; }
    .badge { padding: 2px 10px; border-radius: 999px; background: #2b3460; font-size: 13px; font-weight: 600; }
    [data-status="expired"] .badge { background: #3a3f52; }
    .outcome { color: #ffc83d; }
    .scores { display: flex; gap: 24px; margin: 0 0 8px; }
    .scores div { display: grid; }
    .scores dt { color: #aab1c8; font-size: 13px; }
    .scores dd { margin: 0; font-size: 22px; font-weight: 800; font-variant-numeric: tabular-nums; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Challenges {
  /** Query parameters from the gameplay screen: the verified creator run, and an optional rematch or recipient. */
  readonly session = input<string | undefined>();
  readonly rematchOf = input<string | undefined>();
  readonly to = input<string | undefined>();

  private readonly social = inject(SOCIAL_API);
  protected readonly name = playerName;
  private readonly player = inject(PlayerStore);
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly time = shortTime;

  protected readonly createState = signal<CreateState | null>(null);
  protected readonly copyStatus = signal("");
  private readonly challenges = signal<Challenge[] | null>(null);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listError = signal<string | null>(null);
  protected readonly loadingMore = signal(false);
  private createKey: string | null = null;
  /** Only the newest request may change the page (the query can change while one runs). */
  private generation = 0;

  protected readonly rows = computed((): Row[] | null => {
    const list = this.challenges();
    const handle = this.player.me()?.handle;
    if (list === null || handle === undefined) return null;
    const rematched = new Set(
      list.filter((c) => c.rematchOfChallengeId !== null && c.creator.handle === handle).map((c) => c.rematchOfChallengeId),
    );
    return list.map((challenge) => {
      const side = sideOf(challenge, handle);
      const outcome = outcomeFor(challenge, side);
      const token =
        side === "recipient" && challenge.status === "open" && challenge.shareUrl !== null ? inviteToken(challenge.shareUrl) : null;
      return {
        challenge,
        side,
        opponent: opponentOf(challenge, side),
        myScore: side === "creator" ? challenge.creatorScore : challenge.recipientScore,
        theirScore: side === "creator" ? challenge.recipientScore : challenge.creatorScore,
        outcome: outcome === null ? null : OUTCOME_LABELS[outcome],
        token,
        canRematch: !rematched.has(challenge.challengeId),
      };
    });
  });

  constructor() {
    // The router keeps this component when only the query changes (for example the nav link after a create).
    effect(() => {
      const sessionId = this.session();
      this.rematchOf();
      this.to();
      untracked(() => {
        this.generation++;
        this.createState.set(null);
        this.copyStatus.set("");
        this.createKey = null;
        void (sessionId === undefined ? this.load() : this.create());
      });
    });
  }

  protected async create(): Promise<void> {
    const sessionId = this.session();
    if (sessionId === undefined) return;
    const generation = this.generation;
    const key = (this.createKey ??= challengeKey(sessionId));
    this.createState.set({ kind: "creating" });
    const rematchOf = this.rematchOf();
    const to = this.to();
    try {
      const created =
        rematchOf === undefined
          ? await this.social.createChallenge({ creatorSessionId: sessionId, ...(to === undefined ? {} : { recipientHandle: to }) }, key)
          : await this.social.createRematch(rematchOf, { creatorSessionId: sessionId }, key);
      if (generation !== this.generation) return;
      this.createState.set({ kind: "created", created });
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      const retry = isApiError(error) && error.retryable;
      if (!retry) {
        forgetChallengeKey(sessionId);
        this.createKey = null;
      }
      // The rematch is not possible, but the verified run can still start a normal challenge.
      const asNewChallenge = rematchOf !== undefined && isApiError(error, "rematch_already_created", "challenge_not_completed", "not_found") ? sessionId : null;
      this.createState.set({ kind: "failed", message: createFailure(error), retry, asNewChallenge });
    }
    await this.load();
  }

  protected async load(): Promise<void> {
    const generation = this.generation;
    this.listError.set(null);
    try {
      const page = await this.social.listMyChallenges({ limit: PAGE_SIZE });
      if (generation !== this.generation) return;
      this.challenges.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (error: unknown) {
      if (generation === this.generation) this.listError.set(errorMessage(error));
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (cursor === null) return;
    this.loadingMore.set(true);
    try {
      const page = await this.social.listMyChallenges({ cursor, limit: PAGE_SIZE });
      this.challenges.update((list) => [...(list ?? []), ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (error: unknown) {
      this.listError.set(errorMessage(error));
    }
    this.loadingMore.set(false);
  }

  protected async copy(url: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(url);
      this.copyStatus.set("Link copied.");
    } catch {
      this.copyStatus.set("Copying is not possible here. Select the link and copy it.");
    }
  }

  protected selectAll(event: FocusEvent): void {
    (event.target as HTMLInputElement).select();
  }
}

/** A recipient that is not known is a wrong handle, not a missing run. */
function createFailure(error: unknown): string {
  if (isApiError(error, "not_found")) return "This player or challenge does not exist.";
  return errorMessage(error);
}
