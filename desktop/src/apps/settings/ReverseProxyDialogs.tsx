import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Checkbox, Dialog, PasswordField, Switch, TextField } from '@pithyx/ui';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  proxyCall,
  proxyKey,
  type ProxyHeader,
  type ProxyHost,
  type ProxyHostRequest,
  type ProxyRedirect,
  type ProxySettings,
  type ProxyStream,
  type ProxyTargetTls,
} from '../sdk/proxy';
import { describeProblem } from '../storage/shared';
import { PemFile } from './CertificatesPage';
import styles from './Settings.module.css';

type CertificateSource = ProxyHostRequest['certificate'];

/** Names or addresses typed into one field, separated by spaces, commas or line breaks. */
export function splitList(text: string): string[] {
  return text
    .split(/[\s,]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
}

/** One header per line as `Name: value`; `Name:` alone removes it. */
export function parseHeaders(text: string): ProxyHeader[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '')
    .map((line) => {
      const colon = line.indexOf(':');
      return colon < 0
        ? { name: line, value: '' }
        : { name: line.slice(0, colon).trim(), value: line.slice(colon + 1).trim() };
    });
}

function formatHeaders(headers: readonly ProxyHeader[]): string {
  return headers.map((header) => `${header.name}: ${header.value}`).join('\n');
}

/** A whole number typed into a field; anything else becomes 0, which the daemon refuses with its limits. */
function number(text: string): number {
  const value = Number.parseInt(text, 10);
  return Number.isNaN(value) ? 0 : value;
}

interface BasicUser {
  username: string;
  password: string;
  stored: boolean;
}

function DialogFrame({
  title,
  pending,
  canSave,
  onSave,
  onClose,
  children,
}: {
  title: string;
  pending: boolean;
  canSave: boolean;
  onSave: () => void;
  onClose: () => void;
  children: ReactNode;
}) {
  const { t } = useTranslation('settings');
  return (
    <Dialog
      isOpen
      isDismissable={!pending}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={title}
      actions={
        <>
          <Button onPress={onClose}>{t('proxy.cancel')}</Button>
          <Button variant="primary" isDisabled={pending || !canSave} onPress={onSave}>
            {t('proxy.save')}
          </Button>
        </>
      }
    >
      <div className={styles.group}>{children}</div>
    </Dialog>
  );
}

function CertificateFields({
  source,
  onSource,
  stored,
  certificate,
  onCertificate,
  privateKey,
  onPrivateKey,
}: {
  source: CertificateSource;
  onSource: (source: CertificateSource) => void;
  stored: boolean;
  certificate: File | null;
  onCertificate: (file: File | null) => void;
  privateKey: File | null;
  onPrivateKey: (file: File | null) => void;
}) {
  const { t } = useTranslation('settings');
  return (
    <>
      <label className={styles.selectField}>
        <span className={styles.fieldLabel}>{t('proxy.certificate.label')}</span>
        <select
          className={styles.select}
          value={source}
          onChange={(event) => {
            onSource(event.target.value as CertificateSource);
          }}
        >
          <option value="box">{t('proxy.certificate.box')}</option>
          <option value="custom">{t('proxy.certificate.custom')}</option>
          <option value="acme">{t('proxy.certificate.acme')}</option>
        </select>
        <span className={styles.optionHint}>{t(`proxy.certificate.${source}Hint`)}</span>
      </label>
      {source === 'custom' && (
        <>
          {stored && <p className={styles.muted}>{t('proxy.certificate.stored')}</p>}
          <PemFile
            label={t('certificates.custom.certificate')}
            accept=".pem,.crt,.cer"
            file={certificate}
            onChange={onCertificate}
          />
          <PemFile
            label={t('certificates.custom.key')}
            accept=".pem,.key"
            file={privateKey}
            onChange={onPrivateKey}
          />
        </>
      )}
    </>
  );
}

