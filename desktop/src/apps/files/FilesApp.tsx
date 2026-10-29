import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  isProblem,
  type ConflictPolicy,
  type FileEntry,
  type FileItems,
  type FileOperation,
  type JobInfo,
  type ShareInfo,
  type SortKey,
  type SortOrder,
  type PithyxApp,
} from '@pithyx/sdk';
import { Fragment, useEffect, useEffectEvent, useRef, useState, type DragEvent } from 'react';
import { ToggleButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { useStore } from 'zustand';
import { Button, Checkbox, cx, Dialog, IconButton, Toolbar, ToolbarSeparator } from '@pithyx/ui';
import {
  IconArrowLeft,
  IconArrowRight,
  IconArrowUp,
  IconChevronRight,
  IconClipboard,
  IconClock,
  IconCopy,
  IconDownload,
  IconEye,
  IconFolderPlus,
  IconHome,
  IconLayoutGrid,
  IconFile,
  IconFolder,
  IconHistory,
  IconLayoutSidebarRight,
  IconListDetails,
  IconLock,
  IconMenu2,
  IconPencil,
  IconRefresh,
  IconScissors,
  IconServer2,
  IconTrash,
  IconUpload,
  IconUsers,
  IconX,
} from '../../ui/icons';
import { randomId } from '../../platform/browser';
import { setClipboard, useFilesClipboard } from './clipboard';
import { filesCall, onThumbnails, thumbnailUrl } from '../sdk/filesInternal';
import {
  choiceExtension,
  currentChoice,
  defaultAction,
  openWithChoices,
  type OpenWith,
} from './defaultAction';
import { chooseFileDefault, useFileDefaults } from './fileDefaults';
import { manifestText, useInstalledApps } from '../installed';
import { useAppName } from '../names';
import styles from './FilesApp.module.css';
import { actionsFor } from './fileActions';
import { FilesMenu, type FilesAction } from './FilesMenu';
import {
  ENTRIES_TYPE,
  isEmpty,
  itemsToDrop,
  parseDragged,
  type DraggedEntries,
  type DropTarget,
} from './drop';
import { dragKindOf, FolderView } from './FolderView';
import { compareEntries } from './order';
import { addFileShortcuts } from '../../items/ItemsLayer';
import { itemsKey } from '../../items/query';
import { crumbsOf, freeName, joinPath, nameOf, parentOf } from './paths';
import {
  emptySelection,
  forget,
  isSelected,
  selectAll,
  selectedCount,
  selectOnly,
  single,
  toItems,
  type Selection,
} from './selection';
import { createTransfers, type Transfers } from './transfers';
import { TrashView, trashKey } from './TrashView';
import { RECENT_PLACE, useRecentFiles, type RecentFile } from './recent';
import { RecentView } from './RecentView';
import { VersionsDialog } from './VersionsDialog';
import { createThumbnails, thumbnailKey } from './thumbnails';
import { changeFolder, folderKey, keepOnly, prefetchFolder, useFolder } from './useFolder';

/** Where a window is: a folder of a share, or the share's recycle bin. Saved with the layout. */
interface Place {
  share: string | null;
  path: string;
  view: 'folder' | 'trash';
}

type Layout = 'list' | 'grid';

/** The place of the administrators' list of every other user's home; real share ids are numbers. */
const HOMES = 'homes';

function restoreLayout(saved: unknown): Layout {
  return typeof saved === 'object' &&
    saved !== null &&
    (saved as { layout?: unknown }).layout === 'grid'
    ? 'grid'
    : 'list';
}

const FINISHED = new Set<JobInfo['state']>(['succeeded', 'failed', 'cancelled', 'interrupted']);

function restorePlace(saved: unknown): Place {
  if (typeof saved === 'object' && saved !== null) {
    const { share, path, view } = saved as Partial<Record<keyof Place, unknown>>;
    if (typeof share === 'string' && typeof path === 'string') {
      return { share, path, view: view === 'trash' ? 'trash' : 'folder' };
    }
  }
  return { share: null, path: '', view: 'folder' };
}

function initialSelection(saved: unknown, here: string): { at: string; selection: Selection } {
  if (typeof saved === 'object' && saved !== null) {
    const { share, path, select } = saved as Record<string, unknown>;
    if (typeof share === 'string' && typeof path === 'string' && typeof select === 'string') {
      return { at: `${share}:${path}:folder`, selection: selectOnly(select) };
    }
  }
  return { at: here, selection: emptySelection };
}

function dropKey(share: string, path: string): string {
  return JSON.stringify([share, path]);
}

function codeOf(error: unknown): string | null {
  return isProblem(error) ? error.code : null;
}

/**
 * The file manager. A core app on the SDK like any other: it reaches shares, folders, jobs and the recycle bin only
 * through `app.files`, `app.jobs` and `app.trash`.
 */
export function FilesApp({ app }: { app: PithyxApp }) {
  const { t, i18n } = useTranslation('files');
  const client = useQueryClient();
  const [place, setPlace] = useState<Place>(() => restorePlace(app.context.session));
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [history, setHistory] = useState<{ back: Place[]; forward: Place[] }>({
    back: [],
    forward: [],
  });
  const [sort, setSort] = useState<{ key: SortKey; order: SortOrder }>({
    key: 'name',
    order: 'asc',
  });
  const [transfers] = useState<Transfers>(createTransfers);
  const [layout, setLayout] = useState<Layout>(() => restoreLayout(app.context.session));
  const [thumbnails] = useState(() =>
    createThumbnails((share, folder, names, size) =>
      filesCall(app, 'internal.files.thumbnails', { share, folder, names, size }),
    ),
  );
  const thumbnailStates = useStore(thumbnails.store, (state) => state.byKey);
  const [openWith, setOpenWith] = useState<FileEntry | null>(null);
  const [alwaysUse, setAlwaysUse] = useState(false);
  const installed = useInstalledApps((state) => state.apps);
  const fileChoices = useFileDefaults((state) => state.choices);
  const appName = useAppName();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number; entry: FileEntry | null } | null>(null);
  // The breadcrumb segment or sidebar share a drag is over (dropKey).
  const [dropOn, setDropOn] = useState<string | null>(null);
  const [confirmPermanent, setConfirmPermanent] = useState<FileItems | null>(null);
  const [versionsOf, setVersionsOf] = useState<{
    share: string;
    path: string;
    name: string;
  } | null>(null);
  const [conflict, setConflict] = useState<
    | { kind: 'job'; operation: FileOperation }
    | { kind: 'upload'; files: File[]; share: string; folder: string }
    | null
  >(null);
  const upload = useRef<HTMLInputElement>(null);
  // When the last navigation started, for the timing of how fast a folder shows; null until the first one.
  const navigation = useRef<{ start: number; cached: boolean } | null>(null);
  const clipboard = useFilesClipboard((state) => state.current);

  const shares = useQuery({ queryKey: ['files', 'shares'], queryFn: () => app.files.shares() });
  const shareList = shares.data ?? [];
  // The own home first, then the shared folders; administrators also see every other home (D348), gathered in one
  // entry "homes" that lists them as folders, so a box with a hundred users keeps a short sidebar.
  const ownHomes = shareList.filter((candidate) => candidate.kind === 'home');
  const sharedFolders = shareList.filter((candidate) => candidate.kind === undefined);
  const userHomes = shareList.filter((candidate) => candidate.kind === 'userHome');
  const atHomes = place.share === HOMES && userHomes.length > 0;
  const recent = useRecentFiles();
  const atRecent = place.share === RECENT_PLACE && recent !== undefined;
  // Each home is a share of its own (its trash, versions and pool), so the homes are listed as folders that open it.
  const homeEntries: FileEntry[] = userHomes
    .map((home): FileEntry => ({ name: home.name, kind: 'folder', size: 0, modified: '' }))
    .sort((a, b) => compareEntries(a, b, sort.key, sort.order));
  const share: ShareInfo | undefined =
    atHomes || atRecent
      ? undefined
      : (shareList.find((candidate) => candidate.id === place.share) ?? shareList[0]);
  const shareId = share?.id ?? null;
  const inUserHome = share?.kind === 'userHome';
  const path = share?.id === place.share ? place.path : '';
  const view = share?.id === place.share ? place.view : 'folder';
  const writable = share?.access === 'readWrite';
  const here = `${shareId ?? ''}:${path}:${view}`;

  const [announcement, setAnnouncement] = useState<string | null>(null);
  // Selection and inline rename belong to one folder; another folder starts empty. "Show in Files" on the desktop
  // opens a window with one entry selected.
  const [selectionState, setSelectionState] = useState(() =>
    initialSelection(app.context.session, here),
  );
  const selection = selectionState.at === here ? selectionState.selection : emptySelection;
  const setSelection = (next: Selection) => {
    setSelectionState({ at: here, selection: next });
    setAnnouncement(null);
  };
  const [renamingState, setRenamingState] = useState<{ at: string; name: string } | null>(null);
  const renaming = renamingState?.at === here ? renamingState.name : null;
  const setRenaming = (name: string | null) => {
    setRenamingState(name === null ? null : { at: here, name });
  };

  const folder = useFolder(app, view === 'folder' ? shareId : null, path, sort.key, sort.order, {
    onRemoved: (names) => {
      setSelectionState((state) =>
        state.at === here ? { at: here, selection: forget(state.selection, names) } : state,
      );
    },
    onGone: () => {
      go({ share: shareId, path: parentOf(path), view: 'folder' }, false);
    },
  });
  const location = { share: shareId ?? '' };
  const title = atHomes
    ? t('shares.homes')
    : atRecent
      ? t('recent.title')
      : view === 'trash'
        ? t('trash')
        : path === ''
          ? (share?.name ?? '')
          : nameOf(path);

  // The window title and the saved place follow the folder on screen (the window manager and the layout live
  // outside React).
  useEffect(() => {
    if (title !== '') {
      void app.window.setTitle(t('windowTitle', { folder: title }));
    }
  }, [app, t, title]);
  useEffect(() => {
    if (atRecent) {
      void app.session.save({ share: RECENT_PLACE, path: '', view: 'folder', layout });
    } else if (shareId !== null) {
      void app.session.save({ share: shareId, path, view, layout });
    }
  }, [app, atRecent, shareId, path, view, layout]);
  // Thumbnails rendered by the daemon arrive as events: syncing with the SDK.
  useEffect(() => onThumbnails(app, thumbnails.receive), [app, thumbnails]);
  const thumbnailSize = layout === 'grid' ? 'large' : 'small';

  function thumbnailOf(entry: FileEntry): string | null {
    if (shareId === null) {
      return null;
    }
    const state = thumbnailStates[thumbnailKey(shareId, path, thumbnailSize, entry)];
    return state?.status === 'ready' && state.version !== null
      ? thumbnailUrl(shareId, path, entry.name, thumbnailSize, state.version)
      : null;
  }

  function go(next: Place, remember = true) {
    const key = folderKey(next.share ?? '', next.path);
    navigation.current = {
      start: performance.now(),
      cached: client.getQueriesData({ queryKey: key }).some(([, data]) => data !== undefined),
    };
    if (remember) {
      setHistory((current) => ({ back: [...current.back, place], forward: [] }));
    }
    setPlace(next);
    setAnnouncement(null);
    setSidebarOpen(false);
    setMenu(null);
  }

  function back() {
    const previous = history.back.at(-1);
    if (previous !== undefined) {
      setHistory({ back: history.back.slice(0, -1), forward: [place, ...history.forward] });
      go(previous, false);
    }
  }

  function forward() {
    const [next, ...rest] = history.forward;
    if (next !== undefined) {
      setHistory({ back: [...history.back, place], forward: rest });
      go(next, false);
    }
  }

  const canGoUp = !atHomes && view === 'folder' && (path !== '' || inUserHome);

  function up() {
    if (!canGoUp) {
      return;
    }
    go(
      path === ''
        ? { share: HOMES, path: '', view: 'folder' }
        : { share: shareId, path: parentOf(path), view: 'folder' },
    );
  }

  async function report(error: unknown) {
    await app.ui.showError(error);
  }

  function refresh() {
    if (shareId !== null) {
      void client.invalidateQueries({ queryKey: folderKey(shareId, path) });
    }
  }

  function track(job: JobInfo, operation: FileOperation | null) {
    transfers.addJob(job, operation);
    if (FINISHED.has(job.state)) {
      jobChanged(job);
    }
  }

  function jobChanged(job: JobInfo) {
    const running = transfers.updateJob(job);
    if (running === undefined) {
      return;
    }
    void client.invalidateQueries({ queryKey: itemsKey });
    if (shareId !== null) {
      void client.invalidateQueries({ queryKey: trashKey(shareId) });
      // Other folders are revalidated when they are visited again; the one on screen follows its live changes.
      void client.invalidateQueries({
        queryKey: ['files', shareId, 'folder'],
        refetchType: 'none',
      });
    }
    if (
      job.state === 'succeeded' &&
      running.operation !== null &&
      'all' in running.operation.source.items
    ) {
      // A whole folder was shown as gone at once; list what is really left.
      refresh();
    }
    if (job.state !== 'failed' || job.problem === null) {
      return;
    }
    // A failed job may have been shown optimistically; the folder shows the truth again.
    refresh();
    if (job.problem.code === 'fs.entry.exists' && running.operation !== null) {
      setConflict({ kind: 'job', operation: running.operation });
    } else {
      void report(job.problem);
    }
  }

  const onJobChanged = useEffectEvent(jobChanged);

  // Job progress arrives as SDK events for as long as the window is open.
  useEffect(() => app.on('jobChanged', onJobChanged), [app]);
  // A share that appears or goes away shows at once; a gone share falls back to the first one left. A share whose pool
  // comes back is read again, folder included, so a listing that failed while it was away recovers.
  useEffect(
    () =>
      app.on('sharesChanged', () => {
        void client.invalidateQueries({ queryKey: ['files'] });
      }),
    [app, client],
  );

  async function run(operation: FileOperation) {
    try {
      track(await app.files.run(operation), operation);
    } catch (error) {
      refresh();
      await report(error);
    }
  }

  function remove(items: FileItems, permanent: boolean) {
    if (shareId === null || isEmpty(items)) {
      return;
    }
    // Optimistic: the entries leave the list at once and come back if the job fails.
    if ('names' in items) {
      changeFolder(client, shareId, path, { changed: [], removed: items.names });
    } else {
      keepOnly(client, shareId, path, items.except);
    }
    setSelection(emptySelection);
    void run({
      kind: 'delete',
      source: { location, folder: path, items },
      ...(permanent ? { permanent: true } : {}),
    });
  }

  function paste() {
    if (clipboard === null || shareId === null) {
      return;
    }
    if (clipboard.mode === 'cut' && clipboard.share === shareId && clipboard.folder === path) {
      return;
    }
    void run({
      kind: clipboard.mode === 'cut' ? 'move' : 'copy',
      source: {
        location: { share: clipboard.share },
        folder: clipboard.folder,
        items: clipboard.items,
      },
      target: { location, path },
    });
    if (clipboard.mode === 'cut') {
      setClipboard(null);
    }
  }

  function toClipboard(mode: 'copy' | 'cut') {
    const count = selectedCount(selection, folder.total);
    if (shareId === null || count === 0) {
      return;
    }
    setClipboard({ mode, share: shareId, folder: path, items: toItems(selection), count });
    setAnnouncement(t(mode === 'cut' ? 'announce.cut' : 'announce.copied', { count }));
  }

  async function uploadFiles(
    files: File[],
    into: string,
    policy?: ConflictPolicy,
    intoShare: string | null = shareId,
  ) {
    if (intoShare === null) {
      return;
    }
    const conflicts: File[] = [];
    for (const file of files) {
      const id = randomId();
      const tried = new Set([file.name]);
      let name = file.name;
      transfers.addUpload({ id, name, loaded: 0, total: file.size });
      try {
        for (let attempt = 0; ; attempt++) {
          try {
            const entry = await app.files.upload({ share: intoShare }, into, name, file, {
              upload: id,
              ...(policy === 'overwrite' ? { overwrite: true } : {}),
            });
            if (intoShare === shareId && into === path) {
              changeFolder(client, intoShare, path, { changed: [entry], removed: [] });
            }
            break;
          } catch (error) {
            if (codeOf(error) !== 'fs.entry.exists' || policy === 'skip') {
              throw error;
            }
            if (policy !== 'rename' || attempt > 50) {
              conflicts.push(file);
              break;
            }
            name = freeName(file.name, (candidate) => tried.has(candidate));
            tried.add(name);
          }
        }
      } catch (error) {
        // A conflict was asked about above; a cancel in the taskbar needs no message.
        if (codeOf(error) !== 'fs.entry.exists' && codeOf(error) !== 'sdk.transfer.cancelled') {
          await report(error);
        }
      } finally {
        transfers.finishUpload(id);
      }
    }
    if (conflicts.length > 0) {
      setConflict({ kind: 'upload', files: conflicts, share: intoShare, folder: into });
    }
  }

  /** Creates a folder or an empty file under a free default name and starts renaming it. */
  async function createEntry(kind: 'folder' | 'file') {
    if (shareId === null) {
      return;
    }
    const taken = new Set(folder.entries.map((entry) => entry.name));
    const base = kind === 'folder' ? t('dialog.newFolderName') : t('dialog.newFileName');
    let name = taken.has(base) ? freeName(base, (candidate) => taken.has(candidate)) : base;
    for (let attempt = 0; attempt < 20; attempt++) {
      try {
        const entry =
          kind === 'folder'
            ? await app.files.createFolder(location, joinPath(path, name))
            : await app.files.upload(location, path, name, new Blob([]));
        changeFolder(client, shareId, path, { changed: [entry], removed: [] });
        setSelection(selectOnly(entry.name));
        setRenaming(entry.name);
        return;
      } catch (error) {
        if (codeOf(error) !== 'fs.entry.exists') {
          await report(error);
          return;
        }
        taken.add(name);
        name = freeName(base, (candidate) => taken.has(candidate));
      }
    }
  }

  async function rename(oldName: string, newName: string | null) {
    setRenaming(null);
    const entry = folder.entries.find((candidate) => candidate.name === oldName);
    if (shareId === null || entry === undefined || newName === null) {
      return;
    }
    const trimmed = newName.trim();
    if (trimmed === '' || trimmed === oldName) {
      return;
    }
    changeFolder(client, shareId, path, {
      changed: [{ ...entry, name: trimmed }],
      removed: [oldName],
    });
    setSelection(selectOnly(trimmed));
    try {
      await app.files.rename(location, joinPath(path, oldName), trimmed);
      // Shortcuts on the desktop follow the rename on the daemon.
      void client.invalidateQueries({ queryKey: itemsKey });
    } catch (error) {
      changeFolder(client, shareId, path, { changed: [entry], removed: [trimmed] });
      setSelection(selectOnly(oldName));
      await report(error);
    }
  }

  /** Opens a file of the folder on screen, or of another folder with `at` (a recent file). */
  async function openIn(
    entry: FileEntry,
    how: OpenWith,
    at = { share: shareId, folder: path, writable },
  ) {
    if (how === 'viewer' && at.share !== null) {
      await filesCall(app, 'internal.files.openViewer', {
        share: at.share,
        folder: at.folder,
        name: entry.name,
      }).catch(report);
    } else if (how.startsWith('app:') && at.share !== null) {
      await filesCall(app, 'internal.files.openWithApp', {
        share: at.share,
        folder: at.folder,
        name: entry.name,
        app: how.slice('app:'.length),
        size: entry.size,
        modified: entry.modified,
        writable: at.writable,
      }).catch(report);
    } else if (at.share !== null) {
      await app.files.download({ share: at.share }, joinPath(at.folder, entry.name)).catch(report);
    }
  }

  async function openRecent(file: RecentFile) {
    const entry: FileEntry = {
      name: nameOf(file.path),
      kind: 'file',
      size: file.size,
      modified: file.modified,
    };
    const at = {
      share: String(file.share),
      folder: parentOf(file.path),
      writable: shareList.some(
        (candidate) => candidate.id === String(file.share) && candidate.access === 'readWrite',
      ),
    };
    const action = defaultAction(entry, installed ?? [], useFileDefaults.getState().choices);
    if (action.kind === 'view') {
      await openIn(entry, 'viewer', at);
    } else if (action.kind === 'app') {
      await openIn(entry, `app:${action.id}`, at);
    } else if (action.kind === 'download') {
      await openIn(entry, 'download', at);
    }
  }

  /** Shows a recent file in its folder, selected. */
  function showRecent(file: RecentFile) {
    const target = {
      share: String(file.share),
      path: parentOf(file.path),
      view: 'folder' as const,
    };
    go(target);
    setSelectionState({
      at: `${target.share}:${target.path}:folder`,
      selection: selectOnly(nameOf(file.path)),
    });
  }

  async function open(entry: FileEntry) {
    if (atHomes) {
      const home = userHomes.find((candidate) => candidate.name === entry.name);
      if (home !== undefined) {
        go({ share: home.id, path: '', view: 'folder' });
      }
      return;
    }
    const target = joinPath(path, entry.name);
    const action = defaultAction(entry, installed ?? [], useFileDefaults.getState().choices);
    if (action.kind === 'download') {
      await app.files.download(location, target).catch(report);
    } else if (action.kind === 'view') {
      await openIn(entry, 'viewer');
    } else if (action.kind === 'app') {
      await openIn(entry, `app:${action.id}`);
    } else if (action.kind === 'openFolder') {
      if (entry.kind === 'symlink') {
        // A link may point to a file; the daemon says which when it is listed.
        try {
          await app.files.list(location, target, { limit: 1 });
        } catch (error) {
          if (codeOf(error) === 'fs.entry.not_folder') {
            await app.files.download(location, target).catch(report);
          } else {
            await report(error);
          }
          return;
        }
      }
      go({ share: shareId, path: target, view: 'folder' });
    }
  }

  /** The selected names in the folder on screen, also when everything is selected. */
  function selectedNames(): string[] {
    return folder.entries
      .filter((entry) => isSelected(selection, entry.name))
      .map((entry) => entry.name);
  }

  function selectedEntry(): FileEntry | undefined {
    const name = single(selection);
    return name === null
      ? undefined
      : (atHomes ? homeEntries : folder.entries).find((entry) => entry.name === name);
  }

  function act(action: FilesAction) {
    setMenu(null);
    const entry = selectedEntry();
    switch (action) {
      case 'open':
        if (entry !== undefined) {
          void open(entry);
        }
        break;
      case 'download':
        if (entry !== undefined) {
          void openIn(entry, 'download');
        }
        break;
      case 'openWith':
        if (entry !== undefined) {
          setAlwaysUse(false);
          setOpenWith(entry);
        }
        break;
      case 'copy':
      case 'cut':
        toClipboard(action);
        break;
      case 'paste':
        paste();
        break;
      case 'rename':
        if (entry !== undefined && writable) {
          setRenaming(entry.name);
        }
        break;
      case 'addToDesktop':
        if (shareId !== null) {
          const names = selectedNames();
          const ids = addFileShortcuts(
            client,
            Number(shareId),
            names.map((name) => joinPath(path, name)),
            {
              folders: new Set(
                folder.entries
                  .filter((entry) => entry.kind !== 'file' && names.includes(entry.name))
                  .map((entry) => joinPath(path, entry.name)),
              ),
            },
          );
          setAnnouncement(t('announce.addedToDesktop', { count: ids.length }));
        }
        break;
      case 'versions':
        if (entry !== undefined && shareId !== null) {
          setVersionsOf({ share: shareId, path: joinPath(path, entry.name), name: entry.name });
        }
        break;
      case 'delete':
        if (writable) {
          remove(toItems(selection), false);
        }
        break;
      case 'deletePermanent':
        if (writable && selectedCount(selection, folder.total) > 0) {
          setConfirmPermanent(toItems(selection));
        }
        break;
      case 'newFolder':
        void createEntry('folder');
        break;
      case 'newFile':
        void createEntry('file');
        break;
      case 'upload':
        upload.current?.click();
        break;
      case 'selectAll':
        setSelection(selectAll());
        break;
    }
  }

  function dragData(items: FileItems): string {
    return JSON.stringify({ share: shareId, folder: path, items } satisfies DraggedEntries);
  }

  /**
   * Breadcrumb segments and sidebar shares are drop targets, marked with `data-drop-share`, `data-drop-path` and
   * `data-drop-writable`: entries from a Files window move there (copy with Ctrl or Alt), files from the device are
   * uploaded there. Their containers handle the drag for all of them.
   */
  function zoneOf(event: DragEvent): { key: string; target: DropTarget } | null {
    const zone = (event.target as Element).closest<HTMLElement>('[data-drop-share]');
    const share = zone?.dataset.dropShare;
    const path = zone?.dataset.dropPath ?? '';
    if (zone === null || share === undefined || zone.dataset.dropWritable === undefined) {
      return null;
    }
    return { key: dropKey(share, path), target: { share, path } };
  }

  function onZoneDragOver(event: DragEvent) {
    const zone = zoneOf(event);
    const kind = dragKindOf(event.dataTransfer);
    if (zone === null || kind === null) {
      setDropOn(null);
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect =
      kind === 'entries' && !(event.ctrlKey || event.altKey) ? 'move' : 'copy';
    setDropOn(zone.key);
  }

  function onZoneDragLeave(event: DragEvent) {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
      setDropOn(null);
    }
  }

  function onZoneDrop(event: DragEvent) {
    const zone = zoneOf(event);
    const kind = dragKindOf(event.dataTransfer);
    setDropOn(null);
    if (zone === null || kind === null) {
      return;
    }
    event.preventDefault();
    if (kind === 'files') {
      void uploadFiles(
        [...event.dataTransfer.files],
        zone.target.path,
        undefined,
        zone.target.share,
      );
    } else {
      dropEntries(
        event.dataTransfer.getData(ENTRIES_TYPE),
        zone.target,
        event.ctrlKey || event.altKey,
      );
    }
  }

  /** The attributes of a drop target for the containers' drag handlers. */
  function dropAttributes(share: string, folder: string, writable: boolean) {
    return {
      'data-drop-share': share,
      'data-drop-path': folder,
      'data-drop-writable': writable ? '' : undefined,
      'data-drop-target': dropOn === dropKey(share, folder) ? '' : undefined,
    };
  }

  function dropEntries(data: string, target: DropTarget, copy: boolean) {
    const dragged = parseDragged(data);
    const items = dragged === null ? null : itemsToDrop(dragged, target);
    if (dragged?.share == null || items === null) {
      return;
    }
    void run({
      kind: copy ? 'copy' : 'move',
      source: { location: { share: dragged.share }, folder: dragged.folder, items },
      target: { location: { share: target.share }, path: target.path },
    });
  }

  const count = selectedCount(selection, atHomes ? homeEntries.length : folder.total);
  const menuEntry = menu?.entry ?? null;
  // The apps' file actions for the selected entries (D375); only for a selection of loaded entries named one by one.
  const selectedItems = toItems(selection);
  const chosen =
    menuEntry === null || atHomes || view !== 'folder' || !('names' in selectedItems)
      ? []
      : folder.entries.filter((entry) => selectedItems.names.includes(entry.name));
  const appActions =
    chosen.length > 0 && chosen.length === count
      ? actionsFor(chosen, installed ?? [], writable)
      : [];

  function runAppAction(index: number) {
    const offered = appActions[index];
    if (offered === undefined || shareId === null) {
      return;
    }
    void filesCall(app, 'internal.files.runAction', {
      app: offered.app,
      action: offered.action.id,
      share: shareId,
      folder: path,
      names: chosen.map((entry) => entry.name),
    }).catch(report);
  }
  // The recycle bin of the place on view, else of the user's home: one entry in the navigation (D394).
  const binShare =
    [...ownHomes, ...sharedFolders].find((candidate) => candidate.id === shareId) ?? ownHomes[0];
  const detailName = view === 'folder' && !atHomes ? single(selection) : null;
  const detailEntry =
    detailName === null ? undefined : folder.entries.find((entry) => entry.name === detailName);
  const hasSelection = view === 'folder' && selectedNames().length > 0;
  const cut =
    clipboard?.mode === 'cut' &&
    clipboard.share === shareId &&
    clipboard.folder === path &&
    'names' in clipboard.items
      ? new Set(clipboard.items.names)
      : new Set<string>();

  return (
    <div className={styles.files}>
      <Toolbar
        aria-label={t('commands')}
        variant="bar"
        className={styles.commands}
        end={
          view === 'folder' && !atRecent ? (
            <>
              <Button
                size="sm"
                variant="quiet"
                aria-label={layout === 'grid' ? t('layout.list') : t('layout.grid')}
                onPress={() => {
                  setLayout((current) => (current === 'grid' ? 'list' : 'grid'));
                }}
              >
                {layout === 'grid' ? (
                  <IconListDetails size={18} stroke={1.75} aria-hidden />
                ) : (
                  <IconLayoutGrid size={18} stroke={1.75} aria-hidden />
                )}
                {layout === 'grid' ? t('command.list') : t('command.tiles')}
              </Button>
              <ToggleButton
                className={styles.detailsToggle}
                isSelected={detailsOpen}
                onChange={setDetailsOpen}
              >
                <IconLayoutSidebarRight size={18} stroke={1.75} aria-hidden />
                {t('details')}
              </ToggleButton>
            </>
          ) : undefined
        }
      >
        <Button
          size="sm"
          variant="primary"
          isDisabled={view !== 'folder' || !writable}
          onPress={() => {
            upload.current?.click();
          }}
        >
          <IconUpload size={18} stroke={1.75} aria-hidden />
          {t('actions.upload')}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || !writable}
          onPress={() => void createEntry('folder')}
        >
          <IconFolderPlus size={18} stroke={1.75} aria-hidden />
          {t('actions.newFolder')}
        </Button>
        <ToolbarSeparator />
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || !hasSelection}
          onPress={() => {
            act('copy');
          }}
        >
          <IconCopy size={18} stroke={1.75} aria-hidden />
          {t('actions.copy')}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || !hasSelection || !writable}
          onPress={() => {
            act('cut');
          }}
        >
          <IconScissors size={18} stroke={1.75} aria-hidden />
          {t('actions.cut')}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || !writable || clipboard === null}
          onPress={() => {
            act('paste');
          }}
        >
          <IconClipboard size={18} stroke={1.75} aria-hidden />
          {t('actions.paste')}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || selectedEntry() === undefined || !writable}
          onPress={() => {
            act('rename');
          }}
        >
          <IconPencil size={18} stroke={1.75} aria-hidden />
          {t('actions.rename')}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || !hasSelection || !writable}
          onPress={() => {
            act('delete');
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('command.delete')}
        </Button>
        <ToolbarSeparator />
        <Button
          size="sm"
          variant="quiet"
          isDisabled={view !== 'folder' || selectedEntry()?.kind !== 'file'}
          onPress={() => {
            act('download');
          }}
        >
          <IconDownload size={18} stroke={1.75} aria-hidden />
          {t('actions.download')}
        </Button>
      </Toolbar>
      <div className={styles.toolbar}>
        <IconButton
          label={t('sidebar')}
          size="sm"
          className={styles.sidebarToggle}
          onPress={() => {
            setSidebarOpen((open) => !open);
          }}
        >
          <IconMenu2 size={18} stroke={1.75} />
        </IconButton>
        <IconButton
          label={t('back')}
          size="sm"
          isDisabled={history.back.length === 0}
          onPress={back}
        >
          <IconArrowLeft size={18} stroke={1.75} />
        </IconButton>
        <IconButton
          label={t('forward')}
          size="sm"
          isDisabled={history.forward.length === 0}
          onPress={forward}
        >
          <IconArrowRight size={18} stroke={1.75} />
        </IconButton>
        <IconButton label={t('up')} size="sm" isDisabled={!canGoUp} onPress={up}>
          <IconArrowUp size={18} stroke={1.75} />
        </IconButton>
        <nav
          className={styles.breadcrumb}
          aria-label={t('path')}
          onDragOver={onZoneDragOver}
          onDragLeave={onZoneDragLeave}
          onDrop={onZoneDrop}
        >
          {atRecent && (
            <span className={styles.crumb} aria-current="page">
              {t('recent.title')}
            </span>
          )}
          {(atHomes || inUserHome) && (
            <button
              type="button"
              className={styles.crumb}
              aria-current={atHomes ? 'page' : undefined}
              onClick={() => {
                if (!atHomes) {
                  go({ share: HOMES, path: '', view: 'folder' });
                }
              }}
            >
              {t('shares.homes')}
            </button>
          )}
          {share !== undefined && (
            <button
              type="button"
              className={styles.crumb}
              aria-current={view === 'folder' && path === '' ? 'page' : undefined}
              {...dropAttributes(share.id, '', writable)}
              onClick={() => {
                go({ share: shareId, path: '', view: 'folder' });
              }}
            >
              {inUserHome && <IconChevronRight size={14} stroke={1.75} aria-hidden />}
              {share.name}
            </button>
          )}
          {view === 'trash' ? (
            <span className={styles.crumb} aria-current="page">
              <IconChevronRight size={14} stroke={1.75} aria-hidden />
              {t('trash')}
            </span>
          ) : (
            crumbsOf(path).map((crumb) => (
              <button
                key={crumb.path}
                type="button"
                className={styles.crumb}
                aria-current={crumb.path === path ? 'page' : undefined}
                {...dropAttributes(shareId ?? '', crumb.path, writable && shareId !== null)}
                onClick={() => {
                  if (crumb.path !== path) {
                    go({ share: shareId, path: crumb.path, view: 'folder' });
                  }
                }}
              >
                <IconChevronRight size={14} stroke={1.75} aria-hidden />
                {crumb.name}
              </button>
            ))
          )}
        </nav>
        <IconButton
          label={t('refresh')}
          size="sm"
          isDisabled={view !== 'folder' || atRecent}
          onPress={refresh}
        >
          <IconRefresh size={18} stroke={1.75} />
        </IconButton>
      </div>
      {view === 'folder' && !writable && share !== undefined && (
        <p className={styles.infoBar}>
          <IconLock size={16} stroke={1.75} aria-hidden />
          {t('readOnlyInfo', { name: share.name })}
        </p>
      )}
      <div className={styles.split} data-sidebar-open={sidebarOpen ? '' : undefined}>
        <nav
          className={styles.sidebar}
          aria-label={t('sidebar')}
          onDragOver={onZoneDragOver}
          onDragLeave={onZoneDragLeave}
          onDrop={onZoneDrop}
        >
          {recent !== undefined && (
            <>
              <button
                type="button"
                className={styles.sideItem}
                aria-current={atRecent ? 'page' : undefined}
                onClick={() => {
                  go({ share: RECENT_PLACE, path: '', view: 'folder' });
                }}
              >
                <IconClock className={styles.sideIcon} size={16} stroke={1.75} aria-hidden />
                <span className={styles.sideName}>{t('recent.title')}</span>
              </button>
              <hr className={styles.sideRule} />
            </>
          )}
          {[ownHomes, sharedFolders].map((section, index) => (
            <Fragment key={index}>
              {index > 0 && section.length > 0 && <hr className={styles.sideRule} />}
              {section.map((candidate) => {
                const Icon = candidate.kind === undefined ? IconServer2 : IconHome;
                return (
                  <button
                    key={candidate.id}
                    type="button"
                    className={styles.sideItem}
                    aria-current={
                      candidate.id === shareId && view === 'folder' ? 'page' : undefined
                    }
                    {...dropAttributes(
                      candidate.id,
                      '',
                      candidate.access === 'readWrite' && candidate.unavailable === undefined,
                    )}
                    onClick={() => {
                      go({ share: candidate.id, path: '', view: 'folder' });
                    }}
                  >
                    <Icon className={styles.sideIcon} size={16} stroke={1.75} aria-hidden />
                    <span className={styles.sideName}>{candidate.name}</span>
                    {candidate.unavailable !== undefined ? (
                      <span className={styles.sideNote}>{t('shares.unavailable')}</span>
                    ) : (
                      candidate.access !== 'readWrite' && (
                        <IconLock
                          className={styles.sideLock}
                          size={14}
                          stroke={1.75}
                          role="img"
                          aria-label={t('readOnly')}
                        />
                      )
                    )}
                  </button>
                );
              })}
            </Fragment>
          ))}
          <hr className={styles.sideRule} />
          {userHomes.length > 0 && (
            <button
              type="button"
              className={styles.sideItem}
              aria-current={atHomes || inUserHome ? 'page' : undefined}
              onClick={() => {
                go({ share: HOMES, path: '', view: 'folder' });
              }}
            >
              <IconUsers className={styles.sideIcon} size={16} stroke={1.75} aria-hidden />
              <span className={styles.sideName}>{t('shares.homes')}</span>
            </button>
          )}
          {binShare !== undefined && (
            <button
              type="button"
              className={styles.sideItem}
              aria-current={view === 'trash' ? 'page' : undefined}
              aria-label={t('bin.title', { share: binShare.name })}
              onClick={() => {
                go({ share: binShare.id, path: '', view: 'trash' });
              }}
            >
              <IconTrash className={styles.sideIcon} size={16} stroke={1.75} aria-hidden />
              <span className={styles.sideName}>{t('trash')}</span>
            </button>
          )}
        </nav>
        <div className={styles.main}>
          {atRecent ? (
            <RecentView
              files={recent}
              shares={shareList}
              format={app.format}
              onOpen={(file) => void openRecent(file)}
              onShow={showRecent}
            />
          ) : shares.isSuccess && shareList.length === 0 ? (
            <p className={styles.empty}>{t('noShares')}</p>
          ) : atHomes ? (
            <FolderView
              entries={homeEntries}
              total={homeEntries.length}
              loading={false}
              hasMore={false}
              loadMore={() => undefined}
              sort={sort.key}
              order={sort.order}
              onSort={(key) => {
                setSort((current) => ({
                  key,
                  order: current.key === key && current.order === 'asc' ? 'desc' : 'asc',
                }));
              }}
              selection={selection}
              onSelect={setSelection}
              cut={cut}
              renaming={null}
              onRenamed={() => undefined}
              onOpen={(entry) => void open(entry)}
              layout={layout}
              thumbnail={() => null}
              onVisible={() => undefined}
              onUp={up}
              onKey={() => undefined}
              onMenu={(x, y, entry) => {
                if (entry !== null) {
                  setMenu({ x, y, entry });
                }
              }}
              onDropFiles={() => undefined}
              dragData={dragData}
              onDropEntries={() => undefined}
              onShown={() => undefined}
              folderKey={here}
              format={app.format}
              fixed
            />
          ) : share === undefined ? null : view === 'trash' ? (
            <TrashView
              key={share.id}
              app={app}
              share={share.id}
              shareName={share.name}
              writable={writable}
              onJob={(job) => {
                track(job, null);
              }}
            />
          ) : folder.error !== null ? (
            <div className={styles.empty}>
              <p>{t('loadFailed')}</p>
              <Button size="sm" onPress={() => void report(folder.error)}>
                {t('details')}
              </Button>
            </div>
          ) : (
            <FolderView
              entries={folder.entries}
              total={folder.total}
              loading={folder.loading}
              hasMore={folder.hasMore}
              loadMore={folder.loadMore}
              sort={sort.key}
              order={sort.order}
              onSort={(key) => {
                setSort((current) => ({
                  key,
                  order: current.key === key && current.order === 'asc' ? 'desc' : 'asc',
                }));
              }}
              selection={selection}
              onSelect={setSelection}
              cut={cut}
              renaming={renaming}
              onRenamed={(oldName, newName) => void rename(oldName, newName)}
              onOpen={(entry) => void open(entry)}
              layout={layout}
              thumbnail={thumbnailOf}
              onVisible={(visible) => {
                if (shareId !== null) {
                  thumbnails.request(shareId, path, visible, thumbnailSize);
                }
              }}
              onUp={up}
              onKey={act}
              onMenu={(x, y, entry) => {
                setMenu({ x, y, entry });
              }}
              onDropFiles={(files, into) => {
                if (writable) {
                  void uploadFiles(files, into === null ? path : joinPath(path, into));
                }
              }}
              dragData={dragData}
              onRest={(entry) => {
                if (shareId !== null) {
                  prefetchFolder(
                    client,
                    app,
                    shareId,
                    joinPath(path, entry.name),
                    sort.key,
                    sort.order,
                  );
                }
              }}
              onDropEntries={(data, into, copy) => {
                if (shareId !== null && writable) {
                  dropEntries(
                    data,
                    { share: shareId, path: into === null ? path : joinPath(path, into) },
                    copy,
                  );
                }
              }}
              onShown={() => {
                const started = navigation.current;
                if (started !== null) {
                  performance.measure('pithyx:files:shown', {
                    start: started.start,
                    detail: { cached: started.cached },
                  });
                }
              }}
              folderKey={here}
              format={app.format}
            />
          )}
        </div>
        {detailsOpen && view === 'folder' && !atRecent && (
          <DetailsPane
            entry={detailEntry}
            thumbnail={detailEntry === undefined ? null : thumbnailOf(detailEntry)}
            format={app.format}
            onAction={act}
          />
        )}
      </div>
      <StatusBar
        transfers={transfers}
        readOnly={view === 'folder' && !writable && share !== undefined}
        text={
          atRecent
            ? t('items', { count: recent.length })
            : atHomes
              ? count > 0
                ? t('selected', { count })
                : t('items', { count: userHomes.length })
              : view === 'trash'
                ? ''
                : (announcement ??
                  (count > 0 ? t('selected', { count }) : t('items', { count: folder.total })))
        }
        onCancel={(job) => {
          void app.jobs.cancel(job).catch(report);
        }}
      />
      <input
        ref={upload}
        type="file"
        multiple
        className={styles.hiddenInput}
        onChange={(event) => {
          const files = [...(event.currentTarget.files ?? [])];
          event.currentTarget.value = '';
          void uploadFiles(files, path);
        }}
      />
      {menu !== null && (
        <FilesMenu
          x={menu.x}
          y={menu.y}
          kind={menuEntry === null ? 'folder' : 'entries'}
          count={count}
          single={
            menuEntry === null || count !== 1 ? null : menuEntry.kind === 'file' ? 'file' : 'folder'
          }
          viewable={menuEntry !== null && defaultAction(menuEntry).kind === 'view'}
          writable={writable}
          openOnly={atHomes}
          canPaste={clipboard !== null}
          appActions={appActions.map((offered) => ({
            label: manifestText(offered.action.label, i18n.language),
            icon: installed?.find((candidate) => candidate.id === offered.app)?.icon ?? '',
          }))}
          onAppAction={runAppAction}
          onAction={act}
          onClose={() => {
            setMenu(null);
          }}
        />
      )}
      {versionsOf !== null && (
        <VersionsDialog
          app={app}
          share={versionsOf.share}
          path={versionsOf.path}
          name={versionsOf.name}
          writable={writable}
          onJob={(job) => {
            track(job, null);
          }}
          onClose={() => {
            setVersionsOf(null);
          }}
        />
      )}
      <Dialog
        isOpen={openWith !== null}
        onOpenChange={(isOpen) => {
          if (!isOpen) {
            setOpenWith(null);
          }
        }}
        title={t('openWith.title', { name: openWith?.name ?? '' })}
        actions={
          <Button
            onPress={() => {
              setOpenWith(null);
            }}
          >
            {t('dialog.cancel')}
          </Button>
        }
      >
        {openWith !== null && (
          <div className={styles.openWith}>
            <ul
              className={styles.openWithList}
              aria-label={t('openWith.choices')}
              onKeyDown={(event) => {
                // The arrow keys move between the choices, as in a menu.
                if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
                  return;
                }
                const buttons = [...event.currentTarget.querySelectorAll('button')];
                const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
                const next =
                  buttons[
                    (index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length
                  ];
                next?.focus();
                event.preventDefault();
              }}
            >
              {openWithChoices(openWith, installed ?? []).map((choice) => {
                const appId = choice.startsWith('app:') ? choice.slice('app:'.length) : null;
                const icon =
                  appId === null
                    ? ''
                    : (installed?.find((candidate) => candidate.id === appId)?.icon ?? '');
                const current = choice === currentChoice(openWith, installed ?? [], fileChoices);
                return (
                  <li key={choice}>
                    <button
                      type="button"
                      className={styles.openWithChoice}
                      onClick={() => {
                        const entry = openWith;
                        const extension = choiceExtension(entry.name);
                        setOpenWith(null);
                        if (alwaysUse && extension !== null) {
                          chooseFileDefault(extension, choice).catch(report);
                        }
                        void openIn(entry, choice);
                      }}
                    >
                      {appId === null ? (
                        <span className={styles.openWithIcon} aria-hidden>
                          {choice === 'viewer' ? (
                            <IconEye size={20} stroke={1.75} />
                          ) : (
                            <IconDownload size={20} stroke={1.75} />
                          )}
                        </span>
                      ) : (
                        <img
                          className={styles.openWithIcon}
                          src={icon}
                          alt=""
                          width={28}
                          height={28}
                        />
                      )}
                      <span className={styles.openWithName}>
                        {choice === 'viewer' || choice === 'download'
                          ? t(`openWith.${choice}`)
                          : appName(appId ?? '')}
                      </span>
                      {current && (
                        <span className={styles.openWithCurrent}>{t('openWith.current')}</span>
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
            {choiceExtension(openWith.name) !== null && (
              <div className={styles.openWithAlways}>
                <Checkbox isSelected={alwaysUse} onChange={setAlwaysUse}>
                  {t('openWith.always', { extension: choiceExtension(openWith.name) })}
                </Checkbox>
              </div>
            )}
          </div>
        )}
      </Dialog>
      <Dialog
        isOpen={confirmPermanent !== null}
        onOpenChange={(isOpen) => {
          if (!isOpen) {
            setConfirmPermanent(null);
          }
        }}
        role="alertdialog"
        tone="danger"
        title={t('dialog.permanentTitle')}
        actions={
          <>
            <Button
              onPress={() => {
                setConfirmPermanent(null);
              }}
            >
              {t('dialog.cancel')}
            </Button>
            <Button
              variant="danger"
              onPress={() => {
                if (confirmPermanent !== null) {
                  remove(confirmPermanent, true);
                }
                setConfirmPermanent(null);
              }}
            >
              {t('dialog.permanentConfirm')}
            </Button>
          </>
        }
      >
        <p>{t('dialog.permanentBody', { count })}</p>
      </Dialog>
      <Dialog
        isOpen={conflict !== null}
        onOpenChange={(isOpen) => {
          if (!isOpen) {
            setConflict(null);
          }
        }}
        title={t('dialog.conflictTitle')}
        actions={
          <>
            <Button
              onPress={() => {
                setConflict(null);
              }}
            >
              {t('dialog.cancel')}
            </Button>
            {(['skip', 'overwrite', 'rename'] as const).map((policy) => (
              <Button
                key={policy}
                variant={policy === 'rename' ? 'primary' : 'secondary'}
                onPress={() => {
                  const current = conflict;
                  setConflict(null);
                  if (current?.kind === 'job') {
                    void run({ ...current.operation, onConflict: policy });
                  } else if (current?.kind === 'upload' && policy !== 'skip') {
                    void uploadFiles(current.files, current.folder, policy, current.share);
                  }
                }}
              >
                {t(`dialog.conflict.${policy}`)}
              </Button>
            ))}
          </>
        }
      >
        <p>{t('dialog.conflictBody')}</p>
      </Dialog>
    </div>
  );
}

/** Items and selection, and the uploads and jobs of this window with their progress. */
function StatusBar({
  transfers,
  readOnly,
  text,
  onCancel,
}: {
  transfers: Transfers;
  readOnly: boolean;
  text: string;
  onCancel: (job: number) => void;
}) {
  const { t } = useTranslation('files');
  const uploads = transfers.useStore((state) => state.uploads);
  const jobs = transfers.useStore((state) => state.jobs);
  const rows = [
    ...Object.values(uploads).map((item) => ({
      key: `u${item.id}`,
      label: t('job.uploading', { name: item.name }),
      done: item.loaded,
      total: item.total,
      job: null,
    })),
    ...Object.values(jobs).map(({ job }) => ({
      key: `j${String(job.id)}`,
      label:
        job.progress.current === null
          ? t(`job.kinds.${job.kind}`, { defaultValue: t('job.working') })
          : `${t(`job.kinds.${job.kind}`, { defaultValue: t('job.working') })}: ${job.progress.current}`,
      done: job.progress.bytesTotal > 0 ? job.progress.bytesDone : job.progress.itemsDone,
      total: job.progress.bytesTotal > 0 ? job.progress.bytesTotal : job.progress.itemsTotal,
      job: job.id,
    })),
  ];
  return (
    <div className={styles.status}>
      <span className={styles.statusText} role="status">
        {text}
      </span>
      {readOnly && <span className={styles.statusEnd}>{t('readOnly')}</span>}
      {rows.length > 0 && (
        <ul className={styles.transfers} aria-label={t('job.title')}>
          {rows.map((row) => (
            <li key={row.key} className={styles.transfer}>
              <span className={styles.transferLabel}>{row.label}</span>
              {row.job !== null ? (
                <IconButton
                  label={t('job.cancel')}
                  size="sm"
                  onPress={() => {
                    onCancel(row.job);
                  }}
                >
                  <IconX size={14} stroke={1.75} />
                </IconButton>
              ) : (
                <span />
              )}
              <div
                className={cx(styles.bar)}
                role="progressbar"
                aria-label={row.label}
                aria-valuemin={0}
                aria-valuemax={Math.max(1, row.total)}
                aria-valuenow={row.done}
              >
                <div
                  className={styles.barFill}
                  style={{ width: `${String(row.total > 0 ? (row.done / row.total) * 100 : 0)}%` }}
                />
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** The details pane of D394: a preview, the name, the facts and the actions of the selected item. */
function DetailsPane({
  entry,
  thumbnail,
  format,
  onAction,
}: {
  entry: FileEntry | undefined;
  thumbnail: string | null;
  format: PithyxApp['format'];
  onAction: (action: FilesAction) => void;
}) {
  const { t } = useTranslation('files');
  return (
    <aside className={styles.details} aria-label={t('details')}>
      {entry === undefined ? (
        <p className={styles.detailsEmpty}>{t('detailsEmpty')}</p>
      ) : (
        <>
          <div className={styles.preview}>
            {thumbnail !== null ? (
              <img src={thumbnail} alt="" />
            ) : entry.kind === 'folder' ? (
              <IconFolder size={56} stroke={1.25} aria-hidden />
            ) : (
              <IconFile size={56} stroke={1.25} aria-hidden />
            )}
          </div>
          <h3 className={styles.detailsName}>{entry.name}</h3>
          <dl className={styles.facts}>
            {entry.kind !== 'folder' && (
              <>
                <dt>{t('columns.size')}</dt>
                <dd>{format.bytes(entry.size)}</dd>
              </>
            )}
            {entry.modified !== '' && (
              <>
                <dt>{t('columns.modified')}</dt>
                <dd>{format.dateTime(entry.modified)}</dd>
              </>
            )}
          </dl>
          {entry.kind === 'file' && (
            <div className={styles.detailsActions}>
              <Button
                size="sm"
                onPress={() => {
                  onAction('download');
                }}
              >
                <IconDownload size={16} stroke={1.75} aria-hidden />
                {t('actions.download')}
              </Button>
              <Button
                size="sm"
                onPress={() => {
                  onAction('versions');
                }}
              >
                <IconHistory size={16} stroke={1.75} aria-hidden />
                {t('actions.versions')}
              </Button>
            </div>
          )}
        </>
      )}
    </aside>
  );
}
