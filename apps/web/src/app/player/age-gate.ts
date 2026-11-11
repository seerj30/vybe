import {
  afterNextRender,
  ApplicationRef,
  ChangeDetectionStrategy,
  Component,
  createComponent,
  type ElementRef,
  EnvironmentInjector,
  Injectable,
  inject,
  input,
  output,
  signal,
  viewChild,
} from "@angular/core";

import type { Schemas } from "@vybe/contracts";

/** The age-gate answer, as `createGuest` takes it. Only the API sees it; the page keeps no copy. */
export type BirthDate = Schemas["GuestCreateRequest"];

const MONTHS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
] as const;

/**
 * The month choice ("1" to "12") and the year text of the age gate, or null when one is missing or the year is
 * not 4 digits. The API checks the age (spec §17 decision 1), so the page never tells which dates it accepts.
 */
export function parseBirthDate(monthText: string, yearText: string): BirthDate | null {
  const year = yearText.trim();
  const month = Number(monthText);
  if (!/^\d{4}$/.test(year) || !Number.isInteger(month) || month < 1 || month > 12) return null;
  return { birthYear: Number(year), birthMonth: month };
}

let nextId = 0;

/**
 * Neutral age gate (spec §17 decision 1): an empty month list and an empty year field, no default, no hint of
 * the accepted answer. The answer goes only to `createGuest`.
 */
@Component({
  selector: "vybe-age-gate",
  template: `
    <form class="age-gate" novalidate (submit)="submit($event)">
      <fieldset [attr.aria-describedby]="invalid() ? hintId + ' ' + errorId : hintId">
        <legend>Date of birth</legend>
        <p class="muted" [id]="hintId">VYBE asks every new player for this. It does not keep your answer.</p>
        <div class="fields">
          <label [for]="monthId">
            Month
            <select #month [id]="monthId" name="birthMonth" autocomplete="bday-month" [attr.aria-invalid]="invalid()" (change)="edit()">
              <option value="" selected>Choose…</option>
              @for (name of months; track $index) {
                <option [value]="$index + 1">{{ name }}</option>
              }
            </select>
          </label>
          <label [for]="yearId">
            Year
            <input
              #year
              [id]="yearId"
              name="birthYear"
              type="text"
              inputmode="numeric"
              autocomplete="bday-year"
              maxlength="4"
              [attr.aria-invalid]="invalid()"
              (input)="edit()"
            />
          </label>
        </div>
      </fieldset>
      @if (invalid()) {
        <p class="error" [id]="errorId" role="alert">Choose the month and enter the year with 4 digits.</p>
      }
      <div class="actions">
        <button type="submit" class="button" [disabled]="busy()">{{ busy() ? "Please wait…" : submitLabel() }}</button>
        <ng-content />
      </div>
    </form>
  `,
  styles: `
    .age-gate { display: grid; gap: var(--space-2); max-width: 420px; }
    fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: var(--space-2); }
    legend { font-weight: 700; padding: 0; margin-bottom: 4px; }
    p { margin: 0; }
    .fields { display: flex; flex-wrap: wrap; gap: var(--space-3); }
    label { display: grid; gap: var(--space-1); font-weight: 600; }
    input { width: 9ch; }
    select { min-width: 160px; }
    .actions { margin-top: 8px; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgeGateForm {
  readonly busy = input(false);
  readonly submitLabel = input("Continue");
  /** The answer; the parent sends it to `createGuest`. */
  readonly submitted = output<BirthDate>();

  private readonly id = ++nextId;
  protected readonly months = MONTHS;
  protected readonly monthId = `age-gate-month-${this.id}`;
  protected readonly yearId = `age-gate-year-${this.id}`;
  protected readonly hintId = `age-gate-hint-${this.id}`;
  protected readonly errorId = `age-gate-error-${this.id}`;
  protected readonly invalid = signal(false);
  private readonly month = viewChild.required<ElementRef<HTMLSelectElement>>("month");
  private readonly year = viewChild.required<ElementRef<HTMLInputElement>>("year");

  focus(): void {
    this.month().nativeElement.focus();
  }

  protected edit(): void {
    this.invalid.set(false);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const answer = parseBirthDate(this.month().nativeElement.value, this.year().nativeElement.value);
    if (answer === null) {
      this.invalid.set(true);
      this.focus();
      return;
    }
    this.submitted.emit(answer);
  }
}

/** The age gate in a modal dialog, for flows that create a guest outside `/play` (invite, sign-in). */
@Component({
  selector: "vybe-age-gate-dialog",
  imports: [AgeGateForm],
  template: `
    <dialog #dialog class="card" aria-labelledby="age-gate-dialog-title" (cancel)="close($event, null)">
      <h2 id="age-gate-dialog-title">Before you play</h2>
      <vybe-age-gate submitLabel="Continue" (submitted)="close(null, $event)">
        <button type="button" class="button secondary" (click)="close(null, null)">Cancel</button>
      </vybe-age-gate>
    </dialog>
  `,
  styles: `
    dialog { color: inherit; max-width: min(420px, calc(100vw - 32px)); }
    dialog::backdrop { background: rgb(0 0 0 / 60%); }
    h2 { margin-top: 0; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgeGateDialog {
  /** The answer, or null when the user cancelled. */
  readonly closed = output<BirthDate | null>();
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>("dialog");
  private readonly form = viewChild.required(AgeGateForm);

  constructor() {
    afterNextRender(() => {
      this.dialog().nativeElement.showModal();
      this.form().focus();
    });
  }

  protected close(event: Event | null, answer: BirthDate | null): void {
    event?.preventDefault();
    this.dialog().nativeElement.close();
    this.closed.emit(answer);
  }
}

/** Asks for the date of birth in a dialog before a flow creates a guest. */
@Injectable({ providedIn: "root" })
export class AgeGate {
  private readonly appRef = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);

  /** The answer, or null when the user cancelled. */
  ask(): Promise<BirthDate | null> {
    return new Promise((resolve) => {
      const host = document.createElement("div");
      document.body.appendChild(host);
      const ref = createComponent(AgeGateDialog, { environmentInjector: this.injector, hostElement: host });
      this.appRef.attachView(ref.hostView);
      const subscription = ref.instance.closed.subscribe((answer) => {
        subscription.unsubscribe();
        this.appRef.detachView(ref.hostView);
        ref.destroy();
        host.remove();
        resolve(answer);
      });
    });
  }
}
