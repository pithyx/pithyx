import type { PithyxApp } from '@pithyx/sdk';
import { Button, Dialog, Radio, RadioGroup, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { IconCopy, IconPlugConnected } from '../../ui/icons';
import {
  backupsCall,
  targetsKey,
  type BackupHostKey,
  type BackupTarget,
  type BackupTargetList,
  type BackupTargetRequest,
} from '../sdk/backups';
import { describeProblem } from '../storage/shared';
import styles from './Backup.module.css';

/** What the administrator types for a target; the port as typed, empty for 22. */
interface Fields {
  name: string;
  host: string;
  port: string;
  user: string;
  path: string;
}

const EMPTY: Fields = { name: '', host: '', port: '', user: '', path: '' };

const PORT = /^\d{1,5}$/;

function portOf(fields: Fields): number | undefined {
  return fields.port.trim() === '' ? undefined : Number(fields.port.trim());
}

/** Where the server is; the port left out for 22. */
function serverOf(fields: Fields): { host: string; port?: number } {
  const host = fields.host.trim();
  const port = portOf(fields);
  return port === undefined ? { host } : { host, port };
}

/** What a target is for: a place backups go to, or a server whose folders the box pulls (#396). */
export type Purpose = BackupTarget['purpose'];

/** The target as the daemon takes it; the port left out for 22. */
function requestOf(fields: Fields, purpose: Purpose, hostKey?: string): BackupTargetRequest {
  return {
    ...serverOf(fields),
    name: fields.name.trim(),
    user: fields.user.trim(),
    path: fields.path.trim(),
    purpose,
    ...(hostKey === undefined ? {} : { hostKey }),
  };
}

function complete(fields: Fields): boolean {
  return (
    fields.name.trim() !== '' &&
    fields.host.trim() !== '' &&
    fields.user.trim() !== '' &&
    (fields.port.trim() === '' || PORT.test(fields.port.trim()))
  );
}

/** Puts a target the daemon answered with into the list, in place of its old version. */
export function useReplaceTarget() {
  const queryClient = useQueryClient();
  return (target: BackupTarget) => {
    queryClient.setQueryData<BackupTargetList>(targetsKey, (current) => {
      if (current === undefined) {
        return current;
      }
      const others = current.targets.filter((entry) => entry.id !== target.id);
      return {
        targets: [...others, target].sort((a, b) => a.name.localeCompare(b.name)),
      };
    });
  };
}

function TargetFields({
  purpose,
  fields,
  onChange,
}: {
  purpose: Purpose;
  fields: Fields;
  onChange: (fields: Fields) => void;
}) {
  const { t } = useTranslation('backup');
  const set = (key: keyof Fields) => (value: string) => {
    onChange({ ...fields, [key]: value });
  };
  return (
    <>
      <TextField
        label={t('form.name')}
        description={t('form.nameHint')}
        value={fields.name}
        onChange={set('name')}
        maxLength={64}
        isRequired
      />
      <TextField
        label={t('form.host')}
        description={t('form.hostHint')}
        value={fields.host}
        onChange={set('host')}
        autoCapitalize="none"
        autoComplete="off"
        isRequired
      />
      <TextField
        label={t('form.port')}
        description={t('form.portHint')}
        value={fields.port}
        onChange={set('port')}
        inputMode="numeric"
        placeholder="22"
        isInvalid={fields.port.trim() !== '' && !PORT.test(fields.port.trim())}
      />
      <TextField
        label={t('form.user')}
        {...(purpose === 'server' ? { description: t('serverForm.userHint') } : {})}
        value={fields.user}
        onChange={set('user')}
        autoCapitalize="none"
        autoComplete="off"
        isRequired
      />
      <TextField
        label={t(purpose === 'server' ? 'serverForm.path' : 'form.path')}
        description={t(purpose === 'server' ? 'serverForm.pathHint' : 'form.pathHint')}
        value={fields.path}
        onChange={set('path')}
        {...(purpose === 'server' ? { placeholder: '/' } : {})}
        autoCapitalize="none"
        autoComplete="off"
      />
    </>
  );
}

/** The server's host keys to choose the one to pin, each with its fingerprint. */
function HostKeyChoice({
  label,
  keys,
  selected,
  onSelect,
}: {
  label: string;
  keys: readonly BackupHostKey[];
  selected: string;
  onSelect: (key: string) => void;
}) {
  const { t } = useTranslation('backup');
  return (
    <RadioGroup label={label} orientation="vertical" value={selected} onChange={onSelect}>
      {keys.map((key) => (
        <Radio key={key.key} value={key.key}>
          <span className={`${styles.mono} ${styles.breakable}`}>
            {t('targets.hostKeyValue', { type: key.type, fingerprint: key.fingerprint })}
          </span>
        </Radio>
      ))}
    </RadioGroup>
  );
}

/**
 * A target's public key with a button that copies it; for a server, the whole authorized_keys line that lets the key
 * only read with rrsync.
 */
export function PublicKey({ app, target }: { app: PithyxApp; target: BackupTarget }) {
  const { t } = useTranslation('backup');
  const [copied, setCopied] = useState<'copied' | 'copyFailed' | null>(null);
  const line = target.purpose === 'server' ? target.authorizedKeys : null;
  const publicKey = line ?? target.publicKey;
  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel}>
        {t(line === null ? 'targets.publicKey' : 'servers.authorizedKeys')}
      </span>
      <code className={styles.key}>{publicKey}</code>
      <p className={styles.muted}>
        {line === null
          ? t('targets.publicKeyHint')
          : t('servers.authorizedKeysHint', { path: target.path })}
      </p>
      <div className={styles.row}>
        <Button
          size="sm"
          onPress={() => {
            app.clipboard.writeText(publicKey).then(
              () => {
                setCopied('copied');
              },
              () => {
                setCopied('copyFailed');
              },
            );
          }}
        >
          <IconCopy size={16} stroke={1.75} aria-hidden />
          {t(line === null ? 'targets.copy' : 'servers.copy')}
        </Button>
        {copied !== null && (
          <span className={styles.muted} role="status">
            {t(`targets.${copied}`)}
          </span>
        )}
      </div>
    </div>
  );
}

