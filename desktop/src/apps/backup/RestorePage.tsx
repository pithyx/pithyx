import type { PithyxApp } from '@pithyx/sdk';
import {
  Button,
  DataTable,
  Dialog,
  Radio,
  RadioGroup,
  Select,
  TextField,
  Toolbar,
  type DataTableColumn,
} from '@pithyx/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type MouseEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { useRealtime } from '../../realtime/connection';
import {
  IconArrowLeft,
  IconArrowRight,
  IconArrowUp,
  IconChevronRight,
  IconDownload,
  IconEye,
  IconFile,
  IconFolder,
  IconHistory,
  IconRestore,
} from '../../ui/icons';
import {
  backupsCall,
  browseKey,
  sourcesKey,
  versionsKey,
  type BackupFileEntry,
  type BackupFileVersion,
  type BackupJob,
  type BackupRestoreConflict,
  type BackupRestoreResult,
  type BackupVersion,
} from '../sdk/backups';
import { describeProblem, formatDate } from '../storage/shared';
import type { BoardJob } from './data';
import { EmptyCard } from './EmptyCard';
import styles from './Backup.module.css';

/** Where the page starts: a backup and one of its versions, for example from the backup's own page. */
export interface RestoreStart {
  jobId: number;
  versionId?: number;
}

const isClosed = (error: unknown) =>
  error instanceof Error && (error as { code?: unknown }).code === 'backup.browse.closed';

/** Calls a read of a version and opens the job's browse session first when it is closed. */
async function browsing<T>(app: PithyxApp, id: number, read: () => Promise<T>): Promise<T> {
  try {
    return await read();
  } catch (error) {
    if (!isClosed(error)) {
      throw error;
    }
    await backupsCall(app, 'internal.admin.backups.browse', { id });
    return read();
  }
}

/** The last part of a path; the top of a version names a folder with the share and the path below it. */
const lastPart = (path: string) => path.slice(path.lastIndexOf('/') + 1);

const CHANGE_TONES = { added: 'success', modified: 'accent', removed: 'danger' } as const;

/**
 * Restoring (#395, D419): a backup and one of its versions, picked from a list or on a timeline, and its files and
 * folders as they were, each marked when it changed against the version before. Selected entries go back where they
 * were or into another folder, with their owners, permissions and times; a file in place is kept, replaced or left.
 * The selected file's versions sit in the details pane, each to download or to put next to the current one.
 */
