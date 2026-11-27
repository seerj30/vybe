import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  afterNextRender,
  computed,
  inject,
  isDevMode,
  signal,
  viewChild,
} from "@angular/core";
import { ActivatedRoute, Router, RouterLink } from "@angular/router";
import { API } from "../api/api";
import { AVATAR_3D, itemAsset } from "../avatar/asset-manifest";
import { fromSpaceRoom } from "../room/room-layout";
import { type Intent, MoveSender, STOP, directionOf, forView, intentFromJoystick, intentFromKeys } from "./input";
import { Interpolator, type Pose, Predictor } from "./interpolation";
import { roomObstacles } from "./room-collision";
import { type HubFactory, PlazaClient, type PlazaEvents, type PlazaStatus, type SpaceTarget, TicketRefused, ticketSource } from "./plaza-client";
import { avatarLook } from "./plaza-look";
import type { PlazaRenderer, PlazaStats, TagPosition } from "./plaza-renderer";
import {
  DEFAULT_BOUNDS,
  DEFAULT_SPEED_CM_PER_S,
  MAX_AVATARS,
  MAX_ROOM_AVATARS,
  type Member,
  PHRASES,
  STICKERS,
  type Snapshot,
  type Welcome,
  displayName,
  phraseText,
  safetyErrorCode,
  stickerLabel,
} from "./protocol";
import { HUB_ERROR_TEXT } from "./reconnect";
import { inRoom, type StatusView, statusView } from "./status-view";
import { StickerIcon } from "./sticker-icon";
import type { SpaceReportReason, SpaceSafetyError } from "@vybe/contracts/spaces";
import { REPORT_REASONS } from "../profiles/safety";

type Mode = "loading" | "3d" | "fallback";
/**
 * Which space the page shows (route data `space`): the public plaza, the own room as its owner (`/room/live`), or a
 * friend's open room (`/visit/:handle`, docs/design/p2-room-visits.md §8).
 */
export type SpaceMode = "plaza" | "room-host" | "room-visit";
type Panel = "chat" | "stickers" | "emotes" | "people";
type Expression = "chat" | "sticker" | "emote";

interface Bubble {
  kind: Expression;
  text: string;
  sticker: string | null;
  /** Changes on every new bubble, so a repeated phrase shows again. */
  key: number;
}

interface FeedItem {
  key: number;
  name: string;
  text: string;
  sticker: string | null;
}

interface OwnedEmote {
  sku: string;
  name: string;
}

/** Expressions: 1 per 2 s each on the server (§7); the buttons wait the same time, so nothing is dropped. */
const EXPRESSION_COOLDOWN_MS = 2_000;
const BUBBLE_MS = { chat: 4_500, sticker: 3_000, emote: 2_500 } as const;
const FEED_LIMIT = 40;
const ECHO_MS = 3_000;
const JOYSTICK_RADIUS = 44;
const TOAST_MS = 4_000;

/** Plaza report reasons: the profile list without "Cheating" (there are no games in the plaza). */
const PLAZA_REPORT_REASONS = REPORT_REASONS.filter((reason) => reason.value !== "cheating");

const SAFETY_ERROR_TEXT: Record<SpaceSafetyError, string> = {
  not_found: "That player has left the plaza.",
  rate_limited: "Too many reports or blocks. Try again later.",
  invalid_input: "That did not work. Try again.",
  not_joined: "You are not in the plaza now. Try again when you are back.",
};

/** The connection code (a lazy chunk) did not load: "Try again" loads it again. */
const LOAD_FAILED: PlazaStatus = { kind: "failed", message: "The plaza did not load. Check your connection and try again.", retry: true, leave: false };

const WEBGL_TEXT = "3D is not available on this device, so the plaza shows as a list.";
const LOST_TEXT = "The 3D view stopped, so the plaza shows as a list until it restarts.";

/**
 * `/plaza` (P2 public plaza, docs/design/p2-spaces-protocol.md): up to 16 avatars in one instance, preset
 * quick-chat, stickers and owned emotes. No text input anywhere (P2-3). The Three.js scene and SignalR load in
 * this lazy chunk; without WebGL the page shows the members and the events as lists.
 */
