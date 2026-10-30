import type { PithyxApp } from '@pithyx/sdk';
import {
  ActionBar,
  Button,
  FormRow,
  FormSection,
  PasswordField,
  Radio,
  RadioGroup,
  Tab,
  TabList,
  TabPanel,
  Tabs,
  TextField,
} from '@pithyx/ui';
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
import { PageFooter } from './footer';
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
  // The box's channels apply at once, the email server with the action bar: a tab each, never both on one view (D394).
  return (
    <Tabs>
      <TabList aria-label={t('notifications.tabs')}>
        <Tab id="channels">{t('notifications.system.title')}</Tab>
        <Tab id="smtp">{t('notifications.smtp.title')}</Tab>
      </TabList>
      <TabPanel id="channels" className={styles.tabPanel}>
        <p className={styles.muted}>{t('notifications.system.explain')}</p>
        <ChannelEditor client={client} />
      </TabPanel>
      <TabPanel id="smtp" className={styles.tabPanel}>
        {smtp.isError && <ErrorNotice error={describeProblem(smtp.error)} />}
        {smtp.data !== undefined && (
          <SmtpForm key={smtp.dataUpdatedAt} app={app} saved={smtp.data} />
        )}
      </TabPanel>
    </Tabs>
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
  // The server as the form has it; an empty password keeps (or, for the test, uses) the saved one.
  const server = () => ({
    host: host.trim(),
    port: Number.parseInt(port, 10) || 0,
    security,
    username: username.trim() === '' ? null : username.trim(),
    password: password === '' ? null : password,
    from: from.trim(),
  });
  const [testTo, setTestTo] = useState('');
  const test = useMutation({
    mutationFn: (to: string) =>
      notificationsCall(app, 'internal.admin.notifications.testSmtp', { ...server(), to }),
  });
  const save = useMutation({
    mutationFn: () => notificationsCall(app, 'internal.admin.notifications.setSmtp', server()),
    onSuccess: async (next) => {
      queryClient.setQueryData(smtpKey, next);
      // Whether email channels are possible shows in the channel list.
      await queryClient.invalidateQueries({ queryKey: systemChannelsKey });
    },
  });
  const changed =
    host !== (saved.host ?? '') ||
    port !== String(saved.port) ||
    security !== saved.security ||
    username !== (saved.username ?? '') ||
    password !== '' ||
    from !== (saved.from ?? '');
  const discard = () => {
    setHost(saved.host ?? '');
    setPort(String(saved.port));
    setSecurity(saved.security);
    setUsername(saved.username ?? '');
    setPassword('');
    setFrom(saved.from ?? '');
    save.reset();
  };
  return (
    <>
      <FormSection title={t('notifications.smtp.title')}>
        <p className={styles.sectionIntro}>{t('notifications.smtp.explain')}</p>
        <FormRow label={t('notifications.smtp.host')} hint={t('notifications.smtp.hostHint')}>
          <TextField value={host} onChange={setHost} autoComplete="off" autoCapitalize="none" />
        </FormRow>
        <FormRow label={t('notifications.smtp.port')}>
          <TextField value={port} onChange={setPort} inputMode="numeric" autoComplete="off" />
        </FormRow>
        <FormRow label={t('notifications.smtp.security')}>
          <RadioGroup
            orientation="vertical"
            value={security}
            onChange={(value) => {
              const next = SECURITIES.find((entry) => entry === value);
              if (next !== undefined) {
                setSecurity(next);
              }
            }}
          >
            {SECURITIES.map((entry) => (
              <Radio key={entry} value={entry}>
                {t(`notifications.smtp.securities.${entry}`)}
              </Radio>
            ))}
          </RadioGroup>
        </FormRow>
        <FormRow label={t('notifications.smtp.username')}>
          <TextField
            value={username}
            onChange={setUsername}
            autoComplete="off"
            autoCapitalize="none"
          />
        </FormRow>
        <FormRow
          label={t('notifications.smtp.password')}
          {...(saved.passwordSet ? { hint: t('notifications.smtp.passwordSet') } : {})}
        >
          <PasswordField value={password} onChange={setPassword} autoComplete="new-password" />
        </FormRow>
        <FormRow label={t('notifications.smtp.from')}>
          <TextField
            value={from}
            onChange={setFrom}
            type="email"
            autoComplete="off"
            autoCapitalize="none"
          />
        </FormRow>
      </FormSection>
      <FormSection title={t('notifications.smtp.testTitle')}>
        <FormRow label={t('notifications.smtp.testTo')} hint={t('notifications.smtp.testToHint')}>
          <form
            className={styles.rowControls}
            onSubmit={(event) => {
              event.preventDefault();
              test.mutate(testTo.trim());
            }}
          >
            <TextField
              value={testTo}
              onChange={(value) => {
                setTestTo(value);
                test.reset();
              }}
              type="email"
              autoComplete="email"
              autoCapitalize="none"
            />
            <Button
              type="submit"
              isDisabled={testTo.trim() === '' || host.trim() === '' || test.isPending}
            >
              {t('notifications.smtp.testSend')}
            </Button>
          </form>
        </FormRow>
        {test.isSuccess && (
          <p className={styles.muted} role="status">
            {t('notifications.smtp.testSent', { address: test.variables })}
          </p>
        )}
        {test.isError && <ErrorNotice error={describeProblem(test.error)} />}
      </FormSection>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <PageFooter>
        <ActionBar
          isDirty={changed}
          isPending={save.isPending}
          message={t('notifications.smtp.applyNote')}
          onApply={() => {
            save.mutate();
          }}
          onDiscard={discard}
        />
      </PageFooter>
    </>
  );
}
