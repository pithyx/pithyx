import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { copyText } from '../../platform/browser';
import { IconCheck, IconCopy, IconDownload, IconKey, IconPrinter } from '../../ui/icons';
import { Button, cx, Dialog, TextField } from '@pithyx/ui';
import { internalCall, type Pool } from '../sdk/internal';
import styles from './Storage.module.css';
import { describeProblem, formatDate, type Translate } from './shared';

/**
 * A new pool key (D384), shown once: copy it, download it as a text file or print it, then confirm it was stored.
 * The box keeps no copy unless the key vault is chosen, so a lost key means lost data.
 */
export function PoolKeyCard({
  poolKey,
  pool,
  stored,
  onStoredChange,
}: {
  poolKey: string;
  pool: string;
  stored: boolean;
  onStoredChange: (stored: boolean) => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const [copied, setCopied] = useState<'no' | 'yes' | 'manually'>('no');
  const value = useRef<HTMLElement>(null);
  const sheet = keySheet(t, pool, poolKey, formatDate(new Date().toISOString(), locale));
  return (
    <div className={styles.form}>
      <p>{t('key.intro')}</p>
      <div className={styles.poolKey}>
        <IconKey size={20} stroke={1.75} aria-hidden />
        <code ref={value} className={styles.poolKeyValue} aria-label={t('key.label')}>
          {poolKey}
        </code>
      </div>
      <div className={styles.buttons}>
        <Button
          onPress={() => {
            void copyText(poolKey, value.current).then((done) => {
              setCopied(done ? 'yes' : 'manually');
            });
          }}
        >
          {copied === 'yes' ? (
            <IconCheck size={16} aria-hidden />
          ) : (
            <IconCopy size={16} aria-hidden />
          )}
          {copied === 'yes' ? t('key.copied') : t('key.copy')}
        </Button>
        <Button
          onPress={() => {
            download(sheet.join('\n') + '\n', `pithyx-pool-key-${pool || 'new'}.txt`);
          }}
        >
          <IconDownload size={16} aria-hidden />
          {t('key.download')}
        </Button>
        <Button
          onPress={() => {
            print(sheet);
          }}
        >
          <IconPrinter size={16} aria-hidden />
          {t('key.print')}
        </Button>
      </div>
      <p className={styles.muted} role="status">
        {copied === 'manually' ? t('key.copyManually') : ''}
      </p>
      <label className={styles.check}>
        <input
          type="checkbox"
          checked={stored}
          onChange={(event) => {
            onStoredChange(event.target.checked);
          }}
        />
        <span>{t('key.confirm')}</span>
      </label>
    </div>
  );
}

/** The lines of the downloaded or printed key, in the user's language. */
function keySheet(t: Translate, pool: string, poolKey: string, date: string): string[] {
  return [
    t('key.sheet.title'),
    t('key.sheet.pool', { pool: pool || '-' }),
    t('key.sheet.date', { date }),
    '',
    poolKey,
    '',
    t('key.sheet.hint'),
  ];
}

function download(text: string, fileName: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}

/** Prints only the key sheet: while the print dialog is open, everything else of the page is hidden. */
function print(lines: string[]) {
  const sheet = document.createElement('pre');
  sheet.className = styles.printSheet;
  sheet.textContent = lines.join('\n');
  document.body.append(sheet);
  document.body.dataset.printing = 'pool-key';
  const done = () => {
    sheet.remove();
    delete document.body.dataset.printing;
    window.removeEventListener('afterprint', done);
  };
  window.addEventListener('afterprint', done);
  window.print();
}

/** Unlocks a locked pool with its typed key, and keeps the key in the vault when asked. */
export function UnlockDialog({
  app,
  pool,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const [key, setKey] = useState('');
  const [keep, setKeep] = useState(pool.encryption?.keyInVault ?? false);
  const unlock = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.unlock', {
        pool: pool.name,
        key: key.trim(),
        keepInVault: keep,
      }),
    onSuccess: onClose,
  });
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('unlock.title', { pool: pool.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('unlock.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={key.trim() === '' || unlock.isPending}
            onPress={() => {
              unlock.mutate();
            }}
          >
            {t('unlock.submit')}
          </Button>
        </>
      }
    >
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (key.trim() !== '') {
            unlock.mutate();
          }
        }}
      >
        <p>{t(pool.encryption?.keyInVault ? 'unlock.introVaultFailed' : 'unlock.intro')}</p>
        <TextField
          label={t('unlock.key')}
          description={t('unlock.keyHint')}
          value={key}
          onChange={(value) => {
            setKey(value);
            unlock.reset();
          }}
          autoCapitalize="characters"
          autoComplete="off"
          spellCheck="false"
          autoFocus
        />
        <label className={styles.check}>
          <input
            type="checkbox"
            checked={keep}
            onChange={(event) => {
              setKeep(event.target.checked);
            }}
          />
          <span>{t('unlock.keep')}</span>
        </label>
        <p className={cx(styles.muted, styles.indent)}>{t('vault.hint')}</p>
        {unlock.isError && <ErrorNotice error={describeProblem(unlock.error)} />}
      </form>
    </Dialog>
  );
}

