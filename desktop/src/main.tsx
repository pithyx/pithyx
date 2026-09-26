import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { initI18n } from './i18n';
import './desktop.css';

const root = document.getElementById('root');
if (!root) {
  throw new Error('Missing #root element');
}

const queryClient = new QueryClient();

await initI18n();

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
);
