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

/**
 * The year text of the age gate as a number, or null when it is not 4 digits. The API checks the age
 * (spec §17 decision 1), so the page never tells which years it accepts.
 */
export function parseBirthYear(text: string): number | null {
  const trimmed = text.trim();
  return /^\d{4}$/.test(trimmed) ? Number(trimmed) : null;
}

let nextId = 0;

/**
 * Neutral age gate (spec §17 decision 1): one empty year field, no default, no hint of the accepted answer.
 * The year goes only to `createGuest`; the page keeps no copy of it.
 */
@Component({
  selector: "vybe-age-gate",
  template: `
    <form class="age-gate" novalidate (submit)="submit($event)">
      <label [for]="fieldId">Year of birth</label>
      <p class="muted" [id]="hintId">VYBE asks every new player for this. It does not keep the year.</p>
      <input
        #field
        [id]="fieldId"
        name="birthYear"
        type="text"
        inputmode="numeric"
        autocomplete="bday-year"
        maxlength="4"
        [attr.aria-describedby]="invalid() ? hintId + ' ' + errorId : hintId"
        [attr.aria-invalid]="invalid()"
        [value]="text()"
        (input)="edit($event)"
      />
      @if (invalid()) {
        <p class="error" [id]="errorId" role="alert">Enter the year with 4 digits.</p>
      }
      <div class="actions">
        <button type="submit" class="button" [disabled]="busy()">{{ busy() ? "Please wait…" : submitLabel() }}</button>
        <ng-content />
      </div>
    </form>
  `,
  styles: `
    .age-gate { display: grid; gap: 8px; max-width: 360px; }
    label { font-weight: 700; }
    p { margin: 0; }
    input { min-height: 44px; padding: 8px 12px; border-radius: 10px; border: 1px solid #2b3350; background: #141826; color: inherit; font: inherit; max-width: 10ch; }
    .actions { margin-top: 8px; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgeGateForm {
  readonly busy = input(false);
  readonly submitLabel = input("Continue");
  /** The entered year; the parent sends it to `createGuest`. */
  readonly submitted = output<number>();

  private readonly id = ++nextId;
  protected readonly fieldId = `age-gate-year-${this.id}`;
  protected readonly hintId = `age-gate-hint-${this.id}`;
  protected readonly errorId = `age-gate-error-${this.id}`;
  protected readonly text = signal("");
  protected readonly invalid = signal(false);
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>("field");

  focus(): void {
    this.field().nativeElement.focus();
  }

  protected edit(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
    this.invalid.set(false);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const year = parseBirthYear(this.text());
    if (year === null) {
      this.invalid.set(true);
      this.focus();
      return;
    }
    this.submitted.emit(year);
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
  /** The entered year, or null when the user cancelled. */
  readonly closed = output<number | null>();
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>("dialog");
  private readonly form = viewChild.required(AgeGateForm);

  constructor() {
    afterNextRender(() => {
      this.dialog().nativeElement.showModal();
      this.form().focus();
    });
  }

  protected close(event: Event | null, year: number | null): void {
    event?.preventDefault();
    this.dialog().nativeElement.close();
    this.closed.emit(year);
  }
}

/** Asks for the year of birth in a dialog before a flow creates a guest. */
@Injectable({ providedIn: "root" })
export class AgeGate {
  private readonly appRef = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);

  /** The entered year, or null when the user cancelled. */
  ask(): Promise<number | null> {
    return new Promise((resolve) => {
      const host = document.createElement("div");
      document.body.appendChild(host);
      const ref = createComponent(AgeGateDialog, { environmentInjector: this.injector, hostElement: host });
      this.appRef.attachView(ref.hostView);
      const subscription = ref.instance.closed.subscribe((year) => {
        subscription.unsubscribe();
        this.appRef.detachView(ref.hostView);
        ref.destroy();
        host.remove();
        resolve(year);
      });
    });
  }
}
