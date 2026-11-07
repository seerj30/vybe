import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { Router, RouterLink } from "@angular/router";
import { AccountLinkPanel } from "../auth/account-link-panel";
import { AuthSession } from "../auth/auth-session";
import { PlayerStore } from "../player/player-store";
import { AccountDeletion, DELETION_REQUEST_KEY } from "../settings/account-deletion";
import { BlockedPlayers } from "../settings/blocked-players";
import { DataExportPanel } from "../settings/data-export";

/** `/settings` (spec §3): account state and linking, blocked players, data export, deletion. */
@Component({
  selector: "vybe-settings",
  imports: [DatePipe, RouterLink, AccountLinkPanel, BlockedPlayers, DataExportPanel, AccountDeletion],
  template: `
    <section class="page settings">
      <h1>Settings</h1>
      @if (player.me(); as me) {
        <section class="card" aria-labelledby="account-title">
          <h2 id="account-title">Account</h2>
          <dl class="facts">
            <div><dt>Player name</dt><dd data-testid="account-handle">{{ me.handle }}</dd></div>
            <div><dt>Type</dt><dd data-testid="account-kind">{{ me.kind === "registered" ? "Linked account" : "Guest" }}</dd></div>
            <div>
              <dt>Status</dt>
              <dd data-testid="account-status">
                @switch (me.status) {
                  @case ("active") { Active }
                  @case ("suspended") {
                    Suspended{{ me.suspendedUntil ? " until " + (me.suspendedUntil | date: "short") : "" }}
                  }
                  @case ("deleting") { Deletion in progress }
                }
              </dd>
            </div>
            @if (me.kind === "registered" && auth.user(); as user) {
              <div><dt>Email</dt><dd>{{ user.email }}</dd></div>
            }
          </dl>
          @if (me.status === "suspended") {
            <p class="notice">While your account is suspended, you cannot buy items or create challenges.</p>
          }
          @if (me.status === "deleting") {
            <p class="notice">
              VYBE is deleting your account.
              @if (deletionRequestId(); as id) {
                <a [routerLink]="['/deletion', id]">See the deletion status</a>
              }
            </p>
          }
          @if (signedInAs(); as handle) {
            <p class="notice" role="status">You are signed in as {{ handle }}.</p>
          }
          @if (me.kind === "guest") {
            <h3>Link an account</h3>
            <p class="muted">
              A guest lives only in this browser. Link an account to keep your player, coins and items. Nothing changes in your
              progress.
            </p>
            <vybe-account-link (done)="signedInAs.set($event.signedIn ? $event.me.handle : null)" />
          } @else {
            <div class="actions">
              <button type="button" class="button secondary" (click)="signOut()">Sign out</button>
            </div>
          }
        </section>

        <section class="card" aria-labelledby="blocks-title">
          <h2 id="blocks-title">Blocked players</h2>
          <vybe-blocked-players />
        </section>

        <section class="card" aria-labelledby="export-title">
          <h2 id="export-title">Your data</h2>
          <vybe-data-export [playerId]="me.id" />
        </section>

        @if (me.status !== "deleting") {
          <section class="card" aria-labelledby="delete-title">
            <h2 id="delete-title">Delete account</h2>
            <vybe-account-deletion [registered]="me.kind === 'registered'" />
          </section>
        }
      }
    </section>
  `,
  styles: `
    .settings { display: grid; gap: 16px; }
    .card h2 { margin-top: 0; }
    .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin: 0 0 12px; }
    .facts dt { font-size: 12px; text-transform: uppercase; color: #aab1c8; }
    .facts dd { margin: 0; font-weight: 700; overflow-wrap: anywhere; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Settings {
  protected readonly player = inject(PlayerStore);
  protected readonly auth = inject(AuthSession);
  private readonly router = inject(Router);
  protected readonly signedInAs = signal<string | null>(null);

  constructor() {
    void this.player.refresh().catch(() => undefined);
  }

  protected deletionRequestId(): string | null {
    try {
      return localStorage.getItem(DELETION_REQUEST_KEY);
    } catch {
      return null;
    }
  }

  protected async signOut(): Promise<void> {
    await this.auth.signOut();
    await this.player.refresh().catch(() => null);
    await this.router.navigateByUrl("/");
  }
}
