import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { Badge, Button, Dialog, FormSection, Switch, TextField } from '@pithyx/ui';
import { TrustSteps } from '../../security/TrustSteps';
import {
  tlsCall,
  type CertificateInfo,
  type IssuedCertificate,
  type TlsSettings,
} from '../sdk/tls';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

const tlsKey = ['settings', 'system', 'tls'] as const;

/** Names typed into one field, separated by spaces, commas or line breaks. */
function splitNames(text: string): string[] {
  return text
    .split(/[\s,]+/)
    .map((name) => name.trim())
    .filter((name) => name !== '');
}

/**
 * Certificates (D346): the certificate the web interface and FTPS present, the box's own certificate authority with
 * the root to download and trust, the names of the box's certificate, certificates for other devices and a
 * certificate of your own.
 */
export function CertificatesPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const settings = useQuery({
    queryKey: tlsKey,
    queryFn: () => tlsCall(app, 'internal.admin.tls.settings', {}),
  });
  return (
    <div className={styles.content}>
      <p className={styles.sectionIntro}>{t('certificates.intro')}</p>
      {settings.isError && <ErrorNotice error={describeProblem(settings.error)} />}
      {settings.data !== undefined && <Certificates app={app} settings={settings.data} />}
    </div>
  );
}

function Certificates({ app, settings }: { app: PithyxApp; settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.form}>
      <FormSection title={t('certificates.connection.title')}>
        <p className={styles.sectionIntro}>{t('certificates.connection.explain')}</p>
        {!settings.https && (
          <p className={styles.notice} role="note">
            {t('certificates.connection.plain')}
          </p>
        )}
        <CertificateFacts
          certificate={settings.active}
          source={t(`certificates.source.${settings.source}`)}
        />
      </FormSection>
      <TrustSection settings={settings} />
      <AuthoritySection app={app} settings={settings} />
      <NamesSection app={app} settings={settings} />
      <IssuedSection app={app} settings={settings} />
      <CustomSection app={app} settings={settings} />
    </div>
  );
}

