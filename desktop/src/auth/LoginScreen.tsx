import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@vyxis/api-client';
import { useState, type SubmitEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { describeError } from '../errors/describe';
import { field } from '../forms';
import { ErrorNotice } from '../errors/ErrorNotice';

/** Password first; for users with TOTP the code follows in the same card. */
export function LoginScreen() {
  const { t } = useTranslation('auth');
  const queryClient = useQueryClient();
  const [challenge, setChallenge] = useState<string | null>(null);
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);

  const login = useMutation({
    mutationFn: async (body: { username: string; password: string }) =>
      unwrap(await api.POST('/api/auth/login', { body })),
    onSuccess: (response) => {
      if (response.totpRequired) {
        setChallenge(response.challenge);
      } else {
        queryClient.setQueryData(queryKeys.session, response.user);
      }
    },
  });
  const verify = useMutation({
    mutationFn: async (body: {
      challenge: string;
      code: string | null;
      recoveryCode: string | null;
    }) => unwrap(await api.POST('/api/auth/login/totp', { body })),
    onSuccess: (user) => {
      queryClient.setQueryData(queryKeys.session, user);
    },
  });

  function submitPassword(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    login.mutate({
      username: field(form, 'username'),
      password: field(form, 'password'),
    });
  }

  function submitCode(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    if (challenge === null) {
      return;
    }
    const value = field(new FormData(event.currentTarget), 'code').trim();
    verify.mutate({
      challenge,
      code: useRecoveryCode ? null : value,
      recoveryCode: useRecoveryCode ? value : null,
    });
  }

  if (challenge !== null) {
    return (
      <main className="screen">
        <form className="card" onSubmit={submitCode} aria-labelledby="totp-title">
          <h1 id="totp-title" className="card__title">
            {t('totp.title')}
          </h1>
          <p className="field">
            <label htmlFor="totp-code">
              {useRecoveryCode ? t('totp.recoveryCode') : t('totp.code')}
            </label>
            {useRecoveryCode ? (
              <input
                id="totp-code"
                name="code"
                key="recovery"
                autoComplete="off"
                autoCapitalize="characters"
                spellCheck={false}
                required
              />
            ) : (
              <input
                id="totp-code"
                name="code"
                key="code"
                autoComplete="one-time-code"
                inputMode="numeric"
                pattern="[0-9 ]{6,7}"
                required
              />
            )}
          </p>
          {verify.isError && <ErrorNotice error={describeError(verify.error)} />}
          <button type="submit" className="button button--primary" disabled={verify.isPending}>
            {t('totp.submit')}
          </button>
          <p className="card__row">
            <button
              type="button"
              className="link"
              onClick={() => {
                setUseRecoveryCode(!useRecoveryCode);
                verify.reset();
              }}
            >
              {useRecoveryCode ? t('totp.useCode') : t('totp.useRecoveryCode')}
            </button>
            <button
              type="button"
              className="link"
              onClick={() => {
                setChallenge(null);
                login.reset();
                verify.reset();
              }}
            >
              {t('totp.back')}
            </button>
          </p>
        </form>
      </main>
    );
  }

  return (
    <main className="screen">
      <form className="card" onSubmit={submitPassword} aria-labelledby="login-title">
        <h1 id="login-title" className="card__title">
          {t('login.title')}
        </h1>
        <p className="field">
          <label htmlFor="login-username">{t('login.username')}</label>
          <input
            id="login-username"
            name="username"
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            required
          />
        </p>
        <p className="field">
          <label htmlFor="login-password">{t('login.password')}</label>
          <input
            id="login-password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
          />
        </p>
        {login.isError && <ErrorNotice error={describeError(login.error)} />}
        <button type="submit" className="button button--primary" disabled={login.isPending}>
          {t('login.submit')}
        </button>
      </form>
    </main>
  );
}
