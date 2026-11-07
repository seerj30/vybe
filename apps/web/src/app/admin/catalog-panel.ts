import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { ADMIN_FORM_STYLES, inputText, validReason } from "./admin-form";

interface KnownItem {
  id: string;
  name: string;
  isActive: boolean;
}

/**
 * Catalog item activation (administrator; spec §5 `PATCH /admin/catalog/items/{id}`). The shop lists only
 * active items, so an inactive item is found by its id (the audit log has it too).
 */
@Component({
  selector: "vybe-admin-catalog",
  template: `
    <form (submit)="$event.preventDefault(); save()" aria-label="Catalog item">
      <label>
        Item id
        <input type="text" name="itemId" list="admin-catalog-items" autocomplete="off" [value]="itemId()" (input)="itemId.set(inputText($event).trim())" />
        <datalist id="admin-catalog-items">
          @for (item of items(); track item.id) {
            <option [value]="item.id">{{ item.name }}{{ item.isActive ? "" : " (inactive)" }}</option>
          }
        </datalist>
      </label>
      <fieldset>
        <legend>State</legend>
        <label class="radio"><input type="radio" name="active" [checked]="active() === true" (change)="active.set(true)" /> Active (in the shop)</label>
        <label class="radio"><input type="radio" name="active" [checked]="active() === false" (change)="active.set(false)" /> Inactive (hidden, cannot be bought or worn)</label>
      </fieldset>
      <label>
        Reason
        <input type="text" name="reason" minlength="3" maxlength="500" [value]="reason()" (input)="reason.set(inputText($event))" />
      </label>
      <div class="actions">
        <button type="submit" class="button" [disabled]="busy() || itemId() === '' || active() === null || !validReason(reason())">Save item</button>
      </div>
    </form>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (notice(); as notice) {
      <p class="notice" role="status">{{ notice }}</p>
    }
    @if (items().length > 0) {
      <table>
        <caption>Known items</caption>
        <thead><tr><th scope="col">Name</th><th scope="col">State</th><th scope="col">Id</th></tr></thead>
        <tbody>
          @for (item of items(); track item.id) {
            <tr>
              <td>{{ item.name }}</td>
              <td>{{ item.isActive ? "Active" : "Inactive" }}</td>
              <td><button type="button" class="link" (click)="pick(item.id)" [attr.aria-label]="'Edit ' + item.name"><code>{{ item.id }}</code></button></td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: [
    ADMIN_FORM_STYLES,
    `
      fieldset { border: 1px solid #2b3350; border-radius: 10px; display: grid; gap: 8px; }
      .radio { display: flex; gap: 8px; align-items: center; font-weight: 400; }
      .radio input { min-height: auto; }
      table { width: 100%; border-collapse: collapse; margin-top: 16px; font-size: 14px; }
      caption { text-align: left; font-weight: 700; padding-bottom: 8px; }
      th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid #2b3350; }
      .link { background: none; border: 0; color: #8fb3ff; cursor: pointer; padding: 0; font: inherit; text-align: left; overflow-wrap: anywhere; }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminCatalog {
  private readonly api = inject(AccountApi);
  protected readonly inputText = inputText;
  protected readonly validReason = validReason;
  protected readonly items = signal<KnownItem[]>([]);
  protected readonly itemId = signal("");
  /** No default: the administrator picks the new state for every save, so a save never deactivates by accident. */
  protected readonly active = signal<boolean | null>(null);
  protected readonly reason = signal("");
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  constructor() {
    void this.loadItems();
  }

  protected pick(itemId: string): void {
    this.itemId.set(itemId);
    this.active.set(null);
  }

  protected async save(): Promise<void> {
    const isActive = this.active();
    if (isActive === null) return;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const item = await this.api.adminUpdateCatalogItem(this.itemId(), { isActive, reason: this.reason().trim() });
      this.remember(item);
      this.reason.set("");
      this.active.set(null);
      this.notice.set(`${item.name} is now ${item.isActive ? "active" : "inactive"}.`);
    } catch (error: unknown) {
      this.error.set(isApiError(error, "not_found") ? "VYBE has no catalog item with this id." : errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadItems(): Promise<void> {
    try {
      let cursor: string | undefined;
      do {
        const page: Schemas["CatalogPage"] = await this.api.adminCatalogItems(cursor);
        for (const item of page.items) this.remember({ id: item.id, sku: item.sku, name: item.name, isActive: true });
        cursor = page.nextCursor ?? undefined;
      } while (cursor !== undefined);
    } catch (error: unknown) {
      this.error.set(`The item list did not load. ${errorMessage(error)}`);
    }
  }

  private remember(item: Schemas["AdminCatalogItem"]): void {
    const known = { id: item.id, name: item.name, isActive: item.isActive };
    this.items.update((items) => (items.some((i) => i.id === item.id) ? items.map((i) => (i.id === item.id ? known : i)) : [...items, known]));
  }
}
