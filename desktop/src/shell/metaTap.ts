/** A key event as far as the tap detection needs it. */
export interface TapKey {
  type: 'keydown' | 'keyup';
  key: string;
  repeat?: boolean;
}

const META = new Set(['Meta', 'OS']);

/**
 * Whether the Meta key (the Windows key, Command on a Mac) was pressed and released alone, like the Windows key opens
 * Start. Any other key in between (Meta+Shift+S) makes it a shortcut, not a tap. Returns the new state and whether this
 * event completes a tap.
 */
export function metaTap(alone: boolean, event: TapKey): { alone: boolean; tap: boolean } {
  if (!META.has(event.key)) {
    return { alone: false, tap: false };
  }
  if (event.type === 'keydown') {
    return { alone: event.repeat === true ? alone : true, tap: false };
  }
  return { alone: false, tap: alone };
}

/** Calls `onTap` on every Meta tap on the page; returns the function that stops listening. */
export function listenForMetaTap(onTap: () => void): () => void {
  let alone = false;
  const onKey = (event: KeyboardEvent) => {
    const next = metaTap(alone, {
      type: event.type as TapKey['type'],
      key: event.key,
      repeat: event.repeat,
    });
    alone = next.alone;
    if (next.tap) {
      onTap();
    }
  };
  // A click while Meta is down (Meta+click) or leaving the page is not a tap either.
  const reset = () => {
    alone = false;
  };
  document.addEventListener('keydown', onKey, true);
  document.addEventListener('keyup', onKey, true);
  document.addEventListener('pointerdown', reset, true);
  window.addEventListener('blur', reset);
  return () => {
    document.removeEventListener('keydown', onKey, true);
    document.removeEventListener('keyup', onKey, true);
    document.removeEventListener('pointerdown', reset, true);
    window.removeEventListener('blur', reset);
  };
}