function TextArea({
  label,
  description,
  value,
  onChange,
}: {
  label: string;
  description: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const id = useId();
  return (
    <div className={styles.selectField}>
      <label htmlFor={id} className={styles.fieldLabel}>
        {label}
      </label>
      <textarea
        id={id}
        className={styles.textarea}
        value={value}
        rows={3}
        spellCheck={false}
        autoCapitalize="none"
        aria-describedby={`${id}-hint`}
        onChange={(event) => {
          onChange(event.target.value);
        }}
      />
      <span id={`${id}-hint`} className={styles.optionHint}>
        {description}
      </span>
    </div>
  );
}

/** A new proxy host, or the settings of an existing one. */
export function HostDialog({
  app,
  host,
  onClose,
}: {
  app: PithyxApp;
  host: ProxyHost | null;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [domains, setDomains] = useState(host?.domains.join(', ') ?? '');
  const [enabled, setEnabled] = useState(host?.enabled ?? true);
  const [scheme, setScheme] = useState(host?.target.scheme ?? 'http');
  const [targetHost, setTargetHost] = useState(host?.target.host ?? '');
  const [targetPort, setTargetPort] = useState(String(host?.target.port ?? 80));
  const [tls, setTls] = useState<ProxyTargetTls>(host?.target.tls ?? 'system');
  const [onBox, setOnBox] = useState(host?.target.onBox ?? false);
  const [webSockets, setWebSockets] = useState(host?.webSockets ?? true);
  const [http2, setHttp2] = useState(host?.http2 ?? true);
  const [forceHttps, setForceHttps] = useState(host?.forceHttps ?? true);
  const [hsts, setHsts] = useState(host?.hsts ?? false);
  const [forwardedHeaders, setForwardedHeaders] = useState(host?.forwardedHeaders ?? true);
  const [preserveHost, setPreserveHost] = useState(host?.preserveHost ?? false);
  const [hostHeader, setHostHeader] = useState(host?.hostHeader ?? '');
  const [requestHeaders, setRequestHeaders] = useState(formatHeaders(host?.requestHeaders ?? []));
  const [responseHeaders, setResponseHeaders] = useState(
    formatHeaders(host?.responseHeaders ?? []),
  );
  const [maxBody, setMaxBody] = useState(
    host?.maxBodyMegabytes == null ? '' : String(host.maxBodyMegabytes),
  );
  const [connectTimeout, setConnectTimeout] = useState(String(host?.connectTimeoutSeconds ?? 10));
  const [responseTimeout, setResponseTimeout] = useState(
    String(host?.responseTimeoutSeconds ?? 100),
  );
  const [allow, setAllow] = useState(host?.allowAddresses.join(', ') ?? '');
  const [deny, setDeny] = useState(host?.denyAddresses.join(', ') ?? '');
  const [basicUsers, setBasicUsers] = useState<BasicUser[]>(
    host?.basicAuthUsers.map((username) => ({ username, password: '', stored: true })) ?? [],
  );
  const [signIn, setSignIn] = useState(host?.signIn.required ?? false);
  const [users, setUsers] = useState(host?.signIn.users.join(', ') ?? '');
  const [groups, setGroups] = useState(host?.signIn.groups.join(', ') ?? '');
  const [accessLog, setAccessLog] = useState(host?.accessLog ?? true);
  const [certificate, setCertificate] = useState<CertificateSource>(host?.certificate ?? 'box');
  const [certificateFile, setCertificateFile] = useState<File | null>(null);
  const [keyFile, setKeyFile] = useState<File | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      const request: ProxyHostRequest = {
        domains: splitList(domains),
        enabled,
        target: { scheme, host: targetHost.trim(), port: number(targetPort), tls, onBox },
        webSockets,
        http2,
        certificate,
        forceHttps,
        hsts,
        forwardedHeaders,
        preserveHost,
        hostHeader: hostHeader.trim() === '' ? null : hostHeader.trim(),
        requestHeaders: parseHeaders(requestHeaders),
        responseHeaders: parseHeaders(responseHeaders),
        maxBodyMegabytes: maxBody.trim() === '' ? null : number(maxBody),
        connectTimeoutSeconds: number(connectTimeout),
        responseTimeoutSeconds: number(responseTimeout),
        allowAddresses: splitList(allow),
        denyAddresses: splitList(deny),
        basicAuth: basicUsers.map((user) => ({
          username: user.username.trim(),
          password: user.stored && user.password === '' ? null : user.password,
        })),
        signIn: { required: signIn, users: splitList(users), groups: splitList(groups) },
        accessLog,
        certificatePem: certificate === 'custom' ? ((await certificateFile?.text()) ?? null) : null,
        privateKeyPem: certificate === 'custom' ? ((await keyFile?.text()) ?? null) : null,
      };
      return proxyCall(app, 'internal.admin.proxy.saveHost', {
        ...(host === null ? {} : { id: host.id }),
        host: request,
      });
    },
    onSuccess: (next: ProxySettings) => {
      queryClient.setQueryData(proxyKey, next);
      onClose();
    },
  });

  return (
    <DialogFrame
      title={host === null ? t('proxy.hostDialog.addTitle') : t('proxy.hostDialog.editTitle')}
      pending={save.isPending}
      canSave={splitList(domains).length > 0 && targetHost.trim() !== ''}
      onSave={() => {
        save.mutate();
      }}
      onClose={onClose}
    >
      <TextField
        label={t('proxy.fields.domains')}
        description={t('proxy.fields.domainsHint')}
        value={domains}
        onChange={setDomains}
        autoComplete="off"
        autoCapitalize="none"
      />
      <Switch isSelected={enabled} onChange={setEnabled}>
        {t('proxy.fields.enabled')}
      </Switch>

      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.hostDialog.target')}</legend>
        <label className={styles.selectField}>
          <span className={styles.fieldLabel}>{t('proxy.fields.scheme')}</span>
          <select
            className={styles.select}
            value={scheme}
            onChange={(event) => {
              setScheme(event.target.value);
            }}
          >
            <option value="http">{t('proxy.schemes.http')}</option>
            <option value="https">{t('proxy.schemes.https')}</option>
          </select>
        </label>
        <TextField
          label={t('proxy.fields.targetHost')}
          description={t('proxy.fields.targetHostHint')}
          value={targetHost}
          onChange={setTargetHost}
          autoComplete="off"
          autoCapitalize="none"
        />
        <TextField
          label={t('proxy.fields.targetPort')}
          value={targetPort}
          onChange={setTargetPort}
          inputMode="numeric"
          autoComplete="off"
        />
        {scheme === 'https' && (
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('proxy.fields.tls')}</span>
            <select
              className={styles.select}
              value={tls}
              onChange={(event) => {
                setTls(event.target.value as ProxyTargetTls);
              }}
            >
              <option value="system">{t('proxy.tls.system')}</option>
              <option value="box">{t('proxy.tls.box')}</option>
              <option value="none">{t('proxy.tls.none')}</option>
            </select>
            <span className={styles.optionHint}>{t(`proxy.tls.${tls}Hint`)}</span>
          </label>
        )}
        <Checkbox isSelected={onBox} onChange={setOnBox}>
          {t('proxy.fields.onBox')}
        </Checkbox>
        <Switch isSelected={webSockets} onChange={setWebSockets}>
          {t('proxy.fields.webSockets')}
        </Switch>
      </fieldset>

      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.hostDialog.https')}</legend>
        <CertificateFields
          source={certificate}
          onSource={setCertificate}
          stored={host?.certificate === 'custom'}
          certificate={certificateFile}
          onCertificate={setCertificateFile}
          privateKey={keyFile}
          onPrivateKey={setKeyFile}
        />
        <Switch isSelected={forceHttps} onChange={setForceHttps}>
          {t('proxy.fields.forceHttps')}
        </Switch>
        <Switch isSelected={http2} onChange={setHttp2}>
          {t('proxy.fields.http2')}
        </Switch>
        <Switch isSelected={hsts} onChange={setHsts}>
          {t('proxy.fields.hsts')}
        </Switch>
        {hsts && (
          <p className={styles.notice} role="note">
            {t('proxy.fields.hstsWarning')}
          </p>
        )}
      </fieldset>

      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.hostDialog.access')}</legend>
        <TextField
          label={t('proxy.fields.allow')}
          description={t('proxy.fields.allowHint')}
          value={allow}
          onChange={setAllow}
          autoComplete="off"
          autoCapitalize="none"
        />
        <TextField
          label={t('proxy.fields.deny')}
          value={deny}
          onChange={setDeny}
          autoComplete="off"
          autoCapitalize="none"
        />
        <Switch isSelected={signIn} onChange={setSignIn}>
          {t('proxy.fields.signIn')}
        </Switch>
        {signIn && (
          <>
            <p className={styles.muted}>{t('proxy.fields.signInExplain')}</p>
            <TextField
              label={t('proxy.fields.users')}
              value={users}
              onChange={setUsers}
              autoComplete="off"
              autoCapitalize="none"
            />
            <TextField
              label={t('proxy.fields.groups')}
              description={t('proxy.fields.usersHint')}
              value={groups}
              onChange={setGroups}
              autoComplete="off"
            />
          </>
        )}
        <span className={styles.fieldLabel}>{t('proxy.fields.basicAuth')}</span>
        <span className={styles.optionHint}>{t('proxy.fields.basicAuthHint')}</span>
        {basicUsers.map((user, index) => (
          <div key={index} className={styles.group}>
            <TextField
              label={t('proxy.fields.basicUser')}
              value={user.username}
              isDisabled={user.stored}
              onChange={(username) => {
                setBasicUsers(
                  basicUsers.map((entry, at) => (at === index ? { ...entry, username } : entry)),
                );
              }}
              autoComplete="off"
              autoCapitalize="none"
            />
            <PasswordField
              label={t('proxy.fields.basicPassword')}
              description={user.stored ? t('proxy.fields.basicPasswordKeep') : undefined}
              value={user.password}
              onChange={(password) => {
                setBasicUsers(
                  basicUsers.map((entry, at) => (at === index ? { ...entry, password } : entry)),
                );
              }}
              autoComplete="new-password"
            />
            <div className={styles.buttons}>
              <Button
                variant="danger"
                onPress={() => {
                  setBasicUsers(basicUsers.filter((_, at) => at !== index));
                }}
              >
                {t('proxy.fields.basicRemove')}
              </Button>
            </div>
          </div>
        ))}
        <div className={styles.buttons}>
          <Button
            onPress={() => {
              setBasicUsers([...basicUsers, { username: '', password: '', stored: false }]);
            }}
          >
            {t('proxy.fields.basicAdd')}
          </Button>
        </div>
      </fieldset>

      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.hostDialog.headers')}</legend>
        <Switch isSelected={forwardedHeaders} onChange={setForwardedHeaders}>
          {t('proxy.fields.forwardedHeaders')}
        </Switch>
        <Switch isSelected={preserveHost} onChange={setPreserveHost}>
          {t('proxy.fields.preserveHost')}
        </Switch>
        <TextField
          label={t('proxy.fields.hostHeader')}
          description={t('proxy.fields.hostHeaderHint')}
          value={hostHeader}
          onChange={setHostHeader}
          autoComplete="off"
          autoCapitalize="none"
        />
        <TextArea
          label={t('proxy.fields.requestHeaders')}
          description={t('proxy.fields.headersHint')}
          value={requestHeaders}
          onChange={setRequestHeaders}
        />
        <TextArea
          label={t('proxy.fields.responseHeaders')}
          description={t('proxy.fields.headersHint')}
          value={responseHeaders}
          onChange={setResponseHeaders}
        />
      </fieldset>

      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.hostDialog.limits')}</legend>
        <TextField
          label={t('proxy.fields.maxBody')}
          description={t('proxy.fields.maxBodyHint')}
          value={maxBody}
          onChange={setMaxBody}
          inputMode="numeric"
          autoComplete="off"
        />
        <TextField
          label={t('proxy.fields.connectTimeout')}
          value={connectTimeout}
          onChange={setConnectTimeout}
          inputMode="numeric"
          autoComplete="off"
        />
        <TextField
          label={t('proxy.fields.responseTimeout')}
          value={responseTimeout}
          onChange={setResponseTimeout}
          inputMode="numeric"
          autoComplete="off"
        />
        <Switch isSelected={accessLog} onChange={setAccessLog}>
          {t('proxy.fields.accessLog')}
        </Switch>
      </fieldset>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </DialogFrame>
  );
}

