/**
 * Copies text to the clipboard. The Clipboard API only exists in secure contexts; the box starts with an untrusted
 * certificate and development runs on plain HTTP, so there is a fallback through a temporary text field.
 */
export async function copyText(text: string): Promise<void> {
  if (window.isSecureContext && 'clipboard' in navigator) {
    await navigator.clipboard.writeText(text);
    return;
  }
  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.position = 'fixed';
  field.style.opacity = '0';
  document.body.append(field);
  field.select();
  // eslint-disable-next-line @typescript-eslint/no-deprecated -- the only option outside secure contexts
  document.execCommand('copy');
  field.remove();
}
