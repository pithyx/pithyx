import type { PithyxApp } from '@pithyx/sdk';
import { Button, Dialog } from '@pithyx/ui';
import { useQuery } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { manifestText } from '../installed';
import { storeCall, type AppDetails, type AppStorageAccess, type ShareDetails } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { useAppChange } from './AppPage';
import { FolderPicker } from './FolderPicker';
import { folderPath, type StorageRequest } from './model';
import styles from './Store.module.css';

interface Mapping {
  storageId: string;
  shareId: number;
  path: string;
  access: AppStorageAccess;
}

/** The folders the app asks for, each mapped to a folder in a shared folder or not (D314). */
export function StorageSection({
  app,
  details,
  requests,
}: {
  app: PithyxApp;
  details: AppDetails;
  requests: StorageRequest[];
}) {
  const { t, i18n } = useTranslation('store');
  const [editing, setEditing] = useState<StorageRequest | null>(null);
  const appId = details.id;
  const shares = useQuery({
    queryKey: ['store', 'shares'],
    queryFn: () => storeCall(app, 'internal.admin.apps.shares', {}),
  });
  const unmap = useAppChange(
    appId,
    (storageId: string) => storeCall(app, 'internal.admin.apps.unmapStorage', { appId, storageId }),
    (current, storageId) => ({
      ...current,
      storage: current.storage.filter((entry) => entry.storageId !== storageId),
    }),
  );
  const shareName = (id: number) =>
    shares.data?.find((share) => share.id === id)?.name ?? String(id);

  return (
    <section className={styles.group} aria-labelledby="store-storage">
      <h3 id="store-storage" className={styles.sectionTitle}>
        {t('detail.storage.title')}
      </h3>
      <p className={styles.muted}>{t('detail.storage.intro')}</p>
      <ul className={styles.permissions}>
        {requests.map((request) => {
          const name = manifestText(request.name, i18n.language);
          const mapping = details.storage.find((entry) => entry.storageId === request.id);
          return (
            <li key={request.id} className={styles.permission}>
              <span className={styles.permissionText}>
                <span className={styles.permissionTitle}>{name}</span>
                <span className={styles.muted}>
                  {t(`detail.storage.asks.${request.access}`)} ·{' '}
                  {request.required ? t('detail.storage.required') : t('detail.storage.optional')}
                </span>
                {mapping === undefined ? (
                  <span className={request.required ? styles.warningText : styles.muted}>
                    {request.required
                      ? t('detail.storage.requiredMissing')
                      : t('detail.storage.notMapped')}
                  </span>
                ) : (
                  <span>
                    {t('detail.storage.mapped', {
                      share: shareName(mapping.shareId),
                      path: mapping.path === '' ? t('detail.storage.root') : mapping.path,
                    })}{' '}
                    ({t(`detail.storage.access.${mapping.access}`)})
                  </span>
                )}
              </span>
              <span className={styles.buttons}>
                <Button
                  size="sm"
                  onPress={() => {
                    setEditing(request);
                  }}
                >
                  {mapping === undefined
                    ? t('detail.storage.map', { storage: name })
                    : t('detail.storage.change', { storage: name })}
                </Button>
                {mapping !== undefined && (
                  <Button
                    size="sm"
                    onPress={() => {
                      unmap.mutate(request.id);
                    }}
                  >
                    {t('detail.storage.unmap', { storage: name })}
                  </Button>
                )}
              </span>
            </li>
          );
        })}
      </ul>
      {unmap.isError && <ErrorNotice error={describeProblem(unmap.error)} />}
      {shares.isError && <ErrorNotice error={describeProblem(shares.error)} />}
      {editing !== null && (
        <MappingDialog
          app={app}
          appId={appId}
          request={editing}
          shares={shares.data ?? []}
          current={details.storage.find((entry) => entry.storageId === editing.id)}
          onClose={() => {
            setEditing(null);
          }}
        />
      )}
    </section>
  );
}

function MappingDialog({
  app,
  appId,
  request,
  shares,
  current,
  onClose,
}: {
  app: PithyxApp;
  appId: string;
  request: StorageRequest;
  shares: ShareDetails[];
  current: Mapping | undefined;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const ids = useId();
  const [shareId, setShareId] = useState<number | null>(current?.shareId ?? shares[0]?.id ?? null);
  const [path, setPath] = useState(current?.path ?? '');
  const [access, setAccess] = useState<AppStorageAccess>(current?.access ?? request.access);
  const map = useAppChange(
    appId,
    (mapping: Mapping) => storeCall(app, 'internal.admin.apps.mapStorage', { appId, ...mapping }),
    (details, mapping) => ({
      ...details,
      storage: [
        ...details.storage.filter((entry) => entry.storageId !== mapping.storageId),
        mapping,
      ],
    }),
  );
  const name = manifestText(request.name, i18n.language);
  const save = () => {
    if (shareId !== null) {
      map.mutate(
        { storageId: request.id, shareId, path: folderPath(path), access },
        {
          onSuccess: onClose,
        },
      );
    }
  };

  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('detail.storage.dialogTitle', { storage: name })}
      actions={
        <>
          <Button onPress={onClose}>{t('detail.storage.cancel')}</Button>
          <Button variant="primary" isDisabled={shareId === null || map.isPending} onPress={save}>
            {t('detail.storage.save')}
          </Button>
        </>
      }
    >
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        {shares.length === 0 ? (
          <p className={styles.notice}>{t('detail.storage.noShares')}</p>
        ) : (
          <div className={styles.field}>
            <label className={styles.fieldLabel} htmlFor={`${ids}-share`}>
              {t('detail.storage.share')}
            </label>
            <select
              id={`${ids}-share`}
              className={styles.select}
              value={shareId ?? ''}
              onChange={(event) => {
                setShareId(Number(event.target.value));
                setPath('');
                map.reset();
              }}
            >
              {shares.map((share) => (
                <option key={share.id} value={share.id}>
                  {share.name}
                </option>
              ))}
            </select>
          </div>
        )}
        {shareId !== null && (
          <FolderPicker
            app={app}
            shareId={shareId}
            shareName={shares.find((share) => share.id === shareId)?.name ?? String(shareId)}
            path={path}
            onChange={(next) => {
              setPath(next);
              map.reset();
            }}
          />
        )}
        <fieldset className={styles.choices}>
          <legend className={styles.fieldLabel}>{t('detail.storage.accessLabel')}</legend>
          {(['read', 'readWrite'] as const).map((choice) => (
            <label key={choice} className={styles.check}>
              <input
                type="radio"
                name={`${ids}-access`}
                checked={access === choice}
                disabled={choice === 'readWrite' && request.access === 'read'}
                onChange={() => {
                  setAccess(choice);
                  map.reset();
                }}
              />
              <span>{t(`detail.storage.access.${choice}`)}</span>
            </label>
          ))}
        </fieldset>
        {map.isError && <ErrorNotice error={describeProblem(map.error)} />}
      </form>
    </Dialog>
  );
}
