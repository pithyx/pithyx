// The mock desktop of `pithyx dev`: one app window in the box's sandbox, served over the real protocol with the SDK's
// host side, and switches and buttons for everything the desktop would change or send.
import '@pithyx/ui/tokens.css';
import './style.css';
import {
  PithyxError,
  portEndpoint,
  PROTOCOL_VERSION,
  SDK_VERSION,
  type AppContext,
  type AppLogLevel,
  type BackendStatus,
  type BackendUnavailableReason,
  type EventName,
  type Events,
  type FileRef,
  type SizeClass,
  type StorageInfo,
  type StorageState,
  type Theme,
  type ThemeTokenName,
  type UnloadReason,
} from '@pithyx/sdk';
import { serveApp, type AppConnection, type MethodHandlers } from '@pithyx/sdk/host';
import { createI18n } from '@pithyx/sdk/i18n';
import type { DevConfig } from '../src/dev/config';
import type { MockUser } from '../src/dev/users';
import de from '../../../i18n/de/cli.json';
import en from '../../../i18n/en/cli.json';

const API = '/__pithyx/api/';

// The public tokens, from the desktop's own (desktop/src/apps/sandbox/theme.ts).
const TOKEN_SOURCES: Record<ThemeTokenName, string> = {
  '--pithyx-color-background': '--color-bg',
  '--pithyx-color-surface': '--color-surface-1',
  '--pithyx-color-surface-raised': '--color-menu-solid',
  '--pithyx-color-hover': '--color-surface-hover',
  '--pithyx-color-selected': '--color-selection',
  '--pithyx-color-text': '--color-text',
  '--pithyx-color-text-muted': '--color-text-muted',
  '--pithyx-color-border': '--color-border',
  '--pithyx-color-accent': '--color-accent',
  '--pithyx-color-accent-text': '--color-accent-contrast',
  '--pithyx-color-focus': '--color-focus',
  '--pithyx-color-success': '--color-success',
  '--pithyx-color-warning': '--color-warning',
  '--pithyx-color-danger': '--color-danger',
  '--pithyx-font-ui': '--font-sans',
  '--pithyx-font-mono': '--font-mono',
  '--pithyx-font-size': '--font-size-md',
  '--pithyx-radius': '--radius-md',
  '--pithyx-space': '--space-2',
  '--pithyx-control-height': '--control-height',
};

const ACCENTS = ['petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite'];
const LOCALES = ['en-US', 'en-GB', 'de-DE', 'de-AT', 'fr-FR', 'ja-JP'];
const TIME_ZONES = ['Europe/Berlin', 'UTC', 'America/New_York', 'Asia/Tokyo'];
const SIZE_WIDTHS: Record<SizeClass, number> = { compact: 390, medium: 820, expanded: 0 };
/** A backend status, or `unavailable` with the reason it cannot start. */
type ShellBackend = BackendStatus | `unavailable:${BackendUnavailableReason}`;
const BACKEND_STATES: ShellBackend[] = [
  'stopped',
  'starting',
  'running',
  'updating',
  'unavailable',
  'unavailable:storage',
  'unavailable:pool',
  'unavailable:device',
];
const STORAGE_STATES: StorageState[] = ['ready', 'unmapped', 'locked', 'unavailable'];
const UNLOAD_REASONS: UnloadReason[] = ['logout', 'shutdown', 'restart', 'update'];
const MAX_STORAGE_VALUE = 64 * 1024;

interface State {
  user: MockUser;
  scheme: Theme['scheme'];
  accent: string;
  locale: string;
  timeZone: string;
  sizeClass: SizeClass;
  permissions: Set<string>;
  online: boolean;
  logLevel: AppLogLevel;
  backend: ShellBackend;
  storage: StorageInfo[];
  minimized: boolean;
  open: boolean;
  title: string;
  badge: number | null;
  attention: boolean;
  dirty: boolean | null;
}

