import { afterNextRender, ChangeDetectionStrategy, Component, computed, effect, type ElementRef, inject, Injector, input, signal, untracked, viewChild } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import { AvatarPortrait } from "../avatar/avatar-portrait";
import { pickPresets } from "../avatar/presets";
import { PlayerStore } from "../player/player-store";
import { playerName } from "../profiles/player-name";
import { isBlocked, REPORT_REASONS } from "../profiles/safety";

type PublicProfile = Schemas["PublicProfile"];
type ReportReason = Schemas["ReportReason"];

type Load = { kind: "loading" } | { kind: "unavailable" } | { kind: "failed"; message: string } | { kind: "ready"; profile: PublicProfile };

interface Message {
  text: string;
  error: boolean;
}

/**
 * `/profile/:handle` (spec §3): public profile with challenge, report and block. The API answers 404 for a
 * missing, deleted or not visible profile, so "private" and "unavailable" are one state (ADR 0005 #3).
 */
@Component({
  selector: "vybe-profile",
  imports: [AvatarPortrait, RouterLink],
  template: `
    <section class="page">
      @let l = state();
      @switch (l.kind) {
        @case ("loading") {
          <h1>Profile</h1>
          <p class="muted" role="status">Loading the profile…</p>
        }
        @case ("unavailable") {
          <h1>Profile</h1>
          <p class="notice" role="status">This profile is not available. The player does not exist, or the profile is private.</p>
          <a class="button secondary" routerLink="/hub">Go to the hub</a>
        }
        @case ("failed") {
          <h1>Profile</h1>
          <p class="error" role="alert">{{ l.message }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @case ("ready") {
          @let p = l.profile;
          <div class="card head">
            <vybe-avatar-portrait [presets]="presets()!" />
            <div>
              <h1>{{ name(p) }}</h1>
              <p class="muted">{{ p.handle }} · Level {{ p.level }}</p>
            </div>
          </div>

          @if (isMe()) {
            <p class="notice">This is your public profile. Other players see this name, level and avatar.</p>
          } @else {
            @if (blockCheckFailed()) {
              <p class="notice" role="status">VYBE could not check your block list. If you blocked this player, a challenge to them fails.</p>
            }
            <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>

            @if (blocked()) {
              <div class="card">
                <h2>Blocked</h2>
                <p class="muted">You blocked {{ name(p) }}. You cannot challenge each other.</p>
                <button type="button" class="button secondary" [disabled]="busy()" (click)="unblock(p)">Unblock {{ name(p) }}</button>
              </div>
            } @else {
              <div class="card">
                <h2>Challenge</h2>
                <p class="muted">Play a run, then send it to {{ name(p) }}. Only they can accept it.</p>
                <a class="button" routerLink="/arcade/street-hoops" [queryParams]="{ to: p.handle }">Challenge {{ name(p) }}</a>
              </div>
            }

            <div class="card">
              <h2 #safety tabindex="-1">Safety</h2>
              @if (reportOpen()) {
                <form (submit)="report($event, p)">
                  <fieldset>
                    <legend>Why do you report {{ name(p) }}?</legend>
                    @for (option of reasons; track option.value) {
                      <label class="choice">
                        <input type="radio" name="reason" [value]="option.value" [checked]="reason() === option.value" (change)="reason.set(option.value)" />
                        {{ option.label }}
                      </label>
                    }
                  </fieldset>
                  <label class="details">
                    <span>Details (optional)</span>
                    <textarea maxlength="500" rows="3" [value]="details()" (input)="details.set(textValue($event))"></textarea>
                  </label>
                  <div class="row">
                    <button type="submit" class="button" [disabled]="busy() || reason() === null">Send report</button>
                    <button type="button" class="button secondary" (click)="swap(reportOpen, false)">Cancel</button>
                  </div>
                </form>
              } @else if (confirmBlock()) {
                <p>
                  Block {{ name(p) }}? You cannot challenge each other, and open challenges between you end.
                  They are not told.
                </p>
                <div class="row">
                  <button type="button" class="button" [disabled]="busy()" (click)="block(p)">Yes, block</button>
                  <button type="button" class="button secondary" (click)="swap(confirmBlock, false)">Cancel</button>
                </div>
              } @else {
                <div class="row">
                  <button type="button" class="button secondary" (click)="openReport()">Report {{ name(p) }}</button>
                  @if (!blocked()) {
                    <button type="button" class="button secondary" (click)="swap(confirmBlock, true)">Block {{ name(p) }}</button>
                  }
                </div>
              }
            </div>
          }
        }
      }
    </section>
  `,
  styles: `
    .head { display: flex; gap: 16px; align-items: center; margin-bottom: 16px; }
    .head vybe-avatar-portrait { width: 96px; flex: none; }
    .head h1 { margin: 0; }
    .head p { margin: 0; }
    .card + .card { margin-top: 16px; }
    h2:focus { outline: none; }
    h2 { margin: 0 0 8px; font-size: 18px; }
    fieldset { border: 0; padding: 0; margin: 0 0 12px; }
    legend { font-weight: 600; margin-bottom: 8px; }
    .choice { display: flex; gap: 8px; align-items: center; min-height: 36px; }
    .details { display: grid; gap: 4px; margin-bottom: 12px; }
    textarea { padding: 8px 12px; border-radius: 10px; border: 1px solid #2b3350; background: #141826; color: inherit; font: inherit; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Profile {
  /** Route parameter. */
  readonly handle = input.required<string>();

  private readonly social = inject(SOCIAL_API);
  protected readonly name = playerName;
  private readonly player = inject(PlayerStore);
  protected readonly reasons = REPORT_REASONS;

  protected readonly state = signal<Load>({ kind: "loading" });
  protected readonly blocked = signal(false);
  protected readonly blockCheckFailed = signal(false);
  private readonly injector = inject(Injector);
  private readonly safetyHeading = viewChild<ElementRef<HTMLElement>>("safety");
  /** Only the newest load may set the page (the handle can change while one runs). */
  private generation = 0;
  protected readonly busy = signal(false);
  protected readonly message = signal<Message | null>(null);
  protected readonly reportOpen = signal(false);
  protected readonly confirmBlock = signal(false);
  protected readonly reason = signal<ReportReason | null>(null);
  protected readonly details = signal("");

  protected readonly presets = computed(() => {
    const state = this.state();
    return state.kind === "ready" ? pickPresets(state.profile.avatar) : null;
  });
  protected readonly isMe = computed(() => {
    const state = this.state();
    return state.kind === "ready" && state.profile.handle === this.player.me()?.handle;
  });

  constructor() {
    // The router keeps this component when only the handle changes, so each new handle loads again.
    effect(() => {
      this.handle();
      untracked(() => void this.load());
    });
  }

  protected async load(): Promise<void> {
    const generation = ++this.generation;
    const handle = this.handle();
    this.state.set({ kind: "loading" });
    this.message.set(null);
    this.reportOpen.set(false);
    this.confirmBlock.set(false);
    // A failed block list is not a failed profile: the block button still works (it is idempotent).
    const blocked = isBlocked(this.social, handle.toLowerCase()).catch(() => null);
    let state: Load;
    try {
      state = { kind: "ready", profile: await this.social.getProfile(handle) };
    } catch (error: unknown) {
      state = isApiError(error, "not_found") ? { kind: "unavailable" } : { kind: "failed", message: errorMessage(error) };
    }
    const isBlockedResult = await blocked;
    if (generation !== this.generation) return;
    this.blocked.set(isBlockedResult === true);
    this.blockCheckFailed.set(isBlockedResult === null);
    this.state.set(state);
  }

  protected openReport(): void {
    this.reason.set(null);
    this.details.set("");
    this.swap(this.reportOpen, true);
  }

  /** Shows other safety controls; focus moves to the section heading, so it is not lost with the old button. */
  protected swap(flag: { set(value: boolean): void }, value: boolean): void {
    this.message.set(null);
    flag.set(value);
    this.focusSafety();
  }

  private focusSafety(): void {
    afterNextRender(() => this.safetyHeading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected async report(event: Event, profile: PublicProfile): Promise<void> {
    event.preventDefault();
    const reason = this.reason();
    if (reason === null) return;
    const details = this.details().trim();
    await this.act(async () => {
      await this.social.report({ targetHandle: profile.handle, reason, ...(details === "" ? {} : { details }) });
      this.reportOpen.set(false);
      return "Thank you. VYBE moderators will review your report.";
    });
  }

  protected async block(profile: PublicProfile): Promise<void> {
    await this.act(async () => {
      await this.social.block(profile.handle);
      this.blocked.set(true);
      this.confirmBlock.set(false);
      return `You blocked ${playerName(profile)}.`;
    });
  }

  protected async unblock(profile: PublicProfile): Promise<void> {
    await this.act(async () => {
      await this.social.unblock(profile.handle);
      this.blocked.set(false);
      return `You unblocked ${playerName(profile)}. Challenges that ended do not come back.`;
    });
  }

  protected textValue(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  private async act(action: () => Promise<string>): Promise<void> {
    this.busy.set(true);
    this.message.set(null);
    try {
      this.message.set({ text: await action(), error: false });
    } catch (error: unknown) {
      this.message.set({ text: errorMessage(error), error: true });
    }
    this.busy.set(false);
    this.focusSafety();
  }
}
