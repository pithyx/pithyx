import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@vyxis/api-client';
import { useState, type SubmitEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { describeError } from '../errors/describe';
import { field } from '../forms';
import { ErrorNotice } from '../errors/ErrorNotice';
import { RecoveryKeyCard } from './RecoveryKeyCard';

const MIN_PASSWORD_LENGTH = 12;

export function SetupScreen() {
  const { t } = useTranslation('auth');
  const queryClient = useQueryClient();
  const [mismatch, setMismatch] = useState(false);
  const setup = useMutation({
    mutationFn: async (body: { username: string; password: string; displayName: string | null }) =>
      unwrap(await api.POST('/api/setup', { body })),
  });

  if (setup.data !== undefined) {
    const { user, recoveryKey } = setup.data;
    return (
      <main className="screen">
        <RecoveryKeyCard
          recoveryKey={recoveryKey}
          onContinue={() => {
            // The setup already signed the administrator in.
            queryClient.setQueryData(queryKeys.session, user);
            void queryClient.invalidateQueries({ queryKey: queryKeys.setup });
          }}
        />
      </main>
    );
  }

  function submit(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const password = field(form, 'password');
    const matches = password === field(form, 'confirm');
    setMismatch(!matches);
    if (matches) {
      const displayName = field(form, 'displayName').trim();
      setup.mutate({
        username: field(form, 'username'),
        password,
        displayName: displayName === '' ? null : displayName,
      });
    }
  }

  return (
    <main className="screen">
      <form className="card" onSubmit={submit} aria-labelledby="setup-title">
        <h1 id="setup-title" className="card__title">
          {t('setup.title')}
        </h1>
        <p>{t('setup.intro')}</p>
        <p className="field">
          <label htmlFor="setup-username">{t('setup.username')}</label>
          <input
            id="setup-username"
            name="username"
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            required
            aria-describedby="setup-username-hint"
          />
          <small id="setup-username-hint">{t('setup.usernameHint')}</small>
        </p>
        <p className="field">
          <label htmlFor="setup-display-name">{t('setup.displayName')}</label>
          <input id="setup-display-name" name="displayName" autoComplete="name" />
        </p>
        <p className="field">
          <label htmlFor="setup-password">{t('setup.password')}</label>
          <input
            id="setup-password"
            name="password"
            type="password"
            autoComplete="new-password"
            minLength={MIN_PASSWORD_LENGTH}
            required
            aria-describedby="setup-password-hint"
          />
          <small id="setup-password-hint">
            {t('setup.passwordHint', { minLength: MIN_PASSWORD_LENGTH })}
          </small>
        </p>
        <p className="field">
          <label htmlFor="setup-confirm">{t('setup.confirmPassword')}</label>
          <input
            id="setup-confirm"
            name="confirm"
            type="password"
            autoComplete="new-password"
            required
            aria-invalid={mismatch}
          />
        </p>
        {mismatch && (
          <p className="error-notice" role="alert">
            {t('setup.passwordMismatch')}
          </p>
        )}
        {setup.isError && <ErrorNotice error={describeError(setup.error)} />}
        <button type="submit" className="button button--primary" disabled={setup.isPending}>
          {t('setup.submit')}
        </button>
      </form>
    </main>
  );
}
