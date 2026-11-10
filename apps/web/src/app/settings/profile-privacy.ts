import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { errorMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";

type ProfileVisibility = Schemas["ProfileVisibility"];

const OPTIONS: readonly { value: ProfileVisibility; label: string; help: string }[] = [
  { value: "players", label: "Signed-in players", help: "Players who are signed in to VYBE, also as guests, can see it and challenge you from it." },
  { value: "private", label: "Only me (private)", help: "Nobody else can see your profile or send you a challenge from it. Challenge links still work." },
];

/** `/settings` privacy (spec §17 decision 6): who can see the own profile. Search engines never index profiles. */
@Component({
  selector: "vybe-profile-privacy",
  template: `
    <form (submit)="save($event)">
      <fieldset>
        <legend>Who can see your profile?</legend>
        @for (option of options; track option.value) {
          <label class="choice">
            <input
              type="radio"
              name="profileVisibility"
              [value]="option.value"
              [checked]="choice() === option.value"
              [attr.aria-describedby]="'visibility-help-' + option.value"
              (change)="choice.set(option.value)"
            />
            <span>
              <strong>{{ option.label }}</strong>
              <span class="muted" [id]="'visibility-help-' + option.value">{{ option.help }}</span>
            </span>
          </label>
        }
      </fieldset>
      <button type="submit" class="button" [disabled]="busy() || choice() === current()">Save</button>
    </form>
    <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>
  `,
  styles: `
    fieldset { border: 0; padding: 0; margin: 0 0 12px; display: grid; gap: 8px; }
    legend { font-weight: 600; margin-bottom: 8px; }
    .choice { display: flex; gap: 10px; align-items: flex-start; min-height: 44px; }
    .choice input { margin-top: 5px; }
    .choice span { display: grid; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfilePrivacy {
  /** The saved setting (`GET /me`). */
  readonly current = input.required<ProfileVisibility>();
  private readonly api = inject(AccountApi);
  private readonly player = inject(PlayerStore);
  protected readonly options = OPTIONS;
  protected readonly choice = signal<ProfileVisibility>("players");
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  constructor() {
    effect(() => {
      const current = this.current();
      untracked(() => this.choice.set(current));
    });
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    this.busy.set(true);
    this.message.set(null);
    try {
      const saved = await this.api.updatePrivacy({ profileVisibility: this.choice() });
      this.message.set({ text: saved.profileVisibility === "private" ? "Your profile is now private." : "Signed-in players can now see your profile.", error: false });
      await this.player.refresh().catch(() => null);
    } catch (error: unknown) {
      this.message.set({ text: errorMessage(error), error: true });
    }
    this.busy.set(false);
  }
}
