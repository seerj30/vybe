import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { EMPTY_OUTFIT, type Outfit, outfitFromEquipped } from "../avatar/avatar-look";
import { AvatarViewport } from "../avatar-viewport/avatar-viewport";
import { type AvatarPresets, DEFAULT_PRESETS, PRESET_GROUPS, type PresetKey, pickPresets, samePresets } from "../avatar/presets";

type Avatar = Schemas["Avatar"];
type Status = { kind: "loading" } | { kind: "load_failed"; message: string } | { kind: "editing" } | { kind: "saving" };

/**
 * `/avatar/create` (spec §3): pick presets with a live 2D preview and save them. A failed save keeps
 * the local choices for a retry and never shows success (spec §2 J1).
 */
@Component({
  selector: "vybe-avatar-creator",
  imports: [AvatarViewport, RouterLink],
  template: `
    <section class="page">
      <h1>{{ welcome() ? "Create your avatar" : "Edit your avatar" }}</h1>
      @switch (status().kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading your avatar…</p>
        }
        @case ("load_failed") {
          <p class="error" role="alert">{{ loadError() }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @default {
          <form class="creator" (submit)="$event.preventDefault(); save()">
            <div class="preview card">
              <vybe-avatar-viewport [presets]="choice()" [outfit]="outfit()" />
            </div>
            <div class="groups">
              @for (group of groups; track group.key) {
                <fieldset>
                  <legend>{{ group.legend }}</legend>
                  @for (option of group.options; track option.id) {
                    <label class="option">
                      <input
                        type="radio"
                        [name]="group.key"
                        [value]="option.id"
                        [checked]="choice()[group.key] === option.id"
                        (change)="pick(group.key, option.id)"
                      />
                      <span>{{ option.label }}</span>
                    </label>
                  }
                </fieldset>
              }
            </div>
            <div class="footer">
              <p aria-live="polite" [class.error]="saveError() !== null" [class.notice]="saved()">
                @if (saveError(); as error) {
                  {{ error }}
                } @else if (saved()) {
                  Avatar saved.
                } @else if (dirty()) {
                  You have unsaved changes.
                }
              </p>
              <div class="actions">
                <button type="submit" class="button" [disabled]="status().kind === 'saving' || !canSave()">
                  {{ status().kind === "saving" ? "Saving…" : "Save avatar" }}
                </button>
                @if (saved() && !dirty()) {
                  <a class="button secondary" routerLink="/hub">{{ welcome() ? "Continue to the hub" : "Back to the hub" }}</a>
                }
              </div>
            </div>
          </form>
        }
      }
    </section>
  `,
  styles: `
    .creator { display: grid; gap: var(--space-4); }
    .preview { display: flex; flex-direction: column; align-items: center; }
    .preview vybe-avatar-viewport { max-width: 240px; }
    .groups { display: grid; gap: var(--space-3); }
    fieldset { border: 1px solid var(--color-border); border-radius: var(--radius-lg); padding: 8px 12px 12px; margin: 0; }
    legend { font-weight: 700; padding: 0 4px; }
    .option { display: inline-flex; align-items: center; gap: 6px; min-height: var(--control-height); margin-right: 14px; cursor: pointer; }
    .option input { width: 20px; height: 20px; accent-color: var(--color-accent); }
    .footer p:empty { display: none; }
    @media (min-width: 768px) {
      .creator { grid-template-columns: 240px 1fr; align-items: start; }
      .preview { position: sticky; top: 24px; }
      .footer { grid-column: 2; }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AvatarCreator {
  /** `?welcome=1` during first-run onboarding. */
  readonly welcomeParam = input<string | undefined>(undefined, { alias: "welcome" });

  private readonly api = inject(API);
  protected readonly groups = PRESET_GROUPS;
  protected readonly status = signal<Status>({ kind: "loading" });
  protected readonly server = signal<Avatar | null>(null);
  protected readonly choice = signal<AvatarPresets>(DEFAULT_PRESETS);
  protected readonly saveError = signal<string | null>(null);
  protected readonly saved = signal(false);
  /** What the avatar wears (read-only here): the preview shows the presets with the current items. */
  protected readonly outfit = signal<Outfit>(EMPTY_OUTFIT);

  protected readonly welcome = computed(() => this.welcomeParam() === "1");
  protected readonly dirty = computed(() => {
    const server = this.server();
    return server === null || !samePresets(pickPresets(server), this.choice());
  });
  /** On first run the defaults already are a valid avatar, so saving them unchanged is allowed once. */
  protected readonly canSave = computed(() => this.dirty() || (this.welcome() && !this.saved()));
  protected readonly loadError = computed(() => {
    const status = this.status();
    return status.kind === "load_failed" ? status.message : "";
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.status.set({ kind: "loading" });
    try {
      const [avatar, inventory] = await Promise.all([this.api.getAvatar(), this.api.getInventory().catch(() => null)]);
      this.server.set(avatar);
      if (inventory !== null) this.outfit.set(outfitFromEquipped(avatar.equipped, inventory.items));
      this.choice.set(pickPresets(avatar));
      this.saved.set(false);
      this.status.set({ kind: "editing" });
    } catch (error: unknown) {
      this.status.set({ kind: "load_failed", message: errorMessage(error) });
    }
  }

  protected pick(key: PresetKey, id: string): void {
    this.choice.update((choice) => ({ ...choice, [key]: id }));
    this.saved.set(false);
    this.saveError.set(null);
  }

  protected async save(): Promise<void> {
    const server = this.server();
    if (server === null) return;
    this.status.set({ kind: "saving" });
    this.saveError.set(null);
    try {
      const updated = await this.api.updateAvatar({ ...this.choice(), expectedRevision: server.revision });
      this.server.set(updated.avatar);
      this.saved.set(true);
    } catch (error: unknown) {
      this.saveError.set(errorMessage(error));
      if (isApiError(error, "revision_conflict")) await this.refreshRevision();
    }
    this.status.set({ kind: "editing" });
  }

  /** Takes the newer server revision but keeps the player's unsaved choices for the next save. */
  private async refreshRevision(): Promise<void> {
    try {
      this.server.set(await this.api.getAvatar());
    } catch {
      // The next save gets the conflict again and retries the refresh.
    }
  }
}