const config = (await (await fetch(`${API}config`)).json()) as DevConfig;
const manifest = config.manifest;
const firstUser = config.users[0];
if (firstUser === undefined) {
  throw new Error('no mock users');
}

const state: State = {
  user: firstUser,
  scheme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
  accent: 'petrol',
  locale: navigator.language.startsWith('de') ? 'de-DE' : 'en-US',
  timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  sizeClass: 'expanded',
  permissions: new Set(manifest.permissions),
  online: true,
  logLevel: 'info',
  backend: 'running',
  storage: (manifest.storage ?? []).map((request) => ({
    id: request.id,
    state: 'ready',
    access: request.access === 'read' ? 'read' : 'readWrite',
  })),
  minimized: false,
  open: true,
  title: '',
  badge: null,
  attention: false,
  dirty: null,
};

// The mock desktop speaks the language it gives the app.
const i18n = createI18n(
  {
    get context() {
      return { locale: state.locale } as AppContext;
    },
  },
  { en, de },
);
const t = (key: string, params?: Record<string, unknown>) => i18n.t(`dev.${key}`, params);

function appName(): string {
  const language = state.locale.split('-')[0] ?? 'en';
  return manifest.name[language] ?? manifest.name.en;
}

type Child = Node | string | null | undefined | false;

/** The children that are there. */
function present(children: Child[]): (Node | string)[] {
  return children.filter(
    (child): child is Node | string => child !== null && child !== undefined && child !== false,
  );
}

function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: Partial<Record<string, string | boolean | ((event: Event) => void)>> = {},
  ...children: Child[]
): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag);
  for (const [name, value] of Object.entries(props)) {
    if (typeof value === 'function') {
      element.addEventListener(name.slice(2).toLowerCase(), value);
    } else if (value === true) {
      element.setAttribute(name, '');
    } else if (typeof value === 'string') {
      element.setAttribute(name, value);
    }
  }
  for (const child of children) {
    if (child !== null && child !== undefined && child !== false) {
      element.append(child);
    }
  }
  return element;
}

function theme(): Theme {
  const style = getComputedStyle(document.documentElement);
  const tokens = Object.fromEntries(
    Object.entries(TOKEN_SOURCES).map(([name, source]) => [
      name,
      style.getPropertyValue(source).trim(),
    ]),
  ) as Theme['tokens'];
  return { scheme: state.scheme, density: 'comfortable', tokens };
}

function applyLook(): void {
  document.documentElement.dataset.theme = state.scheme;
  document.documentElement.dataset.accent = state.accent;
  document.documentElement.lang = state.locale;
}

// Per-user storage of the app, kept in this browser like the box keeps it per user.
function storageKey(): string {
  return `pithyx-dev:${manifest.id}:${String(state.user.id)}`;
}

function readStorage(): Record<string, unknown> {
  try {
    return JSON.parse(localStorage.getItem(storageKey()) ?? '{}') as Record<string, unknown>;
  } catch {
    return {};
  }
}

function writeStorage(values: Record<string, unknown>): void {
  localStorage.setItem(storageKey(), JSON.stringify(values));
}

const files = new Map<string, File>();

function fileRef(file: File): FileRef {
  const ref = `mock-${String(files.size + 1)}-${String(Date.now())}`;
  files.set(ref, file);
  return {
    ref,
    name: file.name,
    ...(file.type !== '' && { type: file.type }),
    size: file.size,
    modified: new Date(file.lastModified).toISOString(),
    writable: state.permissions.has('files.write'),
  };
}

function pickFiles(multiple: boolean, extensions?: string[]): Promise<File[]> {
  return new Promise((resolve) => {
    const input = h('input', { type: 'file' });
    input.multiple = multiple;
    if (extensions !== undefined) {
      input.accept = extensions.map((extension) => `.${extension}`).join(',');
    }
    input.addEventListener('change', () => {
      resolve([...(input.files ?? [])]);
    });
    input.addEventListener('cancel', () => {
      resolve([]);
    });
    input.click();
  });
}

function denied(permission: string): PithyxError {
  return new PithyxError({ code: 'app.permission.denied', params: { permission } });
}