/** Tests a target right after it was saved or changed; the outcome also lands on its card. */
function TestNow({ app, target }: { app: PithyxApp; target: BackupTarget }) {
  const { t } = useTranslation('backup');
  const replace = useReplaceTarget();
  const test = useMutation({
    mutationFn: () => backupsCall(app, 'internal.admin.backups.test', { id: target.id }),
    onSuccess: replace,
  });
  const last = test.data?.lastTest;
  return (
    <div className={styles.field}>
      <div className={styles.row}>
        <Button
          isDisabled={test.isPending}
          onPress={() => {
            test.mutate();
          }}
        >
          <IconPlugConnected size={16} stroke={1.75} aria-hidden />
          {t('targets.test')}
        </Button>
      </div>
      {last?.succeeded === true && (
        <p className={styles.success} role="status">
          {t(target.purpose === 'server' ? 'servers.testPassed' : 'targets.testPassed')}
        </p>
      )}
      {last != null && !last.succeeded && last.errorCode !== null && (
        <ErrorNotice
          error={describeProblem({
            code: last.errorCode,
            params: (last.errorParams ?? {}) as Record<string, unknown>,
            requestId: '',
          })}
        />
      )}
      {test.isError && <ErrorNotice error={describeProblem(test.error)} />}
    </div>
  );
}

/** Reads the server's host keys; the first, Ed25519 where offered, is chosen. */
function useScan(app: PithyxApp) {
  const [chosen, setChosen] = useState('');
  const scan = useMutation({
    mutationFn: (server: { host: string; port?: number }) =>
      backupsCall(app, 'internal.admin.backups.scan', server),
    onSuccess: (result) => {
      setChosen(result.keys[0]?.key ?? '');
    },
  });
  return { scan, chosen, setChosen };
}

