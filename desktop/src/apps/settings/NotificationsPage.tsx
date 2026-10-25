import type { PithyxApp } from '@pithyx/sdk';
import { Button, PasswordField, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { ChannelEditor, type ChannelClient } from '../../notifications/ChannelEditor';
import {
  notificationsCall,
  smtpKey,
  systemChannelsKey,
  type SmtpRequest,
  type SmtpStatus,
} from '../sdk/notifications';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

type Security = NonNullable<SmtpRequest['security']>;
const SECURITIES: readonly Security[] = ['startTls', 'tls', 'none'];

/**
 * Settings > Notifications (D379): the SMTP server email channels send through, and the box's own channels, which get
 * the system's alerts once for all administrators. Each user's own channels are in the user menu.
 */
export function NotificationsPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const smtp = useQuery({
    queryKey: smtpKey,
    queryFn: () => notificationsCall(app, 'internal.admin.notifications.smtp', {}),
  });
  const client: ChannelClient = {
    queryKey: systemChannelsKey,
    list: () => notificationsCall(app, 'internal.admin.notifications.channels', {}),
    create: (channel) =>
      notificationsCall(app, 'internal.admin.notifications.createChannel', { channel }),
    update: (id, channel) =>
      notificationsCall(app, 'internal.admin.notifications.updateChannel', { id, channel }),
    remove: (id) => notificationsCall(app, 'internal.admin.notifications.deleteChannel', { id }),
    test: (id) => notificationsCall(app, 'internal.admin.notifications.testChannel', { id }),
  };
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.notifications.title')}</h2>
          <p className={styles.muted}>{t('pages.notifications.description')}</p>
        </div>
      </header>
      <div className={styles.form}>
        <section className={styles.card} aria-labelledby="notifications-smtp">
          <h3 id="notifications-smtp" className={styles.sectionTitle}>
            {t('notifications.smtp.title')}
          </h3>
          <p className={styles.muted}>{t('notifications.smtp.explain')}</p>
          {smtp.isError && <ErrorNotice error={describeProblem(smtp.error)} />}
          {smtp.data !== undefined && (
            <SmtpForm key={smtp.dataUpdatedAt} app={app} saved={smtp.data} />
          )}
        </section>
        <section className={styles.card} aria-labelledby="notifications-system">
          <h3 id="notifications-system" className={styles.sectionTitle}>
            {t('notifications.system.title')}
          </h3>
          <p className={styles.muted}>{t('notifications.system.explain')}</p>
          <ChannelEditor client={client} />
        </section>
      </div>
    </div>
  );
}

function SmtpForm({ app, saved }: { app: PithyxApp; saved: SmtpStatus }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [host, setHost] = useState(saved.host ?? '');
  const [port, setPort] = useState(String(saved.port));
  const [security, setSecurity] = useState<Security>(saved.security);
  const [username, setUsername] = useState(saved.username ?? '');
  const [password, setPassword] = useState('');
  const [from, setFrom] = useState(saved.from ?? '');
  const save = useMutation({
    mutationFn: () =>
      notificationsCall(app, 'internal.admin.notifications.setSmtp', {
        host: host.trim(),
        port: Number.parseInt(port, 10) || 0,
        security,
        username: username.trim() === '' ? null : username.trim(),
        // Empty keeps the saved password; a new one replaces it.
        password: password === '' ? null : password,
        from: from.trim(),
      }),
    onSuccess: async (next) => {
      queryClient.setQueryData(smtpKey, next);
      // Whether email channels are possible shows in the channel list.
      await queryClient.invalidateQueries({ queryKey: systemChannelsKey });
    },
  });
  return (
    <form
      className={styles.group}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <TextField
        label={t('notifications.smtp.host')}
        description={t('notifications.smtp.hostHint')}
        value={host}
        onChange={setHost}
        autoComplete="off"
        autoCapitalize="none"
      />
      <TextField
        label={t('notifications.smtp.port')}
        value={port}
        onChange={setPort}
        inputMode="numeric"
        autoComplete="off"
      />
      <label className={styles.selectField}>
        <span className={styles.fieldLabel}>{t('notifications.smtp.security')}</span>
        <select
          className={styles.select}
          value={security}
          onChange={(event) => {
            setSecurity(event.target.value as Security);
          }}
        >
          {SECURITIES.map((entry) => (
            <option key={entry} value={entry}>
              {t(`notifications.smtp.securities.${entry}`)}
            </option>
          ))}
        </select>
      </label>
      <TextField
        label={t('notifications.smtp.username')}
        value={username}
        onChange={setUsername}
        autoComplete="off"
        autoCapitalize="none"
      />
      <PasswordField
        label={t('notifications.smtp.password')}
        {...(saved.passwordSet ? { description: t('notifications.smtp.passwordSet') } : {})}
        value={password}
        onChange={setPassword}
        autoComplete="new-password"
      />
      <TextField
        label={t('notifications.smtp.from')}
        value={from}
        onChange={setFrom}
        type="email"
        autoComplete="off"
        autoCapitalize="none"
      />
      <div className={styles.buttons}>
        <Button type="submit" variant="primary" isDisabled={save.isPending}>
          {t('notifications.smtp.save')}
        </Button>
      </div>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </form>
  );
}
