import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Checkbox, Dialog, TextField } from '@pithyx/ui';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  proxyCall,
  proxyKey,
  type AcmeAccount,
  type AcmeCertificateStatus,
  type ProxyHost,
  type ProxyRedirect,
  type ProxySettings,
  type ProxyStream,
} from '../sdk/proxy';
import { describeProblem } from '../storage/shared';
import { HostDialog, RedirectDialog, StreamDialog } from './ReverseProxyDialogs';
import styles from './Settings.module.css';

/**
 * Reverse proxy (D347): proxy hosts and redirect hosts that answer on 443 and 80 by name, streams that forward a TCP
 * or UDP port, and what names nobody set up get. The box itself stays on 7484.
 */
export function ReverseProxyPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const settings = useQuery({
    queryKey: proxyKey,
    queryFn: () => proxyCall(app, 'internal.admin.proxy.settings', {}),
  });
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.reverseProxy.title')}</h2>
          <p className={styles.muted}>{t('proxy.intro')}</p>
        </div>
      </header>
      {settings.isError && <ErrorNotice error={describeProblem(settings.error)} />}
      {settings.data !== undefined && (
        <div className={styles.form}>
          <OptionsSection app={app} settings={settings.data} />
          <AcmeSection app={app} />
          <HostsSection app={app} settings={settings.data} />
          <RedirectsSection app={app} settings={settings.data} />
          <StreamsSection app={app} settings={settings.data} />
        </div>
      )}
    </div>
  );
}

function OptionsSection({ app, settings }: { app: PithyxApp; settings: ProxySettings }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [unknownHosts, setUnknownHosts] = useState(settings.unknownHosts);
  const [signInAddress, setSignInAddress] = useState(settings.signInAddress ?? '');
  const save = useMutation({
    mutationFn: () =>
      proxyCall(app, 'internal.admin.proxy.setOptions', {
        unknownHosts,
        signInAddress: signInAddress.trim() === '' ? null : signInAddress.trim(),
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(proxyKey, next);
      setSignInAddress(next.signInAddress ?? '');
    },
  });
  const unchanged =
    unknownHosts === settings.unknownHosts &&
    signInAddress.trim() === (settings.signInAddress ?? '');
  return (
    <section className={styles.card} aria-labelledby="proxy-options">
      <h3 id="proxy-options" className={styles.sectionTitle}>
        {t('proxy.options.title')}
      </h3>
      {settings.plainPort == null ? (
        <p className={styles.notice} role="note">
          {t('proxy.options.noPorts')}
        </p>
      ) : (
        <p className={styles.muted}>
          {settings.tlsPort == null
            ? t('proxy.options.plainOnly', { plain: settings.plainPort })
            : t('proxy.options.ports', { tls: settings.tlsPort, plain: settings.plainPort })}
        </p>
      )}
      <form
        className={styles.group}
        onSubmit={(event) => {
          event.preventDefault();
          save.mutate();
        }}
      >
        <label className={styles.selectField}>
          <span className={styles.fieldLabel}>{t('proxy.options.unknown')}</span>
          <select
            className={styles.select}
            value={unknownHosts}
            onChange={(event) => {
              setUnknownHosts(event.target.value as ProxySettings['unknownHosts']);
            }}
          >
            <option value="redirect">{t('proxy.options.unknownRedirect')}</option>
            <option value="notFound">{t('proxy.options.unknownNotFound')}</option>
          </select>
        </label>
        <TextField
          label={t('proxy.options.signInAddress')}
          description={t('proxy.options.signInAddressHint')}
          value={signInAddress}
          onChange={setSignInAddress}
          type="url"
          autoComplete="off"
          autoCapitalize="none"
        />
        <div className={styles.buttons}>
          <Button type="submit" variant="primary" isDisabled={save.isPending || unchanged}>
            {t('proxy.save')}
          </Button>
        </div>
      </form>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </section>
  );
}

const acmeKey = [...proxyKey, 'acme'] as const;

/** The box's account with the ACME authority: its terms, a contact and which authority (D378). */
function AcmeSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const account = useQuery({
    queryKey: acmeKey,
    queryFn: () => proxyCall(app, 'internal.admin.proxy.acme', {}),
  });
  return (
    <section className={styles.card} aria-labelledby="proxy-acme">
      <h3 id="proxy-acme" className={styles.sectionTitle}>
        {t('proxy.acme.title')}
      </h3>
      <p className={styles.muted}>{t('proxy.acme.explain')}</p>
      {account.isError && <ErrorNotice error={describeProblem(account.error)} />}
      {account.data !== undefined && (
        <AcmeForm key={account.dataUpdatedAt} app={app} account={account.data} />
      )}
    </section>
  );
}

