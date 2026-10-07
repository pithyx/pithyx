import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, Switch } from '@pithyx/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { AppIcon } from '../../shell/AppIcon';
import { IconArrowLeft, IconExternalLink, IconTrash, IconUpload } from '../../ui/icons';
import { manifestText } from '../installed';
import type { AdminAppList, AppDetails } from '../sdk/store';
import { storeCall } from '../sdk/store';
import { describeProblem, formatDate } from '../storage/shared';
import { AccessSection } from './AccessSection';
import { InstallDialog } from './InstallDialog';
import { iconUrl, manifestFacts } from './model';
import { PermissionText } from './PermissionText';
import { StorageSection } from './StorageSection';
import { appsKey } from './StoreApp';
import styles from './Store.module.css';
import { UninstallDialog } from './UninstallDialog';

/**
 * A change of one installed app, applied at once and optimistically: `optimistic` changes the cached app, the daemon's
 * answer replaces it, and an error rolls it back.
 */
export function useAppChange<T>(
  appId: string,
  change: (value: T) => Promise<AppDetails>,
  optimistic: (details: AppDetails, value: T) => AppDetails,
) {
  const queryClient = useQueryClient();
  const replace = (update: (details: AppDetails) => AppDetails) => {
    queryClient.setQueryData<AdminAppList>(appsKey, (list) =>
      list === undefined
        ? list
        : { ...list, apps: list.apps.map((entry) => (entry.id === appId ? update(entry) : entry)) },
    );
  };
  return useMutation({
    mutationFn: change,
    onMutate: async (value) => {
      await queryClient.cancelQueries({ queryKey: appsKey });
      const previous = queryClient.getQueryData<AdminAppList>(appsKey);
      replace((details) => optimistic(details, value));
      return { previous };
    },
    onError: (_error, _value, context) => {
      if (context?.previous !== undefined) {
        queryClient.setQueryData(appsKey, context.previous);
      }
    },
    onSuccess: (details) => {
      replace(() => details);
    },
  });
}

