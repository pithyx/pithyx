import type { FileItems } from '@vyxis/sdk';
import { create } from 'zustand';

/** Entries copied or cut in one Files window, ready to paste in any other. */
export interface FilesClipboard {
  mode: 'copy' | 'cut';
  share: string;
  folder: string;
  items: FileItems;
  count: number;
}

export const useFilesClipboard = create<{ current: FilesClipboard | null }>(() => ({
  current: null,
}));

export function setClipboard(current: FilesClipboard | null): void {
  useFilesClipboard.setState({ current });
}
