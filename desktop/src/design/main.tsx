// Entry of the developer-only design gallery (design.html). The production build uses index.html only.
import '../theme/index.css';
import i18next from 'i18next';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { initI18n } from '../i18n';
import { Gallery, taskbarFrom } from './Gallery';
import { screens } from './screens';

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
const taskbar = taskbarFrom(params.get('taskbar'));

await initI18n();
const lang = params.get('lang');
if (lang === 'en' || lang === 'de') {
  document.documentElement.lang = lang;
  await i18next.changeLanguage(lang);
}

const screen = screens.find((entry) => entry.id === params.get('screen'));
if (screen !== undefined) {
  document.title = `${screen.title} - Vyxis design`;
}

createRoot(root).render(
  <StrictMode>
    {screen === undefined ? (
      <Gallery />
    ) : (
      screen.render({ taskbar, accent, locale: i18next.language })
    )}
  </StrictMode>,
);