async function post<T>(name: string, body: object): Promise<T> {
  const response = await fetch(`${API}${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Pithyx-Request': '1' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new PithyxError((await response.json()) as ConstructorParameters<typeof PithyxError>[0]);
  }
  return (await response.json()) as T;
}

// The window and its parts.
const log = h('ol', { class: 'log', 'aria-live': 'polite' });
const toasts = h('div', { class: 'toasts', 'aria-live': 'polite' });
const frameHost = h('div', { class: 'frame-host' });
const titleText = h('span', { class: 'title-text' });
const dirtyMark = h('span', { class: 'dirty', title: '' }, '●');
const windowElement = h('section', { class: 'window' });
const taskbarEntry = h('button', { class: 'task', type: 'button' });
const panel = h('aside', { class: 'panel' });
let connection: AppConnection | null = null;
let instance = 0;
// The placeholder the box draws until the app is ready (D354), and its 30-second limit.
let loadingElement: HTMLElement | null = null;
let loadingTimer: ReturnType<typeof setTimeout> | undefined;

/**
 * As on the box, a file dialog opens only for the window the user works in: the app's frame has the focus. A fresh
 * click is not needed, so an `await` before the call is fine.
 */
function windowFocused(method: string): boolean {
  const focused =
    !state.minimized &&
    document.activeElement instanceof HTMLIFrameElement &&
    frameHost.contains(document.activeElement);
  if (!focused) {
    addLog('method', t('log.unfocused', { method }));
  }
  return focused;
}

function addLog(kind: string, text: string): void {
  const time = new Date().toLocaleTimeString(state.locale);
  log.prepend(h('li', { class: `entry ${kind}` }, h('time', {}, time), ` ${text}`));
  while (log.childElementCount > 300) {
    log.lastElementChild?.remove();
  }
}

function context(): Omit<AppContext, 'capabilities'> {
  const language = state.locale.split('-')[0] ?? 'en';
  let session: unknown;
  try {
    session = JSON.parse(sessionStorage.getItem(`${storageKey()}:session`) ?? 'null');
  } catch {
    session = null;
  }
  return {
    app: { id: manifest.id, version: manifest.version },
    instance: `mock-${String(instance)}`,
    user: state.user,
    language,
    locale: state.locale,
    timeZone: state.timeZone,
    theme: theme(),
    sizeClass: state.sizeClass,
    launch: { args: null },
    session,
    permissions: [...state.permissions],
    box: { name: 'pithyx-dev', version: SDK_VERSION },
    visible: !state.minimized,
    online: state.online,
    logLevel: state.logLevel,
    backend:
      manifest.backend === undefined
        ? null
        : { ...backendOf(state.backend), endpoints: Object.keys(manifest.backend.endpoints) },
    storage: state.storage,
    host: { version: SDK_VERSION, protocol: PROTOCOL_VERSION },
  };
}

function showMenu(
  x: number,
  y: number,
  items: { id: string; label: string; disabled?: boolean; separator?: true }[],
) {
  return new Promise<string | null>((resolve) => {
    const frame = frameHost.getBoundingClientRect();
    const close = (value: string | null) => {
      menu.remove();
      backdrop.remove();
      resolve(value);
    };
    const backdrop = h('div', {
      class: 'backdrop',
      onClick: () => {
        close(null);
      },
    });
    const menu = h(
      'div',
      { class: 'menu', role: 'menu' },
      ...items.map((item) =>
        item.separator === true
          ? h('hr')
          : h(
              'button',
              {
                type: 'button',
                role: 'menuitem',
                disabled: item.disabled === true,
                onClick: () => {
                  close(item.id);
                },
              },
              item.label,
            ),
      ),
    );
    menu.style.left = `${String(frame.left + x)}px`;
    menu.style.top = `${String(frame.top + y)}px`;
    menu.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        close(null);
      }
    });
    document.body.append(backdrop, menu);
    menu.querySelector('button')?.focus();
  });
}

function toast(
  id: string,
  request: Events['notificationAction'] & {
    title: string;
    body?: string;
    actions?: { id: string; label: string }[];
  },
) {
  const element = h(
    'div',
    { class: 'toast', role: 'status' },
    h('strong', {}, request.title),
    request.body !== undefined && h('p', {}, request.body),
    h(
      'div',
      { class: 'toast-actions' },
      h(
        'button',
        {
          type: 'button',
          onClick: () => {
            activate(null);
          },
        },
        t('notification.open'),
      ),
      ...(request.actions ?? []).map((action) =>
        h(
          'button',
          {
            type: 'button',
            onClick: () => {
              activate(action.id);
            },
          },
          action.label,
        ),
      ),
      h(
        'button',
        {
          type: 'button',
          onClick: () => {
            element.remove();
          },
        },
        t('notification.dismiss'),
      ),
    ),
  );
  function activate(action: string | null) {
    element.remove();
    connection?.emit('notificationAction', { notification: id, action, data: request.data });
    addLog('event', `notificationAction ${JSON.stringify({ notification: id, action })}`);
  }
  toasts.append(element);
}

const methods: MethodHandlers = {
  'window.setTitle': ({ title }) => {
    state.title = title;
    renderWindow();
    return undefined;
  },
  'window.close': () => {
    closeWindow();
    return undefined;
  },
  'window.setSize': ({ width, height }) => {
    const size = {
      width: Math.max(320, Math.round(width)),
      height: Math.max(240, Math.round(height)),
    };
    if (state.sizeClass === 'expanded') {
      windowElement.style.width = `${String(size.width)}px`;
      frameHost.style.height = `${String(size.height)}px`;
    }
    return size;
  },
  'window.setDirty': ({ dirty }) => {
    state.dirty = dirty;
    renderWindow();
    return undefined;
  },
  'window.requestAttention': () => {
    state.attention = true;
    renderWindow();
    return undefined;
  },
  'window.ready': () => {
    hideLoading();
    return undefined;
  },
  'log.write': ({ entries }) => {
    for (const entry of entries) {
      if (entry.level !== 'debug' || state.logLevel === 'debug') {
        addLog(
          `log-${entry.level}`,
          `${entry.level} ${entry.message}${entry.data === undefined ? '' : ` ${JSON.stringify(entry.data)}`}`,
        );
      }
    }
    return undefined;
  },
  notify: (request) => {
    if (!state.permissions.has('notifications')) {
      throw denied('notifications');
    }
    const id = `n${String(Date.now())}`;
    toast(id, { ...request, notification: id, action: null, data: request.data ?? null });
    addLog('method', `notify ${request.title}`);
    return { id };
  },
  setBadge: ({ count }) => {
    state.badge = count === null || count <= 0 ? null : count;
    renderWindow();
    return undefined;
  },
  'session.save': ({ state: saved }) => {
    sessionStorage.setItem(`${storageKey()}:session`, JSON.stringify(saved));
    return undefined;
  },
  'storage.get': ({ key }) => readStorage()[key] ?? null,
  'storage.set': ({ key, value }) => {
    if (JSON.stringify(value).length > MAX_STORAGE_VALUE) {
      throw new PithyxError({ code: 'app.storage.too_large', params: {} });
    }
    writeStorage({ ...readStorage(), [key]: value });
    return undefined;
  },
  'storage.remove': ({ key }) => {
    writeStorage(
      Object.fromEntries(Object.entries(readStorage()).filter(([name]) => name !== key)),
    );
    return undefined;
  },
  'storage.keys': () => Object.keys(readStorage()),
  'backend.token': () =>
    post('tokens', { user: state.user.id, instance: `mock-${String(instance)}` }),
  'backend.ticket': ({ endpoint }) =>
    post('tickets', { user: state.user.id, instance: `mock-${String(instance)}`, endpoint }),
  'directory.users': () => config.directory.users,
  'directory.groups': () => config.directory.groups,
  'contextMenu.show': ({ at, items }) =>
    showMenu(
      at.x,
      at.y,
      items.map((item) =>
        'separator' in item
          ? { id: '', label: '', separator: true as const }
          : { id: item.id, label: item.label, ...(item.disabled === true && { disabled: true }) },
      ),
    ),
  'clipboard.writeText': async ({ text }) => {
    await navigator.clipboard.writeText(text);
    addLog('method', `clipboard.writeText (${String(text.length)})`);
    return undefined;
  },
  openUrl: ({ url }) => {
    window.open(url, '_blank', 'noopener,noreferrer');
    return undefined;
  },
  'files.pick': async ({ multiple, extensions }) => {
    if (!state.permissions.has('files.read')) {
      throw denied('files.read');
    }
    if (!windowFocused('files.pick')) {
      return [];
    }
    return (await pickFiles(multiple === true, extensions)).map(fileRef);
  },
  'files.read': ({ file }) => {
    const found = files.get(file.ref);
    if (found === undefined || !state.permissions.has('files.read')) {
      throw denied('files.read');
    }
    return found;
  },
  'files.write': ({ file, data }) => {
    if (!files.has(file.ref) || !state.permissions.has('files.write')) {
      throw denied('files.write');
    }
    const written = new File([data], file.name, { type: data.type });
    files.set(file.ref, written);
    addLog('method', `files.write ${file.name} (${String(data.size)})`);
    return { ...file, size: written.size, modified: new Date().toISOString() };
  },
  'files.saveAs': ({ suggestedName, data }) => {
    if (!state.permissions.has('files.write')) {
      throw denied('files.write');
    }
    if (!windowFocused('files.saveAs')) {
      return null;
    }
    const link = h('a', { href: URL.createObjectURL(data), download: suggestedName });
    link.click();
    return fileRef(new File([data], suggestedName, { type: data.type }));
  },
  'ui.showError': ({ problem }) => {
    addLog('error', `ui.showError ${problem.code} ${problem.detail ?? ''}`);
    return undefined;
  },
};

function connect(frame: HTMLIFrameElement): void {
  const target = frame.contentWindow;
  if (target === null) {
    return;
  }
  connection?.close();
  const channel = new MessageChannel();
  connection = serveApp(portEndpoint(channel.port1), {
    context: context(),
    methods,
    onReady: ({ sdk }) => {
      addLog('event', t('log.connected', { sdk }));
      if (manifest.window.loading?.untilReady !== true) {
        hideLoading();
      }
    },
  });
  // The frame has an opaque origin, so no target origin can be named; the transferred port carries the trust.
  target.postMessage({ type: 'pithyx:connect' }, '*', [channel.port2]);
}

function openWindow(): void {
  instance += 1;
  state.open = true;
  state.title = '';
  state.dirty = null;
  frameHost.replaceChildren();
  const frame = h('iframe', {
    src: config.entry,
    title: appName(),
    sandbox: 'allow-scripts allow-forms',
    referrerpolicy: 'no-referrer',
  });
  frame.addEventListener('load', () => {
    connect(frame);
  });
  const language = state.locale.split('-')[0] ?? 'en';
  const text = manifest.window.loading?.text;
  loadingElement = h(
    'div',
    { class: 'loading', role: 'status' },
    h('strong', {}, appName()),
    h('span', { class: 'indicator', 'aria-hidden': 'true' }),
    ...(state.backend === 'starting'
      ? [
          h(
            'span',
            {},
            text === undefined
              ? t('window.starting', { name: appName() })
              : (text[state.locale] ?? text[language] ?? text.en),
          ),
        ]
      : state.backend === 'unavailable:storage' ||
          state.backend === 'unavailable:pool' ||
          state.backend === 'unavailable:device'
        ? [
            h(
              'span',
              {},
              t(`window.${state.backend.slice('unavailable:'.length)}`, {
                name: appName(),
              }),
            ),
          ]
        : []),
  );
  clearTimeout(loadingTimer);
  loadingTimer = setTimeout(hideLoading, 30_000);
  frameHost.append(frame, loadingElement);
  renderWindow();
}

function hideLoading(): void {
  clearTimeout(loadingTimer);
  loadingElement?.remove();
  loadingElement = null;
}

function closeWindow(): void {
  connection?.close();
  connection = null;
  hideLoading();
  state.open = false;
  state.badge = null;
  frameHost.replaceChildren(
    h(
      'div',
      { class: 'closed' },
      h('button', { type: 'button', onClick: openWindow }, t('window.reopen')),
    ),
  );
  renderWindow();
}

async function askToClose(): Promise<void> {
  if (connection === null) {
    closeWindow();
    return;
  }
  if (state.dirty === false) {
    closeWindow();
    return;
  }
  const answer = await connection.ask('beforeClose', {}, 5000);
  addLog('event', `beforeClose ${JSON.stringify(answer ?? null)}`);
  if (answer === undefined) {
    if (confirm(t('window.notResponding'))) {
      closeWindow();
    }
  } else if (answer.allow) {
    closeWindow();
  }
}

function emit<E extends EventName>(event: E, data: Events[E]): void {
  connection?.emit(event, data);
  addLog('event', `${event} ${JSON.stringify(data)}`);
}

function setMinimized(minimized: boolean): void {
  if (state.minimized === minimized) {
    return;
  }
  state.minimized = minimized;
  emit(minimized ? 'minimize' : 'restore', {});
  emit('visibilityChange', { visible: !minimized });
  if (!minimized) {
    state.attention = false;
  }
  renderWindow();
}

function renderWindow(): void {
  titleText.textContent = state.title === '' ? appName() : state.title;
  dirtyMark.hidden = state.dirty !== true;
  dirtyMark.title = t('window.dirty');
  windowElement.hidden = state.minimized;
  windowElement.dataset.size = state.sizeClass;
  const width = SIZE_WIDTHS[state.sizeClass];
  windowElement.style.width =
    width > 0 ? `${String(width)}px` : `${String(manifest.window.defaultSize.width)}px`;
  frameHost.style.height = `${String(manifest.window.defaultSize.height)}px`;
  taskbarEntry.replaceChildren(
    ...present([
      h('img', { src: '/__pithyx/icon.svg', alt: '' }),
      h('span', {}, appName()),
      state.badge !== null && h('span', { class: 'badge' }, String(state.badge)),
    ]),
  );
  taskbarEntry.classList.toggle('attention', state.attention);
  taskbarEntry.setAttribute('aria-pressed', String(!state.minimized && state.open));
}

function backendOf(value: ShellBackend): Events['backendStatus'] {
  const [status, reason] = value.split(':') as [BackendStatus, BackendUnavailableReason?];
  return reason === undefined ? { status } : { status, reason };
}

function select<T extends string>(
  label: string,
  values: readonly T[],
  current: T,
  change: (value: T) => void,
  text: (value: T) => string = (value) => value,
) {
  const element = h('select', {
    onChange: (event) => {
      change((event.target as HTMLSelectElement).value as T);
    },
  });
  for (const value of values) {
    const option = h('option', { value }, text(value));
    option.selected = value === current;
    element.append(option);
  }
  return h('label', { class: 'field' }, h('span', {}, label), element);
}

function toggle(label: string, checked: boolean, change: (checked: boolean) => void) {
  const input = h('input', {
    type: 'checkbox',
    onChange: (event) => {
      change((event.target as HTMLInputElement).checked);
    },
  });
  input.checked = checked;
  return h('label', { class: 'toggle' }, input, h('span', {}, label));
}

// A sample of every event's data, to edit and send.
function samples(): { [E in EventName]: Events[E] } {
  const now = new Date().toISOString();
  return {
    launch: { args: null },
    focus: {},
    blur: {},
    minimize: {},
    restore: {},
    themeChange: theme(),
    localeChange: {
      language: state.locale.split('-')[0] ?? 'en',
      locale: state.locale,
      timeZone: state.timeZone,
    },
    sizeClassChange: { sizeClass: state.sizeClass },
    openFile: {
      file: {
        ref: 'mock-file',
        name: 'example.txt',
        type: 'text/plain',
        size: 0,
        modified: now,
        writable: false,
      },
    },
    appUpdated: { version: manifest.version },
    visibilityChange: { visible: !state.minimized },
    connectionChange: { online: state.online },
    backendStatus: backendOf(state.backend),
    permissionsChange: { permissions: [...state.permissions] },
    storageChange: { storage: state.storage },
    notificationAction: { notification: 'n1', action: null, data: null },
    logLevelChange: { level: state.logLevel },
    sharesChanged: {},
    folderChanged: { watch: 'w1', changed: [], removed: [], resync: false, gone: false },
    uploadProgress: { upload: 'u1', loaded: 0, total: 0 },
    jobChanged: {
      id: 1,
      kind: 'files.copy',
      state: 'running',
      progress: { itemsDone: 0, itemsTotal: 1, bytesDone: 0, bytesTotal: 0, current: null },
      problem: null,
      result: null,
    },
  };
}

function eventRow<E extends EventName>(event: E, sample: Events[E]) {
  const editor = h('textarea', { rows: '3', spellcheck: 'false', 'aria-label': event });
  editor.value = JSON.stringify(sample, null, 1);
  return h(
    'details',
    { class: 'event' },
    h('summary', {}, event),
    editor,
    h(
      'button',
      {
        type: 'button',
        onClick: () => {
          try {
            emit(event, JSON.parse(editor.value) as Events[E]);
          } catch {
            addLog('error', t('events.invalidJson', { event }));
          }
        },
      },
      t('events.send', { event }),
    ),
  );
}

function renderPanel(): void {
  const permissions = [...new Set([...manifest.permissions, ...state.permissions])];
  const all = samples();
  panel.replaceChildren(
    ...present([
      h('h2', {}, t('panel.desktop')),
      select(
        t('panel.user'),
        config.users.map((user) => String(user.id)),
        String(state.user.id),
        (value) => {
          void post('signout', { user: state.user.id, instance: 'all' });
          state.user = config.users.find((user) => String(user.id) === value) ?? state.user;
          addLog('event', t('log.userChanged', { user: state.user.name }));
          // Another user is another session: the desktop opens the app afresh.
          void connection?.ask('beforeUnload', { reason: 'logout' }, 2000).then(openWindow);
          if (connection === null) {
            openWindow();
          }
        },
        (id) => {
          const user = config.users.find((candidate) => String(candidate.id) === id);
          return user === undefined
            ? id
            : `${user.name}${user.isAdmin ? ` (${t('panel.admin')})` : ''}`;
        },
      ),
      select(
        t('panel.theme'),
        ['light', 'dark'] as const,
        state.scheme,
        (value) => {
          state.scheme = value;
          applyLook();
          emit('themeChange', theme());
        },
        (value) => t(`panel.scheme.${value}`),
      ),
      select(t('panel.accent'), ACCENTS, state.accent, (value) => {
        state.accent = value;
        applyLook();
        emit('themeChange', theme());
      }),
      select(t('panel.locale'), LOCALES, state.locale, (value) => {
        state.locale = value;
        applyLook();
        emit('localeChange', {
          language: value.split('-')[0] ?? 'en',
          locale: value,
          timeZone: state.timeZone,
        });
        renderPanel();
        renderWindow();
      }),
      select(
        t('panel.timeZone'),
        [...new Set([state.timeZone, ...TIME_ZONES])],
        state.timeZone,
        (value) => {
          state.timeZone = value;
          emit('localeChange', {
            language: state.locale.split('-')[0] ?? 'en',
            locale: state.locale,
            timeZone: value,
          });
        },
      ),
      select(
        t('panel.sizeClass'),
        ['compact', 'medium', 'expanded'] as const,
        state.sizeClass,
        (value) => {
          state.sizeClass = value;
          emit('sizeClassChange', { sizeClass: value });
          renderWindow();
        },
        (value) => t(`panel.size.${value}`),
      ),
      toggle(t('panel.online'), state.online, (online) => {
        state.online = online;
        emit('connectionChange', { online });
      }),
      toggle(t('panel.debugLog'), state.logLevel === 'debug', (debug) => {
        state.logLevel = debug ? 'debug' : 'info';
        emit('logLevelChange', { level: state.logLevel });
      }),
      manifest.backend !== undefined &&
        select(
          t('panel.backend'),
          BACKEND_STATES,
          state.backend,
          (value) => {
            state.backend = value;
            emit('backendStatus', backendOf(value));
          },
          (value) => value.replace(':', ' · '),
        ),
      permissions.length > 0 &&
        h(
          'fieldset',
          {},
          h('legend', {}, t('panel.permissions')),
          ...permissions.map((permission) =>
            toggle(permission, state.permissions.has(permission), (granted) => {
              if (granted) {
                state.permissions.add(permission);
              } else {
                state.permissions.delete(permission);
              }
              emit('permissionsChange', { permissions: [...state.permissions] });
            }),
          ),
        ),
      ...state.storage.map((storage) =>
        select(t('panel.storage', { id: storage.id }), STORAGE_STATES, storage.state, (value) => {
          storage.state = value;
          emit('storageChange', { storage: state.storage });
        }),
      ),
      h('h2', {}, t('panel.window')),
      h(
        'div',
        { class: 'buttons' },
        h('button', { type: 'button', onClick: () => void askToClose() }, t('window.close')),
        h('button', { type: 'button', onClick: openWindow }, t('window.reload')),
        h(
          'button',
          {
            type: 'button',
            onClick: () => {
              emit('focus', {});
            },
          },
          'focus',
        ),
        h(
          'button',
          {
            type: 'button',
            onClick: () => {
              emit('blur', {});
            },
          },
          'blur',
        ),
        h(
          'button',
          {
            type: 'button',
            onClick: () => {
              void pickFiles(false).then(([file]) => {
                if (file !== undefined) {
                  emit('openFile', { file: fileRef(file) });
                }
              });
            },
          },
          t('window.openFile'),
        ),
      ),
      h(
        'div',
        { class: 'buttons' },
        ...UNLOAD_REASONS.map((reason) =>
          h(
            'button',
            {
              type: 'button',
              onClick: () => {
                void connection?.ask('beforeUnload', { reason }, 2000).then((answer) => {
                  addLog('event', `beforeUnload ${reason}: ${JSON.stringify(answer ?? null)}`);
                });
              },
            },
            `beforeUnload ${reason}`,
          ),
        ),
      ),
      h('h2', {}, t('panel.events')),
      ...(Object.keys(all) as EventName[]).map((event) => eventRow(event, all[event])),
    ]),
  );
}

applyLook();
document.title = t('title', { app: appName() });
windowElement.append(
  h(
    'header',
    { class: 'titlebar' },
    h('img', { src: '/__pithyx/icon.svg', alt: '' }),
    titleText,
    dirtyMark,
    h(
      'button',
      {
        type: 'button',
        class: 'chrome',
        'aria-label': t('window.minimize'),
        onClick: () => {
          setMinimized(true);
        },
      },
      '–',
    ),
    h(
      'button',
      {
        type: 'button',
        class: 'chrome',
        'aria-label': t('window.close'),
        onClick: () => void askToClose(),
      },
      '×',
    ),
  ),
  frameHost,
);
taskbarEntry.addEventListener('click', () => {
  setMinimized(!state.minimized);
});
document.body.append(
  h(
    'main',
    { class: 'desktop' },
    h('div', { class: 'workspace' }, windowElement, toasts),
    h('nav', { class: 'taskbar' }, taskbarEntry, h('span', { class: 'hint' }, t('taskbar.hint'))),
  ),
  panel,
  h('section', { class: 'log-panel' }, h('h2', {}, t('panel.log')), log),
);
renderPanel();
openWindow();
