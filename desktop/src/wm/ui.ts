import { create } from 'zustand';
import type { Placement, Rect } from './types';

/** Window manager state that is never saved: the open window menu and the keyboard move or size mode. */
interface WindowUi {
  menu: string | null;
  /** `rect` and `placement` are what Escape restores. */
  mode: { id: string; kind: 'move' | 'size'; rect: Rect; placement: Placement } | null;
}

export const useWindowUi = create<WindowUi>(() => ({ menu: null, mode: null }));
