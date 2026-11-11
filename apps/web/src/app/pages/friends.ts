import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { FRIENDS_API } from "../api/friends-api";
import { errorMessage } from "../api/messages";
import { friendErrorMessage, isHandleInput, requestSentText } from "../friends/friend-view";
import { playerName } from "../profiles/player-name";

type Load =
  | { kind: "loading" }
  | { kind: "failed"; message: string }
  | { kind: "ready"; friends: Schemas["FriendEntry"][]; incoming: Schemas["FriendRequestEntry"][]; outgoing: Schemas["FriendRequestEntry"][] };

interface Message {
  text: string;
  error: boolean;
}

/**
 * `/friends` (P1, docs/design/p1-friends-leaderboard.md): send a request by handle, answer incoming requests,
 * cancel sent requests, and challenge or remove friends.
 */
@Component({
  selector: "vybe-friends",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Friends</h1>
      <p class="muted">Add friends to see their scores on the friends leaderboard and to challenge them from here.
        <a routerLink="/leaderboard" [queryParams]="{ scope: 'friends' }">Friends leaderboard</a></p>

      <form class="card add" (submit)="send($event)" novalidate>
        <h2><label for="friend-handle">Add a friend</label></h2>
        <p class="muted small" id="friend-handle-hint">Type the player's handle: 3 to 20 letters, digits or underscores.</p>
        <div class="row">
          <input id="friend-handle" name="handle" type="text" autocomplete="off" autocapitalize="none" spellcheck="false"
            maxlength="20" aria-describedby="friend-handle-hint" [value]="handle()" (input)="handle.set(textValue($event))" />
          <button type="submit" class="button" [disabled]="busy()">Send request</button>
        </div>
      </form>
      <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>

      @let l = state();
      @switch (l.kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading your friends…</p>
        }
        @case ("failed") {
          <p class="error" role="alert">{{ l.message }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @case ("ready") {
          @if (l.incoming.length > 0) {
            <div class="card">
              <h2>Requests for you</h2>
              <ul class="list" aria-label="Requests for you">
                @for (r of l.incoming; track r.handle) {
                  <li>
                    <span class="who"><strong>{{ name(r) }}</strong> <span class="muted">{{ r.handle }} · Level {{ r.level }}</span></span>
                    <span class="row">
                      <button type="button" class="button" [disabled]="busy()" (click)="accept(r)" [attr.aria-label]="'Accept ' + name(r)">Accept</button>
                      <button type="button" class="button secondary" [disabled]="busy()" (click)="decline(r)" [attr.aria-label]="'Decline ' + name(r)">Decline</button>
                    </span>
                  </li>
                }
              </ul>
            </div>
          }

          <div class="card">
            <h2>Your friends</h2>
            @if (l.friends.length === 0) {
              <p class="empty">No friends yet. Send a request above, or open a player's profile and add them.</p>
            } @else {
              <ul class="list" aria-label="Your friends">
                @for (f of l.friends; track f.handle) {
                  <li>
                    <span class="who">
                      @if (f.profileVisible) {
                        <a [routerLink]="['/profile', f.handle]"><strong>{{ name(f) }}</strong></a>
                      } @else {
                        <strong>{{ name(f) }}</strong>
                      }
                      <span class="muted">{{ f.handle }} · Level {{ f.level }}</span>
                    </span>
                    <span class="row">
                      <a class="button" routerLink="/arcade/street-hoops" [queryParams]="{ to: f.handle }" [attr.aria-label]="'Challenge ' + name(f)">Challenge</a>
                      @if (confirmRemove() === f.handle) {
                        <button type="button" class="button secondary" [disabled]="busy()" (click)="remove(f)">Yes, remove</button>
                        <button type="button" class="button secondary" (click)="confirmRemove.set(null)">Keep</button>
                      } @else {
                        <button type="button" class="button secondary" (click)="confirmRemove.set(f.handle)" [attr.aria-label]="'Remove ' + name(f)">Remove</button>
                      }
                    </span>
                  </li>
                }
              </ul>
            }
          </div>

          @if (l.outgoing.length > 0) {
            <div class="card">
              <h2>Sent requests</h2>
              <ul class="list" aria-label="Sent requests">
                @for (r of l.outgoing; track r.handle) {
                  <li>
                    <span class="who"><strong>{{ name(r) }}</strong> <span class="muted">{{ r.handle }} · Waiting for an answer</span></span>
                    <button type="button" class="button secondary" [disabled]="busy()" (click)="cancel(r)" [attr.aria-label]="'Cancel request to ' + name(r)">Cancel</button>
                  </li>
                }
              </ul>
            </div>
          }
        }
      }
    </section>
  `,
  styles: `
    .card { margin-top: 16px; }
    h2 { margin: 0 0 var(--space-2); }
    .small { font-size: var(--text-sm); margin: 0 0 8px; }
    .row { display: flex; flex-wrap: wrap; gap: var(--space-2); align-items: center; }
    .add .row input { flex: 1 1 200px; }
    .list { list-style: none; margin: 0; padding: 0; }
    .list li { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: var(--space-2); padding: 10px 0; }
    .list li + li { border-top: 1px solid var(--color-border); }
    .who { display: grid; min-width: 0; overflow-wrap: anywhere; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Friends {
  private readonly friendsApi = inject(FRIENDS_API);
  protected readonly name = playerName;
  protected readonly state = signal<Load>({ kind: "loading" });
  protected readonly handle = signal("");
  protected readonly busy = signal(false);
  protected readonly message = signal<Message | null>(null);
  protected readonly confirmRemove = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.state.set({ kind: "loading" });
    try {
      const [friends, requests] = await Promise.all([this.friendsApi.listFriends(), this.friendsApi.listRequests()]);
      this.state.set({ kind: "ready", friends: friends.items, incoming: requests.incoming, outgoing: requests.outgoing });
    } catch (error: unknown) {
      this.state.set({ kind: "failed", message: errorMessage(error) });
    }
  }

  protected async send(event: Event): Promise<void> {
    event.preventDefault();
    const handle = this.handle().trim();
    if (!isHandleInput(handle)) {
      this.message.set({ text: "Type a valid handle: 3 to 20 letters, digits or underscores.", error: true });
      return;
    }
    await this.act(async () => {
      const result = await this.friendsApi.sendRequest(handle);
      this.handle.set("");
      return requestSentText(result);
    });
  }

  protected accept(r: Schemas["FriendRequestEntry"]): Promise<void> {
    return this.act(async () => {
      await this.friendsApi.accept(r.handle);
      return `You and ${playerName(r)} are now friends.`;
    });
  }

  protected decline(r: Schemas["FriendRequestEntry"]): Promise<void> {
    return this.act(async () => {
      await this.friendsApi.decline(r.handle);
      return `You declined the request from ${playerName(r)}.`;
    });
  }

  protected cancel(r: Schemas["FriendRequestEntry"]): Promise<void> {
    return this.act(async () => {
      await this.friendsApi.cancel(r.handle);
      return `You cancelled your request to ${playerName(r)}.`;
    });
  }

  protected remove(f: Schemas["FriendEntry"]): Promise<void> {
    return this.act(async () => {
      await this.friendsApi.remove(f.handle);
      this.confirmRemove.set(null);
      return `${playerName(f)} is no longer your friend.`;
    });
  }

  protected textValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** Runs one action, shows its text, then reloads the lists (the action can change more than one list). */
  private async act(action: () => Promise<string>): Promise<void> {
    this.busy.set(true);
    this.message.set(null);
    try {
      this.message.set({ text: await action(), error: false });
    } catch (error: unknown) {
      this.message.set({ text: friendErrorMessage(error), error: true });
    }
    this.busy.set(false);
    await this.load();
  }
}
