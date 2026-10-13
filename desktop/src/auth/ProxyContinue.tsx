import { useQuery } from '@tanstack/react-query';
import { unwrap } from '@pithyx/api-client';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { AuthLayout } from '../shell/AuthLayout';
import styles from './Auth.module.css';
import { Button } from './controls';

const PARAMETER = 'pithyx-continue';

/**
 * The address a proxy host behind the Pithyx sign-in sent the browser here for (D347), read once at start; null when
 * the page was opened normally.
 */
export const proxyContinue: string | null = new URLSearchParams(window.location.search).get(
  PARAMETER,
);

/** The host name of the address, for the sign-in screen. */
export function continueHost(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/** Removes the parameter from the address bar, so a reload opens the desktop. */
export function dropContinue() {
  const url = new URL(window.location.href);
  url.searchParams.delete(PARAMETER);
  window.history.replaceState(window.history.state, '', url);
}

/**
 * After signing in: asks the box for a one-time ticket into the proxy host and goes there. A refusal (the host does
 * not let this user in) stays here with the reason and a way to the desktop.
 */
export function ProxyContinue({ url, onDesktop }: { url: string; onDesktop: () => void }) {
  const { t } = useTranslation('auth');
  const ticket = useQuery({
    queryKey: ['proxy', 'ticket', url],
    queryFn: async () => unwrap(await api.POST('/api/proxy/tickets', { body: { url } })),
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
  });
  const next = ticket.data?.url;
  // Leaving the page is outside React; the ticket works once and only for 30 seconds.
  useEffect(() => {
    if (next !== undefined) {
      window.location.replace(next);
    }
  }, [next]);
  return (
    <AuthLayout>
      <div className={styles.form}>
        <h1 className={styles.title}>{t('login.continuing', { host: continueHost(url) })}</h1>
        {ticket.isError && (
          <>
            <ErrorNotice error={describeError(ticket.error)} />
            <Button variant="primary" className={styles.submit} onClick={onDesktop}>
              {t('login.openDesktop')}
            </Button>
          </>
        )}
      </div>
    </AuthLayout>
  );
}
