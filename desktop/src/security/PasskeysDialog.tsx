import { unwrap, type components } from '@pithyx/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Dialog, TextField } from '@pithyx/ui';
import { api } from '../api/client';
import { fetchSetupStatus, queryKeys } from '../api/queries';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { useFormatLocale } from '../i18n';
import { createPasskey, passkeyBlocker } from '../platform/browser';
import type { PasskeyCreationOptions } from '../platform/webauthnJson';
import styles from './Security.module.css';
import { trustQuery } from './trust';

type Passkey = components['schemas']['PasskeyInfo'];

const passkeysKey = ['auth', 'passkeys'] as const;

/**
 * The user's passkeys (D380), from the user menu: the list with where each one works, removing one, and adding one
 * after the current password. Adding needs a device that trusts the box and the box opened by name.
 */
export function PasskeysDialog({ onClose, onTrust }: { onClose: () => void; onTrust: () => void }) {
  const { t } = useTranslation('security');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const trust = useQuery(trustQuery);
  const setup = useQuery({
    queryKey: queryKeys.setup,
    queryFn: ({ signal }) => fetchSetupStatus(signal),
  });
  const list = useQuery({
    queryKey: passkeysKey,
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/auth/passkeys', { signal })),
  });
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<Passkey | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const remove = useMutation({
    mutationFn: async (passkey: Passkey) => {
      unwrap(await api.DELETE('/api/auth/passkeys/{id}', { params: { path: { id: passkey.id } } }));
    },
    onSuccess: async (_, passkey) => {
      setRemoving(null);
      setStatus(t('passkeys.removed', { name: passkey.name }));
      await queryClient.invalidateQueries({ queryKey: passkeysKey });
    },
  });
  const blocker = trust.data === undefined ? undefined : passkeyBlocker(trust.data);
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' });
  const hostname = setup.data?.hostname.toLowerCase() ?? 'pithyx';
  const named = `https://${hostname}.local:7484`;
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      size="medium"
      title={t('passkeys.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('passkeys.close')}</Button>
          {trust.data !== undefined && blocker === undefined && (
            <Button
              variant="primary"
              onPress={() => {
                setStatus(null);
                setAdding(true);
              }}
            >
              {t('passkeys.add')}
            </Button>
          )}
        </>
      }
    >
      <div className={styles.form}>
        <p className={styles.muted}>{t('passkeys.intro')}</p>
        {blocker !== undefined && (
          <div className={styles.buttons} role="note">
            <p className={styles.muted}>{t(`passkeys.blocked.${blocker}`, { url: named })}</p>
            {blocker === 'untrusted' && trust.data === 'untrusted' && (
              <Button size="sm" onPress={onTrust}>
                {t('trust.hintAction')}
              </Button>
            )}
          </div>
        )}
        {status !== null && (
          <p className={styles.muted} role="status">
            {status}
          </p>
        )}
        {list.isError && <ErrorNotice error={describeError(list.error)} />}
        {list.data !== undefined &&
          (list.data.items.length === 0 ? (
            <p className={styles.muted}>{t('passkeys.empty')}</p>
          ) : (
            <ul className={styles.list}>
              {list.data.items.map((passkey) => (
                <li key={passkey.id} className={styles.card}>
                  <span>
                    <span className={styles.name}>{passkey.name}</span>{' '}
                    {passkey.backedUp && <Badge tone="neutral">{t('passkeys.synced')}</Badge>}
                    <span className={styles.detail}>
                      {t('passkeys.forName', { host: passkey.rpId })}
                    </span>
                    <span className={styles.detail}>
                      {t('passkeys.created', { date: date.format(new Date(passkey.createdAt)) })}
                      {' · '}
                      {passkey.lastUsedAt === null
                        ? t('passkeys.neverUsed')
                        : t('passkeys.lastUsed', {
                            date: date.format(new Date(passkey.lastUsedAt)),
                          })}
                    </span>
                  </span>
                  <Button
                    size="sm"
                    onPress={() => {
                      setStatus(null);
                      setRemoving(passkey);
                    }}
                  >
                    {t('passkeys.remove')}
                  </Button>
                </li>
              ))}
            </ul>
          ))}
      </div>
      {adding && (
        <AddDialog
          onClose={() => {
            setAdding(false);
          }}
          onAdded={async (passkey) => {
            setAdding(false);
            setStatus(t('passkeys.added', { name: passkey.name }));
            await queryClient.invalidateQueries({ queryKey: passkeysKey });
          }}
        />
      )}
      {removing !== null && (
        <Dialog
          isOpen
          role="alertdialog"
          onOpenChange={(open) => {
            if (!open) {
              setRemoving(null);
            }
          }}
          title={t('passkeys.remove')}
          actions={
            <>
              <Button
                onPress={() => {
                  setRemoving(null);
                }}
              >
                {t('passkeys.cancel')}
              </Button>
              <Button
                variant="danger"
                isDisabled={remove.isPending}
                onPress={() => {
                  remove.mutate(removing);
                }}
              >
                {t('passkeys.remove')}
              </Button>
            </>
          }
        >
          <p className={styles.muted}>{t('passkeys.removeConfirm', { name: removing.name })}</p>
          {remove.isError && <ErrorNotice error={describeError(remove.error)} />}
        </Dialog>
      )}
    </Dialog>
  );
}

function AddDialog({
  onClose,
  onAdded,
}: {
  onClose: () => void;
  onAdded: (passkey: Passkey) => Promise<void>;
}) {
  const { t } = useTranslation('security');
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [cancelled, setCancelled] = useState(false);
  const add = useMutation({
    mutationFn: async () => {
      setCancelled(false);
      const options = unwrap(await api.POST('/api/auth/passkeys/options', { body: { password } }));
      const credential = await createPasskey(options.options as PasskeyCreationOptions);
      if (credential === null) {
        setCancelled(true);
        return null;
      }
      return unwrap(
        await api.POST('/api/auth/passkeys', {
          body: { ceremony: options.ceremony, name: name.trim(), credential },
        }),
      );
    },
    onSuccess: async (passkey) => {
      if (passkey !== null) {
        await onAdded(passkey);
      }
    },
  });
  const submit = () => {
    if (!add.isPending && name.trim() !== '' && password !== '') {
      add.mutate();
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
      title={t('passkeys.add')}
      actions={
        <>
          <Button onPress={onClose}>{t('passkeys.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={add.isPending || name.trim() === '' || password === ''}
            onPress={submit}
          >
            {add.isPending ? t('passkeys.adding') : t('passkeys.save')}
          </Button>
        </>
      }
    >
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <TextField
          label={t('passkeys.name')}
          description={t('passkeys.nameHint')}
          value={name}
          onChange={setName}
          autoComplete="off"
          maxLength={64}
          autoFocus
        />
        <TextField
          label={t('passkeys.password')}
          value={password}
          onChange={setPassword}
          type="password"
          autoComplete="current-password"
        />
        {cancelled && (
          <p className={styles.muted} role="status">
            {t('passkeys.cancelled')}
          </p>
        )}
        {add.isError && <ErrorNotice error={describeError(add.error)} />}
        <button type="submit" hidden />
      </form>
    </Dialog>
  );
}