/**
 * Replaces the key of an unlocked pool: a new key is shown, stored and confirmed first, then ZFS takes it and the old
 * one stops working. The vault then holds the new key, or none.
 */
export function ChangeKeyDialog({
  app,
  pool,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const [stored, setStored] = useState(false);
  const [keep, setKeep] = useState(pool.encryption?.keyInVault ?? false);
  const created = useMutation({
    mutationFn: () => internalCall(app, 'internal.admin.storage.newKey', {}),
  });
  const change = useMutation({
    mutationFn: (key: string) =>
      internalCall(app, 'internal.admin.storage.changeKey', {
        pool: pool.name,
        key,
        keepInVault: keep,
      }),
    onSuccess: onClose,
  });
  const newKey = created.data?.key;
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('changeKey.title', { pool: pool.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('changeKey.cancel')}</Button>
          {newKey === undefined ? (
            <Button
              variant="primary"
              isDisabled={created.isPending}
              onPress={() => {
                created.mutate();
              }}
            >
              {t('changeKey.create')}
            </Button>
          ) : (
            <Button
              variant="primary"
              isDisabled={!stored || change.isPending}
              onPress={() => {
                change.mutate(newKey);
              }}
            >
              {t('changeKey.submit')}
            </Button>
          )}
        </>
      }
    >
      <div className={styles.form}>
        <p>{t('changeKey.intro')}</p>
        {newKey !== undefined && (
          <>
            <PoolKeyCard
              poolKey={newKey}
              pool={pool.name}
              stored={stored}
              onStoredChange={setStored}
            />
            <label className={styles.check}>
              <input
                type="checkbox"
                checked={keep}
                onChange={(event) => {
                  setKeep(event.target.checked);
                }}
              />
              <span>{t('changeKey.keep')}</span>
            </label>
            <p className={cx(styles.muted, styles.indent)}>{t('vault.hint')}</p>
          </>
        )}
        {created.isError && <ErrorNotice error={describeProblem(created.error)} />}
        {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      </div>
    </Dialog>
  );
}

/** Takes the pool's key out of the vault after a warning: from the next start on, the pool waits for its key. */
export function ForgetKeyDialog({
  app,
  pool,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const forget = useMutation({
    mutationFn: () => internalCall(app, 'internal.admin.storage.forgetKey', { pool: pool.name }),
    onSuccess: onClose,
  });
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('forgetKey.title', { pool: pool.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('forgetKey.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={forget.isPending}
            onPress={() => {
              forget.mutate();
            }}
          >
            {t('forgetKey.submit')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <p>{t('forgetKey.intro')}</p>
        <p className={styles.notice}>{t('forgetKey.warning')}</p>
        {forget.isError && <ErrorNotice error={describeProblem(forget.error)} />}
      </div>
    </Dialog>
  );
}
