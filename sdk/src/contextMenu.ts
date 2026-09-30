/** Text fields keep the browser's menu, so paste and spelling suggestions still work there. */
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