export function RestorePage({
  app,
  jobs,
  start,
  onAddJob,
}: {
  app: PithyxApp;
  jobs: readonly BackupJob[] | undefined;
  start: RestoreStart | null;
  onAddJob: () => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const [jobId, setJobId] = useState<number | null>(start?.jobId ?? null);
  const [versionId, setVersionId] = useState<number | null>(start?.versionId ?? null);
  // The opened folders: names from the lists, so a folder at the top of a version that names a share and a path in
  // it stays one step.
  const [trail, setTrail] = useState<string[]>([]);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [restoring, setRestoring] = useState<string[] | null>(null);
  const [boardId, setBoardId] = useState<number | null>(null);

  const job = jobs?.find((entry) => entry.id === jobId) ?? jobs?.[0];
  const versions = useQuery({
    queryKey: [...versionsKey(job?.id ?? 0), job?.lastRun?.id, job?.lastRun?.state],
    queryFn: () => backupsCall(app, 'internal.admin.backups.versions', { id: job?.id ?? 0 }),
    enabled: job !== undefined,
  });
  const list = job === undefined ? [] : (versions.data?.versions ?? []);
  const version = list.find((entry) => entry.id === versionId) ?? list[0];
  const path = trail.join('/');
  const files = useQuery({
    queryKey: [...browseKey(job?.id ?? 0), version?.id, path],
    queryFn: () =>
      browsing(app, job?.id ?? 0, () =>
        backupsCall(app, 'internal.admin.backups.files', {
          id: job?.id ?? 0,
          versionId: version?.id ?? 0,
          path,
        }),
      ),
    enabled: job !== undefined && version !== undefined,
    // No list of the folder before: a selection in it would name entries of the wrong folder.
    retry: false,
  });

  const open = (next: string[]) => {
    setTrail(next);
    setSelected(new Set());
  };
  // The selection stays, so the same file can be compared across versions; only what the version has counts.
  const pickVersion = (id: number) => {
    setVersionId(id);
  };
  const pickJob = (id: number) => {
    setJobId(id);
    setVersionId(null);
    open([]);
  };

  if (jobs?.length === 0) {
    return (
      <div className={styles.page}>
        <p className={styles.intro}>{t('restore.intro')}</p>
        <EmptyCard
          icon={<IconRestore size={24} stroke={1.75} />}
          title={t('restore.noJobsTitle')}
          text={t('restore.noJobs')}
          action={t('jobs.add')}
          onAction={onAddJob}
        />
      </div>
    );
  }

  const entries = files.data?.entries ?? [];
  const full = (name: string) => (path === '' ? name : `${path}/${name}`);
  const picked = entries.filter((entry) => selected.has(entry.name));
  const single = picked.length === 1 ? picked[0] : undefined;
  const selectedBytes = picked.reduce((total, entry) => total + (entry.size ?? 0), 0);
  const index = version === undefined ? -1 : list.indexOf(version);

  return (
    <div className={styles.page}>
      <p className={styles.intro}>{t('restore.intro')}</p>
      <Toolbar aria-label={t('restore.toolbar')}>
        <Select
          label={t('restore.backup')}
          options={(jobs ?? []).map((entry) => ({
            id: String(entry.id),
            label: entry.settings.name,
          }))}
          value={job === undefined ? null : String(job.id)}
          onChange={(value) => {
            pickJob(Number(value));
          }}
        />
        <VersionSelect list={list} version={version} onPick={pickVersion} />
        <Button
          aria-label={t('restore.older')}
          isDisabled={index < 0 || index >= list.length - 1}
          onPress={() => {
            const older = list[index + 1];
            if (older !== undefined) {
              pickVersion(older.id);
            }
          }}
        >
          <IconArrowLeft size={18} stroke={1.75} aria-hidden />
        </Button>
        <Button
          aria-label={t('restore.newer')}
          isDisabled={index <= 0}
          onPress={() => {
            const newer = list[index - 1];
            if (newer !== undefined) {
              pickVersion(newer.id);
            }
          }}
        >
          <IconArrowRight size={18} stroke={1.75} aria-hidden />
        </Button>
      </Toolbar>
      {versions.isError && <ErrorNotice error={describeProblem(versions.error)} />}
      {job !== undefined && versions.isSuccess && list.length === 0 && (
        <p className={styles.muted}>{t('restore.noVersions')}</p>
      )}
      {list.length > 1 && <Timeline list={list} version={version} onPick={pickVersion} />}
      {version !== undefined && job !== undefined && (
        <>
          <div className={styles.restorePath}>
            <Button
              size="sm"
              variant="quiet"
              aria-label={t('restore.up')}
              isDisabled={trail.length === 0}
              onPress={() => {
                open(trail.slice(0, -1));
              }}
            >
              <IconArrowUp size={16} stroke={1.75} aria-hidden />
            </Button>
            <nav className={styles.breadcrumb} aria-label={t('restore.path')}>
              {trail.length === 0 ? (
                <span aria-current="page">{t('restore.top')}</span>
              ) : (
                <button
                  type="button"
                  className={styles.breadcrumbLink}
                  onClick={() => {
                    open([]);
                  }}
                >
                  {t('restore.top')}
                </button>
              )}
              {trail.map((part, depth) => (
                <span key={`${String(depth)}:${part}`} className={styles.breadcrumb}>
                  <IconChevronRight size={14} stroke={1.75} aria-hidden />
                  {depth === trail.length - 1 ? (
                    <span aria-current="page">{part}</span>
                  ) : (
                    <button
                      type="button"
                      className={styles.breadcrumbLink}
                      onClick={() => {
                        open(trail.slice(0, depth + 1));
                      }}
                    >
                      {part}
                    </button>
                  )}
                </span>
              ))}
            </nav>
            {files.data?.compared === true && (
              <span className={styles.restoreHint}>
                <IconEye size={14} stroke={1.75} aria-hidden />
                {t('restore.compared')}
              </span>
            )}
          </div>
          {files.isError && <ErrorNotice error={describeProblem(files.error)} />}
          <FileTable
            entries={entries}
            pending={files.isPending}
            selected={selected}
            onSelect={setSelected}
            onOpen={(entry) => {
              open([...trail, entry.name]);
            }}
            details={
              single === undefined ? undefined : (
                <EntryDetails
                  app={app}
                  job={job}
                  version={version}
                  entry={single}
                  path={full(single.name)}
                  onPickVersion={pickVersion}
                  onRestored={setBoardId}
                />
              )
            }
          />
          <RestoreStatus boardId={boardId} />
          <div className={styles.restoreBar}>
            <span>
              {picked.length === 0
                ? t('restore.noneSelected')
                : selectedBytes === 0
                  ? t('restore.selected', { count: picked.length })
                  : t('restore.selectedFiles', {
                      count: picked.length,
                      size: formatBytes(selectedBytes, locale),
                    })}
            </span>
            <Button
              variant="primary"
              isDisabled={picked.length === 0}
              onPress={() => {
                setRestoring(picked.map((entry) => full(entry.name)));
              }}
            >
              <IconRestore size={18} stroke={1.75} aria-hidden />
              {t('restore.restore')}
            </Button>
          </div>
          {restoring !== null && (
            <RestoreDialog
              app={app}
              job={job}
              version={version}
              paths={restoring}
              onClose={() => {
                setRestoring(null);
              }}
              onStarted={(id) => {
                setRestoring(null);
                setSelected(new Set());
                setBoardId(id);
              }}
            />
          )}
        </>
      )}
    </div>
  );
}

/** The versions newest first, as a select. */
function VersionSelect({
  list,
  version,
  onPick,
}: {
  list: readonly BackupVersion[];
  version: BackupVersion | undefined;
  onPick: (id: number) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  return (
    <Select
      label={t('restore.version')}
      isDisabled={list.length === 0}
      options={list.map((entry) => ({
        id: String(entry.id),
        label: formatDate(entry.time, locale),
      }))}
      value={version === undefined ? null : String(version.id)}
      onChange={(value) => {
        onPick(Number(value));
      }}
    />
  );
}

/** The versions on a line from the oldest to the newest, by their time; a mark picks one. */
function Timeline({
  list,
  version,
  onPick,
}: {
  list: readonly BackupVersion[];
  version: BackupVersion | undefined;
  onPick: (id: number) => void;
}) {
  const locale = useFormatLocale();
  const times = list.map((entry) => Date.parse(entry.time));
  const first = Math.min(...times);
  const span = Math.max(...times) - first || 1;
  const day = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' });
  const positions = times.map((time) => (time - first) / span);
  // One wide target for pointers that picks the nearest version: daily marks sit a few pixels apart, too close to
  // be targets of their own. Keyboards and screen readers pick versions with the select and older and newer.
  const pickAt = (event: MouseEvent<HTMLButtonElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    const at = (event.clientX - box.left) / (box.width || 1);
    let nearest = 0;
    positions.forEach((position, index) => {
      if (Math.abs(position - at) < Math.abs((positions[nearest] ?? 0) - at)) {
        nearest = index;
      }
    });
    const entry = list[nearest];
    if (entry !== undefined) {
      onPick(entry.id);
    }
  };
  return (
    <div className={styles.timeline} aria-hidden>
      <button type="button" tabIndex={-1} className={styles.timelineTrack} onClick={pickAt}>
        {list.map((entry, position) => (
          <span
            key={entry.id}
            className={styles.timelineMark}
            style={{ left: `${String((positions[position] ?? 0) * 100)}%` }}
            data-current={entry.id === version?.id || undefined}
            data-protected={entry.protected || undefined}
            title={formatDate(entry.time, locale)}
          />
        ))}
      </button>
      <div className={styles.timelineEnds}>
        <span>{day.format(first)}</span>
        <span>{day.format(first + span)}</span>
      </div>
    </div>
  );
}

/** The entries of a folder of a version, folders first, with what changed against the version before. */
function FileTable({
  entries,
  pending,
  selected,
  onSelect,
  onOpen,
  details,
}: {
  entries: readonly BackupFileEntry[];
  pending: boolean;
  selected: ReadonlySet<string>;
  onSelect: (keys: ReadonlySet<string>) => void;
  onOpen: (entry: BackupFileEntry) => void;
  details: ReactNode;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const columns: DataTableColumn<BackupFileEntry>[] = [
    {
      id: 'name',
      label: t('restore.name'),
      isRowHeader: true,
      sortValue: (entry) => `${entry.kind === 'folder' ? '0' : '1'}${entry.name.toLowerCase()}`,
      cell: (entry) => (
        <span className={styles.entryName}>
          {entry.kind === 'folder' ? (
            <IconFolder size={16} stroke={1.75} aria-hidden />
          ) : (
            <IconFile size={16} stroke={1.75} aria-hidden />
          )}
          <span className={styles.breakable}>{entry.name}</span>
        </span>
      ),
    },
    {
      id: 'modified',
      label: t('restore.modified'),
      hideWhenNarrow: true,
      sortValue: (entry) => entry.modified,
      cell: (entry) => formatDate(entry.modified, locale),
    },
    {
      id: 'size',
      label: t('jobs.size'),
      align: 'end',
      sortValue: (entry) => entry.size ?? -1,
      cell: (entry) => (entry.size === null ? '' : formatBytes(entry.size, locale)),
    },
    {
      id: 'change',
      label: t('restore.change'),
      cell: (entry) =>
        entry.change === null ? null : (
          <span className={styles.tag} data-tone={CHANGE_TONES[entry.change]}>
            {t(`restore.changes.${entry.change}`)}
          </span>
        ),
    },
  ];
  const byName = new Map(entries.map((entry) => [entry.name, entry]));
  return (
    <DataTable
      aria-label={t('restore.files')}
      columns={columns}
      items={entries}
      getKey={(entry) => entry.name}
      selectionMode="multiple"
      selectedKeys={new Set(selected)}
      onSelectionChange={(keys) => {
        onSelect(
          new Set(
            keys === 'all'
              ? entries.map((entry) => entry.name)
              : [...keys].map((key) => String(key)),
          ),
        );
      }}
      onRowAction={(key) => {
        const entry = byName.get(String(key));
        if (entry?.kind === 'folder') {
          onOpen(entry);
        }
      }}
      defaultSort={{ column: 'name', direction: 'ascending' }}
      emptyState={pending ? t('restore.loading') : t('restore.empty')}
      details={details}
      detailsLabel={t('restore.details')}
    />
  );
}

/** A file or folder: its versions, the one shown with its owner and permissions, and what can be done with it. */
function EntryDetails({
  app,
  job,
  version,
  entry,
  path,
  onPickVersion,
  onRestored,
}: {
  app: PithyxApp;
  job: BackupJob;
  version: BackupVersion;
  entry: BackupFileEntry;
  path: string;
  onPickVersion: (id: number) => void;
  onRestored: (boardId: number) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const history = useQuery({
    queryKey: [...browseKey(job.id), 'history', path],
    queryFn: () =>
      browsing(app, job.id, () =>
        backupsCall(app, 'internal.admin.backups.fileHistory', { id: job.id, path }),
      ),
    retry: false,
  });
  const beside = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.restore', {
        id: job.id,
        versionId: version.id,
        paths: [path],
        destination: null,
        conflict: 'keepBoth',
      }),
    onSuccess: (board) => {
      onRestored(board.id);
    },
  });
  const shown = history.data?.versions.find((entry) => entry.versionId === version.id);
  const current = history.data?.current;
  const size = (value: number | null | undefined) =>
    value == null ? '' : formatBytes(value, locale);
  const describe = (entry: BackupFileVersion) =>
    entry.kind === null ? t('restore.missing') : size(entry.size);
  return (
    <div className={styles.details}>
      <div className={styles.detailsHead}>
        <span className={`${styles.detailsName} ${styles.breakable}`}>{entry.name}</span>
        <span className={`${styles.muted} ${styles.breakable}`}>{path}</span>
      </div>
      <div className={styles.row}>
        <Button
          size="sm"
          onPress={() => {
            void backupsCall(app, 'internal.admin.backups.download', {
              id: job.id,
              versionId: version.id,
              path,
              name: entry.kind === 'folder' ? `${lastPart(path)}.zip` : lastPart(path),
            });
          }}
        >
          <IconDownload size={16} stroke={1.75} aria-hidden />
          {t('restore.download')}
        </Button>
        <Button
          size="sm"
          isDisabled={beside.isPending}
          onPress={() => {
            beside.mutate();
          }}
        >
          <IconRestore size={16} stroke={1.75} aria-hidden />
          {t('restore.beside')}
        </Button>
      </div>
      {beside.isError && <ErrorNotice error={describeProblem(beside.error)} />}
      {shown?.kind != null && (
        <dl className={styles.facts}>
          <dt>{t('restore.owner')}</dt>
          <dd>{shown.owner ?? ''}</dd>
          <dt>{t('restore.mode')}</dt>
          <dd className={styles.mono}>{shown.mode ?? ''}</dd>
          <dt>{t('restore.modified')}</dt>
          <dd>{shown.modified === null ? '' : formatDate(shown.modified, locale)}</dd>
        </dl>
      )}
      <div className={styles.field}>
        <h3 className={styles.detailsTitle}>{t('restore.versionsOf')}</h3>
        {history.isError && <ErrorNotice error={describeProblem(history.error)} />}
        {history.isPending && <p className={styles.muted}>{t('restore.loading')}</p>}
        {history.data !== undefined && (
          <ul className={styles.fileVersions}>
            <li>
              <span>{t('restore.current')}</span>
              <span className={styles.muted}>
                {current == null
                  ? t('restore.missing')
                  : [size(current.size), formatDate(current.modified, locale)]
                      .filter((part) => part !== '')
                      .join(' · ')}
              </span>
            </li>
            {history.data.versions.map((entry) => (
              <li key={entry.versionId}>
                <button
                  type="button"
                  className={styles.breadcrumbLink}
                  aria-current={entry.versionId === version.id || undefined}
                  onClick={() => {
                    onPickVersion(entry.versionId);
                  }}
                >
                  <IconHistory size={14} stroke={1.75} aria-hidden />
                  {formatDate(entry.time, locale)}
                </button>
                <span className={styles.muted}>{describe(entry)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

type Place = 'original' | 'folder';

/** A choice's label with a line about what it does below. */
function Choice({ label, hint }: { label: string; hint: string }) {
  return (
    <span className={styles.choice}>
      <span>{label}</span>
      <span className={styles.choiceHint}>{hint}</span>
    </span>
  );
}

/** Where the selected entries go and what happens with a file that is already there. */
function RestoreDialog({
  app,
  job,
  version,
  paths,
  onClose,
  onStarted,
}: {
  app: PithyxApp;
  job: BackupJob;
  version: BackupVersion;
  paths: string[];
  onClose: () => void;
  onStarted: (boardId: number) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const [place, setPlace] = useState<Place>('original');
  const [shareId, setShareId] = useState<number | null>(null);
  const [folder, setFolder] = useState('');
  const [conflict, setConflict] = useState<BackupRestoreConflict>('keepBoth');
  const sources = useQuery({
    queryKey: sourcesKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.sources', {}),
  });
  const shares = (sources.data?.sources ?? []).filter((source) => source.available);
  const share = shares.find((source) => source.shareId === shareId) ?? shares[0];
  const restore = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.restore', {
        id: job.id,
        versionId: version.id,
        paths,
        destination:
          place === 'original' || share === undefined
            ? null
            : { shareId: share.shareId, path: folder.trim() },
        conflict,
      }),
    onSuccess: (board) => {
      onStarted(board.id);
    },
  });
  const ready = place === 'original' || share !== undefined;
  return (
    <Dialog
      isOpen
      onOpenChange={(isOpen) => {
        if (!isOpen) {
          onClose();
        }
      }}
      title={t('restore.dialogTitle', { count: paths.length })}
      size="medium"
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="primary"
            type="submit"
            form="backup-restore"
            isDisabled={!ready || restore.isPending}
          >
            {t('restore.start')}
          </Button>
        </>
      }
    >
      <form
        id="backup-restore"
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (ready) {
            restore.mutate();
          }
        }}
      >
        <p className={styles.muted}>
          {t('restore.dialogIntro', {
            name: job.settings.name,
            time: formatDate(version.time, locale),
          })}
        </p>
        <RadioGroup
          label={t('restore.place')}
          value={place}
          onChange={(value) => {
            setPlace(value === 'folder' ? 'folder' : 'original');
          }}
        >
          <Radio value="original">
            <Choice label={t('restore.places.original')} hint={t('restore.places.originalHint')} />
          </Radio>
          <Radio value="folder">
            <Choice label={t('restore.places.folder')} hint={t('restore.places.folderHint')} />
          </Radio>
        </RadioGroup>
        {place === 'folder' && (
          <>
            <Select
              label={t('restore.share')}
              options={shares.map((source) => ({ id: String(source.shareId), label: source.name }))}
              value={share === undefined ? null : String(share.shareId)}
              onChange={(value) => {
                setShareId(Number(value));
              }}
            />
            <TextField
              label={t('restore.folder')}
              description={t('restore.folderHint')}
              value={folder}
              onChange={setFolder}
              autoCapitalize="none"
            />
          </>
        )}
        <RadioGroup
          label={t('restore.conflict')}
          value={conflict}
          onChange={(value) => {
            setConflict(value as BackupRestoreConflict);
          }}
        >
          <Radio value="keepBoth">
            <Choice
              label={t('restore.conflicts.keepBoth')}
              hint={t('restore.conflicts.keepBothHint')}
            />
          </Radio>
          <Radio value="overwrite">
            <Choice
              label={t('restore.conflicts.overwrite')}
              hint={t('restore.conflicts.overwriteHint')}
            />
          </Radio>
          <Radio value="skip">
            <Choice label={t('restore.conflicts.skip')} hint={t('restore.conflicts.skipHint')} />
          </Radio>
        </RadioGroup>
        {restore.isError && <ErrorNotice error={describeProblem(restore.error)} />}
      </form>
    </Dialog>
  );
}

/** The restore started last from this page, as the job board shows it: its progress, what it did or why it failed. */
function RestoreStatus({ boardId }: { boardId: number | null }) {
  const board = useRealtime((state) =>
    boardId === null ? undefined : state.data.jobs?.jobs.find((job) => job.id === boardId),
  );
  if (board === undefined) {
    return null;
  }
  return <RestoreState board={board} />;
}

function RestoreState({ board }: { board: BoardJob }) {
  const { t } = useTranslation('backup');
  if (board.state === 'queued' || board.state === 'running') {
    const { bytesDone, bytesTotal } = board.progress;
    const percent = bytesTotal > 0 ? Math.floor((bytesDone / bytesTotal) * 100) : null;
    return (
      <p className={styles.muted} role="status">
        {percent === null ? t('restore.running') : t('restore.runningPercent', { percent })}
      </p>
    );
  }
  if (board.state === 'succeeded') {
    const result = board.result as BackupRestoreResult | null;
    return (
      <p className={styles.success} role="status">
        {result === null
          ? t('restore.done')
          : t('restore.doneCounts', {
              restored: result.restored,
              replaced: result.replaced,
              renamed: result.renamed,
              skipped: result.skipped,
            })}
      </p>
    );
  }
  if (board.errorCode === null) {
    return null;
  }
  return (
    <ErrorNotice
      error={describeProblem({
        code: board.errorCode,
        params: (board.errorParams ?? {}) as Record<string, unknown>,
        requestId: '',
      })}
    />
  );
}
