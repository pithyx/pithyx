/**
 * Text fields keep the browser's menu, so paste and spelling suggestions still work there.
 *
 * @param target - The target of a `contextmenu` event.
 * @returns Whether the target is inside an input, a text area, a select or an editable element.
 */
export function keepsBrowserMenu(target: EventTarget | null): boolean {
  return (
    target instanceof Element &&
    target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])') !==
      null
  );
}

/**
 * An app window is part of the desktop, so a right-click never opens the browser's own menu (Back, Reload, Inspect).
 * An app shows its own menu by listening for `contextmenu` itself.
 */
export function preventBrowserMenu(root: Document): void {
  root.addEventListener('contextmenu', (event) => {
    if (!keepsBrowserMenu(event.target)) {
      event.preventDefault();
    }
  });
}
