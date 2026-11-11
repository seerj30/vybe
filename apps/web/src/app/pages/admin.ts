import { ChangeDetectionStrategy, Component, inject } from "@angular/core";
import { RouterLink } from "@angular/router";
import { AdminAudit } from "../admin/audit-panel";
import { AdminCatalog } from "../admin/catalog-panel";
import { AdminFlags } from "../admin/flags-panel";
import { AdminReports } from "../admin/reports-panel";
import { AdminSuspensions } from "../admin/suspension-panel";
import { AdminWallet } from "../admin/wallet-panel";
import { AuthSession } from "../auth/auth-session";
import { SignInForm } from "../auth/sign-in-form";

/**
 * `/admin` (spec §3, §5 Admin, §15): staff sign in with Supabase; the role comes from
 * `app_metadata.vybe_role`. Moderators see reports, review flags and suspensions; administrators also see the catalog,
 * wallet adjustments and the audit log. The API checks the role on every call; this page only hides
 * what the role cannot use.
 */
@Component({
  selector: "vybe-admin",
  imports: [RouterLink, SignInForm, AdminReports, AdminFlags, AdminSuspensions, AdminCatalog, AdminWallet, AdminAudit],
  template: `
    <section class="page admin">
      <h1>VYBE admin</h1>
      @switch (auth.status()) {
        @case ("loading") {
          <p class="muted" role="status">Loading…</p>
        }
        @case ("signed-out") {
          <p>Staff accounts only. Sign in with your VYBE staff account.</p>
          <vybe-sign-in-form [allowSignUp]="false" />
        }
        @case ("signed-in") {
          @if (auth.user(); as user) {
            <p class="who">
              Signed in as {{ user.email }}
              @if (user.role) {
                <span class="role" data-testid="admin-role">{{ user.role === "administrator" ? "Administrator" : "Moderator" }}</span>
              }
              <button type="button" class="button secondary" (click)="auth.signOut()">Sign out</button>
            </p>
            @if (user.role === null) {
              <h2>Unauthorized</h2>
              <p class="error" role="alert">This account has no admin access. Ask an administrator for the moderator role.</p>
              <a class="button secondary" routerLink="/">Back to VYBE</a>
            } @else {
              <nav class="sections" aria-label="Admin sections">
                <a href="/admin#reports" (click)="go($event, 'reports')">Reports</a>
                <a href="/admin#flags" (click)="go($event, 'flags')">Review flags</a>
                <a href="/admin#suspensions" (click)="go($event, 'suspensions')">Suspensions</a>
                @if (user.role === "administrator") {
                  <a href="/admin#catalog" (click)="go($event, 'catalog')">Catalog</a>
                  <a href="/admin#wallet" (click)="go($event, 'wallet')">Wallet adjustment</a>
                  <a href="/admin#audit" (click)="go($event, 'audit')">Audit log</a>
                }
              </nav>
              <section id="reports" class="card" aria-labelledby="reports-title">
                <h2 id="reports-title">Reports</h2>
                <vybe-admin-reports />
              </section>
              <section id="flags" class="card" aria-labelledby="flags-title">
                <h2 id="flags-title">Review flags</h2>
                <vybe-admin-flags />
              </section>
              <section id="suspensions" class="card" aria-labelledby="suspensions-title">
                <h2 id="suspensions-title">Suspensions</h2>
                <vybe-admin-suspensions />
              </section>
              @if (user.role === "administrator") {
                <section id="catalog" class="card" aria-labelledby="catalog-title">
                  <h2 id="catalog-title">Catalog</h2>
                  <vybe-admin-catalog />
                </section>
                <section id="wallet" class="card" aria-labelledby="wallet-title">
                  <h2 id="wallet-title">Wallet adjustment</h2>
                  <vybe-admin-wallet />
                </section>
                <section id="audit" class="card" aria-labelledby="audit-title">
                  <h2 id="audit-title">Audit log</h2>
                  <vybe-admin-audit />
                </section>
              }
            }
          }
        }
        @default {
          <h2>Unauthorized</h2>
          <p class="error" role="alert">Sign-in is not available, so the admin pages cannot open.</p>
        }
      }
    </section>
  `,
  styles: `
    .admin { max-width: 1100px; display: grid; gap: var(--space-4); }
    .who { display: flex; flex-wrap: wrap; gap: var(--space-3); align-items: center; margin: 0; }
    .role { padding: 2px 10px; border-radius: var(--radius-pill); background: var(--color-secondary); font-weight: 700; }
    .sections { display: flex; flex-wrap: wrap; gap: var(--space-4); }
    .card h2 { margin-top: 0; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Admin {
  protected readonly auth = inject(AuthSession);

  constructor() {
    void this.auth.ready();
  }

  /** In-page links without a router navigation; focus moves to the section for keyboard users. */
  protected go(event: Event, id: string): void {
    event.preventDefault();
    const section = document.getElementById(id);
    section?.scrollIntoView({ block: "start" });
    section?.setAttribute("tabindex", "-1");
    section?.focus({ preventScroll: true });
  }
}