function AcmeForm({ app, account }: { app: PithyxApp; account: AcmeAccount }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [email, setEmail] = useState(account.email ?? '');
  const [directory, setDirectory] = useState(account.directory);
  const [agree, setAgree] = useState(false);
  const save = useMutation({
    mutationFn: () =>
      proxyCall(app, 'internal.admin.proxy.setAcme', {
        directory: directory.trim(),
        email: email.trim() === '' ? null : email.trim(),
        agreeToTerms: agree,
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(acmeKey, next);
    },
  });
  const unchanged =
    !agree && email.trim() === (account.email ?? '') && directory.trim() === account.directory;
  return (
    <form
      className={styles.group}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      {!account.reachable && (
        <p className={styles.notice} role="note">
          {t('proxy.acme.unreachable')}
        </p>
      )}
      {account.termsOfService != null && (
        <a href={account.termsOfService} target="_blank" rel="noreferrer">
          {t('proxy.acme.terms')}
        </a>
      )}
      {account.termsAgreed ? (
        <p className={styles.muted}>{t('proxy.acme.agreed')}</p>
      ) : (
        <Checkbox isSelected={agree} isDisabled={!account.reachable} onChange={setAgree}>
          {t('proxy.acme.agree')}
        </Checkbox>
      )}
      <TextField
        label={t('proxy.acme.email')}
        description={t('proxy.acme.emailHint')}
        value={email}
        onChange={setEmail}
        type="email"
        autoComplete="email"
        autoCapitalize="none"
      />
      <TextField
        label={t('proxy.acme.directory')}
        description={t('proxy.acme.directoryHint')}
        value={directory}
        onChange={setDirectory}
        type="url"
        autoComplete="off"
        autoCapitalize="none"
      />
      <div className={styles.buttons}>
        <Button type="submit" variant="primary" isDisabled={save.isPending || unchanged}>
          {t('proxy.acme.save')}
        </Button>
      </div>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </form>
  );
}

/** Where the certificate from the ACME authority of a host or redirect stands, with a way to ask again now. */
function AcmeLine({
  app,
  kind,
  id,
  acme,
}: {
  app: PithyxApp;
  kind: 'host' | 'redirect';
  id: number;
  acme: AcmeCertificateStatus;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
  const renew = useMutation({
    mutationFn: () => proxyCall(app, 'internal.admin.proxy.renewCertificate', { kind, id }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: proxyKey, exact: true }),
  });
  const running = acme.running || renew.isPending;
  return (
    <>
      <span className={styles.detail}>
        {running
          ? t('proxy.acme.status.running')
          : acme.status === 'valid' && acme.notAfter != null
            ? t('proxy.acme.status.valid', { date: date.format(new Date(acme.notAfter)) })
            : t(`proxy.acme.status.${acme.status === 'failed' ? 'failed' : 'pending'}`)}
        {!running && acme.retryAt != null && (
          <> {t('proxy.acme.status.retry', { date: date.format(new Date(acme.retryAt)) })}</>
        )}
      </span>
      {!running && acme.status === 'failed' && acme.errorCode != null && (
        <ErrorNotice
          error={describeProblem({ code: acme.errorCode, params: acme.errorParams ?? {} })}
        />
      )}
      <div className={styles.buttons}>
        <Button
          isDisabled={running}
          onPress={() => {
            renew.mutate();
          }}
        >
          {t('proxy.acme.renew')}
        </Button>
      </div>
      {renew.isError && <ErrorNotice error={describeProblem(renew.error)} />}
    </>
  );
}

function HostsSection({ app, settings }: { app: PithyxApp; settings: ProxySettings }) {
  const { t } = useTranslation('settings');
  const [editing, setEditing] = useState<ProxyHost | 'new' | null>(null);
  const [removing, setRemoving] = useState<ProxyHost | null>(null);
  return (
    <section className={styles.card} aria-labelledby="proxy-hosts">
      <h3 id="proxy-hosts" className={styles.sectionTitle}>
        {t('proxy.hosts.title')}
      </h3>
      <p className={styles.muted}>{t('proxy.hosts.explain')}</p>
      <div className={styles.buttons}>
        <Button
          variant="primary"
          onPress={() => {
            setEditing('new');
          }}
        >
          {t('proxy.hosts.add')}
        </Button>
      </div>
      {settings.hosts.length === 0 ? (
        <p className={styles.muted}>{t('proxy.hosts.none')}</p>
      ) : (
        <ul className={styles.plainList}>
          {settings.hosts.map((host) => (
            <HostEntry
              key={host.id}
              app={app}
              host={host}
              onEdit={() => {
                setEditing(host);
              }}
              onRemove={() => {
                setRemoving(host);
              }}
            />
          ))}
        </ul>
      )}
      {editing !== null && (
        <HostDialog
          app={app}
          host={editing === 'new' ? null : editing}
          onClose={() => {
            setEditing(null);
          }}
        />
      )}
      {removing !== null && (
        <RemoveDialog
          title={t('proxy.remove.hostTitle', { name: removing.domains[0] ?? '' })}
          explain={t('proxy.remove.hostExplain')}
          remove={() => proxyCall(app, 'internal.admin.proxy.deleteHost', { id: removing.id })}
          onClose={() => {
            setRemoving(null);
          }}
        />
      )}
    </section>
  );
}

const FAILURES = ['timeout', 'refused', 'dns', 'tls'] as const;

/** The kind of failure of a target check, as the page names it. */
function failureOf(error: string | null | undefined): (typeof FAILURES)[number] | 'other' {
  return FAILURES.find((failure) => failure === error) ?? 'other';
}

function targetOf(host: ProxyHost): string {
  const name = host.target.host.includes(':') ? `[${host.target.host}]` : host.target.host;
  return `${host.target.scheme}://${name}:${String(host.target.port)}`;
}

function HostEntry({
  app,
  host,
  onEdit,
  onRemove,
}: {
  app: PithyxApp;
  host: ProxyHost;
  onEdit: () => void;
  onRemove: () => void;
}) {
  const { t } = useTranslation('settings');
  // One look at the target when the page opens, and again on request; nothing polls.
  const status = useQuery({
    queryKey: [...proxyKey, 'check', host.id, host.updatedAt],
    queryFn: () => proxyCall(app, 'internal.admin.proxy.checkHost', { id: host.id }),
    enabled: host.enabled,
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
  });
  return (
    <li className={styles.card}>
      <span className={styles.shareName}>
        <span className={styles.mono}>{host.domains.join(', ')}</span>{' '}
        {!host.enabled && <Badge tone="neutral">{t('proxy.badges.disabled')}</Badge>}
        {host.signIn.required && <Badge tone="accent">{t('proxy.badges.signIn')}</Badge>}
        {host.basicAuthUsers.length > 0 && (
          <Badge tone="accent">{t('proxy.badges.basicAuth')}</Badge>
        )}
        {host.enabled && host.presented == null && (
          <Badge tone="danger">{t('proxy.badges.noCertificate')}</Badge>
        )}
      </span>
      <span className={styles.detail}>
        {t('proxy.hosts.target', { target: targetOf(host) })}
        {host.enabled && status.data !== undefined && (
          <>
            {' '}
            {status.data.reachable ? (
              <Badge tone="highlight">
                {t('proxy.status.reachable', { status: status.data.status ?? 0 })}
              </Badge>
            ) : (
              <Badge tone="danger">
                {t(`proxy.status.unreachable.${failureOf(status.data.error)}`)}
              </Badge>
            )}
          </>
        )}
      </span>
      <span className={styles.detail}>{t('proxy.hosts.log', { source: host.logSource })}</span>
      {host.acme != null && <AcmeLine app={app} kind="host" id={host.id} acme={host.acme} />}
      <div className={styles.buttons}>
        <Button onPress={onEdit}>{t('proxy.edit')}</Button>
        {host.enabled && (
          <Button
            isDisabled={status.isFetching}
            onPress={() => {
              void status.refetch();
            }}
          >
            {t('proxy.status.check')}
          </Button>
        )}
        <Button variant="danger" onPress={onRemove}>
          {t('proxy.remove.action')}
        </Button>
      </div>
      {status.isError && <ErrorNotice error={describeProblem(status.error)} />}
    </li>
  );
}

function RedirectsSection({ app, settings }: { app: PithyxApp; settings: ProxySettings }) {
  const { t } = useTranslation('settings');
  const [editing, setEditing] = useState<ProxyRedirect | 'new' | null>(null);
  const [removing, setRemoving] = useState<ProxyRedirect | null>(null);
  return (
    <section className={styles.card} aria-labelledby="proxy-redirects">
      <h3 id="proxy-redirects" className={styles.sectionTitle}>
        {t('proxy.redirects.title')}
      </h3>
      <p className={styles.muted}>{t('proxy.redirects.explain')}</p>
      <div className={styles.buttons}>
        <Button
          onPress={() => {
            setEditing('new');
          }}
        >
          {t('proxy.redirects.add')}
        </Button>
      </div>
      {settings.redirects.length === 0 ? (
        <p className={styles.muted}>{t('proxy.redirects.none')}</p>
      ) : (
        <ul className={styles.plainList}>
          {settings.redirects.map((redirect) => (
            <li key={redirect.id} className={styles.card}>
              <span className={styles.shareName}>
                <span className={styles.mono}>{redirect.domains.join(', ')}</span>{' '}
                {!redirect.enabled && <Badge tone="neutral">{t('proxy.badges.disabled')}</Badge>}
              </span>
              <span className={styles.detail}>
                {t('proxy.redirects.target', {
                  status: redirect.status,
                  target: redirect.target,
                })}
              </span>
              {redirect.acme != null && (
                <AcmeLine app={app} kind="redirect" id={redirect.id} acme={redirect.acme} />
              )}
              <div className={styles.buttons}>
                <Button
                  onPress={() => {
                    setEditing(redirect);
                  }}
                >
                  {t('proxy.edit')}
                </Button>
                <Button
                  variant="danger"
                  onPress={() => {
                    setRemoving(redirect);
                  }}
                >
                  {t('proxy.remove.action')}
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
      {editing !== null && (
        <RedirectDialog
          app={app}
          redirect={editing === 'new' ? null : editing}
          onClose={() => {
            setEditing(null);
          }}
        />
      )}
      {removing !== null && (
        <RemoveDialog
          title={t('proxy.remove.redirectTitle', { name: removing.domains[0] ?? '' })}
          explain={t('proxy.remove.redirectExplain')}
          remove={() => proxyCall(app, 'internal.admin.proxy.deleteRedirect', { id: removing.id })}
          onClose={() => {
            setRemoving(null);
          }}
        />
      )}
    </section>
  );
}

function StreamsSection({ app, settings }: { app: PithyxApp; settings: ProxySettings }) {
  const { t } = useTranslation('settings');
  const [editing, setEditing] = useState<ProxyStream | 'new' | null>(null);
  const [removing, setRemoving] = useState<ProxyStream | null>(null);
  return (
    <section className={styles.card} aria-labelledby="proxy-streams">
      <h3 id="proxy-streams" className={styles.sectionTitle}>
        {t('proxy.streams.title')}
      </h3>
      <p className={styles.muted}>{t('proxy.streams.explain')}</p>
      <div className={styles.buttons}>
        <Button
          onPress={() => {
            setEditing('new');
          }}
        >
          {t('proxy.streams.add')}
        </Button>
      </div>
      {settings.streams.length === 0 ? (
        <p className={styles.muted}>{t('proxy.streams.none')}</p>
      ) : (
        <ul className={styles.plainList}>
          {settings.streams.map((stream) => (
            <li key={stream.id} className={styles.card}>
              <span className={styles.shareName}>
                {stream.name === '' ? t('proxy.streams.unnamed') : stream.name}{' '}
                {!stream.enabled && <Badge tone="neutral">{t('proxy.badges.disabled')}</Badge>}
                {stream.enabled && !stream.listening && (
                  <Badge tone="danger">{t('proxy.badges.notListening')}</Badge>
                )}
              </span>
              <span className={styles.detail}>
                {t('proxy.streams.route', {
                  port: stream.port,
                  protocols: [
                    stream.tcp ? t('proxy.protocols.tcp') : null,
                    stream.udp ? t('proxy.protocols.udp') : null,
                  ]
                    .filter((protocol) => protocol !== null)
                    .join(', '),
                  target: `${stream.targetHost}:${String(stream.targetPort)}`,
                })}
              </span>
              <div className={styles.buttons}>
                <Button
                  onPress={() => {
                    setEditing(stream);
                  }}
                >
                  {t('proxy.edit')}
                </Button>
                <Button
                  variant="danger"
                  onPress={() => {
                    setRemoving(stream);
                  }}
                >
                  {t('proxy.remove.action')}
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
      {editing !== null && (
        <StreamDialog
          app={app}
          stream={editing === 'new' ? null : editing}
          onClose={() => {
            setEditing(null);
          }}
        />
      )}
      {removing !== null && (
        <RemoveDialog
          title={t('proxy.remove.streamTitle', { port: removing.port })}
          explain={t('proxy.remove.streamExplain')}
          remove={() => proxyCall(app, 'internal.admin.proxy.deleteStream', { id: removing.id })}
          onClose={() => {
            setRemoving(null);
          }}
        />
      )}
    </section>
  );
}

function RemoveDialog({
  title,
  explain,
  remove,
  onClose,
}: {
  title: string;
  explain: string;
  remove: () => Promise<ProxySettings>;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: remove,
    onSuccess: (next) => {
      queryClient.setQueryData(proxyKey, next);
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      tone="danger"
      role="alertdialog"
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={title}
      actions={
        <>
          <Button onPress={onClose}>{t('proxy.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={mutation.isPending}
            onPress={() => {
              mutation.mutate();
            }}
          >
            {t('proxy.remove.confirm')}
          </Button>
        </>
      }
    >
      <p>{explain}</p>
      {mutation.isError && <ErrorNotice error={describeProblem(mutation.error)} />}
    </Dialog>
  );
}
