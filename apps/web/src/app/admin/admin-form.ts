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
  form { display: grid; gap: 12px; max-width: 560px; }
  label { display: grid; gap: 4px; font-weight: 600; }
  input, select, textarea { min-height: 44px; padding: 8px 12px; border-radius: 10px; border: 1px solid #3a4468; background: #101421; color: inherit; font: inherit; }
  textarea { min-height: 72px; resize: vertical; }
  .hint { font-weight: 400; font-size: 14px; color: #aab1c8; }
`;
