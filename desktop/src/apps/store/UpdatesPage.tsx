import type { PithyxApp } from '@pithyx/sdk';
import { Button, FormRow, FormSection, StatusBadge, TextField, Toolbar } from '@pithyx/ui';
import { useMutation, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { AppIcon } from '../../shell/AppIcon';
import { IconRefresh } from '../../ui/icons';
import { manifestText } from '../installed';
import {
  storeCall,
  type AdminAppList,
  type AppUpdate,
  type StoreState,
  type UpdatePolicy,
} from '../sdk/store';
import { describeProblem, formatDate } from '../storage/shared';
import { InstallDialog } from './InstallDialog';
import { iconUrl, manifestFacts } from './model';
import { ReleaseNotes } from './StoreInfo';
import { storeKey } from './storeState';
import styles from './Store.module.css';

const POLICIES: readonly UpdatePolicy[] = ['automatic', 'notify', 'none'];

/**
 * Newer versions of installed apps that the catalogs offer: what each asks for anew and what the box will do with it
 * (installs it on its own, or waits for an administrator), updating one through the install dialog, checking now, and
 * the update policy with the daily check time (D329).
 */
export function UpdatesPage({
  app,
  state,
  installed,
}: {
  app: PithyxApp;
  state: UseQueryResult<StoreState>;
  installed: AdminAppList | undefined;
}) {
  const { t, i18n } = useTranslation('store');
  const locale = useFormatLocale();
  const [updating, setUpdating] = useState<{ update: AppUpdate; name: string } | null>(null);
  const check = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.store.check', {}),
  });
  const data = state.data;
  const updates = data?.updates ?? [];

  return (
    <>
      <Toolbar aria-label={t('updates.toolbar')}>
        <Button
          variant="primary"
          isDisabled={check.isPending || data === undefined}
          onPress={() => {
            check.mutate();
          }}
        >
          <IconRefresh size={18} stroke={1.75} aria-hidden />
          {t('settings.updates.checkNow')}
        </Button>
      </Toolbar>
      {check.isSuccess && (
        <p className={styles.muted} role="status">
          {t('settings.updates.checking')}
        </p>
      )}
      {check.isError && <ErrorNotice error={describeProblem(check.error)} />}
      {state.isError && <ErrorNotice error={describeProblem(state.error)} />}
      {data !== undefined && (
        <p className={styles.muted}>
          {t('settings.updates.next', { time: formatDate(data.settings.nextCheckAt, locale) })}
          {data.settings.lastCheckAt !== null &&
            ` · ${t('settings.updates.last', { time: formatDate(data.settings.lastCheckAt, locale) })}`}
        </p>
      )}
      {data !== undefined && updates.length === 0 && (
        <p className={styles.empty}>{t('updates.empty')}</p>
      )}
      {updates.length > 0 && (
        <ul className={styles.updates} aria-label={t('nav.updates')}>
          {updates.map((update) => {
            const details = installed?.apps.find((entry) => entry.id === update.id);
            const facts = details === undefined ? null : manifestFacts(details.manifest);
            const name = facts === null ? update.id : manifestText(facts.name, i18n.language);
            return (
              <li key={update.id} className={styles.update}>
                <AppIcon
                  app={{ icon: details === undefined ? null : iconUrl(details), tone: 'graphite' }}
                  size={40}
                />
                <div className={styles.updateText}>
                  <span className={styles.updateName}>{name}</span>
                  <span className={styles.muted}>
                    {t('updates.versions', { from: update.installedVersion, to: update.version })}
                  </span>
                  {update.critical && <span className={styles.muted}>{t('updates.critical')}</span>}
                  <span className={styles.cardActions}>
                    {update.addedPermissions.length > 0 && (
                      <StatusBadge tone="warning">
                        {t('updates.newPermissions', { count: update.addedPermissions.length })}
                      </StatusBadge>
                    )}
                    {update.elevated && (
                      <StatusBadge tone="danger">{t('updates.elevated')}</StatusBadge>
                    )}
                    {update.critical && (
                      <StatusBadge tone="danger">{t('elevated.rootBadge')}</StatusBadge>
                    )}
                    <StatusBadge tone={update.automatic ? 'info' : 'neutral'}>
                      {update.automatic
                        ? t('updates.automatic')
                        : t(`updates.policy.${update.policy ?? 'notify'}`)}
                    </StatusBadge>
                  </span>
                  <ReleaseNotes notes={update.releaseNotes} version={update.version} />
                </div>
                <Button
                  variant="primary"
                  isDisabled={details?.busy === true}
                  onPress={() => {
                    setUpdating({ update, name });
                  }}
                >
                  {t('updates.update')}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
      {data !== undefined && <UpdateSettings app={app} state={data} />}
      {updating !== null && (
        <InstallDialog
          app={app}
          updating={updating.name}
          catalog={{
            catalog: updating.update.catalog,
            appId: updating.update.id,
            version: updating.update.version,
            verified: (data?.catalogs ?? []).some(
              (catalog) => catalog.id === updating.update.catalog && catalog.verified,
            ),
          }}
          onClose={() => {
            setUpdating(null);
          }}
        />
      )}
    </>
  );
}

/** What the box does when a catalog has a newer version, and when it checks; both apply at once. */
function UpdateSettings({ app, state }: { app: PithyxApp; state: StoreState }) {
  const { t } = useTranslation('store');
  const ids = useId();
  const queryClient = useQueryClient();
  // A choice shows at once; the daemon's answer and the topic replace it, an error drops it (optimistic).
  const [pending, setPending] = useState<UpdatePolicy | null>(null);
  const settings = { ...state.settings, ...(pending === null ? {} : { policy: pending }) };
  const [time, setTime] = useState(settings.time);
  const save = useMutation({
    mutationFn: (change: { policy?: UpdatePolicy; time?: string }) =>
      storeCall(app, 'internal.admin.store.setSettings', {
        policy: change.policy ?? settings.policy ?? 'notify',
        time: change.time ?? settings.time,
        channel: settings.channel,
      }),
    onMutate: (change) => {
      if (change.policy !== undefined) {
        setPending(change.policy);
      }
    },
    onSuccess: (saved) => {
      queryClient.setQueryData<StoreState>(storeKey, (current) =>
        current === undefined ? current : { ...current, settings: saved },
      );
    },
    onSettled: () => {
      setPending(null);
    },
  });
  const timeValid = /^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(time);

  return (
    <FormSection title={t('settings.updates.title')}>
      <FormRow label={t('settings.updates.policy')} hint={t('settings.updates.criticalHint')}>
        <fieldset className={styles.choices}>
          <legend className="visually-hidden">{t('settings.updates.policy')}</legend>
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
        </fieldset>
      </FormRow>
      <FormRow label={t('settings.updates.time')} hint={t('settings.updates.timeHint')}>
        <div className={styles.inline}>
          <TextField
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
      </FormRow>
      <p className={styles.muted}>{t('settings.updates.notifications')}</p>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormSection>
  );
}
