// Entry of the developer-only design gallery (design.html). The production build uses index.html only.
import '../theme/index.css';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import i18next from 'i18next';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { initI18n, loadAllTranslations } from '../i18n';

const root = document.getElementById('root');
if (!root) {
  throw new Error('Missing #root element');
}

const params = new URLSearchParams(location.search);
const theme = params.get('theme');
if (theme === 'light' || theme === 'dark') {
  document.documentElement.dataset.theme = theme;
}
const accent = params.get('accent') ?? 'petrol';
document.documentElement.dataset.accent = accent;
const lang = params.get('lang');
await initI18n(lang === 'en' || lang === 'de' ? lang : undefined);
await loadAllTranslations();

// After i18n, so the sample data can follow the language (sampleLanguage.ts).
const { Gallery, taskbarFrom } = await import('./Gallery');
const { screens } = await import('./screens');
const taskbar = taskbarFrom(params.get('taskbar'));

const screen = screens.find((entry) => entry.id === params.get('screen'));
if (screen !== undefined) {
  document.title = `${screen.title} - Pithyx design`;
}

// Screens of the shell use queries too (the password lists of the setup screen); they never reach a daemon, and an app
// scene brings its own client with its sample data.
const client = new QueryClient({
  defaultOptions: { queries: { retry: false, staleTime: Infinity } },
});

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={client}>
      {screen === undefined ? (
        <Gallery />
      ) : (
        screen.render({ taskbar, accent, locale: i18next.language })
      )}
    </QueryClientProvider>
  </StrictMode>,
);
