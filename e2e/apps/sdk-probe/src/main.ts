// A sample third-party app: plain TypeScript, no framework, only @pithyx/sdk. It shows what the desktop hands over,
// uses the window, notification and storage calls, logs lifecycle events and checks that its sandbox holds.
import { connect, type NotificationActivation, type PithyxApp } from '@pithyx/sdk';
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
  let dirty = false;
  let hang = false;
  let attention = false;
  let picked = '';
  let activation: NotificationActivation | null = app.context.launch.notification ?? null;
  const events: string[] = [];

  // A message posted straight to the desktop window is not the app's port; the desktop must ignore it.
  window.parent.postMessage(
    { type: 'request', id: 1, method: 'window.setTitle', params: { title: 'forged' } },
    '*',
  );
  const probes = await probeSandbox();

  function toggle(label: string, checked: boolean, change: (on: boolean) => void) {
    const wrapper = element('label');
    const input = element('input', { type: 'checkbox' });
    input.checked = checked;
    input.addEventListener('change', () => {
      change(input.checked);
    });
    wrapper.append(input, ` ${label}`);
    return wrapper;
  }

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
      [t('online'), context.online ? t('yes') : t('no'), 'online'],
      [t('visible'), context.visible ? t('yes') : t('no'), 'visible'],
      [t('launch'), JSON.stringify(context.launch.args), 'launch'],
      [
        t('activation'),
        activation === null
          ? '-'
          : `${activation.action ?? t('notificationItself')} ${JSON.stringify(activation.data)}`,
        'activation',
      ],
      [t('picked'), picked === '' ? '-' : picked, 'picked'],
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
    const notifyActions = element('button', { type: 'button' }, t('notifyActions'));
    notifyActions.addEventListener('click', () => {
      void app.notify({
        title: t('notificationTitle'),
        body: t('notificationBody', { count }),
        actions: [
          { id: 'reply', label: t('reply') },
          { id: 'later', label: t('later') },
        ],
        data: { count },
      });
    });
    const badge = element('button', { type: 'button' }, t('badge'));
    badge.addEventListener('click', () => {
      void app.setBadge(count);
    });
    const menu = element('button', { type: 'button' }, t('menu'));
    const showMenu = (x: number, y: number) => {
      void app.contextMenu
        .show({ x, y }, [
          { id: 'copy', label: t('menuCopy'), shortcut: 'Ctrl+C' },
          { id: 'pin', label: t('menuPin'), checked: true },
          { id: 'disabled', label: t('menuDisabled'), disabled: true },
          { separator: true },
          { id: 'delete', label: t('menuDelete'), danger: true },
        ])
        .then((id) => {
          picked = id ?? t('dismissed');
          render();
        });
    };
    menu.addEventListener('click', () => {
      const rect = menu.getBoundingClientRect();
      showMenu(rect.left, rect.bottom);
    });
    menu.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      showMenu(event.clientX, event.clientY);
    });
    const copy = element('button', { type: 'button' }, t('copy'));
    copy.addEventListener('click', () => {
      void app.clipboard.writeText(t('copied', { count }));
    });
    const website = element('button', { type: 'button' }, t('website'));
    website.addEventListener('click', () => {
      // The box itself, so the link works without the internet.
      void app.openUrl(new URL('/', location.href).href);
    });
    const log = element('button', { type: 'button' }, t('log'));
    log.addEventListener('click', () => {
      app.log.debug('demo debug entry', { count });
      app.log.info('demo info entry', { count });
      app.log.warn('demo warn entry', { count });
      app.log.error('demo error entry', { count });
    });
    actions.append(setTitle, notify, increment, notifyActions, badge, menu, copy, website, log);
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
    root.append(
      toggle(t('dirty'), dirty, (on) => {
        dirty = on;
        void app.window.setDirty(on);
      }),
      toggle(t('hang'), hang, (on) => {
        hang = on;
      }),
      toggle(t('attention'), attention, (on) => {
        attention = on;
      }),
    );

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
    'visibilityChange',
    'connectionChange',
    'backendStatus',
    'permissionsChange',
    'storageChange',
    'notificationAction',
    'logLevelChange',
  ] as const) {
    app.on(name, () => {
      events.push(name);
      render();
    });
  }
  app.on('notificationAction', (data) => {
    activation = data;
    render();
  });
  app.on('launch', (launch) => {
    activation = launch.notification ?? activation;
    render();
  });
  // Asks for attention once the user leaves the window, so the taskbar entry pulses while another window has the focus.
  app.on('blur', () => {
    if (attention) {
      void app.window.requestAttention();
    }
  });
  app.answer('beforeClose', () =>
    hang ? new Promise<never>(() => undefined) : { allow: !keepOpen },
  );
  app.answer('beforeUnload', async () => {
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
