/** The place of the Recent list in Files, saved like a share id; real share ids are numbers. */
export const RECENT_PLACE = 'recent';

/** A file the user opened recently, as the box lists it (#333). */
export interface RecentFile {
  /** The id of the share the file is in. */
  share: number;
  /** The file's path inside the share. */
  path: string;
  size: number;
  /** When the file last changed, UTC ISO 8601. */
  modified: string;
  /** When the user last opened it, UTC ISO 8601. */
  openedAt: string;
}

/**
 * The signed-in user's recent files, newest first, for the Pithyx menu and the Recent place in Files; `undefined`
 * while the box keeps no such list, which hides both. The box does not keep one yet (#333).
 */
export function useRecentFiles(): readonly RecentFile[] | undefined {
  return undefined;
}

/**
 * Tells the box that the user opened a file: a double click or "Open with" in Files, a file shortcut, an app's file
 * dialog. The box does not keep the list yet (#333), so nothing is sent.
 */
export const noteRecentFile: (share: number, path: string) => void = () => undefined;