function closeOn(onClose: () => void) {
  return (open: boolean) => {
    if (!open) {
      onClose();
    }
  };
}

/**
 * A new target or server in three steps: where it is, the host key whose fingerprint the administrator compares and
 * confirms, then the key (for a server its authorized_keys line) for the server and a first test.
 */
export function AddTargetDialog({
  app,
  purpose,
  onClose,
}: {
  app: PithyxApp;
  purpose: Purpose;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const [fields, setFields] = useState<Fields>(EMPTY);
  const [step, setStep] = useState<'details' | 'hostKey'>('details');
  const { scan, chosen, setChosen } = useScan(app);
  const replace = useReplaceTarget();
  const create = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.create', requestOf(fields, purpose, chosen)),
    onSuccess: replace,
  });
  const saved = create.data;
  const keys = scan.data?.keys ?? [];

  const actions =
    saved !== undefined ? (
      <Button variant="primary" onPress={onClose}>
        {t('form.close')}
      </Button>
    ) : step === 'details' ? (
      <>
        <Button onPress={onClose}>{t('form.cancel')}</Button>
        <Button
          variant="primary"
          isDisabled={!complete(fields) || scan.isPending}
          onPress={() => {
            scan.mutate(serverOf(fields), {
              onSuccess: () => {
                setStep('hostKey');
              },
            });
          }}
        >
          {t('form.next')}
        </Button>
      </>
    ) : (
      <>
        <Button
          onPress={() => {
            create.reset();
            setStep('details');
          }}
        >
          {t('form.back')}
        </Button>
        <Button
          variant="primary"
          isDisabled={chosen === '' || create.isPending}
          onPress={() => {
            create.mutate();
          }}
        >
          {t('add.confirm')}
        </Button>
      </>
    );

  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t(purpose === 'server' ? 'addServer.title' : 'add.title')}
      size="medium"
      actions={actions}
    >
      <div className={styles.form}>
        {saved !== undefined ? (
          <>
            <p className={styles.success} role="status">
              {t(purpose === 'server' ? 'addServer.saved' : 'add.saved', { name: saved.name })}
            </p>
            <PublicKey app={app} target={saved} />
            <TestNow app={app} target={saved} />
          </>
        ) : step === 'details' ? (
          <>
            <TargetFields purpose={purpose} fields={fields} onChange={setFields} />
            {scan.isError && <ErrorNotice error={describeProblem(scan.error)} />}
          </>
        ) : (
          <>
            <p className={styles.muted}>{t('add.fingerprints')}</p>
            <HostKeyChoice
              label={t('add.hostKey')}
              keys={keys}
              selected={chosen}
              onSelect={setChosen}
            />
            {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
          </>
        )}
      </div>
    </Dialog>
  );
}

