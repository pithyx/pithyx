import { unwrap, type components } from '@pithyx/api-client';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Button, Checkbox, Dialog, TextField } from '@pithyx/ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { IconFile, IconFolder, IconHome, IconServer2, IconUsers } from '../../ui/icons';
import { useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import styles from './FileDialog.module.css';
import { answerFileDialog, type ChosenFile, type FileDialogRequest } from './store';

type Share = components['schemas']['ShareInfo'];
type Entry = components['schemas']['FileEntry'];

const PAGE_SIZE = 200;

/** Whether a file name has one of the extensions (without dot, lowercase); no extensions means every file. */
export function matchesExtensions(name: string, extensions: readonly string[]): boolean {
  const lower = name.toLowerCase();
  return (
    extensions.length === 0 ||
    extensions.some((extension) => lower.endsWith(`.${extension.toLowerCase()}`))
  );
}

/**
 * The top level of the dialog: the user's own home first, then the shared folders; other users' homes (which only
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

/** The path of a name in a folder of a share. */
export function pathIn(folder: string, name: string): string {
  return folder === '' ? name : `${folder}/${name}`;
}

/**
 * The desktop's file dialog for an app's `files.pick` and `files.saveAs`: the user's shared folders and their files,
 * drawn and answered by the desktop. The app learns nothing but the files the user chose; the dialog reads the folders
 * with the user's own session.
 */
export function FileDialog({ request }: { request: FileDialogRequest }) {
  const { t } = useTranslation();
  const appName = useAppName();
  const app = useDesktop((state) => state.windows[request.windowId]?.app);
  const save = request.kind === 'save';
  const [shareId, setShareId] = useState<number | null>(null);
  const [inHomes, setInHomes] = useState(false);
  const [folder, setFolder] = useState('');
  const [chosen, setChosen] = useState<readonly string[]>([]);
  const [name, setName] = useState(request.suggestedName);

  const shares = useQuery({
    queryKey: ['appHost', 'shares'],
    queryFn: async () => unwrap(await api.GET('/api/shares')),
  });
  const share = shares.data?.find((candidate) => candidate.id === shareId);
  const places = placesOf(shares.data ?? []);
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
  const existing = entries.find((entry) => entry.kind === 'file' && entry.name === name.trim());
  const target = name.trim();

  function close() {
    answerFileDialog(null);
  }

  function choose(files: Entry[]) {
    if (share === undefined) {
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

  function open(entry: Entry) {
    if (entry.kind === 'file') {
      if (save) {
        setName(entry.name);
      } else if (!request.multiple) {
        choose([entry]);
      } else {
        toggle(entry.name);
      }
    } else {
      setFolder(pathIn(folder, entry.name));
      setChosen([]);
    }
  }

  function toggle(entryName: string) {
    setChosen((current) =>
      current.includes(entryName)
        ? current.filter((candidate) => candidate !== entryName)
        : [...current, entryName],
    );
  }

  const crumbs = folder === '' ? [] : folder.split('/');
  const title = t(save ? 'appHost.files.saveTitle' : 'appHost.files.pickTitle', {
    name: app === undefined ? '' : appName(app),
  });
  return (
    <Dialog
      isOpen
      isDismissable
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
      <div className={styles.dialog}>
        {share === undefined ? (
          shares.isPending ? (
            <p>{t('appHost.files.loading')}</p>
          ) : shares.isError ? (
            <p>{t('appHost.files.failed')}</p>
          ) : shares.data.length === 0 ? (
            <p>{t('appHost.files.noShares')}</p>
          ) : (
            <>
              {inHomes && (
                <nav className={styles.crumbs} aria-label={t('appHost.files.location')}>
                  <button
                    type="button"
                    className={styles.crumb}
                    onClick={() => {
                      setInHomes(false);
                    }}
                  >
                    {t('appHost.files.shares')}
                  </button>
                  <span className={styles.crumb}>{t('appHost.files.homes')}</span>
                </nav>
              )}
              <ul
                className={styles.list}
                aria-label={inHomes ? t('appHost.files.homes') : t('appHost.files.shares')}
              >
                {(inHomes ? places.homes : [...places.own, ...places.shared]).map((candidate) => (
                  <li key={candidate.id}>
                    <button
                      type="button"
                      className={styles.row}
                      disabled={candidate.unavailable != null}
                      onClick={() => {
                        setShareId(candidate.id);
                        setFolder('');
                        setChosen([]);
                      }}
                    >
                      {candidate.kind === 'home' ? (
                        <IconHome size={18} stroke={1.75} aria-hidden />
                      ) : candidate.kind === 'userHome' ? (
                        <IconFolder size={18} stroke={1.75} aria-hidden />
                      ) : (
                        <IconServer2 size={18} stroke={1.75} aria-hidden />
                      )}
                      <span className={styles.name}>{shareName(candidate)}</span>
                    </button>
                  </li>
                ))}
                {!inHomes && places.homes.length > 0 && (
                  <li>
                    <button
                      type="button"
                      className={styles.row}
                      onClick={() => {
                        setInHomes(true);
                      }}
                    >
                      <IconUsers size={18} stroke={1.75} aria-hidden />
                      <span className={styles.name}>{t('appHost.files.homes')}</span>
                    </button>
                  </li>
                )}
              </ul>
            </>
          )
        ) : (
          <>
            <nav className={styles.crumbs} aria-label={t('appHost.files.location')}>
              <button
                type="button"
                className={styles.crumb}
                onClick={() => {
                  setShareId(null);
                  setInHomes(false);
                  setFolder('');
                  setChosen([]);
                }}
              >
                {t('appHost.files.shares')}
              </button>
              {share.kind === 'userHome' && (
                <button
                  type="button"
                  className={styles.crumb}
                  onClick={() => {
                    setShareId(null);
                    setInHomes(true);
                    setFolder('');
                    setChosen([]);
                  }}
                >
                  {t('appHost.files.homes')}
                </button>
              )}
              <button
                type="button"
                className={styles.crumb}
                onClick={() => {
                  setFolder('');
                }}
              >
                {shareName(share)}
              </button>
              {crumbs.map((part, index) => (
                <button
                  key={crumbs.slice(0, index + 1).join('/')}
                  type="button"
                  className={styles.crumb}
                  onClick={() => {
                    setFolder(crumbs.slice(0, index + 1).join('/'));
                  }}
                >
                  {part}
                </button>
              ))}
            </nav>
            {listing.isPending ? (
              <p>{t('appHost.files.loading')}</p>
            ) : listing.isError ? (
              <p>{t('appHost.files.failed')}</p>
            ) : visible.length === 0 ? (
              <p>{t('appHost.files.empty')}</p>
            ) : (
              <ul className={styles.list} aria-label={t('appHost.files.entries')}>
                {visible.map((entry) => (
                  <li key={entry.name} className={styles.item}>
                    {!save && request.multiple && entry.kind === 'file' ? (
                      <Checkbox
                        isSelected={chosen.includes(entry.name)}
                        onChange={() => {
                          toggle(entry.name);
                        }}
                      >
                        <IconFile size={18} stroke={1.75} aria-hidden />
                        <span className={styles.name}>{entry.name}</span>
                      </Checkbox>
                    ) : (
                      <button
                        type="button"
                        className={styles.row}
                        onClick={() => {
                          open(entry);
                        }}
                      >
                        {entry.kind === 'file' ? (
                          <IconFile size={18} stroke={1.75} aria-hidden />
                        ) : (
                          <IconFolder size={18} stroke={1.75} aria-hidden />
                        )}
                        <span className={styles.name}>{entry.name}</span>
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {listing.hasNextPage && (
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
            {save && (
              <>
                <TextField
                  label={t('appHost.files.name')}
                  value={name}
                  onChange={setName}
                  autoFocus
                />
                {!writable && <p role="alert">{t('appHost.files.readOnly')}</p>}
                {existing !== undefined && <p role="status">{t('appHost.files.exists')}</p>}
              </>
            )}
          </>
        )}
      </div>
    </Dialog>
  );
}
