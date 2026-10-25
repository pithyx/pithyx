import { useMutation } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import { useEffect, useEffectEvent, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { getPasskey, passkeyAutofillAvailable } from '../platform/browser';
import type { PasskeyRequestOptions } from '../platform/webauthnJson';
import styles from './Auth.module.css';
import { Button } from './controls';

type User = components['schemas']['UserInfo'];

/**
 * Signs in with a passkey of this box's name (D380): with `autofill`, the browser offers its passkeys in the user name
 * field until `signal` aborts; otherwise it shows its own dialog. Null when the user cancelled.
 */
async function signInWithPasskey(autofill: boolean, signal?: AbortSignal): Promise<User | null> {
  const options = unwrap(
    await api.POST('/api/auth/passkeys/sign-in/options', signal === undefined ? {} : { signal }),
  );
  const credential = await getPasskey(options.options as PasskeyRequestOptions, {
    autofill,
    ...(signal === undefined ? {} : { signal }),
  });
  if (credential === null) {
    return null;
  }
  return unwrap(
    await api.POST('/api/auth/passkeys/sign-in', {
      body: { ceremony: options.ceremony, credential },
    }),
  );
}

/**
 * The passkey part of the sign-in form on a device where passkeys work: the browser's autofill offers them in the user
 * name field while the form is shown (`onAutofill` marks the field), and the button asks with the browser's dialog.
 */
export function PasskeySignIn({
  onAutofill,
  onStart,
  onSignedIn,
}: {
  onAutofill: (active: boolean) => void;
  onStart: () => void;
  onSignedIn: (user: User) => void;
}) {
  const { t } = useTranslation('auth');
  const autofillRequest = useRef<AbortController | null>(null);
  const passkey = useMutation({
    mutationFn: () => {
      autofillRequest.current?.abort();
      onStart();
      return signInWithPasskey(false);
    },
    onSuccess: (user) => {
      if (user !== null) {
        onSignedIn(user);
      }
    },
  });
  const autofilled = useEffectEvent((user: User | null, active: boolean) => {
    onAutofill(active);
    if (user !== null) {
      onSignedIn(user);
    }
  });
  // A press on the button takes over from the autofill.
  useEffect(() => {
    const controller = new AbortController();
    autofillRequest.current = controller;
    void passkeyAutofillAvailable().then(async (available) => {
      if (!available || controller.signal.aborted) {
        return;
      }
      autofilled(null, true);
      try {
        autofilled(await signInWithPasskey(true, controller.signal), true);
      } catch (error) {
        // The button still works; the reason goes to the console only.
        if (!(error instanceof DOMException && error.name === 'AbortError')) {
          console.warn('Passkey autofill failed', error);
        }
      }
    });
    return () => {
      controller.abort();
    };
  }, []);
  return (
    <>
      {passkey.isError && <ErrorNotice error={describeError(passkey.error)} />}
      <Button
        className={styles.submit}
        pending={passkey.isPending}
        onClick={() => {
          passkey.mutate();
        }}
      >
        {passkey.isPending ? t('login.passkeyWaiting') : t('login.passkey')}
      </Button>
    </>
  );
}
