// One polite live region for the results of keyboard window actions ("Disks snapped left").
let element: HTMLElement | null = null;

export function registerAnnouncer(target: HTMLElement | null): void {
  element = target;
}

export function announce(text: string): void {
  if (element === null) {
    return;
  }
  // Clearing first makes screen readers repeat an identical message.
  element.textContent = '';
  const target = element;
  requestAnimationFrame(() => {
    target.textContent = text;
  });
}
