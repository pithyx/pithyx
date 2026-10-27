import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, Dialog, Switch, TextField } from '@pithyx/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { IconArrowLeft, IconPlus, IconRefresh, IconTrash, IconUpload } from '../../ui/icons';
import {
  storeCall,
  type CatalogInfo,
  type StoreChannel,
  type StoreState,
  type UpdatePolicy,
} from '../sdk/store';
import { describeProblem, formatDate } from '../storage/shared';
import { InstallDialog } from './InstallDialog';
import { catalogName, storeKey } from './storeState';
import styles from './Store.module.css';

const POLICIES: readonly UpdatePolicy[] = ['automatic', 'notify', 'none'];

/**
 * The Store's settings behind the gear (D329): the update policy, the daily check time with "check now", the channel,
 * the catalogs with their status (the community catalog on after a warning, custom catalogs with their deploy key),
 * and installing an app from a file.
 */
export function StoreSettings({
  app,
  state,
  onBack,
}: {
  app: PithyxApp;
  state: StoreState;
  onBack: () => void;
}) {
  const { t } = useTranslation('store');
  const locale = useFormatLocale();
  const ids = useId();
  const queryClient = useQueryClient();
  // Choices show at once; the daemon's answer and the topic replace them, an error drops them (optimistic).
  const [pending, setPending] = useState<{ policy?: UpdatePolicy; channel?: StoreChannel }>({});
  const settings = { ...state.settings, ...pending };
  const [time, setTime] = useState(settings.time);
  const [adding, setAdding] = useState(false);
  const [installing, setInstalling] = useState(false);
  const [community, setCommunity] = useState<CatalogInfo | null>(null);
  const save = useMutation({
    mutationFn: (change: { policy?: UpdatePolicy; time?: string; channel?: StoreChannel }) =>
      storeCall(app, 'internal.admin.store.setSettings', {
        policy: change.policy ?? settings.policy ?? 'notify',
        time: change.time ?? settings.time,
        channel: change.channel ?? settings.channel,
      }),
    onMutate: (change) => {
      setPending((current) => ({
        ...current,
        ...(change.policy === undefined ? {} : { policy: change.policy }),
        ...(change.channel === undefined ? {} : { channel: change.channel }),
      }));
    },
    onSuccess: (saved) => {
      queryClient.setQueryData<StoreState>(storeKey, (current) =>
        current === undefined ? current : { ...current, settings: saved },
      );
    },
    onSettled: () => {
      setPending({});
    },
  });
  const check = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.store.check', {}),
  });
  const timeValid = /^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(time);

  return (
    <div className={styles.app}>
      <nav className={styles.crumbs}>
        <button type="button" className={styles.back} onClick={onBack}>
          <IconArrowLeft size={16} stroke={1.75} aria-hidden />
          {t('settings.back')}
        </button>
      </nav>
      <div className={styles.page}>
        <h2 className={styles.pageTitle}>{t('settings.title')}</h2>

        <section className={styles.group} aria-labelledby={`${ids}-updates`}>
          <h3 id={`${ids}-updates`} className={styles.sectionTitle}>
            {t('settings.updates.title')}
          </h3>
          <fieldset className={styles.choices}>
            <legend className={styles.fieldLabel}>{t('settings.updates.policy')}</legend>
            {POLICIES.map((policy) => (
              <label key={policy} className={styles.check}>
                <input
                  type="radio"
                  name={`${ids}-policy`}
                  checked={settings.policy === policy}
                  onChange={() => {
                    save.mutate({ policy });
                  }}
                />
                <span>
                  {t(`settings.updates.policies.${policy}`)}
                  <span className={styles.choiceHint}>
                    {t(`settings.updates.policyHints.${policy}`)}
                  </span>
                </span>
              </label>
            ))}
            <p className={styles.muted}>{t('settings.updates.criticalHint')}</p>
          </fieldset>
          <div className={styles.inline}>
            <TextField
              label={t('settings.updates.time')}
              description={t('settings.updates.timeHint')}
              value={time}
              inputMode="numeric"
              {...(timeValid ? {} : { errorMessage: t('settings.updates.timeInvalid') })}
              isInvalid={!timeValid}
              onChange={setTime}
            />
            <Button
              isDisabled={!timeValid || time === settings.time || save.isPending}
              onPress={() => {
                save.mutate({ time });
              }}
            >
              {t('settings.save')}
            </Button>
          </div>
          <p className={styles.muted}>
            {t('settings.updates.next', { time: formatDate(settings.nextCheckAt, locale) })}
            {settings.lastCheckAt !== null &&
              ` · ${t('settings.updates.last', { time: formatDate(settings.lastCheckAt, locale) })}`}
          </p>
          <div className={styles.buttons}>
            <Button
              isDisabled={check.isPending}
              onPress={() => {
                check.mutate();
              }}
            >
              <IconRefresh size={16} stroke={1.75} aria-hidden />
              {t('settings.updates.checkNow')}
            </Button>
          </div>
          {check.isSuccess && (
            <p className={styles.muted} role="status">
              {t('settings.updates.checking')}
            </p>
          )}
          {check.isError && <ErrorNotice error={describeProblem(check.error)} />}
          <p className={styles.muted}>{t('settings.updates.notifications')}</p>
        </section>

        <section className={styles.group} aria-labelledby={`${ids}-channel`}>
          <h3 id={`${ids}-channel`} className={styles.sectionTitle}>
            {t('settings.channel.title')}
          </h3>
          <fieldset className={styles.choices}>
            <legend className="visually-hidden">{t('settings.channel.title')}</legend>
            {(['stable', 'beta'] as const).map((channel) => (
              <label key={channel} className={styles.check}>
                <input
                  type="radio"
                  name={`${ids}-channel`}
                  checked={settings.channel === channel}
                  onChange={() => {
                    save.mutate({ channel });
                  }}
                />
                <span>
                  {t(`settings.channel.${channel}`)}
                  <span className={styles.choiceHint}>{t(`settings.channel.${channel}Hint`)}</span>
                </span>
              </label>
            ))}
          </fieldset>
          {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
        </section>

        <section className={styles.group} aria-labelledby={`${ids}-catalogs`}>
          <div className={styles.pageHeader}>
            <h3 id={`${ids}-catalogs`} className={styles.sectionTitle}>
              {t('settings.catalogs.title')}
            </h3>
            <Button
              onPress={() => {
                setAdding(true);
              }}
            >
              <IconPlus size={16} stroke={1.75} aria-hidden />
              {t('settings.catalogs.add')}
            </Button>
          </div>
          <ul className={styles.catalogs}>
            {state.catalogs.map((catalog) => (
              <CatalogRow
                key={catalog.key}
                app={app}
                catalog={catalog}
                onEnableCommunity={() => {
                  setCommunity(catalog);
                }}
              />
            ))}
          </ul>
        </section>

        <section className={styles.group} aria-labelledby={`${ids}-file`}>
          <h3 id={`${ids}-file`} className={styles.sectionTitle}>
            {t('settings.file.title')}
          </h3>
          <p className={styles.muted}>{t('settings.file.intro')}</p>
          <div className={styles.buttons}>
            <Button
              onPress={() => {
                setInstalling(true);
              }}
            >
              <IconUpload size={16} stroke={1.75} aria-hidden />
              {t('installed.installFromFile')}
            </Button>
          </div>
        </section>
      </div>
      {adding && (
        <AddCatalogDialog
          app={app}
          onClose={() => {
            setAdding(false);
          }}
        />
      )}
      {community !== null && (
        <CommunityDialog
          app={app}
          catalog={community}
          onClose={() => {
            setCommunity(null);
          }}
        />
      )}
      {installing && (
        <InstallDialog
          app={app}
          onClose={() => {
            setInstalling(false);
          }}
        />
      )}
    </div>
  );
}

