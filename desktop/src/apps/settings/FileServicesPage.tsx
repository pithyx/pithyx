import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, cx, FormSection, StatusBadge, Switch, TextField } from '@pithyx/ui';
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
  // The address the browser uses reaches the box from this computer; other computers may need another one.
  const host = window.location.hostname;
  const addresses = {
    windows: `\\\\${host}\\`,
    smb: `smb://${host}/`,
    sftp: `sftp://${host}`,
    webdav: `${window.location.origin}/webdav/`,
    ftp: `ftpes://${host}`,
  };

  return (
    <div className={styles.content}>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <FormSection title={t('fileServices.smb.title')} end={<State state={saved.smb} />}>
        <p className={styles.sectionIntro}>{t('fileServices.smb.explain')}</p>
        <Switch
          isSelected={saved.smbEnabled}
          isDisabled={save.isPending}
          onChange={(smbEnabled) => {
            save.mutate({ smbEnabled });
          }}
        >
          {t('fileServices.smb.switch')}
        </Switch>
        <form
          className={cx(styles.group, styles.narrow)}
          onSubmit={(event) => {
            event.preventDefault();
            save.mutate({ workgroup });
          }}
        >
          <TextField
            label={t('fileServices.smb.workgroup')}
            description={t('fileServices.smb.workgroupHint')}
            value={workgroup}
            onChange={setWorkgroup}
            autoComplete="off"
            maxLength={15}
          />
          <div className={styles.buttons}>
            <Button
              type="submit"
              variant="primary"
              isDisabled={workgroup.trim().toUpperCase() === saved.workgroup || save.isPending}
            >
              {t('fileServices.save')}
            </Button>
          </div>
        </form>
        <ul className={styles.explainList}>
          <li>
            {t('fileServices.smb.windows')} <span className={styles.mono}>{addresses.windows}</span>
          </li>
          <li>
            {t('fileServices.smb.mac')} <span className={styles.mono}>{addresses.smb}</span>
          </li>
          <li>{t('fileServices.smb.trash')}</li>
          <li>{t('fileServices.smb.versions')}</li>
        </ul>
      </FormSection>
      <FormSection title={t('fileServices.sftp.title')} end={<State state={saved.sftp} />}>
        <p className={styles.sectionIntro}>{t('fileServices.sftp.explain')}</p>
        <Switch
          isSelected={saved.sftpEnabled}
          isDisabled={save.isPending}
          onChange={(sftpEnabled) => {
            save.mutate({ sftpEnabled });
          }}
        >
          {t('fileServices.sftp.switch')}
        </Switch>
        <ul className={styles.explainList}>
          <li>
            {t('fileServices.sftp.connect')} <span className={styles.mono}>{addresses.sftp}</span>
          </li>
          <li>{t('fileServices.sftp.view')}</li>
        </ul>
      </FormSection>
      <FormSection title={t('fileServices.webdav.title')}>
        <p className={styles.sectionIntro}>{t('fileServices.webdav.explain')}</p>
        <Switch
          isSelected={saved.webDavEnabled}
          isDisabled={save.isPending}
          onChange={(webDavEnabled) => {
            save.mutate({ webDavEnabled });
          }}
        >
          {t('fileServices.webdav.switch')}
        </Switch>
        <ul className={styles.explainList}>
          <li>
            {t('fileServices.webdav.connect')}{' '}
            <span className={styles.mono}>{addresses.webdav}</span>
          </li>
          <li>{t('fileServices.webdav.trash')}</li>
        </ul>
      </FormSection>
      <FormSection title={t('fileServices.ftp.title')} end={<State state={saved.ftp} />}>
        <p className={styles.sectionIntro}>{t('fileServices.ftp.explain')}</p>
        <Switch
          isSelected={saved.ftpEnabled}
          isDisabled={save.isPending}
          onChange={(ftpEnabled) => {
            save.mutate({ ftpEnabled });
          }}
        >
          {t('fileServices.ftp.switch')}
        </Switch>
        <Switch
          isSelected={saved.ftpPlainAllowed}
          isDisabled={save.isPending}
          onChange={(ftpPlainAllowed) => {
            save.mutate({ ftpPlainAllowed });
          }}
        >
          {t('fileServices.ftp.plainSwitch')}
        </Switch>
        <p className={styles.notice} role="note">
          {t('fileServices.ftp.plainWarning')}
        </p>
        <ul className={styles.explainList}>
          <li>
            {t('fileServices.ftp.connect')}{' '}
            <span className={styles.mono}>{`${addresses.ftp}:${String(saved.ftpPort)}`}</span>
          </li>
          <li>{t('fileServices.ftp.tls')}</li>
          {saved.ftpFingerprint !== null && (
            <li>
              {t('fileServices.ftp.fingerprint')}{' '}
              <span className={styles.mono}>{saved.ftpFingerprint}</span>
            </li>
          )}
        </ul>
      </FormSection>
      <FormSection title={t('fileServices.ssh.title')}>
        <p className={styles.sectionIntro}>{t('fileServices.ssh.explain')}</p>
        <Switch
          isSelected={saved.sshEnabled}
          isDisabled={save.isPending}
          onChange={(sshEnabled) => {
            save.mutate({ sshEnabled });
          }}
        >
          {t('fileServices.ssh.switch')}
        </Switch>
        <p className={styles.notice} role="note">
          {t('fileServices.ssh.warning')}
        </p>
      </FormSection>
      <FormSection title={t('fileServices.firewall.title')}>
        <p className={styles.sectionIntro}>{t('fileServices.firewall.explain')}</p>
        <Switch
          isSelected={saved.privateNetworksOnly}
          isDisabled={save.isPending}
          onChange={(privateNetworksOnly) => {
            save.mutate({ privateNetworksOnly });
          }}
        >
          {t('fileServices.firewall.privateSwitch')}
        </Switch>
        <p className={styles.muted}>{t('fileServices.firewall.privateExplain')}</p>
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
