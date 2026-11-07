import { afterNextRender, ChangeDetectionStrategy, Component, effect, type ElementRef, inject, Injector, input, signal, untracked, viewChild } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError, newIdempotencyKey } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import { type Challenge, OUTCOME_LABELS, outcomeFor, shortTime, sideOf } from "../challenges/challenge-view";
import { AgeGate } from "../player/age-gate";
import { PlayerStore } from "../player/player-store";
import { playerName } from "../profiles/player-name";
import { Generations } from "../challenges/generations";
import { whenVisible } from "../challenges/when-visible";

type Preview = Schemas["ChallengePreview"];

type View =
  | { kind: "loading" }
  | { kind: "failed"; message: string }
  | { kind: "unavailable" }
  | { kind: "expired"; preview: Preview }
  | { kind: "redeemed"; preview: Preview }
  | { kind: "own"; preview: Preview }
  | { kind: "open"; preview: Preview; signedIn: boolean }
  | { kind: "participant"; preview: Preview; challenge: Challenge; recipient: boolean };

/**
 * `/challenge/:token` (spec §3, §2 J2): the invite preview works without a player. Accept binds the
 * caller as the recipient; the recipient then plays one run on the creator's court and sees the result.
 * A block, a wrong recipient and an unknown token all show the same "not available" state (spec §5).
 */
