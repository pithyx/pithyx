import type { PasskeyCreationOptions, PasskeyRequestOptions } from './webauthnJson';

/**
 * The one place for browser features that exist only in a secure context (HTTPS with a trusted certificate, or
 * localhost), and for opening other tabs. Pithyx must work fully without a secure context: over plain HTTP by IP
 * address and with the self-signed certificate of a fresh install. ESLint refuses these APIs everywhere else.
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

/**
 * Opens a URL in a new browser tab that has no reference back to the desktop (`noopener`) and sends no referrer. Only
 * for a user action: browsers block tabs opened without one.
 */
export function openInNewTab(url: string): void {
  window.open(url, '_blank', 'noopener,noreferrer');
}

/**
 * Whether this device trusts the box's certificate (D380): `trusted`, `untrusted` (HTTPS with a certificate the
 * browser warned about), or `unsupported` (plain HTTP, the development server, or a browser without service workers).
 * Browsers refuse service workers on a connection with a certificate error, so registering the box's worker is the
 * test, and once it passes the worker runs: trusting the box is what switches it on.
 */
export type DeviceTrust = 'trusted' | 'untrusted' | 'unsupported';

let trustCheck: Promise<DeviceTrust> | undefined;

/** The trust of this device, checked once per page; `recheck` asks again after the user installed the root. */
export function deviceTrust(recheck = false): Promise<DeviceTrust> {
  if (trustCheck === undefined || recheck) {
    trustCheck = checkTrust();
  }
  return trustCheck;
}

async function checkTrust(): Promise<DeviceTrust> {
  if (import.meta.env.DEV || location.protocol !== 'https:') {
    return 'unsupported';
  }
  if (!window.isSecureContext || !('serviceWorker' in navigator)) {
    return 'untrusted';
  }
  try {
    await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' });
    return 'trusted';
  } catch {
    return 'untrusted';
  }
}

/** Why passkeys cannot be used here, or `undefined` when they can. */
export type PasskeyBlocker = 'untrusted' | 'address' | 'browser';

/**
 * Passkeys need a device that trusts the box, a browser with WebAuthn and the box opened by name: a relying party is
 * a domain, never an address.
 */
export function passkeyBlocker(trust: DeviceTrust): PasskeyBlocker | undefined {
  const host = location.hostname;
  if (host.startsWith('[') || /^\d{1,3}(\.\d{1,3}){3}$/.test(host)) {
    return 'address';
  }
  if (trust !== 'trusted') {
    return 'untrusted';
  }
  return typeof window.PublicKeyCredential === 'function' ? undefined : 'browser';
}

/** Whether the browser offers passkeys in the user name field's autofill. */
export async function passkeyAutofillAvailable(): Promise<boolean> {
  try {
    return (
      typeof window.PublicKeyCredential === 'function' &&
      (await window.PublicKeyCredential.isConditionalMediationAvailable())
    );
  } catch {
    return false;
  }
}

/**
 * Creates a passkey and returns the authenticator's answer as JSON for the daemon; null when the user cancelled. The
 * JSON conversion loads with the first passkey, so it stays out of the sign-in bundle.
 */
export async function createPasskey(options: PasskeyCreationOptions): Promise<object | null> {
  const json = await import('./webauthnJson');
  const credential = await navigator.credentials.create({
    publicKey: json.creationOptions(options),
  });
  return credential instanceof window.PublicKeyCredential
    ? json.registrationJson(credential)
    : null;
}

/**
 * Asks for a passkey of the box and returns the answer as JSON for the daemon; null when the user cancelled. With
 * `autofill`, the browser offers the passkeys in the user name field instead of a dialog, until `signal` aborts.
 */
export async function getPasskey(
  options: PasskeyRequestOptions,
  { autofill = false, signal }: { autofill?: boolean; signal?: AbortSignal } = {},
): Promise<object | null> {
  const json = await import('./webauthnJson');
  let credential: Credential | null;
  try {
    credential = await navigator.credentials.get({
      publicKey: json.requestOptions(options),
      ...(autofill ? { mediation: 'conditional' as CredentialMediationRequirement } : {}),
      ...(signal === undefined ? {} : { signal }),
    });
  } catch (error) {
    // Cancelled by the user, timed out or aborted: nothing to report.
    if (
      error instanceof DOMException &&
      (error.name === 'NotAllowedError' || error.name === 'AbortError')
    ) {
      return null;
    }
    throw error;
  }
  return credential instanceof window.PublicKeyCredential ? json.assertionJson(credential) : null;
}
