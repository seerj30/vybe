import { afterNextRender, ChangeDetectionStrategy, Component, computed, effect, type ElementRef, inject, Injector, input, signal, untracked, viewChild } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import { API } from "../api/api";
import { AvatarPortrait } from "../avatar/avatar-portrait";
import { EMPTY_ROOM, fromApi, type RoomState } from "../room/room-layout";
import { RoomViewport } from "../room/room-viewport";
import { pickPresets } from "../avatar/presets";
import { PlayerStore } from "../player/player-store";
import { playerName } from "../profiles/player-name";
import { Generations } from "../challenges/generations";
import { isBlocked, REPORT_REASONS } from "../profiles/safety";
import { FRIENDS_API } from "../api/friends-api";
import { friendErrorMessage, requestSentText } from "../friends/friend-view";
import { TIER_LABELS } from "../achievements/achievement-view";

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
  imports: [AvatarPortrait, RoomViewport, RouterLink],
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

          @if (p.badges.length > 0) {
            <section class="card badges" data-testid="badges" aria-labelledby="badges-title">
              <h2 id="badges-title">Badges <span class="muted small">({{ p.badges.length }})</span></h2>
              <ul>
                @for (b of p.badges; track b.code) {
                  <li [attr.data-badge]="b.code">
                    <span class="medal" [attr.data-tier]="b.tier" aria-hidden="true">★</span>
                    {{ b.name }}<span class="visually-hidden"> ({{ tier[b.tier] }})</span>
                  </li>
                }
              </ul>
              @if (isMe()) {
                <a routerLink="/achievements">All achievements and progress</a>
              }
            </section>
          } @else if (isMe()) {
            <section class="card badges" aria-labelledby="badges-title">
              <h2 id="badges-title">Badges</h2>
              <p class="muted">No badges yet. Finish a run to unlock your first one.</p>
              <a routerLink="/achievements">See all achievements</a>
            </section>
          }

          <section class="card room" aria-labelledby="room-title">
            <h2 id="room-title">Room</h2>
            @switch (room().kind) {
              @case ("loading") {
                <p class="muted" role="status">Loading the room…</p>
              }
              @case ("failed") {
                <p class="muted">The room could not load. <button type="button" class="link" (click)="loadRoom(p.handle)">Try again</button></p>
              }
              @case ("ready") {
                <vybe-room-viewport [room]="roomState()" [owner]="isMe() ? null : name(p)" />
              }
            }
            @if (isMe()) {
              <a class="button secondary" routerLink="/room">Edit your room</a>
            }
          </section>

          @if (isMe()) {
            @if (player.me()?.profileVisibility === "private") {
              <p class="notice" data-testid="own-profile-private">
                Your profile is private. Only you can see it. You can change this in <a routerLink="/settings">Settings</a>.
              </p>
            } @else {
              <p class="notice">This is your profile. Signed-in players see this name, level and avatar.</p>
            }
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

              <div class="card" data-testid="friend-card">
                <h2>Friends</h2>
                @switch (friendRelation()) {
                  @case ("friends") {
                    <p class="muted">You and {{ name(p) }} are friends.</p>
                    <button type="button" class="button secondary" [disabled]="busy()" (click)="removeFriend(p)">Remove friend</button>
                  }
                  @case ("outgoing") {
                    <p class="muted">You sent {{ name(p) }} a friend request. It waits for an answer.</p>
                    <button type="button" class="button secondary" [disabled]="busy()" (click)="cancelFriendRequest(p)">Cancel request</button>
                  }
                  @case ("incoming") {
                    <p>{{ name(p) }} sent you a friend request.</p>
                    <div class="row">
                      <button type="button" class="button" [disabled]="busy()" (click)="acceptFriend(p)">Accept</button>
                      <button type="button" class="button secondary" [disabled]="busy()" (click)="declineFriend(p)">Decline</button>
                    </div>
                  }
                  @case ("none") {
                    <button type="button" class="button secondary" [disabled]="busy()" (click)="addFriend(p)">Add {{ name(p) }} as a friend</button>
                  }
                  @default {
                    <p class="muted">VYBE could not load your friendship with {{ name(p) }}. <a routerLink="/friends">Open your friends</a></p>
                  }
                }
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
    .card + .card, .room { margin-top: 16px; }
    .room vybe-room-viewport { max-width: 560px; }
    .room .button { margin-top: 8px; }
    .link { min-height: 44px; padding: 0; border: 0; background: none; color: #8fb3ff; font: inherit; text-decoration: underline; cursor: pointer; }
    .badges ul { list-style: none; margin: 0 0 8px; padding: 0; display: flex; flex-wrap: wrap; gap: 8px; }
    .badges li { display: inline-flex; align-items: center; gap: 6px; padding: 6px 12px; border-radius: 999px; background: #141826; font-weight: 600; }
    .medal { color: #cd7f32; }
    .medal[data-tier="silver"] { color: #c9d1e0; }
    .medal[data-tier="gold"] { color: #ffc83d; }
    .small { font-size: 14px; font-weight: 400; }
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
  private readonly api = inject(API);
  /** The room on the profile (docs/design/p1-my-room.md): the same visibility as the profile. */
  protected readonly room = signal<{ kind: "loading" | "failed" } | { kind: "ready"; room: RoomState }>({ kind: "loading" });
  protected readonly roomState = computed(() => {
    const room = this.room();
    return room.kind === "ready" ? room.room : EMPTY_ROOM;
  });
  private readonly friendsApi = inject(FRIENDS_API);
  /** P1 friends (stream O): the caller's relation to this player; null when it could not load. */
  protected readonly friendRelation = signal<Schemas["FriendshipState"]["relation"] | null>(null);
  protected readonly name = playerName;
  protected readonly player = inject(PlayerStore);
  protected readonly reasons = REPORT_REASONS;
  protected readonly tier = TIER_LABELS;

  protected readonly state = signal<Load>({ kind: "loading" });
  protected readonly blocked = signal(false);
  protected readonly blockCheckFailed = signal(false);
  private readonly injector = inject(Injector);
  private readonly safetyHeading = viewChild<ElementRef<HTMLElement>>("safety");
  /** Only the newest load may set the page; an action answer for an earlier handle is dropped. */
  private readonly loads = new Generations();
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
    const isCurrent = this.loads.next();
    const handle = this.handle();
    this.state.set({ kind: "loading" });
    this.busy.set(false);
    this.message.set(null);
    this.reportOpen.set(false);
    this.confirmBlock.set(false);
    // A failed block list is not a failed profile: the block button still works (it is idempotent).
    const blocked = isBlocked(this.social, handle.toLowerCase()).catch(() => null);
    const relation = this.friendsApi.relation(handle).then((r) => r.relation, () => null);
    let state: Load;
    try {
      state = { kind: "ready", profile: await this.social.getProfile(handle) };
    } catch (error: unknown) {
      state = isApiError(error, "not_found") ? { kind: "unavailable" } : { kind: "failed", message: errorMessage(error) };
    }
    const isBlockedResult = await blocked;
    const relationResult = await relation;
    if (!isCurrent()) return;
    this.friendRelation.set(relationResult);
    this.blocked.set(isBlockedResult === true);
    this.blockCheckFailed.set(isBlockedResult === null);
    this.state.set(state);
    if (state.kind === "ready") void this.loadRoom(state.profile.handle);
  }

  protected async loadRoom(handle: string): Promise<void> {
    this.room.set({ kind: "loading" });
    try {
      const room = fromApi(await this.api.getProfileRoom(handle));
      if (this.handle().toLowerCase() === handle) this.room.set({ kind: "ready", room });
    } catch {
      if (this.handle().toLowerCase() === handle) this.room.set({ kind: "failed" });
    }
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
    await this.act(
      () => this.social.report({ targetHandle: profile.handle, reason, ...(details === "" ? {} : { details }) }),
      () => {
        this.reportOpen.set(false);
        return "Thank you. VYBE moderators will review your report.";
      },
    );
  }

  protected async block(profile: PublicProfile): Promise<void> {
    await this.act(
      () => this.social.block(profile.handle),
      () => {
        this.blocked.set(true);
        this.confirmBlock.set(false);
        this.friendRelation.set("none"); // a block ends the friendship and any request
        return `You blocked ${playerName(profile)}.`;
      },
    );
  }

  protected async unblock(profile: PublicProfile): Promise<void> {
    await this.act(
      () => this.social.unblock(profile.handle),
      () => {
        this.blocked.set(false);
        return `You unblocked ${playerName(profile)}. Challenges that ended do not come back.`;
      },
    );
  }

  protected async addFriend(profile: PublicProfile): Promise<void> {
    let text = "";
    await this.act(async () => {
      const result = await this.friendsApi.sendRequest(profile.handle);
      text = requestSentText({ ...result, handle: playerName(profile) });
      this.friendRelation.set(result.status === "accepted" ? "friends" : "outgoing");
    }, () => text, friendErrorMessage);
  }

  protected async acceptFriend(profile: PublicProfile): Promise<void> {
    await this.act(() => this.friendsApi.accept(profile.handle), () => {
      this.friendRelation.set("friends");
      return `You and ${playerName(profile)} are now friends.`;
    }, friendErrorMessage);
  }

  protected async declineFriend(profile: PublicProfile): Promise<void> {
    await this.act(() => this.friendsApi.decline(profile.handle), () => {
      this.friendRelation.set("none");
      return `You declined the request from ${playerName(profile)}.`;
    }, friendErrorMessage);
  }

  protected async cancelFriendRequest(profile: PublicProfile): Promise<void> {
    await this.act(() => this.friendsApi.cancel(profile.handle), () => {
      this.friendRelation.set("none");
      return `You cancelled your friend request to ${playerName(profile)}.`;
    }, friendErrorMessage);
  }

  protected async removeFriend(profile: PublicProfile): Promise<void> {
    await this.act(() => this.friendsApi.remove(profile.handle), () => {
      this.friendRelation.set("none");
      return `${playerName(profile)} is no longer your friend.`;
    }, friendErrorMessage);
  }

  protected textValue(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  /**
   * Sends one safety request, then changes the page with `applied`. When the handle changed while the
   * request ran, the answer is for another profile: the new page stays as it is.
   */
  private async act(request: () => Promise<unknown>, applied: () => string, describe: (error: unknown) => string = errorMessage): Promise<void> {
    const isCurrent = this.loads.snapshot();
    this.busy.set(true);
    this.message.set(null);
    let message: Message;
    try {
      await request();
      if (!isCurrent()) return;
      message = { text: applied(), error: false };
    } catch (error: unknown) {
      if (!isCurrent()) return;
      message = { text: describe(error), error: true };
    }
    this.message.set(message);
    this.busy.set(false);
    this.focusSafety();
  }
}
