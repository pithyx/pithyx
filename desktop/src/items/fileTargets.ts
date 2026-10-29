import { unwrap } from '@pithyx/api-client';
import type { FileEntry } from '@pithyx/sdk';
import { api } from '../api/client';
import {
  defaultAction,
  openWithChoices,
  type DefaultAction,
  type OpenWith,
} from '../apps/files/defaultAction';
import { useFileDefaults } from '../apps/files/fileDefaults';
import { noteRecentFile, RECENT_PLACE } from '../apps/files/recent';
import { coreAppIds } from '../apps/ids';
import { useInstalledApps } from '../apps/installed';
import { viewKindOf } from '../apps/viewer/kinds';
import { showError } from '../errors/errorStore';
import { download } from '../shell/transfers';
import {
  IconFile,
  IconFileText,
  IconFileTypePdf,
  IconFileZip,
  IconMovie,
  IconMusic,
  IconPhoto,
  type TablerIcon,
} from '../ui/icons';
import { openApp } from '../wm/store';

const ARCHIVES = new Set(['zip', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'zst', '7z', 'rar']);

/** The last part of a target's path: its own name. */
export function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

function parentOf(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

/** The glyph of a file on the desktop, by its type. */
export function fileIcon(name: string): TablerIcon {
  const dot = name.lastIndexOf('.');
  if (dot > 0 && ARCHIVES.has(name.slice(dot + 1).toLowerCase())) {
    return IconFileZip;
  }
  switch (viewKindOf(name)) {
    case 'image':
      return IconPhoto;
    case 'pdf':
      return IconFileTypePdf;
    case 'text':
      return IconFileText;
    case 'audio':
      return IconMusic;
    case 'video':
      return IconMovie;
    default:
      return IconFile;
  }
}

/** The ways "Open with" offers for a file shortcut, as Files offers them. */
export function openWithFor(name: string): OpenWith[] {
  return openWithChoices(
    { name, kind: 'file', size: 0, modified: '' },
    useInstalledApps.getState().apps ?? [],
  );
}

/** Opens Files in a folder; with `select` the entry of that name is selected. */
export function showInFiles(share: number, folder: string, select?: string): void {
  openApp(coreAppIds.files, undefined, {
    session: {
      share: String(share),
      path: folder,
      view: 'folder',
      ...(select === undefined ? {} : { select }),
    },
  });
}

/** Opens Files at the list of the user's recent files. */
export function showRecentInFiles(): void {
  openApp(coreAppIds.files, undefined, {
    session: { share: RECENT_PLACE, path: '', view: 'folder' },
  });
}

/** Opens Files in the folder of a shortcut's target, with the target selected. */
export function showTargetInFiles(share: number, path: string): void {
  showInFiles(share, parentOf(path), baseName(path));
}

async function entryOf(share: number, path: string): Promise<FileEntry> {
  const entry = unwrap(
    await api.GET('/api/files/{shareId}/entry', {
      params: { path: { shareId: share }, query: { path } },
    }),
  );
  return { name: entry.name, kind: entry.kind, size: entry.size, modified: entry.modified };
}

async function writable(share: number): Promise<boolean> {
  const shares = unwrap(await api.GET('/api/shares'));
  return shares.some((entry) => entry.id === share && entry.access === 'readWrite');
}

function asAction(choice: OpenWith): DefaultAction {
  if (choice === 'viewer') {
    return { kind: 'view' };
  }
  if (choice === 'download') {
    return { kind: 'download' };
  }
  return { kind: 'app', id: choice.slice('app:'.length) };
}

/**
 * Opens the target of a shortcut or a recent file as a double click in Files would: a folder in Files, a file in its default app, the
 * viewer or as a download. `how` is a choice of "Open with".
 */
export async function openTarget(share: number, path: string, how?: OpenWith): Promise<void> {
  try {
    const entry = await entryOf(share, path);
    const action =
      how === undefined
        ? defaultAction(
            entry,
            useInstalledApps.getState().apps ?? [],
            useFileDefaults.getState().choices,
          )
        : asAction(how);
    const folder = parentOf(path);
    if (action.kind === 'view' || action.kind === 'app') {
      noteRecentFile(share, path);
    }
    switch (action.kind) {
      case 'openFolder':
        showInFiles(share, path);
        break;
      case 'view':
        openApp(coreAppIds.viewer, undefined, {
          session: { share: String(share), folder, name: entry.name },
        });
        break;
      case 'app':
        openApp(action.id, {
          args: null,
          pendingFile: {
            share: String(share),
            path,
            size: entry.size,
            modified: entry.modified,
            writable: await writable(share),
          },
        });
        break;
      case 'download':
        download(
          `/api/files/${String(share)}/content?${new URLSearchParams({ path }).toString()}`,
          entry.name,
        );
        break;
      case 'none':
        break;
    }
  } catch (error) {
    showError(error);
  }
}
