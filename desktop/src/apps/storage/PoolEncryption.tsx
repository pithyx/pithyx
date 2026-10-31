import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { formatBytes } from '../../desktop/format';
import { useFormatLocale } from '../../i18n';
import { copyText } from '../../platform/browser';
import { IconCheck, IconCopy, IconDownload, IconKey, IconPrinter } from '../../ui/icons';
import { Button, cx, Dialog, PasswordField, TextField } from '@pithyx/ui';
import { internalCall, type NewPoolKey, type Pool, type UsbStick } from '../sdk/internal';
import styles from './Storage.module.css';
import { describeProblem, formatDate, storageKey } from './shared';
import { keyGroups, keySheet, normalizeGroup, pickTwo, stickName } from './poolKey';

/**
 * A pool key (D384): copy it, download it as a text file or print it. The sheet also carries the key in hex with the
 * commands that open the pool on any Linux with OpenZFS, without Pithyx. A new key is confirmed as stored by ticking the
 * box and typing two of its groups from the stored copy; the box keeps no copy unless the key vault is chosen.
 */
export function PoolKeyCard({
  poolKey,
  pool,
  onStoredChange,
}: {
  poolKey: NewPoolKey;
  pool: string;
  /** Set for a new key: called with true once the user ticked the box and typed both groups right. */
  onStoredChange?: (stored: boolean) => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const [copied, setCopied] = useState<'no' | 'yes' | 'manually'>('no');
  const [ticked, setTicked] = useState(false);
  const [typed, setTyped] = useState<readonly [string, string]>(['', '']);
  const groups = keyGroups(poolKey.key);
  // Two different groups, chosen once per key, that the user types from the stored copy.
  const [asked] = useState(() => pickTwo(groups.length));
  const value = useRef<HTMLElement>(null);
  const sheet = keySheet(t, pool, poolKey, formatDate(new Date().toISOString(), locale));
  const report = (nextTicked: boolean, nextTyped: readonly [string, string]) => {
    onStoredChange?.(
      nextTicked &&
        asked.every((group, index) => normalizeGroup(nextTyped[index] ?? '') === groups[group]),
    );
  };
  return (
    <div className={styles.form}>
      <p>{t(onStoredChange === undefined ? 'key.introShown' : 'key.intro')}</p>
      <div className={styles.poolKey}>
        <IconKey size={20} stroke={1.75} aria-hidden />
        <code ref={value} className={styles.poolKeyValue} aria-label={t('key.label')}>
          {poolKey.key}
        </code>
      </div>
      <div className={styles.buttons}>
        <Button
          onPress={() => {
            void copyText(poolKey.key, value.current).then((done) => {
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
      {onStoredChange !== undefined && (
        <>
          <label className={styles.check}>
            <input
              type="checkbox"
              checked={ticked}
              onChange={(event) => {
                setTicked(event.target.checked);
                report(event.target.checked, typed);
              }}
            />
            <span>{t('key.confirm')}</span>
          </label>
          <p className={styles.muted}>{t('key.verify')}</p>
          <div className={styles.keyGroups}>
            {asked.map((group, index) => (
              <TextField
                key={group}
                // Numbered as the key prints its groups, PXPK being the first.
                label={t('key.group', { number: group + 2 })}
                value={typed[index] ?? ''}
                onChange={(next) => {
                  const nextTyped: [string, string] =
                    index === 0 ? [next, typed[1]] : [typed[0], next];
                  setTyped(nextTyped);
                  report(ticked, nextTyped);
                }}
                isInvalid={
                  (typed[index] ?? '').trim().length >= 4 &&
                  normalizeGroup(typed[index] ?? '') !== groups[group]
                }
                errorMessage={t('key.groupWrong')}
                autoCapitalize="characters"
                autoComplete="off"
                spellCheck="false"
                maxLength={6}
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
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
        <p>
          {t(
            pool.encryption?.vaultFailed
              ? 'unlock.introVaultFailed'
              : pool.encryption?.usbFailed
                ? 'unlock.introUsbFailed'
                : 'unlock.intro',
          )}
        </p>
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
        {unlock.isError && mountProblem(unlock.error) && (
          <p className={styles.notice}>{t('unlock.mountFailed')}</p>
        )}
      </form>
    </Dialog>
  );
}

/** A command that failed or hung after the key was loaded: the pool is open, and unlocking again mounts the rest. */
function mountProblem(error: unknown): boolean {
  const code = describeProblem(error).code;
  return code === 'storage.command.failed' || code === 'storage.command.timed_out';
}

/**
 * Shows the pool's key from the vault again, for an administrator who lost the stored copy: the password and, with
 * two-factor sign-in on, a current authenticator code first (D384).
 */
export function RevealKeyDialog({
  app,
  pool,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const twoFactor = app.context.user.twoFactor;
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const reveal = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.revealKey', {
        pool: pool.name,
        password,
        ...(twoFactor ? { totpCode: code.trim() } : {}),
      }),
  });
  const ready = password !== '' && (!twoFactor || code.trim() !== '');
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('reveal.title', { pool: pool.name })}
      actions={
        reveal.data === undefined ? (
          <>
            <Button onPress={onClose}>{t('reveal.cancel')}</Button>
            <Button
              variant="primary"
              isDisabled={!ready || reveal.isPending}
              onPress={() => {
                reveal.mutate();
              }}
            >
              {t('reveal.submit')}
            </Button>
          </>
        ) : (
          <Button variant="primary" onPress={onClose}>
            {t('reveal.close')}
          </Button>
        )
      }
    >
      {reveal.data === undefined ? (
        <form
          className={styles.form}
          onSubmit={(event) => {
            event.preventDefault();
            if (ready) {
              reveal.mutate();
            }
          }}
        >
          <p>{t('reveal.intro')}</p>
          {pool.encryption?.vaultFailed === true && (
            <p className={styles.notice}>{t('reveal.vaultFailed')}</p>
          )}
          <PasswordField
            label={t('reveal.password')}
            value={password}
            onChange={setPassword}
            autoComplete="current-password"
            autoFocus
          />
          {twoFactor && (
            <TextField
              label={t('reveal.code')}
              value={code}
              onChange={setCode}
              inputMode="numeric"
              autoComplete="one-time-code"
            />
          )}
          {reveal.isError && <ErrorNotice error={describeProblem(reveal.error)} />}
        </form>
      ) : (
        <PoolKeyCard poolKey={reveal.data} pool={pool.name} />
      )}
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
  const newKey = created.data;
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
                change.mutate(newKey.key);
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
        {pool.encryption?.usbKey != null && (
          <p className={styles.notice}>
            {t('changeKey.usbOld', { stick: pool.encryption.usbKey.stick })}
          </p>
        )}
        {newKey !== undefined && (
          <>
            <PoolKeyCard poolKey={newKey} pool={pool.name} onStoredChange={setStored} />
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

/**
 * Writes the pool's key to a USB stick (D385): the typed key, or the vault's after the password and, with two-factor
 * sign-in on, a current code. The box then unlocks the pool whenever the stick is plugged in; afterwards the stick may be
 * pulled and the pool stays unlocked until the next restart, which the dialog says plainly.
 */
export function WriteUsbKeyDialog({
  app,
  pool,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const twoFactor = app.context.user.twoFactor;
  const inVault = pool.encryption?.keyInVault ?? false;
  const [device, setDevice] = useState<string | null>(null);
  const [source, setSource] = useState<'vault' | 'typed'>(inVault ? 'vault' : 'typed');
  const [key, setKey] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [removeFromVault, setRemoveFromVault] = useState(true);
  const sticks = useQuery({
    queryKey: [...storageKey, 'usbSticks'],
    queryFn: () => internalCall(app, 'internal.admin.storage.usbSticks', {}),
  });
  const writable = (sticks.data ?? []).filter((stick) => !stick.readOnly);
  // A single stick that can take the key is chosen without asking.
  const chosen =
    (sticks.data ?? []).find((stick) => stick.device === device) ??
    (device === null && writable.length === 1 ? writable[0] : undefined);
  const write = useMutation({
    mutationFn: (stick: UsbStick) =>
      internalCall(app, 'internal.admin.storage.writeKeyToUsb', {
        pool: pool.name,
        device: stick.device,
        ...(source === 'typed'
          ? { key: key.trim() }
          : { password, ...(twoFactor ? { totpCode: code.trim() } : {}) }),
        removeFromVault: inVault && removeFromVault,
      }),
  });
  const ready =
    chosen !== undefined &&
    !chosen.readOnly &&
    (source === 'typed'
      ? key.trim() !== ''
      : password !== '' && (!twoFactor || code.trim() !== ''));
  const submit = () => {
    if (ready && !write.isPending) {
      write.mutate(chosen);
    }
  };
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('usb.title', { pool: pool.name })}
      actions={
        write.isSuccess ? (
          <Button variant="primary" onPress={onClose}>
            {t('usb.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('usb.cancel')}</Button>
            <Button variant="primary" isDisabled={!ready || write.isPending} onPress={submit}>
              {t('usb.submit')}
            </Button>
          </>
        )
      }
    >
      {write.isSuccess ? (
        <p className={styles.notice} role="status">
          {t('usb.done', { stick: stickName(write.variables), pool: pool.name })}
        </p>
      ) : (
        <form
          className={styles.form}
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <p>{t('usb.intro')}</p>
          <p className={styles.notice}>{t('usb.warning')}</p>
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('usb.stick')}</legend>
            {sticks.data?.length === 0 && <p className={styles.muted}>{t('usb.none')}</p>}
            {(sticks.data ?? []).map((stick) => (
              <label key={stick.device} className={styles.check}>
                <input
                  type="radio"
                  name="usbStick"
                  checked={chosen?.device === stick.device}
                  disabled={stick.readOnly}
                  onChange={() => {
                    setDevice(stick.device);
                    write.reset();
                  }}
                />
                <span>
                  {t('usb.stickOption', {
                    name: stickName(stick),
                    size: formatBytes(stick.sizeBytes, locale),
                    fileSystem: stick.fileSystem,
                  })}
                  {stick.readOnly && <span className={styles.muted}> · {t('usb.readOnly')}</span>}
                </span>
              </label>
            ))}
          </fieldset>
          {inVault && (
            <fieldset className={styles.diskChoice}>
              <legend className={styles.fieldLabel}>{t('usb.source')}</legend>
              <label className={styles.check}>
                <input
                  type="radio"
                  name="usbKeySource"
                  checked={source === 'vault'}
                  onChange={() => {
                    setSource('vault');
                    write.reset();
                  }}
                />
                <span>{t('usb.sourceVault')}</span>
              </label>
              <label className={styles.check}>
                <input
                  type="radio"
                  name="usbKeySource"
                  checked={source === 'typed'}
                  onChange={() => {
                    setSource('typed');
                    write.reset();
                  }}
                />
                <span>{t('usb.sourceTyped')}</span>
              </label>
            </fieldset>
          )}
          {source === 'vault' ? (
            <>
              <p className={styles.muted}>{t('usb.vaultHint')}</p>
              <PasswordField
                label={t('usb.password')}
                value={password}
                onChange={(value) => {
                  setPassword(value);
                  write.reset();
                }}
                autoComplete="current-password"
              />
              {twoFactor && (
                <TextField
                  label={t('usb.code')}
                  value={code}
                  onChange={setCode}
                  inputMode="numeric"
                  autoComplete="one-time-code"
                />
              )}
            </>
          ) : (
            <TextField
              label={t('usb.key')}
              description={t('usb.keyHint')}
              value={key}
              onChange={(value) => {
                setKey(value);
                write.reset();
              }}
              autoCapitalize="characters"
              autoComplete="off"
              spellCheck="false"
            />
          )}
          {inVault && (
            <label className={styles.check}>
              <input
                type="checkbox"
                checked={removeFromVault}
                onChange={(event) => {
                  setRemoveFromVault(event.target.checked);
                }}
              />
              <span>{t('usb.removeFromVault')}</span>
            </label>
          )}
          {write.isError && <ErrorNotice error={describeProblem(write.error)} />}
        </form>
      )}
    </Dialog>
  );
}
