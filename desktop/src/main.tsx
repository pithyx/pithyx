import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { I18nProvider } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { queryKeys } from './api/queries';
import { App } from './App';
import { isSessionExpired } from './errors/describe';
import { initI18n } from './i18n';
import './theme/index.css';

const root = document.getElementById('root');
if (!root) {
  throw new Error('Missing #root element');
}

const queryClient: QueryClient = new QueryClient({
  queryCache: new QueryCache({
    // Any request that finds the session gone sends the user back to the login screen.
    onError: (error, query) => {
      if (isSessionExpired(error) && query.queryKey !== queryKeys.session) {
        void queryClient.invalidateQueries({ queryKey: queryKeys.session });
      }
    },
  }),
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => !isSessionExpired(error) && failureCount < 3,
    },
  },
});

await initI18n();

/** React Aria formats and labels in the language the user picked, not the browser language. */
function Root() {
  const { i18n } = useTranslation();
  return (
    <I18nProvider locale={i18n.language}>
      <App />
    </I18nProvider>
  );
}

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Root />
    </QueryClientProvider>
  </StrictMode>,
);