function CatalogRow({
  app,
  catalog,
  onEnableCommunity,
}: {
  app: PithyxApp;
  catalog: CatalogInfo;
  onEnableCommunity: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const locale = useFormatLocale();
  const ids = useId();
  const toggle = useMutation({
    mutationFn: (enabled: boolean) =>
      storeCall(app, 'internal.admin.store.updateCatalog', { key: catalog.key, enabled }),
  });
  const sync = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.store.syncCatalog', { key: catalog.key }),
  });
  const remove = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.store.removeCatalog', { key: catalog.key }),
  });
  const name = catalogName(catalog, i18n.language);
  return (
    <li className={styles.catalog}>
      <div className={styles.pageHeader}>
        <div className={styles.headerText}>
          <span id={`${ids}-name`} className={styles.tileTitle}>
            {name}
          </span>
          <span className={`${styles.muted} ${styles.breakable}`}>{catalog.url}</span>
          <span className={styles.badges}>
            <Badge tone={catalog.verified ? 'accent' : 'neutral'}>
              {catalog.verified ? t('settings.catalogs.signed') : t('catalog.badge.custom')}
            </Badge>
            <Badge
              tone={
                catalog.status === 'failed'
                  ? 'danger'
                  : catalog.status === 'ok'
                    ? 'neutral'
                    : 'accent'
              }
            >
              {catalog.busy
                ? t('settings.catalogs.status.busy')
                : t(`settings.catalogs.status.${catalog.status}`)}
            </Badge>
          </span>
        </div>
        <Switch
          isSelected={catalog.enabled}
          isDisabled={toggle.isPending}
          onChange={(enabled) => {
            if (enabled && catalog.kind === 'community') {
              onEnableCommunity();
            } else {
              toggle.mutate(enabled);
            }
          }}
        >
          <span className="visually-hidden">{t('settings.catalogs.enable', { name })}</span>
        </Switch>
      </div>
      <p className={styles.muted}>
        {catalog.verifiedAt === null
          ? t('settings.catalogs.never')
          : t('settings.catalogs.verified', {
              time: formatDate(catalog.verifiedAt, locale),
              count: catalog.appCount,
            })}
      </p>
      {catalog.errorCode !== null && (
        <ErrorNotice
          error={describeProblem({
            code: catalog.errorCode,
            params: (catalog.errorParams ?? {}) as Record<string, unknown>,
            requestId: '',
          })}
        />
      )}
      {catalog.sshPublicKey !== null && (
        <div className={styles.field}>
          <span className={styles.fieldLabel}>{t('settings.catalogs.deployKey')}</span>
          <code className={styles.key}>{catalog.sshPublicKey}</code>
          <p className={styles.muted}>{t('settings.catalogs.deployKeyHint')}</p>
        </div>
      )}
      {catalog.hostKey !== null && (
        <p className={`${styles.muted} ${styles.breakable}`}>
          {t('settings.catalogs.hostKey', { key: catalog.hostKey })}
        </p>
      )}
      <div className={styles.buttons}>
        <Button
          size="sm"
          isDisabled={!catalog.enabled || catalog.busy || sync.isPending}
          onPress={() => {
            sync.mutate();
          }}
        >
          <IconRefresh size={16} stroke={1.75} aria-hidden />
          {t('settings.catalogs.sync')}
        </Button>
        {catalog.kind === 'custom' && (
          <Button
            size="sm"
            variant="danger"
            isDisabled={catalog.busy || remove.isPending}
            onPress={() => {
              remove.mutate();
            }}
          >
            <IconTrash size={16} stroke={1.75} aria-hidden />
            {t('settings.catalogs.remove')}
          </Button>
        )}
      </div>
      {(toggle.isError || sync.isError || remove.isError) && (
        <ErrorNotice error={describeProblem(toggle.error ?? sync.error ?? remove.error)} />
      )}
    </li>
  );
}

function AddCatalogDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('store');
  const [url, setUrl] = useState('');
  const add = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.store.addCatalog', { url: url.trim() }),
  });
  const added = add.data;
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('settings.catalogs.addTitle')}
      actions={
        added === undefined ? (
          <>
            <Button onPress={onClose}>{t('install.cancel')}</Button>
            <Button
              variant="primary"
              isDisabled={url.trim() === '' || add.isPending}
              onPress={() => {
                add.mutate();
              }}
            >
              {t('settings.catalogs.addButton')}
            </Button>
          </>
        ) : (
          <Button variant="primary" onPress={onClose}>
            {t('install.close')}
          </Button>
        )
      }
    >
      <div className={styles.form}>
        <p className={styles.warning}>{t('settings.catalogs.addWarning')}</p>
        {added === undefined && (
          <TextField
            label={t('settings.catalogs.url')}
            description={t('settings.catalogs.urlHint')}
            value={url}
            onChange={setUrl}
            autoCapitalize="none"
          />
        )}
        {add.isError && <ErrorNotice error={describeProblem(add.error)} />}
        {added?.sshPublicKey != null && (
          <div className={styles.field}>
            <span className={styles.fieldLabel}>{t('settings.catalogs.deployKey')}</span>
            <code className={styles.key}>{added.sshPublicKey}</code>
            <p className={styles.muted}>{t('settings.catalogs.deployKeyNext')}</p>
          </div>
        )}
        {added?.sshPublicKey === null && (
          <p className={styles.success} role="status">
            {t('settings.catalogs.added')}
          </p>
        )}
      </div>
    </Dialog>
  );
}

function CommunityDialog({
  app,
  catalog,
  onClose,
}: {
  app: PithyxApp;
  catalog: CatalogInfo;
  onClose: () => void;
}) {
  const { t } = useTranslation('store');
  const enable = useMutation({
    mutationFn: () =>
      storeCall(app, 'internal.admin.store.updateCatalog', { key: catalog.key, enabled: true }),
    onSuccess: onClose,
  });
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('settings.community.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('install.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={enable.isPending}
            onPress={() => {
              enable.mutate();
            }}
          >
            {t('settings.community.confirm')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <p className={styles.warning}>{t('settings.community.warning')}</p>
        {enable.isError && <ErrorNotice error={describeProblem(enable.error)} />}
      </div>
    </Dialog>
  );
}