const STATUSES = ['301', '302', '307', '308'] as const;

/** A new redirect host, or the settings of an existing one. */
export function RedirectDialog({
  app,
  redirect,
  onClose,
}: {
  app: PithyxApp;
  redirect: ProxyRedirect | null;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [domains, setDomains] = useState(redirect?.domains.join(', ') ?? '');
  const [enabled, setEnabled] = useState(redirect?.enabled ?? true);
  const [target, setTarget] = useState(redirect?.target ?? '');
  const [status, setStatus] = useState<(typeof STATUSES)[number]>(
    STATUSES.find((code) => code === String(redirect?.status)) ?? '301',
  );
  const [preservePath, setPreservePath] = useState(redirect?.preservePath ?? true);
  const [certificate, setCertificate] = useState<CertificateSource>(redirect?.certificate ?? 'box');
  const [certificateFile, setCertificateFile] = useState<File | null>(null);
  const [keyFile, setKeyFile] = useState<File | null>(null);
  const save = useMutation({
    mutationFn: async () =>
      proxyCall(app, 'internal.admin.proxy.saveRedirect', {
        ...(redirect === null ? {} : { id: redirect.id }),
        redirect: {
          domains: splitList(domains),
          enabled,
          target: target.trim(),
          status: Number(status),
          preservePath,
          certificate,
          certificatePem:
            certificate === 'custom' ? ((await certificateFile?.text()) ?? null) : null,
          privateKeyPem: certificate === 'custom' ? ((await keyFile?.text()) ?? null) : null,
        },
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(proxyKey, next);
      onClose();
    },
  });
  return (
    <DialogFrame
      title={
        redirect === null ? t('proxy.redirectDialog.addTitle') : t('proxy.redirectDialog.editTitle')
      }
      pending={save.isPending}
      canSave={splitList(domains).length > 0 && target.trim() !== ''}
      onSave={() => {
        save.mutate();
      }}
      onClose={onClose}
    >
      <TextField
        label={t('proxy.fields.domains')}
        description={t('proxy.fields.domainsHint')}
        value={domains}
        onChange={setDomains}
        autoComplete="off"
        autoCapitalize="none"
      />
      <Switch isSelected={enabled} onChange={setEnabled}>
        {t('proxy.fields.enabled')}
      </Switch>
      <TextField
        label={t('proxy.fields.redirectTarget')}
        description={t('proxy.fields.redirectTargetHint')}
        value={target}
        onChange={setTarget}
        type="url"
        autoComplete="off"
        autoCapitalize="none"
      />
      <label className={styles.selectField}>
        <span className={styles.fieldLabel}>{t('proxy.fields.status')}</span>
        <select
          className={styles.select}
          value={status}
          onChange={(event) => {
            setStatus(STATUSES.find((code) => code === event.target.value) ?? '301');
          }}
        >
          {STATUSES.map((code) => (
            <option key={code} value={code}>
              {t(`proxy.statuses.${code}`)}
            </option>
          ))}
        </select>
      </label>
      <Switch isSelected={preservePath} onChange={setPreservePath}>
        {t('proxy.fields.preservePath')}
      </Switch>
      <CertificateFields
        source={certificate}
        onSource={setCertificate}
        stored={redirect?.certificate === 'custom'}
        certificate={certificateFile}
        onCertificate={setCertificateFile}
        privateKey={keyFile}
        onPrivateKey={setKeyFile}
      />
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </DialogFrame>
  );
}

/** A new stream, or the settings of an existing one. */
export function StreamDialog({
  app,
  stream,
  onClose,
}: {
  app: PithyxApp;
  stream: ProxyStream | null;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [name, setName] = useState(stream?.name ?? '');
  const [enabled, setEnabled] = useState(stream?.enabled ?? true);
  const [port, setPort] = useState(stream === null ? '' : String(stream.port));
  const [tcp, setTcp] = useState(stream?.tcp ?? true);
  const [udp, setUdp] = useState(stream?.udp ?? false);
  const [targetHost, setTargetHost] = useState(stream?.targetHost ?? '');
  const [targetPort, setTargetPort] = useState(stream === null ? '' : String(stream.targetPort));
  const [privileged, setPrivileged] = useState(stream !== null && stream.port < 1024);
  const save = useMutation({
    mutationFn: () =>
      proxyCall(app, 'internal.admin.proxy.saveStream', {
        ...(stream === null ? {} : { id: stream.id }),
        stream: {
          name: name.trim(),
          enabled,
          port: number(port),
          tcp,
          udp,
          targetHost: targetHost.trim(),
          targetPort: number(targetPort),
          privilegedPort: privileged,
        },
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(proxyKey, next);
      onClose();
    },
  });
  return (
    <DialogFrame
      title={stream === null ? t('proxy.streamDialog.addTitle') : t('proxy.streamDialog.editTitle')}
      pending={save.isPending}
      canSave={port.trim() !== '' && targetHost.trim() !== '' && targetPort.trim() !== ''}
      onSave={() => {
        save.mutate();
      }}
      onClose={onClose}
    >
      <TextField
        label={t('proxy.fields.streamName')}
        value={name}
        onChange={setName}
        autoComplete="off"
      />
      <Switch isSelected={enabled} onChange={setEnabled}>
        {t('proxy.fields.streamEnabled')}
      </Switch>
      <TextField
        label={t('proxy.fields.port')}
        description={t('proxy.fields.portHint')}
        value={port}
        onChange={setPort}
        inputMode="numeric"
        autoComplete="off"
      />
      {number(port) > 0 && number(port) < 1024 && (
        <Checkbox isSelected={privileged} onChange={setPrivileged}>
          {t('proxy.fields.privileged')}
        </Checkbox>
      )}
      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('proxy.fields.protocols')}</legend>
        <Checkbox isSelected={tcp} onChange={setTcp}>
          {t('proxy.protocols.tcp')}
        </Checkbox>
        <Checkbox isSelected={udp} onChange={setUdp}>
          {t('proxy.protocols.udp')}
        </Checkbox>
      </fieldset>
      <TextField
        label={t('proxy.fields.targetHost')}
        description={t('proxy.fields.targetHostHint')}
        value={targetHost}
        onChange={setTargetHost}
        autoComplete="off"
        autoCapitalize="none"
      />
      <TextField
        label={t('proxy.fields.targetPort')}
        value={targetPort}
        onChange={setTargetPort}
        inputMode="numeric"
        autoComplete="off"
      />
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </DialogFrame>
  );
}