@Component({
  selector: "vybe-invite",
  imports: [RouterLink],
  template: `
    <section class="page invite">
      <h1>Street Hoops challenge</h1>
      @let v = view();
      <div class="state" #state tabindex="-1">
      @switch (v.kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading the challenge…</p>
        }
        @case ("failed") {
          <p class="error" role="alert">{{ v.message }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @case ("unavailable") {
          <p class="notice" role="status">This challenge is not available. The link is wrong, or you cannot take part in it.</p>
          <a class="button secondary" routerLink="/hub">Go to the hub</a>
        }
        @default {
          @let p = v.preview;
          <div class="card">
            <p class="lead">
              <strong>{{ name(p.creator) }}</strong> <span class="muted">(level {{ p.creator.level }})</span> scored
            </p>
            <p class="score" data-testid="creator-score">{{ p.creatorScore }}</p>
            <p class="muted">Play the same 60-second court. The higher verified score wins.</p>
          </div>

          <div class="detail">
          @switch (v.kind) {
            @case ("expired") {
              <p class="notice" role="status">This challenge has expired. Nobody can accept it any more.</p>
            }
            @case ("redeemed") {
              <p class="notice" role="status">Another player already accepted this challenge.</p>
            }
            @case ("own") {
              <p class="notice" role="status">This is your challenge. Send the link to a friend; you cannot accept it yourself.</p>
              <div class="actions">
                <button type="button" class="button secondary" (click)="refresh()">Refresh</button>
                <a class="button secondary" routerLink="/challenges">My challenges</a>
              </div>
            }
            @case ("open") {
              <p class="muted">Accept by {{ time(p.acceptDeadline) }}. You get one run for this challenge.</p>
              @if (actionError(); as error) {
                <p class="error" role="alert">{{ error }}</p>
              }
              <div class="actions">
                <button type="button" class="button" [disabled]="busy()" (click)="accept(v.signedIn)">
                  {{ busy() ? "Accepting…" : v.signedIn ? "Accept challenge" : "Play as a guest and accept" }}
                </button>
              </div>
              @if (!v.signedIn) {
                <p class="muted small">A guest account keeps your progress in this browser.</p>
              }
            }
            @case ("participant") {
              @let c = v.challenge;
              @if (c.status === "accepted" && v.recipient && c.recipientScore === null) {
                <p class="notice" role="status">You accepted. Play your run before {{ time(c.completeDeadline ?? p.acceptDeadline) }}.</p>
                <a class="button" routerLink="/arcade/street-hoops" [queryParams]="{ challenge: c.challengeId }">Play your challenge run</a>
              } @else if (c.status === "completed") {
                <h2>Result: {{ outcomeLabel(v.challenge) }}</h2>
                <dl class="scores">
                  <div><dt>{{ name(c.creator) }}</dt><dd>{{ c.creatorScore }}</dd></div>
                  <div><dt>{{ c.recipient ? name(c.recipient) : "" }}</dt><dd>{{ c.recipientScore ?? "—" }}</dd></div>
                </dl>
                <div class="actions">
                  <a class="button" routerLink="/arcade/street-hoops" [queryParams]="{ rematchOf: c.challengeId }">Rematch</a>
                  <a class="button secondary" routerLink="/challenges">My challenges</a>
                </div>
              } @else if (c.status === "expired") {
                <p class="notice" role="status">This challenge has expired without a result.</p>
              } @else {
                <p class="notice" role="status">Waiting for the run of {{ c.recipient ? name(c.recipient) : "the other player" }}.</p>
                <div class="actions">
                  <button type="button" class="button secondary" (click)="refresh()">Refresh</button>
                  <a class="button secondary" routerLink="/challenges">My challenges</a>
                </div>
              }
            }
          }
          </div>
        }
      }
      </div>
    </section>
  `,
  styles: `
    .invite { max-width: 560px; }
    .lead { margin: 0; }
    .score { margin: 4px 0; font-size: 44px; font-weight: 800; color: #ffc83d; font-variant-numeric: tabular-nums; }
    .small { font-size: 13px; }
    h2 { font-size: 20px; margin: 20px 0 8px; }
    .scores { display: flex; gap: 32px; margin: 0; }
    .scores div { display: grid; }
    .scores dt { color: #aab1c8; font-size: 13px; }
    .scores dd { margin: 0; font-size: 28px; font-weight: 800; font-variant-numeric: tabular-nums; }
    .detail { margin-top: 16px; }
    .state:focus { outline: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Invite {
  /** Route parameter. */
  readonly token = input.required<string>();

  private readonly api = inject(API);
  private readonly social = inject(SOCIAL_API);
  protected readonly name = playerName;
  private readonly player = inject(PlayerStore);
  private readonly ageGate = inject(AgeGate);
  protected readonly time = shortTime;

  protected readonly view = signal<View>({ kind: "loading" });
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  /** One key per accept, reused while the outcome is unknown (spec §2 J1). */
  private acceptKey: string | null = null;
  /** Only the newest load may set the view (the token can change while one runs). */
  private readonly loads = new Generations();
  private readonly injector = inject(Injector);
  private readonly stateRegion = viewChild<ElementRef<HTMLElement>>("state");

  constructor() {
    // The router keeps this component when only the token changes, so each new token loads again.
    effect(() => {
      this.token();
      untracked(() => {
        this.acceptKey = null;
        this.busy.set(false);
        this.actionError.set(null);
        void this.load();
      });
    });
    // The other player's accept or run changes this page: show it when the player comes back to the tab.
    whenVisible(() => this.refresh());
  }

  /** Reads the challenge again without the loading state; skipped during an accept. */
  protected refresh(): void {
    if (!this.busy() && this.view().kind !== "loading") void this.load(true);
  }

  /** `quiet` keeps the current view until the new one is known (after an accept), so focus has a place. */
  protected async load(quiet = false): Promise<void> {
    const isCurrent = this.loads.next();
    if (!quiet) this.view.set({ kind: "loading" });
    let view: View;
    try {
      const handle = (await this.player.ensure().catch(() => null))?.handle ?? null;
      view = await this.viewFor(await this.social.getInvite(this.token()), handle);
    } catch (error: unknown) {
      view = isApiError(error, "challenge_unavailable", "not_found") ? { kind: "unavailable" } : { kind: "failed", message: errorMessage(error) };
    }
    if (isCurrent()) this.view.set(view);
  }

  protected async accept(signedIn: boolean): Promise<void> {
    // The token can change while the accept runs: an old answer must not change the new page.
    const isCurrent = this.loads.snapshot();
    const token = this.token();
    this.busy.set(true);
    this.actionError.set(null);
    try {
      if (!signedIn && !(await this.startGuest())) {
        this.busy.set(false); // the age gate was cancelled: nothing changed
        return;
      }
      if (!signedIn) {
        const current = this.view();
        if (isCurrent() && current.kind === "open") this.view.set({ ...current, signedIn: true });
      }
      this.acceptKey ??= newIdempotencyKey();
      await this.social.acceptInvite(token, this.acceptKey);
      if (!isCurrent()) return;
      this.acceptKey = null;
      await this.load(true);
    } catch (error: unknown) {
      if (!isCurrent()) return;
      if (!(isApiError(error) && error.retryable)) this.acceptKey = null;
      this.acceptFailed(error);
    }
    this.busy.set(false);
    // The accept button is gone: keyboard and screen-reader users continue at the new state.
    if (this.view().kind !== "open") afterNextRender(() => this.stateRegion()?.nativeElement.focus(), { injector: this.injector });
  }

  protected outcomeLabel(challenge: Challenge): string {
    const handle = this.player.me()?.handle ?? "";
    const outcome = outcomeFor(challenge, sideOf(challenge, handle));
    return outcome === null ? "" : OUTCOME_LABELS[outcome];
  }

  private async viewFor(preview: Preview, handle: string | null): Promise<View> {
    if (handle === preview.creator.handle && preview.status === "open") return { kind: "own", preview };
    if (preview.status === "open") return { kind: "open", preview, signedIn: handle !== null };
    if (preview.status === "expired") return { kind: "expired", preview };
    if (handle === null) return { kind: "redeemed", preview };
    try {
      const challenge = await this.social.getChallenge(preview.challengeId);
      return { kind: "participant", preview, challenge, recipient: sideOf(challenge, handle) === "recipient" };
    } catch (error: unknown) {
      // Not a participant: somebody else redeemed the link.
      if (isApiError(error, "not_found")) return { kind: "redeemed", preview };
      return { kind: "failed", message: errorMessage(error) };
    }
  }

  /** The age gate first (spec §17 decision 1); false when the user cancelled it. */
  private async startGuest(): Promise<boolean> {
    const birthYear = await this.ageGate.ask();
    if (birthYear === null) return false;
    try {
      await this.api.createGuest(birthYear);
    } catch (error: unknown) {
      if (!isApiError(error, "already_authenticated")) throw error;
    }
    await this.player.startedGuest();
    return true;
  }

  private acceptFailed(error: unknown): void {
    const current = this.view();
    const preview = "preview" in current ? current.preview : null;
    if (isApiError(error, "challenge_unavailable")) this.view.set({ kind: "unavailable" });
    else if (preview !== null && isApiError(error, "challenge_already_accepted")) this.view.set({ kind: "redeemed", preview });
    else if (preview !== null && isApiError(error, "challenge_expired")) this.view.set({ kind: "expired", preview });
    else this.actionError.set(errorMessage(error));
  }
}
