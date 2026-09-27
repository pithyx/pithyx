// A sample third-party app: plain TypeScript, no framework, only @pithyx/sdk. It shows what the desktop hands over,
// uses the window, notification and storage calls, logs lifecycle events and checks that its sandbox holds.
import { connect, type PithyxApp } from '@pithyx/sdk';
import { createI18n, pickLanguage } from '@pithyx/sdk/i18n';
import de from '../i18n/de.json';
import en from '../i18n/en.json';
import './style.css';

type Messages = typeof en;

// Only for the error text when no desktop connected; everything else goes through the SDK's i18n helper.
function messagesFor(locale: string): Messages {
  return pickLanguage(locale, ['en', 'de']) === 'de' ? de : en;
}

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  attributes: Record<string, string> = {},
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    node.setAttribute(name, value);
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}

/** What the sandbox lets the app reach. Every probe must come back blocked. */
async function probeSandbox(): Promise<Record<keyof Messages['probe'], boolean>> {
  const attempt = (action: () => unknown) => {
    try {
      action();
      return false;
    } catch {
      return true;
    }
  };
  let api = true;
  try {
    const response = await fetch('/api/auth/session', { credentials: 'include' });
    // A 401 means the request went out without the desktop's session cookie.
    api = response.status === 401;
  } catch {
    // The daemon's API sends no CORS headers, so the opaque origin cannot read it at all.
  }
  return {
    cookie: attempt(() => document.cookie),
    parent: attempt(() => window.parent.document.title),
    storage: attempt(() => localStorage.getItem('probe')),
    api,
    // Checked by the desktop: the title must not change to the forged value.
    forged: true,
  };
}

async function main(root: HTMLElement) {
  const app: PithyxApp = await connect();
  const i18n = createI18n(app, { en, de });
  const t = (key: string, params?: Record<string, unknown>) => i18n.t(key, params);
  let count = (await app.storage.get<number>('count')) ?? 0;
  let keepOpen = false;
  const events: string[] = [];

  // A message posted straight to the desktop window is not the app's port; the desktop must ignore it.
  window.parent.postMessage(
    { type: 'request', id: 1, method: 'window.setTitle', params: { title: 'forged' } },
    '*',
  );
  const probes = await probeSandbox();

  function render() {
    const { context } = app;
    root.replaceChildren();
    root.removeAttribute('aria-busy');
    root.append(element('h1', {}, t('heading')), element('p', {}, t('intro')));

    const facts = element('dl', { 'data-testid': 'context' });
    for (const [label, value, key] of [
      [t('user'), context.user.displayName ?? context.user.name, 'user'],
      [t('locale'), context.locale, 'locale'],
      [t('timeZone'), context.timeZone, 'timeZone'],
      [t('theme'), context.theme.scheme, 'theme'],
      [t('sizeClass'), context.sizeClass, 'sizeClass'],
    ] as const) {
      facts.append(element('dt', {}, label), element('dd', { 'data-fact': key }, value));
    }
    root.append(facts);

    const actions = element('div', { class: 'actions' });
    const setTitle = element('button', { type: 'button' }, t('setTitle'));
    setTitle.addEventListener('click', () => {
      void app.window.setTitle(t('titleValue', { count }));
    });
    const notify = element('button', { type: 'button' }, t('notify'));
    notify.addEventListener('click', () => {
      void app.notify({ title: t('notificationTitle'), body: t('notificationBody', { count }) });
    });
    const increment = element('button', { type: 'button' }, t('count'));
    increment.addEventListener('click', () => {
      count += 1;
      void app.storage.set('count', count);
      void app.session.save({ count });
      render();
    });
    actions.append(setTitle, notify, increment);
    root.append(
      actions,
      element('p', {}, `${t('counter')}: `),
      element('output', { 'aria-label': t('counter') }, String(count)),
    );

    const keep = element('label');
    const checkbox = element('input', { type: 'checkbox' });
    checkbox.checked = keepOpen;
    checkbox.addEventListener('change', () => {
      keepOpen = checkbox.checked;
    });
    keep.append(checkbox, ` ${t('keepOpen')}`);
    root.append(keep);

    const list = element('ul', { 'aria-label': t('events') });
    for (const name of events) {
      list.append(element('li', {}, name));
    }
    root.append(element('h2', {}, t('events')), list);

    const checks = element('dl', { 'aria-label': t('probes') });
    for (const [key, blocked] of Object.entries(probes) as [keyof Messages['probe'], boolean][]) {
      checks.append(
        element('dt', {}, t(`probe.${key}`)),
        element('dd', { 'data-probe': key }, blocked ? t('blocked') : t('reachable')),
      );
    }
    root.append(element('h2', {}, t('probes')), checks);
  }

  for (const name of [
    'launch',
    'focus',
    'blur',
    'minimize',
    'restore',
    'themeChange',
    'localeChange',
    'sizeClassChange',
    'openFile',
    'appUpdated',
    'backendUnavailable',
    'backendRestored',
    'permissionRevoked',
  ] as const) {
    app.on(name, () => {
      events.push(name);
      render();
    });
  }
  app.answer('beforeClose', () => ({ allow: !keepOpen }));
  app.answer('beforeLogout', async () => {
    await app.storage.set('count', count);
    return {};
  });
  render();
}

const root = document.getElementById('app');
if (root !== null) {
  main(root).catch(() => {
    root.removeAttribute('aria-busy');
    root.textContent = messagesFor(navigator.language).failed;
  });
}