function CertificateFacts({
  certificate,
  source,
}: {
  certificate: CertificateInfo;
  source?: string;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' });
  return (
    <dl className={styles.facts}>
      {source !== undefined && (
        <div className={styles.fact}>
          <dt>{t('certificates.facts.source')}</dt>
          <dd>{source}</dd>
        </div>
      )}
      <div className={styles.fact}>
        <dt>{t('certificates.facts.subject')}</dt>
        <dd>{certificate.subject}</dd>
      </div>
      {certificate.names.length > 0 && (
        <div className={styles.fact}>
          <dt>{t('certificates.facts.names')}</dt>
          <dd className={styles.mono}>{certificate.names.join(', ')}</dd>
        </div>
      )}
      <div className={styles.fact}>
        <dt>{t('certificates.facts.validUntil')}</dt>
        <dd>{date.format(new Date(certificate.notAfter))}</dd>
      </div>
      <div className={styles.fact}>
        <dt>{t('certificates.facts.fingerprint')}</dt>
        <dd className={styles.mono}>{certificate.fingerprint}</dd>
      </div>
    </dl>
  );
}

function TrustSection({ settings }: { settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  return (
    <FormSection title={t('certificates.trust.title')}>
      <TrustSteps
        facts={
          <>
            <CertificateFacts certificate={settings.authority.root} />
            <p className={styles.muted}>{t('certificates.trust.compare')}</p>
          </>
        }
      />
    </FormSection>
  );
}

function AuthoritySection({ app, settings }: { app: PithyxApp; settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const [creating, setCreating] = useState(false);
  const { authority } = settings;
  return (
    <FormSection title={t('certificates.authority.title')}>
      <p className={styles.sectionIntro}>{t('certificates.authority.explain')}</p>
      {authority.restricted ? (
        <dl className={styles.facts}>
          <div className={styles.fact}>
            <dt>{t('certificates.authority.domains')}</dt>
            <dd className={styles.mono}>{authority.domains.join(', ')}</dd>
          </div>
          <div className={styles.fact}>
            <dt>{t('certificates.authority.addresses')}</dt>
            <dd className={styles.mono}>{authority.addressRanges.join(', ')}</dd>
          </div>
        </dl>
      ) : (
        <p className={styles.notice} role="note">
          {t('certificates.authority.unrestricted')}
        </p>
      )}
      <p className={styles.muted}>
        {t('certificates.authority.intermediate', {
          date: new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(
            new Date(authority.intermediate.notAfter),
          ),
        })}
      </p>
      <div className={styles.buttons}>
        <Button
          onPress={() => {
            setCreating(true);
          }}
        >
          {t('certificates.authority.create')}
        </Button>
      </div>
      {creating && (
        <CreateAuthorityDialog
          app={app}
          settings={settings}
          onClose={() => {
            setCreating(false);
          }}
        />
      )}
    </FormSection>
  );
}

function CreateAuthorityDialog({
  app,
  settings,
  onClose,
}: {
  app: PithyxApp;
  settings: TlsSettings;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [restricted, setRestricted] = useState(true);
  const [domains, setDomains] = useState('');
  const create = useMutation({
    mutationFn: () =>
      tlsCall(app, 'internal.admin.tls.createAuthority', {
        restricted,
        domains: restricted ? splitNames(domains) : [],
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(tlsKey, next);
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      tone="danger"
      isDismissable={!create.isPending}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('certificates.authority.dialog.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('certificates.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={create.isPending}
            onPress={() => {
              create.mutate();
            }}
          >
            {t('certificates.authority.dialog.confirm')}
          </Button>
        </>
      }
    >
      <p>{t('certificates.authority.dialog.explain')}</p>
      <Switch isSelected={restricted} onChange={setRestricted}>
        {t('certificates.authority.dialog.restricted')}
      </Switch>
      {restricted ? (
        <>
          <p className={styles.muted}>
            {t('certificates.authority.dialog.suggested', {
              domains: settings.suggestedDomains.join(', '),
            })}
          </p>
          <TextField
            label={t('certificates.authority.dialog.domains')}
            description={t('certificates.authority.dialog.domainsHint')}
            value={domains}
            onChange={setDomains}
            autoComplete="off"
            autoCapitalize="none"
          />
        </>
      ) : (
        <p className={styles.notice} role="note">
          {t('certificates.authority.dialog.unrestrictedWarning')}
        </p>
      )}
      {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
    </Dialog>
  );
}

function NamesSection({ app, settings }: { app: PithyxApp; settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [extra, setExtra] = useState(settings.extraNames.join(', '));
  const save = useMutation({
    mutationFn: () =>
      tlsCall(app, 'internal.admin.tls.setNames', { extraNames: splitNames(extra) }),
    onSuccess: (next) => {
      queryClient.setQueryData(tlsKey, next);
      setExtra(next.extraNames.join(', '));
    },
  });
  return (
    <FormSection title={t('certificates.names.title')}>
      <p className={styles.sectionIntro}>{t('certificates.names.explain')}</p>
      <p className={styles.mono}>{settings.boxNames.join(', ')}</p>
      <form
        className={styles.group}
        onSubmit={(event) => {
          event.preventDefault();
          save.mutate();
        }}
      >
        <TextField
          label={t('certificates.names.extra')}
          description={t('certificates.names.extraHint')}
          value={extra}
          onChange={setExtra}
          autoComplete="off"
          autoCapitalize="none"
        />
        <div className={styles.buttons}>
          <Button
            type="submit"
            variant="primary"
            isDisabled={
              save.isPending || splitNames(extra).join(',') === settings.extraNames.join(',')
            }
          >
            {t('certificates.save')}
          </Button>
        </div>
      </form>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormSection>
  );
}

function IssuedSection({ app, settings }: { app: PithyxApp; settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  const [issuing, setIssuing] = useState(false);
  const [removing, setRemoving] = useState<IssuedCertificate | null>(null);
  const download = useMutation({
    mutationFn: (id: number) => tlsCall(app, 'internal.admin.tls.downloadIssued', { id }),
  });
  return (
    <FormSection title={t('certificates.issued.title')}>
      <p className={styles.sectionIntro}>{t('certificates.issued.explain')}</p>
      <div className={styles.buttons}>
        <Button
          variant="primary"
          onPress={() => {
            setIssuing(true);
          }}
        >
          {t('certificates.issued.issue')}
        </Button>
      </div>
      {settings.issued.length === 0 ? (
        <p className={styles.muted}>{t('certificates.issued.none')}</p>
      ) : (
        <ul className={styles.plainList}>
          {settings.issued.map((issued) => (
            <IssuedEntry
              key={issued.id}
              issued={issued}
              onDownload={() => {
                download.mutate(issued.id);
              }}
              onRemove={() => {
                setRemoving(issued);
              }}
            />
          ))}
        </ul>
      )}
      {download.isError && <ErrorNotice error={describeProblem(download.error)} />}
      {issuing && (
        <IssueDialog
          app={app}
          onClose={() => {
            setIssuing(false);
          }}
        />
      )}
      {removing !== null && (
        <RemoveIssuedDialog
          app={app}
          issued={removing}
          onClose={() => {
            setRemoving(null);
          }}
        />
      )}
    </FormSection>
  );
}

function IssuedEntry({
  issued,
  onDownload,
  onRemove,
}: {
  issued: IssuedCertificate;
  onDownload: () => void;
  onRemove: () => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' });
  return (
    <li className={styles.card}>
      <span className={styles.shareName}>
        <span className={styles.mono}>{issued.certificate.names.join(', ')}</span>{' '}
        {!issued.currentAuthority && (
          <Badge tone="neutral">{t('certificates.issued.earlier')}</Badge>
        )}
      </span>
      <span className={styles.detail}>
        {t('certificates.issued.details', {
          until: date.format(new Date(issued.certificate.notAfter)),
          created: date.format(new Date(issued.createdAt)),
          by: issued.createdBy ?? '',
          key: t(`certificates.keyType.${issued.keyType}`),
        })}
      </span>
      <div className={styles.buttons}>
        <Button onPress={onDownload}>{t('certificates.issued.download')}</Button>
        <Button variant="danger" onPress={onRemove}>
          {t('certificates.issued.remove')}
        </Button>
      </div>
    </li>
  );
}

function IssueDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [names, setNames] = useState('');
  const [days, setDays] = useState('397');
  const [rsa, setRsa] = useState(false);
  const issue = useMutation({
    mutationFn: () =>
      tlsCall(app, 'internal.admin.tls.issue', {
        names: splitNames(names),
        validityDays: Number.parseInt(days, 10) || 0,
        keyType: rsa ? 'rsa' : 'ecdsa',
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tlsKey }),
  });
  return (
    <Dialog
      isOpen
      isDismissable={!issue.isPending}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('certificates.issueDialog.title')}
      actions={
        issue.isSuccess ? (
          <Button variant="primary" onPress={onClose}>
            {t('certificates.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('certificates.cancel')}</Button>
            <Button
              variant="primary"
              isDisabled={issue.isPending || splitNames(names).length === 0}
              onPress={() => {
                issue.mutate();
              }}
            >
              {t('certificates.issueDialog.confirm')}
            </Button>
          </>
        )
      }
    >
      {issue.isSuccess ? (
        <p className={styles.notice} role="status">
          {t('certificates.issueDialog.done')}
        </p>
      ) : (
        <>
          <p className={styles.muted}>{t('certificates.issueDialog.explain')}</p>
          <TextField
            label={t('certificates.issueDialog.names')}
            description={t('certificates.issueDialog.namesHint')}
            value={names}
            onChange={setNames}
            autoComplete="off"
            autoCapitalize="none"
          />
          <TextField
            label={t('certificates.issueDialog.days')}
            description={t('certificates.issueDialog.daysHint')}
            value={days}
            onChange={setDays}
            inputMode="numeric"
            autoComplete="off"
          />
          <Switch isSelected={rsa} onChange={setRsa}>
            {t('certificates.issueDialog.rsa')}
          </Switch>
          {issue.isError && <ErrorNotice error={describeProblem(issue.error)} />}
        </>
      )}
    </Dialog>
  );
}

function RemoveIssuedDialog({
  app,
  issued,
  onClose,
}: {
  app: PithyxApp;
  issued: IssuedCertificate;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () => tlsCall(app, 'internal.admin.tls.deleteIssued', { id: issued.id }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: tlsKey });
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
      title={t('certificates.removeDialog.title', { names: issued.certificate.names.join(', ') })}
      actions={
        <>
          <Button onPress={onClose}>{t('certificates.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={remove.isPending}
            onPress={() => {
              remove.mutate();
            }}
          >
            {t('certificates.removeDialog.confirm')}
          </Button>
        </>
      }
    >
      <p>{t('certificates.removeDialog.explain')}</p>
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </Dialog>
  );
}

export function PemFile({
  label,
  accept,
  file,
  onChange,
}: {
  label: string;
  accept: string;
  file: File | null;
  onChange: (file: File | null) => void;
}) {
  const { t } = useTranslation('settings');
  const ids = useId();
  const input = useRef<HTMLInputElement>(null);
  return (
    <div className={styles.group}>
      <span id={`${ids}-label`} className={styles.fieldLabel}>
        {label}
      </span>
      <div className={styles.filePicker}>
        <Button
          aria-describedby={`${ids}-name`}
          onPress={() => {
            input.current?.click();
          }}
        >
          {t('certificates.custom.choose')}
        </Button>
        <span id={`${ids}-name`} className={styles.muted}>
          {file?.name ?? t('certificates.custom.noFile')}
        </span>
        {/* The native picker opens from the button; the input itself stays out of sight and out of the tab order. */}
        <input
          ref={input}
          className="visually-hidden"
          type="file"
          accept={accept}
          tabIndex={-1}
          aria-labelledby={`${ids}-label`}
          onChange={(event) => {
            onChange(event.target.files?.[0] ?? null);
          }}
        />
      </div>
    </div>
  );
}

function CustomSection({ app, settings }: { app: PithyxApp; settings: TlsSettings }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [certificate, setCertificate] = useState<File | null>(null);
  const [key, setKey] = useState<File | null>(null);
  const upload = useMutation({
    mutationFn: async () =>
      tlsCall(app, 'internal.admin.tls.setCustom', {
        certificatePem: (await certificate?.text()) ?? '',
        privateKeyPem: (await key?.text()) ?? '',
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(tlsKey, next);
      setCertificate(null);
      setKey(null);
    },
  });
  const remove = useMutation({
    mutationFn: () => tlsCall(app, 'internal.admin.tls.removeCustom', {}),
    onSuccess: (next) => {
      queryClient.setQueryData(tlsKey, next);
    },
  });
  return (
    <FormSection title={t('certificates.custom.title')}>
      <p className={styles.sectionIntro}>{t('certificates.custom.explain')}</p>
      {settings.custom !== null && (
        <>
          <CertificateFacts certificate={settings.custom} />
          {settings.source !== 'custom' && (
            <p className={styles.notice} role="note">
              {t('certificates.custom.unused')}
            </p>
          )}
          <div className={styles.buttons}>
            <Button
              variant="danger"
              isDisabled={remove.isPending}
              onPress={() => {
                remove.mutate();
              }}
            >
              {t('certificates.custom.remove')}
            </Button>
          </div>
        </>
      )}
      <form
        className={styles.group}
        onSubmit={(event) => {
          event.preventDefault();
          upload.mutate();
        }}
      >
        <PemFile
          label={t('certificates.custom.certificate')}
          accept=".pem,.crt,.cer"
          file={certificate}
          onChange={setCertificate}
        />
        <PemFile
          label={t('certificates.custom.key')}
          accept=".pem,.key"
          file={key}
          onChange={setKey}
        />
        <div className={styles.buttons}>
          <Button
            type="submit"
            variant="primary"
            isDisabled={upload.isPending || certificate === null || key === null}
          >
            {t('certificates.custom.upload')}
          </Button>
        </div>
      </form>
      {upload.isError && <ErrorNotice error={describeProblem(upload.error)} />}
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </FormSection>
  );
}
