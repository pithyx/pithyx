import { unwrap, type components } from '@pithyx/api-client';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import {
  Button,
  DataTable,
  Dialog,
  IconButton,
  NavList,
  TextField,
  type DataTableColumn,
  type NavGroup,
} from '@pithyx/ui';
import { useState } from 'react';
import type { Key, Selection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { formatBytes } from '../../desktop/format';
import { formatLocale } from '../../i18n';
import {
  IconArrowUp,
  IconChevronRight,
  IconFile,
  IconFolder,
  IconHome,
  IconLock,
  IconServer2,
  IconUsers,
} from '../../ui/icons';
import { useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import styles from './FileDialog.module.css';
import { answerFileDialog, type ChosenFile, type FileDialogRequest } from './store';

type Share = components['schemas']['ShareInfo'];
type Entry = components['schemas']['FileEntry'];

const PAGE_SIZE = 200;

/** The place of the list of other users' homes in the navigation; shares are `share:<id>`. */
const HOMES = 'homes';

/** Whether a file name has one of the extensions (without dot, lowercase); no extensions means every file. */
export function matchesExtensions(name: string, extensions: readonly string[]): boolean {
  const lower = name.toLowerCase();
  return (
    extensions.length === 0 ||
    extensions.some((extension) => lower.endsWith(`.${extension.toLowerCase()}`))
  );
}

/**
 * The places of the dialog: the user's own home first, then the shared folders; other users' homes (which only
 * administrators see) sit behind one entry, as in Files, so a box with many users keeps a short list.
 */
export function placesOf(shares: readonly Share[]): {
  own: Share[];
  shared: Share[];
  homes: Share[];
} {
  const byName = (a: Share, b: Share) => a.name.localeCompare(b.name);
  return {
    own: shares.filter((share) => share.kind === 'home'),
    shared: shares.filter((share) => share.kind !== 'home' && share.kind !== 'userHome'),
    homes: shares.filter((share) => share.kind === 'userHome').sort(byName),
  };
}

/** Where the dialog opens: the own home, else the first shared folder that is there; null without any. */
export function firstPlace(shares: readonly Share[]): string | null {
  const { own, shared, homes } = placesOf(shares);
  const first = [...own, ...shared].find((share) => share.unavailable == null);
  return first === undefined ? (homes.length > 0 ? HOMES : null) : `share:${String(first.id)}`;
}

/** The path of a name in a folder of a share. */
export function pathIn(folder: string, name: string): string {
  return folder === '' ? name : `${folder}/${name}`;
}

/** The names a table selection holds, out of the names on screen. */
function namesOf(keys: Selection, shown: readonly string[]): string[] {
  return keys === 'all' ? [...shown] : shown.filter((name) => keys.has(name));
}

/**
 * The desktop's file dialog for an app's `files.pick` and `files.saveAs`, in the look of Files (D394): the places on
 * the left, the path and a table of the folder on the right, the file name below when saving. It is drawn and
 * answered by the desktop; the app learns nothing but the files the user chose, and the dialog reads the folders with
 * the user's own session.
 */
export function FileDialog({ request }: { request: FileDialogRequest }) {
  const { t, i18n } = useTranslation();
  const appName = useAppName();
  const app = useDesktop((state) => state.windows[request.windowId]?.app);
  const save = request.kind === 'save';
  const locale = formatLocale(i18n.language);
  const [placeChoice, setPlaceChoice] = useState<string | null>(null);
  const [folder, setFolder] = useState('');
  const [chosen, setChosen] = useState<readonly string[]>([]);
  const [name, setName] = useState(request.suggestedName);

  const shares = useQuery({
    queryKey: ['appHost', 'shares'],
    queryFn: async () => unwrap(await api.GET('/api/shares')),
  });
  const shareList = shares.data ?? [];
  const places = placesOf(shareList);
  const place = placeChoice ?? firstPlace(shareList);
  const share = shareList.find((candidate) => `share:${String(candidate.id)}` === place);
  const shareId = share?.id ?? null;
  const shareName = (candidate: Share) =>
    candidate.kind === 'home' ? t('appHost.files.home') : candidate.name;
  const listing = useInfiniteQuery({
    queryKey: ['appHost', 'list', shareId, folder],
    enabled: shareId !== null,
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/files/{shareId}/list', {
          params: {
            path: { shareId: shareId ?? 0 },
            query: {
              path: folder,
              limit: PAGE_SIZE,
              ...(pageParam === undefined ? {} : { cursor: pageParam }),
            },
          },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
  const entries: Entry[] = listing.data?.pages.flatMap((page) => page.entries) ?? [];
  const visible = entries.filter(
    (entry) =>
      entry.kind === 'folder' ||
      entry.kind === 'symlink' ||
      (entry.kind === 'file' && (save || matchesExtensions(entry.name, request.extensions))),
  );
  const writable = share?.access === 'readWrite';
  const target = name.trim();
  const existing = entries.find((entry) => entry.kind === 'file' && entry.name === target);
  const atHomes = place === HOMES;
  // The homes of other users are listed as folders that open each home, as in Files.
  const homeRows: Entry[] = places.homes.map((home) => ({
    name: home.name,
    kind: 'folder',
    size: 0,
    modified: '',
  }));
  const rows = atHomes ? homeRows : visible;

  function close() {
    answerFileDialog(null);
  }

  function choose(files: Entry[]) {
    if (share === undefined || files.length === 0) {
      return;
    }
    const picked: ChosenFile[] = files.map((entry) => ({
      share: share.id,
      path: pathIn(folder, entry.name),
      name: entry.name,
      size: entry.size,
      modified: entry.modified,
      access: share.access === 'readWrite' ? 'readWrite' : 'read',
      overwrite: save && existing !== undefined,
    }));
    answerFileDialog(picked);
  }

  function go(next: string, path = '') {
    setPlaceChoice(next);
    setFolder(path);
    setChosen([]);
  }

  function open(key: Key) {
    const entry = rows.find((candidate) => candidate.name === key);
    if (entry === undefined) {
      return;
    }
    if (atHomes) {
      const home = places.homes.find((candidate) => candidate.name === entry.name);
      if (home !== undefined) {
        go(`share:${String(home.id)}`);
      }
    } else if (entry.kind !== 'file') {
      setFolder(pathIn(folder, entry.name));
      setChosen([]);
    } else if (save) {
      setName(entry.name);
    } else {
      const names = new Set([...chosen, entry.name]);
      choose(request.multiple ? entries.filter((file) => names.has(file.name)) : [entry]);
    }
  }

  function select(keys: Selection) {
    const files = namesOf(
      keys,
      rows.filter((entry) => entry.kind === 'file').map((entry) => entry.name),
    );
    setChosen(files);
    if (save && files[0] !== undefined) {
      setName(files[0]);
    }
  }

  function up() {
    if (folder !== '') {
      setFolder(folder.includes('/') ? folder.slice(0, folder.lastIndexOf('/')) : '');
      setChosen([]);
    } else if (share?.kind === 'userHome') {
      go(HOMES);
    }
  }

  const groups: NavGroup[] = [
    ...(places.own.length > 0
      ? [
          {
            id: 'own',
            items: places.own.map((home) => ({
              id: `share:${String(home.id)}`,
              label: shareName(home),
              icon: <IconHome size={16} stroke={1.75} aria-hidden />,
              isDisabled: home.unavailable != null,
            })),
          },
        ]
      : []),
    ...(places.shared.length > 0
      ? [
          {
            id: 'shared',
            label: t('appHost.files.shares'),
            items: places.shared.map((candidate) => ({
              id: `share:${String(candidate.id)}`,
              label: candidate.name,
              icon: <IconServer2 size={16} stroke={1.75} aria-hidden />,
              isDisabled: candidate.unavailable != null,
              ...(candidate.access === 'readWrite'
                ? {}
                : {
                    marker: (
                      <IconLock
                        size={14}
                        stroke={1.75}
                        aria-label={t('appHost.files.readOnlyPlace')}
                      />
                    ),
                  }),
            })),
          },
        ]
      : []),
    ...(places.homes.length > 0
      ? [
          {
            id: 'homes',
            items: [
              {
                id: HOMES,
                label: t('appHost.files.homes'),
                icon: <IconUsers size={16} stroke={1.75} aria-hidden />,
              },
            ],
          },
        ]
      : []),
  ];

  const columns: DataTableColumn<Entry>[] = [
    {
      id: 'name',
      label: t('appHost.files.columns.name'),
      isRowHeader: true,
      sortValue: (entry) => entry.name,
      cell: (entry) => (
        <span className={styles.entry}>
          {entry.kind === 'file' ? (
            <IconFile className={styles.fileIcon} size={16} stroke={1.75} aria-hidden />
          ) : (
            <IconFolder className={styles.folderIcon} size={16} stroke={1.75} aria-hidden />
          )}
          <span className={styles.name}>{entry.name}</span>
        </span>
      ),
    },
    {
      id: 'modified',
      label: t('appHost.files.columns.modified'),
      muted: true,
      width: 170,
      sortValue: (entry) => entry.modified,
      cell: (entry) =>
        entry.modified === ''
          ? ''
          : new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
              new Date(entry.modified),
            ),
    },
    {
      id: 'size',
      label: t('appHost.files.columns.size'),
      align: 'end',
      muted: true,
      width: 90,
      sortValue: (entry) => (entry.kind === 'file' ? entry.size : -1),
      cell: (entry) => (entry.kind === 'file' ? formatBytes(entry.size, locale) : ''),
    },
  ];

  const crumbs = folder === '' ? [] : folder.split('/');
  const title = t(save ? 'appHost.files.saveTitle' : 'appHost.files.pickTitle', {
    name: app === undefined ? '' : appName(app),
  });
  const types = request.extensions.map((extension) => `.${extension.toLowerCase()}`).join(', ');
  return (
    <Dialog
      isOpen
      isDismissable
      size="large"
      onOpenChange={(isOpen) => {
        if (!isOpen) {
          close();
        }
      }}
      title={title}
      actions={
        <>
          <Button onPress={close}>{t('appHost.files.cancel')}</Button>
          {save ? (
            <Button
              variant="primary"
              isDisabled={share === undefined || !writable || target === '' || target.includes('/')}
              onPress={() => {
                choose([
                  existing ?? {
                    name: target,
                    kind: 'file',
                    size: 0,
                    modified: new Date().toISOString(),
                  },
                ]);
              }}
            >
              {existing === undefined ? t('appHost.files.save') : t('appHost.files.replace')}
            </Button>
          ) : (
            <Button
              variant="primary"
              isDisabled={chosen.length === 0}
              onPress={() => {
                choose(entries.filter((entry) => chosen.includes(entry.name)));
              }}
            >
              {t('appHost.files.choose')}
            </Button>
          )}
        </>
      }
    >
      {shares.isPending ? (
        <p>{t('appHost.files.loading')}</p>
      ) : shares.isError ? (
        <p>{t('appHost.files.failed')}</p>
      ) : place === null ? (
        <p>{t('appHost.files.noShares')}</p>
      ) : (
        <div className={styles.dialog}>
          <NavList
            className={styles.places}
            aria-label={t('appHost.files.places')}
            groups={groups}
            selectedId={share?.kind === 'userHome' ? HOMES : place}
            onSelect={(id) => {
              go(id);
            }}
            searchable={false}
          />
          <div className={styles.main}>
            <div className={styles.address}>
              <IconButton
                label={t('appHost.files.up')}
                size="sm"
                isDisabled={folder === '' && share?.kind !== 'userHome'}
                onPress={up}
              >
                <IconArrowUp size={18} stroke={1.75} />
              </IconButton>
              <nav className={styles.crumbs} aria-label={t('appHost.files.location')}>
                {(atHomes || share?.kind === 'userHome') && (
                  <Crumb
                    label={t('appHost.files.homes')}
                    current={atHomes}
                    onPress={() => {
                      go(HOMES);
                    }}
                  />
                )}
                {share !== undefined && (
                  <Crumb
                    label={shareName(share)}
                    current={folder === ''}
                    separator={share.kind === 'userHome'}
                    onPress={() => {
                      setFolder('');
                      setChosen([]);
                    }}
                  />
                )}
                {crumbs.map((part, index) => {
                  const path = crumbs.slice(0, index + 1).join('/');
                  return (
                    <Crumb
                      key={path}
                      label={part}
                      current={path === folder}
                      separator
                      onPress={() => {
                        setFolder(path);
                        setChosen([]);
                      }}
                    />
                  );
                })}
              </nav>
            </div>
            {save && share !== undefined && !writable && (
              <p className={styles.infoBar}>
                <IconLock size={16} stroke={1.75} aria-hidden />
                {t('appHost.files.readOnly')}
              </p>
            )}
            {!atHomes && listing.isPending ? (
              <p className={styles.message}>{t('appHost.files.loading')}</p>
            ) : !atHomes && listing.isError ? (
              <p className={styles.message}>{t('appHost.files.failed')}</p>
            ) : (
              <div className={styles.tableBox}>
                <DataTable
                  key={`${place}:${folder}`}
                  aria-label={t('appHost.files.entries')}
                  columns={columns}
                  items={rows}
                  getKey={(entry) => entry.name}
                  selectionMode={
                    atHomes ? 'none' : !save && request.multiple ? 'multiple' : 'single'
                  }
                  selectedKeys={new Set(save ? (existing === undefined ? [] : [target]) : chosen)}
                  onSelectionChange={select}
                  onRowAction={open}
                  emptyState={t('appHost.files.empty')}
                />
              </div>
            )}
            {!atHomes && listing.hasNextPage && (
              <Button
                size="sm"
                variant="quiet"
                onPress={() => {
                  void listing.fetchNextPage();
                }}
              >
                {t('appHost.files.more')}
              </Button>
            )}
            {save ? (
              <div className={styles.footer}>
                <TextField
                  label={t('appHost.files.name')}
                  value={name}
                  onChange={setName}
                  autoFocus
                />
                {existing !== undefined && (
                  <p className={styles.hint} role="status">
                    {t('appHost.files.exists')}
                  </p>
                )}
              </div>
            ) : (
              request.extensions.length > 0 && (
                <p className={styles.hint}>{t('appHost.files.types', { types })}</p>
              )
            )}
          </div>
        </div>
      )}
    </Dialog>
  );
}

/** A part of the path: a button that goes there, or the current folder as plain text. */
function Crumb({
  label,
  current,
  separator = false,
  onPress,
}: {
  label: string;
  current: boolean;
  separator?: boolean;
  onPress: () => void;
}) {
  const chevron = separator && (
    <IconChevronRight className={styles.separator} size={14} stroke={1.75} aria-hidden />
  );
  return current ? (
    <span className={styles.crumb} aria-current="location">
      {chevron}
      {label}
    </span>
  ) : (
    <button type="button" className={styles.crumb} onClick={onPress}>
      {chevron}
      {label}
    </button>
  );
}
