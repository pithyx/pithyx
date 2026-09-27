import { create } from 'zustand';
import type { Rect } from './types';

/** Window manager state that is never saved: the open window menu and the keyboard move or size mode. */
interface WindowUi {
  menu: string | null;
  mode: { id: string; kind: 'move' | 'size'; start: Rect } | null;
}

export const useWindowUi = create<WindowUi>(() => ({ menu: null, mode: null }));
