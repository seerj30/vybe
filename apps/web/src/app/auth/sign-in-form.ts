import { ChangeDetectionStrategy, Component, inject, input, output, signal } from "@angular/core";
import { AccountLink } from "./account-link";
import { AuthSession } from "./auth-session";

type Mode = "sign-in" | "sign-up";

/** Email and password sign-in with Supabase Auth; optionally "create an account". */
@Component({
  selector: "vybe-sign-in-form",
  template: `
    <form class="sign-in" (submit)="$event.preventDefault(); submit()" [attr.aria-label]="mode() === 'sign-in' ? 'Sign in' : 'Create an account'">
      <label>
        Email
        <input type="email" name="email" autocomplete="email" required [value]="email()" (input)="email.set(text($event))" />
      </label>
      <label>
        Password
        <input
          type="password"
          name="password"
          required
          minlength="6"
          [attr.autocomplete]="mode() === 'sign-in' ? 'current-password' : 'new-password'"
          [value]="password()"
          (input)="password.set(text($event))"
        />
      </label>
      @if (error(); as error) {
        <p class="error" role="alert">{{ error }}</p>
      }
      @if (info(); as info) {
        <p class="notice" role="status">{{ info }}</p>
      }
      <div class="actions">
        <button type="submit" class="button" [disabled]="busy()">
          {{ busy() ? "Please wait…" : mode() === "sign-in" ? "Sign in" : "Create account" }}
        </button>
        @if (allowSignUp()) {
          <button type="button" class="button secondary" (click)="toggle()" [disabled]="busy()">
            {{ mode() === "sign-in" ? "Create an account instead" : "I already have an account" }}
          </button>
        }
      </div>
    </form>
  `,
  styles: `
    .sign-in { display: grid; gap: var(--space-3); max-width: 420px; }
    label { display: grid; gap: var(--space-1); font-weight: 600; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInForm {
  private readonly auth = inject(AuthSession);
  private readonly link = inject(AccountLink);

  readonly allowSignUp = input(true);
  readonly signedIn = output();

  protected readonly mode = signal<Mode>("sign-in");
  protected readonly email = signal("");
  protected readonly password = signal("");
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly info = signal<string | null>(null);

  protected toggle(): void {
    this.mode.update((mode) => (mode === "sign-in" ? "sign-up" : "sign-in"));
    this.error.set(null);
    this.info.set(null);
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.info.set(null);
    try {
      if (this.mode() === "sign-in") {
        const result = await this.auth.signIn(this.email(), this.password());
        if (result.ok) {
          this.password.set("");
          this.signedIn.emit();
        } else {
          this.error.set(result.message);
        }
        return;
      }
      // Age gate before the Auth user exists (spec §17 decision 1): a browser without a guest gets one first.
      const ready = await this.link.prepareSignUp();
      if (!ready.ok) {
        if (ready.message !== null) this.error.set(ready.message);
        return;
      }
      const result = await this.auth.signUp(this.email(), this.password());
      if (result.kind === "error") {
        this.error.set(result.message);
      } else if (result.kind === "confirm-email") {
        this.password.set("");
        this.info.set("Check your email and open the confirmation link. VYBE then links your progress to the account.");
      } else {
        this.password.set("");
        this.signedIn.emit();
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
