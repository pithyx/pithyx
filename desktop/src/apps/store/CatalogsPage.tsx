import type { PithyxApp } from '@pithyx/sdk';
import {
  Button,
  Card,
  Dialog,
  FormSection,
  StatusBadge,
  Switch,
  TextField,
  Toolbar,
  type StatusTone,
} from '@pithyx/ui';
import { useMutation, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { IconBooks, IconPlus, IconRefresh, IconTrash } from '../../ui/icons';
import { storeCall, type CatalogInfo, type StoreChannel, type StoreState } from '../sdk/store';
import { describeProblem, formatDate } from '../storage/shared';
import { catalogName, storeKey } from './storeState';
import styles from './Store.module.css';

/**
 * The catalogs (D329): the channel, and every catalog as a card with its state (the community catalog on after a
 * warning, custom catalogs with their deploy key), fetching one now, and adding or removing a custom one.
 */
export function CatalogsPage({
  app,
  state,
}: {
  app: PithyxApp;
  state: UseQueryResult<StoreState>;
}) {
  const { t } = useTranslation('store');
  const [adding, setAdding] = useState(false);
  const [community, setCommunity] = useState<CatalogInfo | null>(null);
  const data = state.data;

  return (
    <>
      <Toolbar aria-label={t('settings.catalogs.toolbar')}>
        <Button
          variant="primary"
          onPress={() => {
            setAdding(true);
          }}
        >
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t('settings.catalogs.add')}
        </Button>
      </Toolbar>
      <p className={styles.muted}>{t('settings.catalogs.intro')}</p>
      {state.isError && <ErrorNotice error={describeProblem(state.error)} />}
      {data !== undefined && (
        <>
          <div className={styles.catalogs} role="list" aria-label={t('nav.catalogs')}>
            {data.catalogs.map((catalog) => (
              <div key={catalog.key} role="listitem">
                <CatalogCard
                  app={app}
                  catalog={catalog}
                  onEnableCommunity={() => {
                    setCommunity(catalog);
                  }}
                />
              </div>
            ))}
          </div>
          <ChannelSection app={app} state={data} />
        </>
      )}
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
    </>
  );
}

/** Whether the box follows the catalogs' released versions or every new one; applies at once. */
function ChannelSection({ app, state }: { app: PithyxApp; state: StoreState }) {
  const { t } = useTranslation('store');
  const ids = useId();
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (channel: StoreChannel) =>
      storeCall(app, 'internal.admin.store.setSettings', {
        policy: state.settings.policy ?? 'notify',
        time: state.settings.time,
        channel,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData<StoreState>(storeKey, (current) =>
        current === undefined ? current : { ...current, settings: saved },
      );
    },
  });
  // The choice shows at once; the daemon's answer and the topic replace it, an error drops it (optimistic).
  const channel = save.isPending ? save.variables : state.settings.channel;
  return (
    <FormSection title={t('settings.channel.title')}>
      <fieldset className={styles.choices}>
        <legend className="visually-hidden">{t('settings.channel.title')}</legend>
        {(['stable', 'beta'] as const).map((option) => (
          <label key={option} className={styles.check}>
            <input
              type="radio"
              name={`${ids}-channel`}
              checked={channel === option}
              onChange={() => {
                save.mutate(option);
              }}
            />
            <span>
              {t(`settings.channel.${option}`)}
              <span className={styles.choiceHint}>{t(`settings.channel.${option}Hint`)}</span>
            </span>
          </label>
        ))}
      </fieldset>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormSection>
  );
}

const STATUS_TONES = {
  off: 'neutral',
  new: 'info',
  ok: 'success',
  failed: 'danger',
} as const satisfies Record<CatalogInfo['status'], StatusTone>;

function CatalogCard({
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
  const facts = [
    catalog.verified ? t('settings.catalogs.signed') : t('catalog.badge.custom'),
    catalog.verifiedAt === null
      ? t('settings.catalogs.never')
      : t('settings.catalogs.verified', {
          time: formatDate(catalog.verifiedAt, locale),
          count: catalog.appCount,
        }),
  ].join(' · ');
  return (
    <Card
      icon={<IconBooks size={24} stroke={1.75} aria-hidden />}
      title={name}
      badge={
        <StatusBadge tone={catalog.busy ? 'info' : STATUS_TONES[catalog.status]}>
          {catalog.busy
            ? t('settings.catalogs.status.busy')
            : t(`settings.catalogs.status.${catalog.status}`)}
        </StatusBadge>
      }
      facts={facts}
      defaultExpanded={catalog.errorCode !== null || catalog.sshPublicKey !== null}
      actions={
        <>
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
            {t('settings.catalogs.enable', { name })}
          </Switch>
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
        </>
      }
    >
      <p className={`${styles.muted} ${styles.breakable}`}>{catalog.url}</p>
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
      {(toggle.isError || sync.isError || remove.isError) && (
        <ErrorNotice error={describeProblem(toggle.error ?? sync.error ?? remove.error)} />
      )}
    </Card>
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