/** Changes a target; a new server or port first asks for the new server's host key. */
export function EditTargetDialog({
  app,
  target,
  onClose,
}: {
  app: PithyxApp;
  target: BackupTarget;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const [fields, setFields] = useState<Fields>({
    name: target.name,
    host: target.host,
    port: target.port === 22 ? '' : String(target.port),
    user: target.user,
    path: target.path,
  });
  const [step, setStep] = useState<'details' | 'hostKey'>('details');
  const { scan, chosen, setChosen } = useScan(app);
  const replace = useReplaceTarget();
  const moved =
    fields.host.trim().toLowerCase() !== target.host || (portOf(fields) ?? 22) !== target.port;
  const update = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.update', {
        ...requestOf(fields, target.purpose, moved ? chosen : undefined),
        id: target.id,
      }),
    onSuccess: (saved) => {
      replace(saved);
      onClose();
    },
  });

  const actions =
    step === 'details' ? (
      <>
        <Button onPress={onClose}>{t('form.cancel')}</Button>
        <Button
          variant="primary"
          isDisabled={!complete(fields) || scan.isPending || update.isPending}
          onPress={() => {
            if (!moved) {
              update.mutate();
              return;
            }
            scan.mutate(serverOf(fields), {
              onSuccess: () => {
                setStep('hostKey');
              },
            });
          }}
        >
          {moved ? t('form.next') : t('form.save')}
        </Button>
      </>
    ) : (
      <>
        <Button
          onPress={() => {
            update.reset();
            setStep('details');
          }}
        >
          {t('form.back')}
        </Button>
        <Button
          variant="primary"
          isDisabled={chosen === '' || update.isPending}
          onPress={() => {
            update.mutate();
          }}
        >
          {t('add.confirm')}
        </Button>
      </>
    );

  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t('edit.title', { name: target.name })}
      size="medium"
      actions={actions}
    >
      <div className={styles.form}>
        {step === 'details' ? (
          <>
            <TargetFields purpose={target.purpose} fields={fields} onChange={setFields} />
            {moved && <p className={styles.muted}>{t('edit.moved')}</p>}
            {scan.isError && <ErrorNotice error={describeProblem(scan.error)} />}
          </>
        ) : (
          <>
            <p className={styles.muted}>{t('add.fingerprints')}</p>
            <HostKeyChoice
              label={t('add.hostKey')}
              keys={scan.data?.keys ?? []}
              selected={chosen}
              onSelect={setChosen}
            />
          </>
        )}
        {update.isError && <ErrorNotice error={describeProblem(update.error)} />}
      </div>
    </Dialog>
  );
}

/** After the server answered with another host key: the confirmed one next to the offered ones, to accept one. */
export function HostKeyDialog({
  app,
  target,
  onClose,
}: {
  app: PithyxApp;
  target: BackupTarget;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const [chosen, setChosen] = useState<string | null>(null);
  const offered = useQuery({
    queryKey: ['backups', 'scan', target.id],
    queryFn: () =>
      backupsCall(app, 'internal.admin.backups.scan', { host: target.host, port: target.port }),
    gcTime: 0,
    retry: false,
  });
  const keys = offered.data?.keys ?? [];
  const selected = chosen ?? keys[0]?.key ?? '';
  const replace = useReplaceTarget();
  const accept = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.setHostKey', { id: target.id, hostKey: selected }),
    onSuccess: (saved) => {
      replace(saved);
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t('hostKey.title', { name: target.name })}
      size="medium"
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={selected === '' || accept.isPending}
            onPress={() => {
              accept.mutate();
            }}
          >
            {t('hostKey.accept')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <p className={styles.warning}>{t('hostKey.intro')}</p>
        <div className={styles.field}>
          <span className={styles.fieldLabel}>{t('hostKey.pinned')}</span>
          <span className={`${styles.mono} ${styles.breakable}`}>
            {t('targets.hostKeyValue', {
              type: target.hostKey.type,
              fingerprint: target.hostKey.fingerprint,
            })}
          </span>
        </div>
        {keys.length > 0 && (
          <HostKeyChoice
            label={t('hostKey.offered')}
            keys={keys}
            selected={selected}
            onSelect={setChosen}
          />
        )}
        {offered.isError && <ErrorNotice error={describeProblem(offered.error)} />}
        {accept.isError && <ErrorNotice error={describeProblem(accept.error)} />}
      </div>
    </Dialog>
  );
}

export function DeleteTargetDialog({
  app,
  target,
  onClose,
}: {
  app: PithyxApp;
  target: BackupTarget;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () => backupsCall(app, 'internal.admin.backups.delete', { id: target.id }),
    onSuccess: () => {
      queryClient.setQueryData<BackupTargetList>(targetsKey, (current) =>
        current === undefined
          ? current
          : { targets: current.targets.filter((entry) => entry.id !== target.id) },
      );
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t('delete.title', { name: target.name })}
      tone="danger"
      role="alertdialog"
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={remove.isPending}
            onPress={() => {
              remove.mutate();
            }}
          >
            {t('delete.confirm')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <p>{t(target.purpose === 'server' ? 'deleteServer.text' : 'delete.text')}</p>
        {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
      </div>
    </Dialog>
  );
}