@Component({
  selector: "vybe-plaza",
  imports: [RouterLink, StickerIcon],
  host: {
    "[attr.data-mode]": "mode()",
    "[attr.data-space]": "space",
    "[attr.data-status]": "status().kind",
    "[attr.data-instance]": "instanceId()",
    "[attr.data-slot]": "ownSlot()",
    "[attr.data-avatars]": "stats()?.avatars",
    "[attr.data-draw-calls]": "stats()?.drawCalls",
    "[attr.data-triangles]": "stats()?.triangles",
    "[attr.data-moving]": "moving()",
    "(window:keydown)": "keyDown($event)",
    "(window:keyup)": "keyUp($event)",
    "(window:blur)": "releaseKeys()",
    "(document:visibilitychange)": "visibility()",
  },
  template: `
    <div class="plaza">
      <canvas #canvas class="scene" aria-hidden="true" [hidden]="mode() === 'fallback'"></canvas>

      @if (mode() === "3d") {
        <div #tags class="tags" aria-hidden="true">
          @for (member of shownMembers(); track member.slot) {
            <div class="tag" [attr.data-slot]="member.slot" [class.own]="member.slot === ownSlot()">
              @if (bubbles().get(member.slot); as bubble) {
                @switch (bubble.kind) {
                  @case ("sticker") {
                    <div class="bubble sticker" [attr.data-key]="bubble.key"><vybe-sticker-icon [sticker]="bubble.sticker!" [size]="44" /></div>
                  }
                  @case ("emote") {
                    <div class="bubble emote" [attr.data-key]="bubble.key">{{ bubble.text }}</div>
                  }
                  @default {
                    <div class="bubble" [attr.data-key]="bubble.key" data-testid="bubble">{{ bubble.text }}</div>
                  }
                }
              }
              <span class="name" data-testid="name-tag">{{ member.slot === ownSlot() ? "You" : name(member) }}</span>
            </div>
          }
        </div>
      }

      <header class="top">
        @switch (space) {
          @case ("room-host") {
            <button type="button" class="pill back" (click)="closeRoom()" data-testid="close-room">Close room</button>
          }
          @case ("room-visit") {
            <a class="pill back" routerLink="/friends" aria-label="Back to your friends">‹ Friends</a>
          }
          @default {
            <a class="pill back" routerLink="/hub" aria-label="Back to the hub">‹ Hub</a>
          }
        }
        <h1 class="pill instance" data-testid="instance">{{ title() }}</h1>
        <button type="button" class="pill people" [attr.aria-pressed]="panel() === 'people'" (click)="toggle('people')" data-testid="people-button">
          <span aria-hidden="true">●</span> {{ memberCount() }} / {{ maxAvatars }}<span class="visually-hidden"> players here. Show the list</span>
        </button>
      </header>

      @if (toast(); as text) {
        <p class="toast" role="status">{{ text }}</p>
      }

      @if (mode() === "fallback") {
        <section class="fallback" [attr.aria-label]="space === 'plaza' ? 'Plaza as a list' : 'Room as a list'">
          <p class="notice">{{ fallbackText() }}</p>
          <h2>Players here</h2>
          <ul class="list" data-testid="fallback-members">
            @for (member of members(); track member.slot) {
              <li>{{ member.slot === ownSlot() ? "You" : name(member) }}</li>
            }
          </ul>
          <h2>What happens</h2>
          @if (feed().length === 0) {
            <p class="muted">Nothing yet. Send a phrase or a sticker.</p>
          } @else {
            <ul class="list" data-testid="fallback-feed">
              @for (item of feed(); track item.key) {
                <li>
                  <strong>{{ item.name }}</strong>:
                  @if (item.sticker; as s) {
                    <vybe-sticker-icon [sticker]="s" [size]="22" /> {{ item.text }}
                  } @else {
                    {{ item.text }}
                  }
                </li>
              }
            </ul>
          }
        </section>
      } @else {
        <!-- Screen readers hear the plaza events; the 3D view shows them as bubbles. -->
        <ul class="visually-hidden" role="log" [attr.aria-label]="space === 'plaza' ? 'Plaza events' : 'Room events'">
          @for (item of feed().slice(-5); track item.key) {
            <li>{{ item.name }}: {{ item.text }}</li>
          }
        </ul>
      }

      <div class="bottom">
        @if (touch() && mode() === "3d") {
          <div
            class="joystick"
            role="presentation"
            data-testid="joystick"
            (pointerdown)="stickStart($event)"
            (pointermove)="stickMove($event)"
            (pointerup)="stickEnd($event)"
            (pointercancel)="stickEnd($event)"
            (lostpointercapture)="stickEnd($event)"
          >
            <span class="knob" [style.transform]="knobTransform()"></span>
          </div>
        } @else if (mode() === "3d") {
          <p class="hint muted">Move with W A S D or the arrow keys.</p>
        }
        <div class="actions" role="toolbar" aria-label="Say something">
          <button type="button" class="action" [attr.aria-pressed]="panel() === 'chat'" (click)="toggle('chat')" data-testid="chat-button">Chat</button>
          <button type="button" class="action" [attr.aria-pressed]="panel() === 'stickers'" (click)="toggle('stickers')" data-testid="stickers-button">Stickers</button>
          <button type="button" class="action" [attr.aria-pressed]="panel() === 'emotes'" (click)="toggle('emotes')" data-testid="emotes-button">Emotes</button>
        </div>
      </div>

      @if (panel(); as open) {
        <section class="sheet" role="dialog" [attr.aria-label]="panelTitle()" data-testid="panel">
          <div class="sheet-head">
            <h2>{{ panelTitle() }}</h2>
            <button type="button" class="close" (click)="panel.set(null)" aria-label="Close">✕</button>
          </div>
          @switch (open) {
            @case ("chat") {
              <div class="grid phrases">
                @for (phrase of phrases; track phrase.id) {
                  <button type="button" class="choice" [disabled]="!canSend('chat')" (click)="sendQuickChat(phrase.id)">{{ phrase.text }}</button>
                }
              </div>
            }
            @case ("stickers") {
              <div class="grid stickers">
                @for (sticker of stickers; track sticker.id) {
                  <button type="button" class="choice icon" [disabled]="!canSend('sticker')" (click)="sendSticker(sticker.id)" [attr.aria-label]="sticker.label">
                    <vybe-sticker-icon [sticker]="sticker.id" [size]="40" />
                  </button>
                }
              </div>
            }
            @case ("emotes") {
              @if (ownedEmotes(); as emotes) {
                @if (emotes.length === 0) {
                  <p class="muted">You have no emotes yet. <a routerLink="/shop">Get one in the shop.</a></p>
                } @else {
                  <div class="grid">
                    @for (emote of emotes; track emote.sku) {
                      <button type="button" class="choice" [disabled]="!canSend('emote')" (click)="sendEmote(emote.sku)">{{ emote.name }}</button>
                    }
                  </div>
                }
              } @else if (emotesFailed()) {
                <p class="muted">Your emotes could not load. Try again later.</p>
              } @else {
                <p class="muted" role="status">Loading…</p>
              }
            }
            @case ("people") {
              <ul class="people-list" data-testid="member-list">
                @for (member of members(); track member.slot) {
                  <li [attr.data-slot]="member.slot">
                    <div class="member-row">
                      <span class="person">
                        {{ member.slot === ownSlot() ? "You" : name(member) }}
                        @if (member.slot === ownerSlot()) {
                          <span class="host-tag" data-testid="host-tag">Host</span>
                        }
                      </span>
                      @if (member.slot === ownSlot()) {
                        <span class="muted small">This is you</span>
                      } @else {
                        <span class="member-actions">
                          @if (member.handle; as handle) {
                            <a class="chip small-chip" [routerLink]="['/profile', handle]">Profile</a>
                          }
                          @if (space === "room-host") {
                            <button type="button" class="chip small-chip" [disabled]="safetyBusy()" (click)="askSafety('remove', member)" data-testid="remove-member">Remove</button>
                          }
                          <button type="button" class="chip small-chip" [disabled]="safetyBusy()" (click)="askSafety('report', member)" data-testid="report-member">Report</button>
                          <button type="button" class="chip small-chip" [disabled]="safetyBusy()" (click)="askSafety('block', member)" data-testid="block-member">Block</button>
                        </span>
                      }
                    </div>
                    @if (safety(); as ask) {
                      @if (ask.slot === member.slot && ask.key === member.key) {
                        @if (ask.kind === "report") {
                          <div class="safety" role="group" [attr.aria-label]="'Report ' + name(member)" data-testid="report-form">
                            <p class="small">Why do you report {{ name(member) }}? The player is not told who reported.</p>
                            <div class="grid">
                              @for (reason of reportReasons; track reason.value) {
                                <button type="button" class="choice" [disabled]="safetyBusy()" (click)="report(member, reason.value)">{{ reason.label }}</button>
                              }
                            </div>
                            <button type="button" class="link small" (click)="safety.set(null)">Cancel</button>
                          </div>
                        } @else if (ask.kind === "remove") {
                          <div class="safety" role="group" [attr.aria-label]="'Remove ' + name(member)" data-testid="remove-form">
                            <p class="small">Remove {{ name(member) }} from your room? The player cannot come back while the room stays open.</p>
                            <div class="safety-buttons">
                              <button type="button" class="button danger" [disabled]="safetyBusy()" (click)="removeVisitor(member)" data-testid="confirm-remove">Remove</button>
                              <button type="button" class="button secondary" (click)="safety.set(null)">Cancel</button>
                            </div>
                          </div>
                        } @else {
                          <div class="safety" role="group" [attr.aria-label]="'Block ' + name(member)" data-testid="block-form">
                            <p class="small">{{ blockText(member) }}</p>
                            <div class="safety-buttons">
                              <button type="button" class="button danger" [disabled]="safetyBusy()" (click)="block(member)" data-testid="confirm-block">Block</button>
                              <button type="button" class="button secondary" (click)="safety.set(null)">Cancel</button>
                            </div>
                          </div>
                        }
                      }
                    }
                  </li>
                }
              </ul>
            }
          }
        </section>
      }

      @if (statusView(); as view) {
        <div class="overlay" role="alertdialog" aria-modal="false" [attr.aria-label]="view.title" data-testid="status">
          <div class="card status-card">
            @if (view.busy) {
              <span class="spinner" aria-hidden="true"></span>
            }
            <p class="status-text" role="status">{{ view.text }}</p>
            <div class="status-actions">
              @if (view.action === "retry") {
                <button type="button" class="button" [disabled]="!retryReady()" (click)="retry()">{{ view.button }}</button>
              } @else if (view.action === "reload") {
                <button type="button" class="button" (click)="reload()">Reload</button>
              } @else if (view.action === "start") {
                <a class="button" routerLink="/">Go to the start page</a>
              }
              @if (view.exits) {
                @if (space === "room-host") {
                  <a class="button" routerLink="/room" data-testid="back-to-room">Back to My Room</a>
                } @else {
                  <a class="button" routerLink="/plaza" data-testid="go-to-plaza">Go to the plaza</a>
                }
              }
              <a class="button secondary" routerLink="/hub">Back to the hub</a>
            </div>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    :host { display: block; position: fixed; inset: 0; overflow: hidden; background: #9fc6e8; color: var(--color-text); }
    .plaza { position: absolute; inset: 0; }
    .scene { position: absolute; inset: 0; width: 100%; height: 100%; display: block; touch-action: none; }
    .scene[hidden] { display: none; }
    .tags { position: absolute; inset: 0; pointer-events: none; overflow: hidden; }
    .tag { position: absolute; left: 0; top: 0; visibility: hidden; display: flex; flex-direction: column; align-items: center; gap: 4px; transform: translate(-9999px, 0); will-change: transform; }
    .name { padding: 1px 8px; border-radius: var(--radius-pill); background: rgb(13 17 32 / 0.72); font-size: 12px; font-weight: 700; white-space: nowrap; max-width: 140px; overflow: hidden; text-overflow: ellipsis; }
    .tag.own .name { background: rgb(255 200 61 / 0.9); color: var(--color-on-accent); }
    .bubble { max-width: 160px; padding: 6px 10px; border-radius: 14px; background: #fff; color: #141826; font-size: 13px; font-weight: 700; line-height: 1.25; text-align: center; box-shadow: 0 2px 6px rgb(0 0 0 / 0.25); animation: pop 220ms var(--ease-out); }
    .bubble.sticker { padding: 4px; border-radius: 50%; }
    .bubble.emote { background: var(--color-secondary); color: var(--color-text); font-style: italic; }
    @keyframes pop { from { transform: scale(0.6); opacity: 0; } to { transform: scale(1); opacity: 1; } }
    .top { position: absolute; top: 0; left: 0; right: 0; display: flex; align-items: center; gap: var(--space-2); padding: max(var(--space-3), env(safe-area-inset-top)) var(--space-3) 0; pointer-events: none; }
    .top > * { pointer-events: auto; }
    .pill { display: inline-flex; align-items: center; gap: 6px; min-height: var(--control-height); padding: 0 var(--space-4); margin: 0; border: 0; border-radius: var(--radius-pill); background: rgb(13 17 32 / 0.78); color: var(--color-text); font: inherit; font-weight: 700; text-decoration: none; cursor: pointer; }
    .instance { font-size: var(--text-md); cursor: default; }
    .people { margin-left: auto; font-variant-numeric: tabular-nums; }
    .people span[aria-hidden] { color: var(--color-success); font-size: 10px; }
    .people[aria-pressed="true"] { background: var(--color-accent); color: var(--color-on-accent); }
    .toast { position: absolute; top: calc(max(var(--space-3), env(safe-area-inset-top)) + 52px); left: 50%; transform: translateX(-50%); margin: 0; padding: var(--space-2) var(--space-4); border-radius: var(--radius-md); background: var(--color-danger-bg); color: var(--color-danger-ink); font-size: var(--text-sm); max-width: calc(100% - 32px); }
    .bottom { position: absolute; left: 0; right: 0; bottom: 0; display: flex; align-items: flex-end; justify-content: space-between; gap: var(--space-3); padding: 0 var(--space-3) max(var(--space-3), env(safe-area-inset-bottom)); pointer-events: none; }
    .bottom > * { pointer-events: auto; }
    .hint { margin: 0; padding: 6px 12px; border-radius: var(--radius-pill); background: rgb(13 17 32 / 0.6); font-size: var(--text-xs); color: var(--color-text); }
    .actions { display: flex; gap: var(--space-2); margin-left: auto; }
    @media (max-width: 480px) { .actions { flex-direction: column; align-items: stretch; } }
    .action { min-height: var(--control-height); padding: 0 var(--space-4); border: 0; border-radius: var(--radius-lg); background: var(--color-secondary); color: var(--color-text); font: inherit; font-weight: 700; cursor: pointer; box-shadow: 0 2px 8px rgb(0 0 0 / 0.25); }
    .action:hover { background: var(--color-secondary-hover); }
    .action[aria-pressed="true"] { background: var(--color-accent); color: var(--color-on-accent); }
    .joystick { position: relative; width: 112px; height: 112px; flex: none; border-radius: 50%; background: rgb(13 17 32 / 0.35); border: 2px solid rgb(255 255 255 / 0.5); touch-action: none; }
    .knob { position: absolute; left: 50%; top: 50%; width: 48px; height: 48px; margin: -24px 0 0 -24px; border-radius: 50%; background: rgb(255 255 255 / 0.85); }
    .sheet { position: absolute; left: 0; right: 0; bottom: 0; max-height: 55dvh; overflow-y: auto; padding: var(--space-3) var(--space-4) max(var(--space-4), env(safe-area-inset-bottom)); border-radius: var(--radius-xl) var(--radius-xl) 0 0; background: var(--color-surface); border-top: 1px solid var(--color-border); box-shadow: 0 -6px 24px rgb(0 0 0 / 0.35); }
    @media (min-width: 768px) { .sheet { left: auto; right: var(--space-4); bottom: 76px; width: 380px; border-radius: var(--radius-xl); border: 1px solid var(--color-border); max-height: 60dvh; } }
    .sheet-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: var(--space-3); }
    .sheet-head h2 { margin: 0; font-size: var(--text-lg); }
    .close { min-width: var(--control-height); min-height: var(--control-height); border: 0; border-radius: var(--radius-md); background: var(--color-secondary); color: inherit; font: inherit; cursor: pointer; }
    .grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: var(--space-2); }
    .grid.stickers { grid-template-columns: repeat(4, 1fr); }
    .choice { min-height: var(--control-height); padding: var(--space-2); border: 1px solid var(--color-border-strong); border-radius: var(--radius-md); background: var(--color-inset); color: var(--color-text); font: inherit; font-size: var(--text-sm); font-weight: 600; cursor: pointer; }
    .choice:hover:not(:disabled) { border-color: var(--color-accent); }
    .choice:disabled { opacity: 0.5; cursor: wait; }
    .choice.icon { display: flex; align-items: center; justify-content: center; min-height: 60px; }
    .people-list { list-style: none; margin: 0; padding: 0; display: grid; }
    .people-list li { display: grid; gap: var(--space-2); padding: var(--space-2) 0; }
    .member-row { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
    .member-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: var(--space-1); }
    .safety { display: grid; gap: var(--space-2); padding: var(--space-3); border-radius: var(--radius-md); background: var(--color-inset); }
    .safety p { margin: 0; }
    .safety-buttons { display: flex; gap: var(--space-2); }
    .link { justify-self: start; padding: 0; border: 0; background: none; color: var(--color-accent); font: inherit; cursor: pointer; text-decoration: underline; }
    .people-list li + li { border-top: 1px solid var(--color-border); }
    .person { font-weight: 700; overflow: hidden; text-overflow: ellipsis; }
    .host-tag { margin-left: var(--space-1); padding: 1px 8px; border-radius: var(--radius-pill); background: var(--color-accent); color: var(--color-on-accent); font-size: var(--text-xs); font-weight: 700; }
    .small { font-size: var(--text-xs); }
    .small-chip { min-height: 36px; padding: var(--space-1) var(--space-3); font-size: var(--text-xs); }
    .fallback { position: absolute; inset: 64px 0 76px; overflow-y: auto; padding: var(--space-3) var(--space-4); background: var(--color-bg); }
    .fallback h2 { font-size: var(--text-md); margin: var(--space-4) 0 var(--space-2); }
    .list { margin: 0; padding-left: var(--space-5); display: grid; gap: var(--space-1); }
    .list vybe-sticker-icon { vertical-align: middle; }
    .overlay { position: absolute; inset: 0; display: grid; place-items: center; padding: var(--space-4); background: rgb(13 17 32 / 0.55); }
    .status-card { max-width: 360px; width: 100%; text-align: center; display: grid; gap: var(--space-3); justify-items: center; }
    .status-text { margin: 0; }
    .status-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); justify-content: center; }
    .spinner { width: 32px; height: 32px; border-radius: 50%; border: 4px solid var(--color-border); border-top-color: var(--color-accent); animation: spin 0.9s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .bubble, .spinner { animation: none; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Plaza {
  private readonly api = inject(API);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>("canvas");
  private readonly tagLayer = viewChild<ElementRef<HTMLElement>>("tags");

  protected readonly phrases = PHRASES;
  protected readonly stickers = STICKERS;
  protected readonly name = displayName;
  protected readonly space: SpaceMode = spaceMode(this.route.snapshot.data["space"]);
  /** The owner's handle of a visited room (route `/visit/:handle`). */
  private readonly roomHandle = this.route.snapshot.paramMap.get("handle") ?? "";
  private readonly view: "plaza" | "room" = this.space === "plaza" ? "plaza" : "room";
  protected readonly maxAvatars = this.space === "plaza" ? MAX_AVATARS : MAX_ROOM_AVATARS;

  protected readonly status = signal<PlazaStatus>({ kind: "connecting" });
  protected readonly mode = signal<Mode>("loading");
  protected readonly instanceId = signal<number | null>(null);
  protected readonly ownSlot = signal<number | null>(null);
  /** The owner's slot in a room (null in the plaza). */
  protected readonly ownerSlot = signal<number | null>(null);
  private readonly memberMap = signal<ReadonlyMap<number, Member>>(new Map());
  /** Slots with a snapshot entry: an avatar shows from its first position (protocol §5). */
  private readonly placed = signal<ReadonlySet<number>>(new Set());
  protected readonly bubbles = signal<ReadonlyMap<number, Bubble>>(new Map());
  protected readonly feed = signal<readonly FeedItem[]>([]);
  protected readonly panel = signal<Panel | null>(null);
  protected readonly toast = signal<string | null>(null);
  /** The open report or block question in the people list: the member by slot and key (slots are reused). */
  protected readonly safety = signal<{ kind: "report" | "block" | "remove"; slot: number; key: number } | null>(null);
  protected readonly safetyBusy = signal(false);
  protected readonly reportReasons = PLAZA_REPORT_REASONS;
  protected readonly ownedEmotes = signal<readonly OwnedEmote[] | null>(null);
  protected readonly emotesFailed = signal(false);
  private readonly cooling = signal<ReadonlySet<Expression>>(new Set());
  protected readonly touch = signal(false);
  protected readonly knob = signal({ x: 0, y: 0 });
  /** True while the last intent sent to the server moves the avatar (also a test hook: `data-moving`). */
  protected readonly moving = signal(false);
  protected readonly stats = signal<PlazaStats | null>(null, {
    equal: (a, b) => a?.drawCalls === b?.drawCalls && a?.triangles === b?.triangles && a?.avatars === b?.avatars,
  });
  protected readonly retryReady = signal(true);
  private readonly fallbackReason = signal<"webgl" | "lost">("webgl");

  protected readonly members = computed(() => [...this.memberMap().values()].sort((a, b) => a.slot - b.slot));
  protected readonly shownMembers = computed(() => this.members().filter((m) => this.placed().has(m.slot)));
  protected readonly memberCount = computed(() => this.memberMap().size);
  protected readonly knobTransform = computed(() => `translate(${this.knob().x}px, ${this.knob().y}px)`);
  protected readonly fallbackText = computed(() => (this.fallbackReason() === "lost" ? LOST_TEXT : WEBGL_TEXT));
  protected readonly panelTitle = computed(() => {
    switch (this.panel()) {
      case "chat":
        return "Quick chat";
      case "stickers":
        return "Stickers";
      case "emotes":
        return "Emotes";
      case "people":
        return this.space === "plaza" ? `Players in Plaza ${this.instanceId() ?? ""}`.trim() : "People in this room";
      default:
        return "";
    }
  });
  protected readonly title = computed(() => {
    if (this.space === "room-host") return "Your room";
    if (this.space === "room-visit") return `${this.roomHandle}'s room`;
    const id = this.instanceId();
    return id === null ? "Plaza" : `Plaza ${id}`;
  });
  protected readonly statusView = computed(() => {
    const place = this.view;
    const view = statusView(this.status(), place);
    if (view !== null || this.mode() !== "loading") return view;
    const text = place === "room" ? "Loading the room…" : "Loading the plaza…";
    return { title: text.slice(0, -1), text, busy: true, action: null, button: "", exits: false } satisfies StatusView;
  });

  private readonly reducedMotion = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  private readonly interpolator = new Interpolator();
  private readonly predictor = new Predictor(DEFAULT_BOUNDS, DEFAULT_SPEED_CM_PER_S);
  private readonly sender = new MoveSender((dx, dz) => {
    this.moving.set(dx !== 0 || dz !== 0);
    this.client?.move(dx, dz);
  });
  private readonly held = new Set<NonNullable<ReturnType<typeof directionOf>>>();
  private readonly failedItems = new Map<number, Set<string>>();
  private readonly echoes: { kind: Expression; id: string; at: number }[] = [];
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private readonly bubbleTimers = new Map<number, ReturnType<typeof setTimeout>>();
  private client: PlazaClient | null = null;
  private renderer: PlazaRenderer | null = null;
  private inputTimer: ReturnType<typeof setInterval> | null = null;
  private toastTimer: ReturnType<typeof setTimeout> | null = null;
  private stickPointer: number | null = null;
  private stickCenter = { x: 0, y: 0 };
  private stickIntent: Intent | null = null;
  private feedKey = 0;
  private roomShown = false;
  private destroyed = false;

  constructor() {
    this.touch.set(typeof matchMedia === "function" && matchMedia("(pointer: coarse)").matches);
    this.api.getInventory().then(
      (inventory) =>
        this.ownedEmotes.set(
          inventory.items.filter((item) => item.slot === "emote").map((item) => ({ sku: item.sku, name: itemAsset(item.sku)?.name ?? item.name })),
        ),
      () => this.emotesFailed.set(true),
    );
    afterNextRender(() => void this.start());
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  // ------------------------------------------------------------------ set-up

  private async start(): Promise<void> {
    await this.mountRenderer();
    if (this.destroyed) return;
    this.inputTimer = setInterval(() => this.sender.tick(performance.now()), 25);
    await this.openClient();
  }

  /** Loads the connection code (a lazy chunk) and connects. A failed load shows "Try again", never a spinner. */
  private async openClient(): Promise<void> {
    this.status.set({ kind: "connecting" });
    let factory: Awaited<ReturnType<Plaza["hubFactory"]>>;
    try {
      factory = await this.hubFactory();
    } catch {
      if (!this.destroyed) this.status.set(LOAD_FAILED);
      return;
    }
    if (this.destroyed) return;
    this.client = new PlazaClient(factory.create, this.events, undefined, factory.ticketError, this.target());
    this.client.connect();
  }

  private async mountRenderer(): Promise<void> {
    try {
      const { PlazaRenderer } = await import("./plaza-renderer");
      if (this.destroyed) return;
      this.renderer = new PlazaRenderer(this.canvas().nativeElement, {
        view: this.view,
        reducedMotion: this.reducedMotion,
        idleAnimation: AVATAR_3D.idleAnimation,
        beforeFrame: (now, dt) => {
          if (this.client?.joined) this.predictor.step(this.sender.current.dx, this.sender.current.dz, dt, now);
        },
        pose: (slot, now) => this.pose(slot, now),
        ownSlot: () => this.ownSlot(),
        onTags: (tags) => this.placeTags(tags),
        onStats: (stats) => this.stats.set(stats),
        onContextLost: () => {
          // The joystick goes away with the scene: its pointerup never comes, so stop here.
          this.haltInput();
          this.fallbackReason.set("lost");
          this.mode.set("fallback");
        },
        onContextRestored: () => this.mode.set("3d"),
      });
      await this.renderer.ready(AVATAR_3D.base.assetUrl);
      if (!this.destroyed) this.mode.set("3d");
    } catch {
      // No WebGL, or the base avatar did not load: the plaza shows as lists (members and events).
      this.renderer?.dispose();
      this.renderer = null;
      this.fallbackReason.set("webgl");
      this.mode.set("fallback");
    }
  }

  /**
   * The real hub, or the dev fake hub with `?fake=1`. The fake hub is a separate chunk that a production build
   * also emits, but only `isDevMode()` imports it: nobody can reach it in production.
   */
  private async hubFactory(): Promise<{ create: HubFactory; ticketError: () => unknown }> {
    const query = this.route.snapshot.queryParamMap;
    if (isDevMode() && query.get("fake") === "1" && this.space === "plaza") {
      const { FakeHub, FAKE_DEFAULTS } = await import("./fake-hub");
      const number = (key: string, fallback: number) => {
        const value = Number(query.get(key));
        return query.has(key) && Number.isFinite(value) ? value : fallback;
      };
      const options = {
        ...FAKE_DEFAULTS,
        bots: number("bots", FAKE_DEFAULTS.bots),
        seed: number("seed", FAKE_DEFAULTS.seed),
        lively: query.get("still") !== "1",
      };
      return { create: () => new FakeHub(options), ticketError: () => null };
    }
    const { signalRFactory, createSpaceTicket } = await import("./hub-connection");
    let lastError: unknown = null;
    const ticket = ticketSource(async () => {
      lastError = null;
      return createSpaceTicket(this.api);
    });
    const recorded = async () => {
      try {
        return await ticket();
      } catch (error: unknown) {
        lastError = error;
        throw error;
      }
    };
    return { create: signalRFactory(recorded), ticketError: () => (lastError instanceof TicketRefused ? lastError : null) };
  }

  private target(): SpaceTarget {
    if (this.space === "room-host") return { kind: "host" };
    if (this.space === "room-visit") return { kind: "visit", handle: this.roomHandle };
    return { kind: "plaza" };
  }

  private stop(): void {
    this.destroyed = true;
    this.client?.dispose();
    this.client = null;
    this.renderer?.dispose();
    this.renderer = null;
    if (this.inputTimer !== null) clearInterval(this.inputTimer);
    if (this.toastTimer !== null) clearTimeout(this.toastTimer);
    for (const timer of this.timers) clearTimeout(timer);
    for (const timer of this.bubbleTimers.values()) clearTimeout(timer);
  }

  // ------------------------------------------------------------------ server events

  private readonly events: PlazaEvents = {
    status: (status) => {
      this.status.set(status);
      if (status.kind !== "joined") {
        this.sender.disable(performance.now(), false);
        this.moving.set(false); // the server stops an avatar without a connection
      } else if (!document.hidden) {
        this.sender.enable();
        this.applyInput(); // keys still held (or the stick) after a reconnect move again
      }
      if (status.kind === "kicked" && status.rule.action.kind === "leave") void this.router.navigate(["/"]);
      if (status.kind === "kicked" && status.retryAt !== null) {
        const wait = status.retryAt - Date.now();
        this.retryReady.set(wait <= 0);
        if (wait > 0) this.later(() => this.retryReady.set(true), wait);
      } else {
        this.retryReady.set(true);
      }
    },
    welcome: (welcome, moved) => this.welcome(welcome, moved),
    snapshot: (snapshot) => this.snapshot(snapshot),
    joined: (member) => {
      // A `joined` for an occupied slot replaces that member (§5: reconnect, new look or privacy).
      this.failedItems.delete(member.slot);
      this.memberMap.update((map) => new Map(map).set(member.slot, member));
      this.showAvatar(member);
    },
    left: (slot) => {
      this.memberMap.update((map) => {
        const next = new Map(map);
        next.delete(slot);
        return next;
      });
      this.interpolator.remove(slot);
      this.placed.update((set) => without(set, slot));
      this.renderer?.removeAvatar(slot);
      this.failedItems.delete(slot);
      this.clearBubble(slot);
    },
    look: (slot, look) => {
      const member = this.memberMap().get(slot);
      if (member === undefined) return;
      const next = { ...member, look };
      this.memberMap.update((map) => new Map(map).set(slot, next));
      this.failedItems.delete(slot);
      this.showAvatar(next);
    },
    emote: (slot, sku) => {
      if (this.isEcho(slot, "emote", sku)) return;
      this.showEmote(slot, sku);
    },
    quickChat: (slot, id) => {
      if (this.isEcho(slot, "chat", id)) return;
      this.showQuickChat(slot, id);
    },
    sticker: (slot, id) => {
      if (this.isEcho(slot, "sticker", id)) return;
      this.showSticker(slot, id);
    },
    error: (code) => this.showToast(HUB_ERROR_TEXT[code]),
  };

  private welcome(welcome: Welcome, moved: boolean): void {
    this.safety.set(null); // a new instance: the slots and keys of the question no longer apply
    this.interpolator.reset();
    this.predictor.reset();
    this.predictor.configure(welcome.bounds, welcome.speedCmPerS, welcome.room === null ? [] : roomObstacles(welcome.room));
    this.sender.reset();
    this.moving.set(false); // a new instance: the server avatar stands until the next Move
    this.applyInput();
    this.renderer?.clear();
    this.failedItems.clear();
    for (const slot of this.bubbleTimers.keys()) this.clearBubble(slot);
    this.echoes.length = 0;
    this.instanceId.set(welcome.instanceId);
    this.ownSlot.set(welcome.slot);
    this.ownerSlot.set(welcome.ownerSlot);
    if (welcome.room !== null) this.showRoom(welcome.room);
    this.placed.set(new Set());
    this.memberMap.set(new Map(welcome.members.map((m) => [m.slot, m])));
    for (const member of welcome.members) this.showAvatar(member);
    if (moved) this.showToast(`You are now in Plaza ${welcome.instanceId}.`, false);
  }

  /** Room view: the owner's layout. It cannot change while the room is open, so it loads once per page. */
  private showRoom(layout: NonNullable<Welcome["room"]>): void {
    const renderer = this.renderer;
    if (renderer === null || this.roomShown) return;
    this.roomShown = true;
    renderer.setRoom(fromSpaceRoom(layout)).then(
      (failed) => {
        if (failed.length > 0) this.showToast("Some room items could not load, so boxes show instead.");
      },
      () => {
        this.roomShown = false; // the next Welcome (a reconnect) tries again
        this.showToast("The room could not load. You can still meet here.");
      },
    );
  }

  private snapshot(snapshot: Snapshot): void {
    const now = performance.now();
    if (!this.interpolator.push(snapshot.tick, snapshot.avatars, now)) return;
    const own = this.ownSlot();
    const ownSample = own === null ? null : snapshot.avatars.find((a) => a.slot === own);
    if (ownSample !== undefined && ownSample !== null) this.predictor.correct(ownSample, now);
    const slots = new Set(snapshot.avatars.map((a) => a.slot).filter((slot) => this.memberMap().has(slot)));
    const current = this.placed();
    if (slots.size !== current.size || [...slots].some((s) => !current.has(s))) this.placed.set(slots);
  }

  private pose(slot: number, now: number): Pose | null {
    if (!this.placed().has(slot)) return null;
    if (slot === this.ownSlot() && this.predictor.placed) {
      return { x: this.predictor.x, z: this.predictor.z, angle: this.predictor.angle, state: this.predictor.moving ? 1 : 0 };
    }
    return this.interpolator.sample(slot, now);
  }

  private showAvatar(member: Member): void {
    const renderer = this.renderer;
    if (renderer === null) return;
    const failed = this.failedItems.get(member.slot) ?? new Set<string>();
    void renderer.setAvatar(member.slot, avatarLook(member.look, failed)).then((missing) => {
      // Items that did not load: show the starter items (spec §9), once per item.
      const fresh = missing.filter((sku) => !failed.has(sku));
      if (fresh.length === 0 || this.memberMap().get(member.slot) !== member) return;
      this.failedItems.set(member.slot, new Set([...failed, ...fresh]));
      this.showAvatar(member);
    });
  }

  // ------------------------------------------------------------------ expressions

  protected canSend(kind: Expression): boolean {
    return this.status().kind === "joined" && !this.cooling().has(kind);
  }

  protected sendQuickChat(id: string): void {
    if (!this.beginSend("chat", id)) return;
    this.client?.quickChat(id);
    this.showQuickChat(this.ownSlot()!, id);
  }

  protected sendSticker(id: string): void {
    if (!this.beginSend("sticker", id)) return;
    this.client?.sticker(id);
    this.showSticker(this.ownSlot()!, id);
  }

  protected sendEmote(sku: string): void {
    if (!this.beginSend("emote", sku)) return;
    this.client?.emote(sku);
    this.showEmote(this.ownSlot()!, sku);
  }

  /** Shows the own expression at once; the server's echo (if it sends one to the sender) is then skipped. */
  private beginSend(kind: Expression, id: string): boolean {
    if (!this.canSend(kind) || this.ownSlot() === null) return false;
    this.cooling.update((set) => new Set(set).add(kind));
    this.later(() => this.cooling.update((set) => without(set, kind)), EXPRESSION_COOLDOWN_MS);
    const now = performance.now();
    // Keep only echoes that can still come (the server may not echo at all).
    const fresh = this.echoes.filter((e) => now - e.at < ECHO_MS);
    this.echoes.splice(0, this.echoes.length, ...fresh, { kind, id, at: now });
    this.panel.set(null);
    return true;
  }

  private isEcho(slot: number, kind: Expression, id: string): boolean {
    if (slot !== this.ownSlot()) return false;
    const now = performance.now();
    const index = this.echoes.findIndex((e) => e.kind === kind && e.id === id && now - e.at < ECHO_MS);
    if (index < 0) return false;
    this.echoes.splice(index, 1);
    return true;
  }

  private showQuickChat(slot: number, id: string): void {
    const text = phraseText(id);
    const member = this.memberMap().get(slot);
    if (text === undefined || member === undefined) return; // an id of a newer manifest: drop
    this.setBubble(slot, { kind: "chat", text, sticker: null, key: this.feedKey });
    this.addFeed(member, text, null);
  }

  private showSticker(slot: number, id: string): void {
    const label = stickerLabel(id);
    const member = this.memberMap().get(slot);
    if (label === undefined || member === undefined) return;
    this.setBubble(slot, { kind: "sticker", text: label, sticker: id, key: this.feedKey });
    this.addFeed(member, `${label} sticker`, id);
  }

  private showEmote(slot: number, sku: string): void {
    const asset = itemAsset(sku);
    const member = this.memberMap().get(slot);
    if (asset === undefined || asset.slot !== "emote" || member === undefined) return;
    this.addFeed(member, `${asset.name} emote`, null);
    const renderer = this.renderer;
    if (renderer !== null && this.mode() === "3d" && !this.reducedMotion) {
      void renderer.playEmote(slot, asset.assetUrl).then((played) => {
        if (!played) this.setBubble(slot, { kind: "emote", text: asset.name, sticker: null, key: this.feedKey });
      });
    } else {
      // Reduced motion (or no 3D): the emote shows as a word, not a movement.
      this.setBubble(slot, { kind: "emote", text: asset.name, sticker: null, key: this.feedKey });
    }
  }

  private setBubble(slot: number, bubble: Bubble): void {
    this.bubbles.update((map) => new Map(map).set(slot, bubble));
    const old = this.bubbleTimers.get(slot);
    if (old !== undefined) clearTimeout(old);
    this.bubbleTimers.set(
      slot,
      setTimeout(() => this.clearBubble(slot), BUBBLE_MS[bubble.kind]),
    );
  }

  private clearBubble(slot: number): void {
    const timer = this.bubbleTimers.get(slot);
    if (timer !== undefined) clearTimeout(timer);
    this.bubbleTimers.delete(slot);
    if (!this.bubbles().has(slot)) return;
    this.bubbles.update((map) => {
      const next = new Map(map);
      next.delete(slot);
      return next;
    });
  }

  private addFeed(member: Member, text: string, sticker: string | null): void {
    const name = member.slot === this.ownSlot() ? "You" : displayName(member);
    const item = { key: ++this.feedKey, name, text, sticker };
    this.feed.update((items) => [...items, item].slice(-FEED_LIMIT));
  }

  // ------------------------------------------------------------------ tags

  /** Moves the name tags and bubbles each frame (direct style writes: no change detection at 60 Hz). */
  private placeTags(tags: readonly TagPosition[]): void {
    const layer = this.tagLayer()?.nativeElement;
    if (layer === undefined) return;
    const elements = layer.querySelectorAll<HTMLElement>(".tag");
    const bySlot = new Map(tags.map((t) => [t.slot, t]));
    for (const element of elements) {
      const tag = bySlot.get(Number(element.dataset["slot"]));
      if (tag === undefined || !tag.visible) {
        element.style.visibility = "hidden";
        continue;
      }
      element.style.visibility = "visible";
      element.style.transform = `translate(${Math.round(tag.x)}px, ${Math.round(tag.y)}px) translate(-50%, -100%)`;
    }
  }

  // ------------------------------------------------------------------ input

  protected keyDown(event: KeyboardEvent): void {
    if (event.key === "Escape" && this.panel() !== null) {
      this.panel.set(null);
      return;
    }
    const direction = directionOf(event.code);
    if (direction === undefined || event.ctrlKey || event.metaKey || event.altKey || this.mode() !== "3d") return;
    // An open panel keeps the arrow keys for its own controls.
    if (this.panel() !== null) return;
    event.preventDefault();
    if (event.repeat && this.held.has(direction)) return;
    this.held.add(direction);
    this.sender.set(this.toSpace(intentFromKeys(this.held)), performance.now());
  }

  protected keyUp(event: KeyboardEvent): void {
    const direction = directionOf(event.code);
    if (direction === undefined || !this.held.delete(direction)) return;
    this.sender.set(this.toSpace(intentFromKeys(this.held)), performance.now());
  }

  /** Window blur: keys and touches released outside the page never send keyup or pointerup, so stop. */
  protected releaseKeys(): void {
    this.haltInput();
  }

  /** Stops the avatar: no keys held, the stick back in the center, `Move(0, 0)` on the next send slot. */
  private haltInput(): void {
    this.held.clear();
    this.stickReset();
    this.sender.set(STOP, performance.now());
  }

  /** Sends the intent of the input that is down now (stick first, then keys). */
  private applyInput(): void {
    this.sender.set(this.toSpace(this.stickIntent ?? intentFromKeys(this.held)), performance.now());
  }

  /** Screen intent to space axes (the room camera looks the other way). */
  private toSpace(intent: Intent): Intent {
    return forView(intent, this.view);
  }

  /** Hidden tab: `Move(0, 0)`, no more input, and no rendering until it shows again (protocol §4). */
  protected visibility(): void {
    const now = performance.now();
    if (document.hidden) {
      this.held.clear();
      this.stickReset();
      this.sender.disable(now, this.client?.joined ?? false);
      this.renderer?.setRunning(false);
    } else {
      if (this.client?.joined) this.sender.enable();
      this.renderer?.setRunning(true);
    }
  }

  protected stickStart(event: PointerEvent): void {
    const pad = event.currentTarget as HTMLElement;
    const box = pad.getBoundingClientRect();
    this.stickCenter = { x: box.left + box.width / 2, y: box.top + box.height / 2 };
    this.stickPointer = event.pointerId;
    pad.setPointerCapture?.(event.pointerId);
    this.stickMove(event);
  }

  protected stickMove(event: PointerEvent): void {
    if (event.pointerId !== this.stickPointer) return;
    let x = event.clientX - this.stickCenter.x;
    let y = event.clientY - this.stickCenter.y;
    const length = Math.hypot(x, y);
    if (length > JOYSTICK_RADIUS) {
      x = (x / length) * JOYSTICK_RADIUS;
      y = (y / length) * JOYSTICK_RADIUS;
    }
    this.knob.set({ x, y });
    this.stickIntent = intentFromJoystick(x, y, JOYSTICK_RADIUS);
    this.sender.set(this.toSpace(this.stickIntent), performance.now());
  }

  protected stickEnd(event: PointerEvent): void {
    if (event.pointerId !== this.stickPointer) return;
    this.stickReset(); // first: releasing the capture fires `lostpointercapture`, which comes back here
    (event.currentTarget as HTMLElement | null)?.releasePointerCapture?.(event.pointerId);
    this.sender.set(this.toSpace(intentFromKeys(this.held)), performance.now());
  }

  private stickReset(): void {
    this.stickPointer = null;
    this.stickIntent = null;
    this.knob.set({ x: 0, y: 0 });
  }

  // ------------------------------------------------------------------ UI

  protected askSafety(kind: "report" | "block" | "remove", member: Member): void {
    this.safety.set({ kind, slot: member.slot, key: member.key });
  }

  protected blockText(member: Member): string {
    const name = this.name(member);
    switch (this.space) {
      case "room-host":
        return `Block ${name}? The player leaves your room, and you will not meet in the plaza or in rooms again. Open challenges and your friendship end. The player is not told.`;
      case "room-visit":
        return `Block ${name}? You leave this room, and you will not meet in the plaza or in rooms again. Open challenges and a friendship between you end. The player is not told.`;
      default:
        return `Block ${name}? You will not meet in the plaza again. Open challenges and a friendship between you end. The player is not told.`;
    }
  }

  protected async removeVisitor(member: Member): Promise<void> {
    await this.safetyCall(() => this.client!.removeVisitor(member.slot, member.key), `${this.name(member)} left your room.`);
  }

  /** The owner closes the room (visitors see "The room closed") and goes back to My Room. */
  protected closeRoom(): void {
    this.client?.closeRoom();
    void this.router.navigate(["/room"]);
  }

  protected async report(member: Member, reason: SpaceReportReason): Promise<void> {
    await this.safetyCall(() => this.client!.report(member.slot, member.key, reason), "Report sent. Thank you. A moderator will look at it.");
  }

  protected async block(member: Member): Promise<void> {
    const name = this.name(member);
    // The server moves this player to another instance (`moved`) before the call completes; a visitor leaves the room.
    const done = this.space === "plaza" ? `Blocked ${name}. You will not meet again in the plaza.` : `Blocked ${name}.`;
    if (await this.safetyCall(() => this.client!.block(member.slot, member.key), done)) {
      this.panel.set(null);
    }
  }

  private async safetyCall(call: () => Promise<void>, done: string): Promise<boolean> {
    if (this.client === null || this.safetyBusy()) return false;
    this.safetyBusy.set(true);
    try {
      await call();
      this.safety.set(null);
      this.showToast(done, false);
      return true;
    } catch (error: unknown) {
      if (this.status().kind !== "joined") return false; // the call ended with the room (a block in a room, a close)
      const text = SAFETY_ERROR_TEXT[safetyErrorCode(error) ?? "invalid_input"];
      this.showToast(this.view === "room" ? inRoom(text) : text);
      if (safetyErrorCode(error) === "not_found") this.safety.set(null);
      return false;
    } finally {
      if (!this.destroyed) this.safetyBusy.set(false);
    }
  }

  protected toggle(panel: Panel): void {
    this.panel.update((open) => (open === panel ? null : panel));
  }

  protected retry(): void {
    if (this.client === null) void this.openClient();
    else this.client.connect();
  }

  protected reload(): void {
    location.reload();
  }

  private showToast(text: string, error = true): void {
    this.toast.set(text);
    if (this.toastTimer !== null) clearTimeout(this.toastTimer);
    this.toastTimer = setTimeout(() => this.toast.set(null), error ? TOAST_MS : TOAST_MS / 2);
  }

  private later(fn: () => void, ms: number): void {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      if (!this.destroyed) fn();
    }, ms);
    this.timers.add(timer);
  }
}

function without<T>(set: ReadonlySet<T>, value: T): ReadonlySet<T> {
  if (!set.has(value)) return set;
  const next = new Set(set);
  next.delete(value);
  return next;
}

function spaceMode(value: unknown): SpaceMode {
  return value === "room-host" || value === "room-visit" ? value : "plaza";
}
