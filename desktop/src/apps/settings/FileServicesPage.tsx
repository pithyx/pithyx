import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { cx, FormRow, FormSection, StatusBadge, Switch, TextField } from '@pithyx/ui';
import {
  systemCall,
  type FileServiceSettings,
  type FileServiceSettingsRequest,
} from '../sdk/system';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

const fileServicesKey = ['settings', 'system', 'fileServices'] as const;

/**
 * File services: SMB, SFTP, WebDAV, FTP and SSH logins switched on and off for the box, the SMB workgroup, how each service is
 * doing, how other computers connect and whether the firewall lets only private networks reach them. Who may use which protocol is set per user on the Users page, which shared folders they
 * offer on the Shared folders page.
 */
export function FileServicesPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const settings = useQuery({
    queryKey: fileServicesKey,
    queryFn: () => systemCall(app, 'internal.admin.system.fileServices', {}),
  });
  return (
    <div className={styles.content}>
      <p className={styles.sectionIntro}>{t('fileServices.intro')}</p>
      {settings.isError && <ErrorNotice error={describeProblem(settings.error)} />}
      {settings.data !== undefined && <FileServicesForm app={app} saved={settings.data} />}
    </div>
  );
}

function FileServicesForm({ app, saved }: { app: PithyxApp; saved: FileServiceSettings }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [workgroup, setWorkgroup] = useState(saved.workgroup);
  const save = useMutation({
    mutationFn: (request: FileServiceSettingsRequest) =>
      systemCall(app, 'internal.admin.system.setFileServices', request),
    onSuccess: (next) => {
      queryClient.setQueryData(fileServicesKey, next);
      setWorkgroup(next.workgroup);
    },
  });
  // The workgroup applies like the switches around it, when the field is left or with Enter.
  const commitWorkgroup = () => {
    if (workgroup.trim().toUpperCase() !== saved.workgroup && !save.isPending) {
      save.mutate({ workgroup });
    }
  };
  // The address the browser uses reaches the box from this computer; other computers may need another one.
  const host = window.location.hostname;
  const addresses = {
    windows: `\\\\${host}\\`,
    smb: `smb://${host}/`,
    sftp: `sftp://${host}`,
    webdav: `${window.location.origin}/webdav/`,
    ftp: `ftpes://${host}`,
  };

  const toggle = (name: Toggle, label: string) => (
    <Switch
      isSelected={saved[name]}
      isDisabled={save.isPending}
      onChange={(value) => {
        save.mutate({ [name]: value });
      }}
    >
      {label}
    </Switch>
  );

  return (
    <div className={styles.content}>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <FormSection title={t('fileServices.smb.title')} end={<State state={saved.smb} />}>
        <FormRow label={t('fileServices.service')} hint={t('fileServices.smb.explain')}>
          {toggle('smbEnabled', t('fileServices.smb.switch'))}
        </FormRow>
        <FormRow label={t('fileServices.smb.workgroup')} hint={t('fileServices.smb.workgroupHint')}>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              commitWorkgroup();
            }}
          >
            <TextField
              value={workgroup}
              onChange={setWorkgroup}
              onBlur={commitWorkgroup}
              autoComplete="off"
              maxLength={15}
            />
          </form>
        </FormRow>
        <FormRow label={t('fileServices.connect')}>
          <ul className={styles.explainList}>
            <li>
              {t('fileServices.smb.windows')}{' '}
              <span className={styles.mono}>{addresses.windows}</span>
            </li>
            <li>
              {t('fileServices.smb.mac')} <span className={styles.mono}>{addresses.smb}</span>
            </li>
            <li>{t('fileServices.smb.trash')}</li>
            <li>{t('fileServices.smb.versions')}</li>
          </ul>
        </FormRow>
      </FormSection>
      <FormSection title={t('fileServices.sftp.title')} end={<State state={saved.sftp} />}>
        <FormRow label={t('fileServices.service')} hint={t('fileServices.sftp.explain')}>
          {toggle('sftpEnabled', t('fileServices.sftp.switch'))}
        </FormRow>
        <FormRow label={t('fileServices.connect')}>
          <ul className={styles.explainList}>
            <li>
              {t('fileServices.sftp.connect')} <span className={styles.mono}>{addresses.sftp}</span>
            </li>
            <li>{t('fileServices.sftp.view')}</li>
          </ul>
        </FormRow>
      </FormSection>
      <FormSection title={t('fileServices.webdav.title')}>
        <FormRow label={t('fileServices.service')} hint={t('fileServices.webdav.explain')}>
          {toggle('webDavEnabled', t('fileServices.webdav.switch'))}
        </FormRow>
        <FormRow label={t('fileServices.connect')}>
          <ul className={styles.explainList}>
            <li>
              {t('fileServices.webdav.connect')}{' '}
              <span className={styles.mono}>{addresses.webdav}</span>
            </li>
            <li>{t('fileServices.webdav.trash')}</li>
          </ul>
        </FormRow>
      </FormSection>
      <FormSection title={t('fileServices.ftp.title')} end={<State state={saved.ftp} />}>
        <FormRow label={t('fileServices.service')} hint={t('fileServices.ftp.explain')}>
          {toggle('ftpEnabled', t('fileServices.ftp.switch'))}
        </FormRow>
        <FormRow label={t('fileServices.ftp.plain')} hint={t('fileServices.ftp.plainWarning')}>
          {toggle('ftpPlainAllowed', t('fileServices.ftp.plainSwitch'))}
        </FormRow>
        <FormRow label={t('fileServices.connect')}>
          <ul className={styles.explainList}>
            <li>
              {t('fileServices.ftp.connect')}{' '}
              <span className={styles.mono}>{`${addresses.ftp}:${String(saved.ftpPort)}`}</span>
            </li>
            <li>{t('fileServices.ftp.tls')}</li>
            {saved.ftpFingerprint !== null && (
              <li>
                {t('fileServices.ftp.fingerprint')}{' '}
                <span className={cx(styles.mono, styles.breakAll)}>{saved.ftpFingerprint}</span>
              </li>
            )}
          </ul>
        </FormRow>
      </FormSection>
      <FormSection title={t('fileServices.ssh.title')}>
        <FormRow label={t('fileServices.service')} hint={t('fileServices.ssh.explain')}>
          {toggle('sshEnabled', t('fileServices.ssh.switch'))}
        </FormRow>
        <p className={styles.notice} role="note">
          {t('fileServices.ssh.warning')}
        </p>
      </FormSection>
      <FormSection title={t('fileServices.firewall.title')}>
        <FormRow label={t('fileServices.firewall.reach')} hint={t('fileServices.firewall.explain')}>
          {toggle('privateNetworksOnly', t('fileServices.firewall.privateSwitch'))}
          <p className={styles.muted}>{t('fileServices.firewall.privateExplain')}</p>
        </FormRow>
        {!saved.firewallActive && (
          <p className={styles.notice} role="note">
            {t('fileServices.firewall.inactive')}
          </p>
        )}
      </FormSection>
      <p className={styles.notice} role="note">
        {t('fileServices.rights')}
      </p>
    </div>
  );
}

type Toggle =
  | 'smbEnabled'
  | 'sftpEnabled'
  | 'webDavEnabled'
  | 'ftpEnabled'
  | 'ftpPlainAllowed'
  | 'sshEnabled'
  | 'privateNetworksOnly';

function State({ state }: { state: FileServiceSettings['smb'] }) {
  const { t } = useTranslation('settings');
  const tone =
    state === 'running'
      ? 'success'
      : state === 'failed'
        ? 'danger'
        : state === 'unavailable'
          ? 'warning'
          : 'neutral';
  return <StatusBadge tone={tone}>{t(`fileServices.state.${state}`)}</StatusBadge>;
}
