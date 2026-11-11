/** Spec §5 Admin: every write needs a reason of 3–500 characters (contract `AdminReason`). */
export const REASON_MIN = 3;
export const REASON_MAX = 500;

export function validReason(reason: string): boolean {
  const length = reason.trim().length;
  return length >= REASON_MIN && length <= REASON_MAX;
}

export function inputText(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;
}

/** Shared form styles of the admin panels. */
export const ADMIN_FORM_STYLES = `
  form { display: grid; gap: var(--space-3); max-width: 560px; }
  label { display: grid; gap: var(--space-1); font-weight: 600; }
  textarea { min-height: 72px; }
  .hint { font-weight: 400; font-size: var(--text-sm); color: var(--color-text-muted); }
`;
