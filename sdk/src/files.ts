/**
 * Types of the file manager API (`files.*`, `trash.*`, `jobs.*`). They describe shares, folder pages, selections,
 * file operations that run as jobs, the recycle bin and live folder changes. Paths are always relative to their
 * location with `/` as separator; an app never sees a path on the host.
 *
 * @packageDocumentation
 */
import type { Problem } from './errors';

/** What an entry is. Symlinks are listed as themselves and never followed in listings. */
export type FileKind = 'file' | 'folder' | 'symlink' | 'other';

/**
 * Where paths start: a named storage from the manifest, or a share by its id (needs `files.shares`). Paths inside are
 * relative, with `/` as separator, and never leave it; `.` and `..` are refused.
 */
export type FileLocation = { storage: string } | { share: string };

/** One entry of a folder. */
export interface FileEntry {
  /** Name within its folder. */
  name: string;
  kind: FileKind;
  /** Bytes for files, 0 for folders. */
  size: number;
  /** Last change of the content, ISO 8601 UTC. */
  modified: string;
}

/** A share the user can use, with the user's own access. */
export interface ShareInfo {
  /** Stable id, used as `{ share: id }`. */
  id: string;
  /** Name shown to users. */
  name: string;
  access: 'read' | 'readWrite';
}

/** Order of a folder listing. Name order is natural ("file2" before "file10") and ignores case. */
export type SortKey = 'name' | 'size' | 'modified';
export type SortOrder = 'asc' | 'desc';

/** One page of a folder. */
export interface FolderPage {
  /** The folder, relative to its location ("" for the root). */
  path: string;
  /** Stable id of the folder, also after a rename. */
  folderId: string;
  /** Entries in the folder when the page was read. */
  total: number;
  /** This page, in the requested order. */
  entries: FileEntry[];
  /** Pass as `cursor` for the next page; null on the last page. The cursor stays valid while the folder changes. */
  nextCursor: string | null;
}

/**
 * Entries of one folder that an operation works on. `all` with `except` names a whole folder without listing it, so
 * selecting everything in a folder of 100,000 entries never sends 100,000 names.
 */
export type FileItems = { names: string[] } | { all: true; except: string[] };

/** A selection in one folder. */
export interface FileSelection {
  location: FileLocation;
  /** The folder the names are in. */
  folder: string;
  items: FileItems;
}

/**
 * What happens when the target already has an entry of the same name: `fail` stops the job with `fs.entry.exists`,
 * `skip` leaves the entry out, `overwrite` replaces files and merges folders, `rename` picks a free name such as
 * "Report (2).pdf".
 */
export type ConflictPolicy = 'fail' | 'skip' | 'overwrite' | 'rename';

/** A copy, move or delete that runs as a job. */
export interface FileOperation {
  kind: 'copy' | 'move' | 'delete';
  source: FileSelection;
  /** The folder to copy or move into. Not used for delete. */
  target?: { location: FileLocation; path: string };
  /** For copy and move; `fail` when left out. */
  onConflict?: ConflictPolicy;
  /**
   * For delete: remove for good instead of moving to the share's recycle bin. Set it only when the user explicitly
   * asked for it.
   */
  permanent?: boolean;
}

export type JobState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'interrupted';

/** A long operation of the user, and its progress. */
export interface JobInfo {
  id: number;
  /** For example `files.copy`, `files.trash`, `files.restore`. */
  kind: string;
  state: JobState;
  progress: {
    itemsDone: number;
    itemsTotal: number;
    bytesDone: number;
    bytesTotal: number;
    /** The entry being worked on, relative to its location. */
    current: string | null;
  };
  /** Why the job failed, with the stable code and its params. */
  problem: Problem | null;
  /** When it finished: entries done and paths left out (conflicts with `skip`, symlinks, which are never copied). */
  result: { done: number; skipped: string[] } | null;
}

/** An item in a share's recycle bin. */
export interface TrashEntry {
  /** Id for restoring or deleting it for good. */
  id: number;
  /** Its name when it was deleted. */
  name: string;
  /** Where it was, relative to the share; restoring puts it back there. */
  originalPath: string;
  kind: FileKind;
  /** Bytes, for a folder everything inside it. */
  size: number;
  deletedAt: string;
  /** User name of who deleted it, if that user still exists. */
  deletedBy: string | null;
  /** When it is deleted for good, or null when the share keeps items until the bin is emptied. */
  expiresAt: string | null;
}

/** One page of a recycle bin, newest first. */
export interface TrashPage {
  items: TrashEntry[];
  nextCursor: string | null;
  /** Days the share keeps deleted items; 0 keeps them until the bin is emptied. */
  retentionDays: number;
}

/** Changes of a watched folder since the last event. */
export interface FolderChange {
  /** The id `files.watch` returned. */
  watch: string;
  /** Entries that appeared or changed, with their current metadata. */
  changed: FileEntry[];
  /** Names that are gone. */
  removed: string[];
  /** Changes were lost (the kernel queue overflowed): list the folder again. */
  resync: boolean;
  /** The folder itself was deleted or moved away; no more events come for this watch. */
  gone: boolean;
}

/** Progress of an upload started with an `upload` id. */
export interface UploadProgress {
  upload: string;
  /** Bytes sent so far. */
  loaded: number;
  total: number;
}