/** One installed app: open, update or remove it, and its permissions, folders, users, logging and details. */
export function AppPage({
  app,
  details,
  onBack,
}: {
  app: PithyxApp;
  details: AppDetails;
  onBack: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const locale = useFormatLocale();
  const [dialog, setDialog] = useState<'update' | 'uninstall' | null>(null);
  const facts = manifestFacts(details.manifest);
  const name = manifestText(facts.name, i18n.language);
  const description =
    facts.description === null ? null : manifestText(facts.description, i18n.language);
  const appId = details.id;
  const enabled = useAppChange(
    appId,
    (value: boolean) => storeCall(app, 'internal.admin.apps.setEnabled', { appId, enabled: value }),
    (current, value) => ({ ...current, enabled: value }),
  );
  const permission = useAppChange(
    appId,
    ({ permission: name, granted }: { permission: string; granted: boolean }) =>
      storeCall(app, 'internal.admin.apps.setPermission', { appId, permission: name, granted }),
    (current, { permission: name, granted }) => ({
      ...current,
      permissions: current.permissions.map((entry) =>
        entry.permission === name ? { ...entry, granted } : entry,
      ),
    }),
  );
  const logLevel = useAppChange(
    appId,
    (debug: boolean) =>
      storeCall(app, 'internal.admin.apps.setLogLevel', { appId, level: debug ? 'debug' : 'info' }),
    (current, debug) => ({ ...current, logLevel: debug ? 'debug' : 'info' }),
  );

  return (
    <div className={styles.app}>
      <nav className={styles.crumbs}>
        <button type="button" className={styles.back} onClick={onBack}>
          <IconArrowLeft size={16} stroke={1.75} aria-hidden />
          {t('detail.back')}
        </button>
      </nav>
      <article className={styles.page} aria-labelledby="store-app-name">
        <header className={styles.appHeader}>
          <AppIcon app={{ icon: iconUrl(details), tone: 'graphite' }} size={64} />
          <div className={styles.headerText}>
            <h2 id="store-app-name" className={styles.pageTitle}>
              {name}
            </h2>
            <p className={styles.muted}>
              {t('installed.version', { version: details.version })}
              {facts.author !== null && ` · ${facts.author}`}
            </p>
            <span className={styles.badges}>
              <Badge tone="neutral">
                {t(`detail.source.${details.source === 'catalog' ? 'catalog' : 'sideload'}`)}
              </Badge>
              {details.busy && <Badge tone="accent">{t('installed.busy')}</Badge>}
            </span>
          </div>
          <div className={styles.buttons}>
            <Button
              variant="primary"
              isDisabled={!details.enabled || !details.available || details.busy}
              onPress={() => {
                void storeCall(app, 'internal.admin.apps.open', { appId });
              }}
            >
              <IconExternalLink size={16} stroke={1.75} aria-hidden />
              {t('detail.open')}
            </Button>
            <Button
              isDisabled={details.busy}
              onPress={() => {
                setDialog('update');
              }}
            >
              <IconUpload size={16} stroke={1.75} aria-hidden />
              {t('detail.update')}
            </Button>
            <Button
              variant="danger"
              isDisabled={details.busy}
              onPress={() => {
                setDialog('uninstall');
              }}
            >
              <IconTrash size={16} stroke={1.75} aria-hidden />
              {t('detail.uninstall')}
            </Button>
          </div>
        </header>
        {description !== null && description !== '' && <p>{description}</p>}
        {!details.available && <p className={styles.warning}>{t('detail.unavailable')}</p>}
        <div className={styles.group}>
          <Switch
            isSelected={details.enabled}
            isDisabled={details.busy}
            onChange={(value) => {
              enabled.mutate(value);
            }}
          >
            {t('detail.enabled')}
          </Switch>
          <p className={styles.muted}>{t('detail.enabledHint')}</p>
          {enabled.isError && <ErrorNotice error={describeProblem(enabled.error)} />}
        </div>

        <section className={styles.group} aria-labelledby="store-permissions">
          <h3 id="store-permissions" className={styles.sectionTitle}>
            {t('detail.permissions.title')}
          </h3>
          {details.permissions.length === 0 ? (
            <p className={styles.muted}>{t('detail.permissions.none')}</p>
          ) : (
            <>
              <p className={styles.muted}>{t('detail.permissions.intro')}</p>
              <ul className={styles.permissions}>
                {details.permissions.map((entry) => (
                  <li key={entry.permission} className={styles.permission}>
                    <PermissionText
                      permission={entry.permission}
                      id={`store-permission-${entry.permission}`}
                    />
                    <Switch
                      aria-labelledby={`store-permission-${entry.permission}`}
                      isSelected={entry.granted}
                      onChange={(granted) => {
                        permission.mutate({ permission: entry.permission, granted });
                      }}
                    >
                      <span className="visually-hidden">{entry.permission}</span>
                    </Switch>
                  </li>
                ))}
              </ul>
            </>
          )}
          {permission.isError && <ErrorNotice error={describeProblem(permission.error)} />}
        </section>

        {facts.storage.length > 0 && (
          <StorageSection app={app} details={details} requests={facts.storage} />
        )}

        <AccessSection app={app} appId={appId} />

        <section className={styles.group} aria-labelledby="store-logs">
          <h3 id="store-logs" className={styles.sectionTitle}>
            {t('detail.logs.title')}
          </h3>
          <Switch
            isSelected={details.logLevel === 'debug'}
            onChange={(debug) => {
              logLevel.mutate(debug);
            }}
          >
            {t('detail.logs.debug')}
          </Switch>
          <p className={styles.muted}>{t('detail.logs.hint')}</p>
          {logLevel.isError && <ErrorNotice error={describeProblem(logLevel.error)} />}
        </section>

        <section className={styles.group} aria-labelledby="store-facts">
          <h3 id="store-facts" className={styles.sectionTitle}>
            {t('detail.facts.title')}
          </h3>
          <dl className={styles.facts}>
            <dt>{t('detail.facts.version')}</dt>
            <dd>{details.version}</dd>
            {facts.author !== null && (
              <>
                <dt>{t('detail.facts.author')}</dt>
                <dd>{facts.author}</dd>
              </>
            )}
            {facts.license !== null && (
              <>
                <dt>{t('detail.facts.license')}</dt>
                <dd>{facts.license}</dd>
              </>
            )}
            {facts.homepage !== null && (
              <>
                <dt>{t('detail.facts.homepage')}</dt>
                <dd className={styles.breakable}>{facts.homepage}</dd>
              </>
            )}
            <dt>{t('detail.facts.installedAt')}</dt>
            <dd>{formatDate(details.installedAt, locale)}</dd>
            <dt>{t('detail.facts.updatedAt')}</dt>
            <dd>{formatDate(details.updatedAt, locale)}</dd>
            <dt>{t('detail.facts.uid')}</dt>
            <dd>{details.uid}</dd>
            <dt>{t('detail.facts.sha256')}</dt>
            <dd className={styles.hash}>{details.sha256}</dd>
          </dl>
        </section>
      </article>
      {dialog === 'update' && (
        <InstallDialog
          app={app}
          updating={name}
          onClose={() => {
            setDialog(null);
          }}
        />
      )}
      {dialog === 'uninstall' && (
        <UninstallDialog
          app={app}
          appId={appId}
          name={name}
          onClose={() => {
            setDialog(null);
          }}
          onRemoved={onBack}
        />
      )}
    </div>
  );
}
