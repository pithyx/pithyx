/**
 * The one place for browser features that exist only in a secure context (HTTPS with a trusted certificate, or
 * localhost). Pithyx must work fully without one: over plain HTTP by IP address and with the self-signed certificate
 * of a fresh install. ESLint refuses these APIs everywhere else.
 */

/** Whether the page runs in a secure context; features that need one (passkeys, service worker) wait for it. */
export function isSecureContext(): boolean {
  return window.isSecureContext;
}

/** A random RFC 4122 version 4 id. `crypto.getRandomValues` exists in every context, `randomUUID` does not. */
export function randomId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Copies text to the clipboard and says whether it worked. The Clipboard API exists only in secure contexts; outside
 * them a temporary text field and `execCommand('copy')` do it. If both fail, the text of `fallback` is selected on
 * the page, so the user can copy it with the keyboard or the context menu, and the caller shows a hint.
 */
export async function copyText(text: string, fallback?: HTMLElement | null): Promise<boolean> {
  if (window.isSecureContext && 'clipboard' in navigator) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Denied or unavailable; try the fallback below.
    }
  }
  if (copyThroughTextField(text)) {
    return true;
  }
  if (fallback !== undefined && fallback !== null) {
    selectContents(fallback);
  }
  return false;
}

function copyThroughTextField(text: string): boolean {
  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.position = 'fixed';
  field.style.opacity = '0';
  const focused = document.activeElement;
  document.body.append(field);
  field.select();
  let copied: boolean;
  try {
    // eslint-disable-next-line @typescript-eslint/no-deprecated -- the only option outside secure contexts
    copied = document.execCommand('copy');
  } catch {
    copied = false;
  }
  field.remove();
  if (focused instanceof HTMLElement) {
    focused.focus();
  }
  return copied;
}

/** Selects the text of an element on the page, as a manual copy fallback. */
export function selectContents(element: HTMLElement): void {
  const selection = window.getSelection();
  if (selection === null) {
    return;
  }
  const range = document.createRange();
  range.selectNodeContents(element);
  selection.removeAllRanges();
  selection.addRange(range);
}
