import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, Dialog } from '@pithyx/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { AppIcon } from '../../shell/AppIcon';
import { manifestText } from '../installed';
import { storeCall, type AppAccess, type AppPackagePreview } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { JobProgress, useAppJob } from './jobs';
import { appPools, manifestFacts } from './model';
import { PermissionText } from './PermissionText';
import { StorageSection } from './StorageSection';
import { useInstalled } from './StoreApp';
import styles from './Store.module.css';

/** A version of a catalog app to install instead of a package file. */
export interface CatalogVersion {
  catalog: string;
  appId: string;
  version: string;
  /** The catalog is one of the project's, signed with a key built into the box. */
  verified: boolean;
}

/**
 * Installs an app from its package file or a catalog, or updates an installed one: the daemon checks the upload (or
 * the catalog's package) and answers what
 * installing does; the dialog shows it (permissions with their meaning, folders, background service, root, special
 * rights), asks who may use a new app (prefilled after D304) and, for the first app, the pool for app data. The
 * install or update runs as a job.
 */
export function InstallDialog({
  app,
  updating,
  catalog,
  onClose,
}: {
  app: PithyxApp;
  /** The name of the app being updated, for the title. */
  updating?: string;
  /** Installs this catalog version instead of asking for a file. */
  catalog?: CatalogVersion;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const ids = useId();
  const fileInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [access, setAccess] = useState<AppAccess | null>(null);
  const [pool, setPool] = useState<string | null>(null);
  const { job, start } = useAppJob(app);
  const upload = useMutation({
    mutationFn: (chosen: File) =>
      storeCall(app, 'internal.admin.apps.upload', { file: chosen, name: chosen.name }),
  });
  const staged = useQuery({
    queryKey: ['store', 'stage', catalog?.catalog, catalog?.appId, catalog?.version],
    queryFn: () =>
      storeCall(app, 'internal.admin.store.stage', {
        catalog: catalog?.catalog ?? '',
        appId: catalog?.appId ?? '',
        version: catalog?.version ?? '',
      }),
    enabled: catalog !== undefined,
    // A staged package is the daemon's for 15 minutes; it is fetched once per dialog.
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
  });
  const preview = catalog === undefined ? upload.data : staged.data;
  const needsPool = preview?.pool === null;
  const pools = useQuery({
    queryKey: ['store', 'pools'],
    queryFn: () => storeCall(app, 'internal.admin.apps.pools', {}),
    enabled: needsPool,
  });
  const choices = pools.data === undefined ? [] : appPools(pools.data);
  const chosenPool = pool ?? choices[0] ?? null;
  const chosenAccess = access ?? preview?.suggestedAccess ?? 'everybody';
  const install = useMutation({
    mutationFn: (checked: AppPackagePreview) =>
      storeCall(app, 'internal.admin.apps.install', {
        packageId: checked.packageId,
        ...(needsPool && chosenPool !== null ? { pool: chosenPool } : {}),
        ...(checked.action === 'install' ? { access: chosenAccess } : {}),
      }),
    onSuccess: start,
  });
  const facts = preview === undefined ? undefined : manifestFacts(preview.manifest);
  const name = facts === undefined ? '' : manifestText(facts.name, i18n.language);
  const isUpdate = preview !== undefined && preview.action !== 'install';
  // Once installed, the folders it asks for are mapped here; a backend waits for its required ones (D360).
  const installed = useInstalled(app);
  const details =
    job?.state === 'succeeded' && preview !== undefined
      ? installed.data?.apps.find((entry) => entry.id === preview.id)
      : undefined;
  const requests = facts?.storage ?? [];
  const waitsForFolders = requests.some(
    (request) =>
      request.required && !details?.storage.some((entry) => entry.storageId === request.id),
  );

  function close() {
    // A checked package nobody installs is discarded at once instead of after 15 minutes.
    if (preview !== undefined && job === null) {
      void storeCall(app, 'internal.admin.apps.discard', { packageId: preview.packageId }).catch(
        () => undefined,
      );
    }
    onClose();
  }

  const actions = (
    <>
      {job?.state === 'succeeded' && preview !== undefined && !waitsForFolders && (
        <Button
          onPress={() => {
            void storeCall(app, 'internal.admin.apps.open', { appId: preview.id });
            onClose();
          }}
        >
          {t('install.openApp')}
        </Button>
      )}
      <Button onPress={close}>{job === null ? t('install.cancel') : t('install.close')}</Button>
      {job === null && (
        <Button
          variant="primary"
          isDisabled={
            preview === undefined || install.isPending || (needsPool && chosenPool === null)
          }
          onPress={() => {
            if (preview !== undefined) {
              install.mutate(preview);
            }
          }}
        >
          {isUpdate ? t('install.update') : t('install.install')}
        </Button>
      )}
    </>
  );

  return (
    <Dialog
      isOpen
      size="large"
      onOpenChange={(open) => {
        if (!open) {
          close();
        }
      }}
      title={
        updating === undefined ? t('install.title') : t('install.updateTitle', { name: updating })
      }
      actions={actions}
    >
      <div className={styles.form}>
        {job === null && catalog === undefined && (
          <div className={styles.field}>
            <span id={`${ids}-file`} className={styles.fieldLabel}>
              {t('install.file')}
            </span>
            <div className={styles.filePicker}>
              <Button
                aria-describedby={`${ids}-name`}
                isDisabled={upload.isPending}
                onPress={() => {
                  fileInput.current?.click();
                }}
              >
                {t('install.chooseFile')}
              </Button>
              <span id={`${ids}-name`} className={styles.muted}>
                {file?.name ?? t('install.noFile')}
              </span>
              {/* The native picker opens from the button; the input itself stays out of sight and out of the tab order. */}
              <input
                ref={fileInput}
                className="visually-hidden"
                type="file"
                accept=".pithyxapp"
                tabIndex={-1}
                aria-labelledby={`${ids}-file`}
                onChange={(event) => {
                  const chosen = event.target.files?.[0] ?? null;
                  if (upload.data !== undefined) {
                    void storeCall(app, 'internal.admin.apps.discard', {
                      packageId: upload.data.packageId,
                    }).catch(() => undefined);
                  }
                  setFile(chosen);
                  setAccess(null);
                  upload.reset();
                  if (chosen !== null) {
                    upload.mutate(chosen);
                  }
                }}
              />
            </div>
          </div>
        )}
        {(upload.isPending || (catalog !== undefined && staged.isPending)) && (
          <p className={styles.loading} role="status">
            {t('install.checking')}
          </p>
        )}
        {upload.isError && <ErrorNotice error={describeProblem(upload.error)} />}
        {staged.isError && <ErrorNotice error={describeProblem(staged.error)} />}
        {preview !== undefined && (
          <Preview
            preview={preview}
            verified={catalog?.verified ?? false}
            storage={details === undefined}
          >
            {job === null && preview.action === 'install' && (
              <fieldset className={styles.choices}>
                <legend className={styles.fieldLabel}>{t('install.access')}</legend>
                {(['everybody', 'admins'] as const).map((choice) => (
                  <label key={choice} className={styles.check}>
                    <input
                      type="radio"
                      name={`${ids}-access`}
                      checked={chosenAccess === choice}
                      onChange={() => {
                        setAccess(choice);
                      }}
                    />
                    <span>{t(`detail.access.${choice}`)}</span>
                  </label>
                ))}
                {preview.suggestedAccess === 'admins' && (
                  <p className={styles.muted}>{t('install.accessSuggested')}</p>
                )}
              </fieldset>
            )}
            {job === null && needsPool && (
              <div className={styles.field}>
                <label className={styles.fieldLabel} htmlFor={`${ids}-pool`}>
                  {t('install.pool')}
                </label>
                {pools.isPending && (
                  <p className={styles.loading} role="status">
                    {t('install.loadingPools')}
                  </p>
                )}
                {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
                {pools.data !== undefined && choices.length === 0 && (
                  <p className={styles.notice}>{t('install.noPools')}</p>
                )}
                {choices.length > 0 && (
                  <select
                    id={`${ids}-pool`}
                    className={styles.select}
                    value={chosenPool ?? ''}
                    aria-describedby={`${ids}-pool-hint`}
                    onChange={(event) => {
                      setPool(event.target.value);
                    }}
                  >
                    {choices.map((choice) => (
                      <option key={choice} value={choice}>
                        {choice}
                      </option>
                    ))}
                  </select>
                )}
                <p id={`${ids}-pool-hint`} className={styles.muted}>
                  {t('install.poolHint')}
                </p>
              </div>
            )}
          </Preview>
        )}
        {install.isError && <ErrorNotice error={describeProblem(install.error)} />}
        {job !== null && (
          <JobProgress
            job={job}
            running={isUpdate ? t('install.updating', { name }) : t('install.running', { name })}
            succeeded={isUpdate ? t('install.updated', { name }) : t('install.installed', { name })}
            stopped={t('install.stopped')}
          />
        )}
        {details !== undefined && requests.length > 0 && (
          <>
            {waitsForFolders && (
              <p className={styles.notice}>{t('install.mapFolders', { name })}</p>
            )}
            <StorageSection app={app} details={details} requests={requests} />
          </>
        )}
      </div>
    </Dialog>
  );
}

/** What installing the checked package does: the app, its permissions and folders, and what deserves a warning. */
function Preview({
  preview,
  verified,
  storage,
  children,
}: {
  preview: AppPackagePreview;
  verified: boolean;
  /** Lists the folders it asks for; once installed, the dialog maps them instead. */
  storage: boolean;
  children: ReactNode;
}) {
  const { t, i18n } = useTranslation('store');
  const locale = useFormatLocale();
  const facts = manifestFacts(preview.manifest);
  const name = manifestText(facts.name, i18n.language);
  const description =
    facts.description === null ? null : manifestText(facts.description, i18n.language);
  const added = new Set(preview.addedPermissions);
  const isUpdate = preview.action !== 'install';
  return (
    <div className={styles.preview}>
      <div className={styles.previewHead}>
        <AppIcon app={{ icon: null, tone: 'graphite' }} size={48} />
        <div className={styles.headerText}>
          <h3 className={styles.previewTitle}>{name}</h3>
          <p className={styles.muted}>
            {t(`install.action.${preview.action}`, {
              version: preview.version,
              installed: preview.installedVersion ?? '',
            })}
            {facts.author !== null && ` · ${t('install.by', { author: facts.author })}`}
          </p>
          <p className={styles.muted}>
            {t('install.size', {
              files: preview.fileCount,
              size: formatBytes(preview.unpackedBytes, locale),
            })}
          </p>
        </div>
      </div>
      {description !== null && description !== '' && <p>{description}</p>}
      <p className={styles.notice}>
        {preview.catalog == null
          ? t('install.unverified')
          : verified
            ? t('install.fromCatalog', { catalog: preview.catalog })
            : t('install.fromCustomCatalog', { catalog: preview.catalog })}
      </p>
      {preview.keptData && <p className={styles.notice}>{t('install.keptData')}</p>}
      {facts.root && <p className={styles.warning}>{t('install.root')}</p>}
      {facts.elevated.length > 0 && (
        <p className={styles.warning}>
          {t('install.elevated', { rights: facts.elevated.join(', ') })}
        </p>
      )}
      <p className={styles.muted}>
        {facts.backend ? t('install.backend.yes') : t('install.backend.no')}
      </p>
      <section className={styles.group} aria-labelledby="install-permissions">
        <h4 id="install-permissions" className={styles.subTitle}>
          {t('install.permissions')}
        </h4>
        {facts.permissions.length === 0 ? (
          <p className={styles.muted}>{t('install.noPermissions')}</p>
        ) : (
          <ul className={styles.permissions}>
            {facts.permissions.map((permission) => (
              <li key={permission} className={styles.permission}>
                <PermissionText permission={permission} />
                {isUpdate && added.has(permission) && (
                  <Badge tone="highlight">{t('install.newPermission')}</Badge>
                )}
              </li>
            ))}
          </ul>
        )}
        {preview.removedPermissions.length > 0 && (
          <p className={styles.muted}>
            {t('install.dropped', { permissions: preview.removedPermissions.join(', ') })}
          </p>
        )}
      </section>
      {storage && facts.storage.length > 0 && (
        <section className={styles.group} aria-labelledby="install-storage">
          <h4 id="install-storage" className={styles.subTitle}>
            {t('install.storage')}
          </h4>
          <ul className={styles.permissions}>
            {facts.storage.map((request) => (
              <li key={request.id} className={styles.permission}>
                <span className={styles.permissionText}>
                  <span className={styles.permissionTitle}>
                    {manifestText(request.name, i18n.language)}
                  </span>
                  <span className={styles.muted}>
                    {t(`detail.storage.asks.${request.access}`)} ·{' '}
                    {request.required ? t('detail.storage.required') : t('detail.storage.optional')}
                  </span>
                </span>
              </li>
            ))}
          </ul>
          <p className={styles.muted}>{t('install.storageHint')}</p>
        </section>
      )}
      {children}
    </div>
  );
}
