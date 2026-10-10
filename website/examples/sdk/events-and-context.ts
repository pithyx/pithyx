// A viewer that follows its window: launch arguments, notification clicks, visibility, the connection, saved window
// state, unsaved changes and updates.
import { connect, type LaunchArgs, type PithyxApp } from '@pithyx/sdk';

interface WindowState {
  view: 'inbox' | 'archive';
  scrollTop: number;
}

const app: PithyxApp = await connect();
const state: WindowState = { view: 'inbox', scrollTop: 0, ...(await restoreState(app)) };
let dirty = false;
let poll: ReturnType<typeof setInterval> | undefined;

/** What this window saved before a reload, or on another device before "Get desktop from ...". */
async function restoreState(app: PithyxApp): Promise<Partial<WindowState>> {
  const saved = await app.session.restore();
  return typeof saved === 'object' && saved !== null ? saved : {};
}

function isView(value: unknown): value is WindowState['view'] {
  return value === 'inbox' || value === 'archive';
}

/** At start from the context, later from `launch` when a `single` app is launched again. */
function handleLaunch(launch: LaunchArgs) {
  if (launch.notification !== undefined) {
    // The user clicked a notification while no window of the app was open.
    openFromNotification(launch.notification.data, launch.notification.action);
  } else if (isView(launch.args)) {
    state.view = launch.args;
  }
  render();
}

function openFromNotification(data: unknown, action: string | null) {
  app.log.info('Opened from a notification', { action });
  if (typeof data === 'object' && data !== null && 'view' in data && isView(data.view)) {
    state.view = data.view;
  }
}

function startPolling() {
  poll ??= setInterval(() => void refresh(), 30_000);
}

function stopPolling() {
  clearInterval(poll);
  poll = undefined;
}

async function refresh() {
  if (app.context.online) {
    await app.window.setTitle(state.view === 'inbox' ? 'Inbox' : 'Archive');
  }
}

async function markDirty(value: boolean) {
  dirty = value;
  await app.window.setDirty(value);
}

async function save() {
  await app.session.save(state);
  await markDirty(false);
}

function render() {
  document.body.dataset.view = state.view;
  document.body.dataset.size = app.context.sizeClass;
  document.scrollingElement?.scrollTo(0, state.scrollTop);
}

handleLaunch(app.context.launch);
app.on('launch', handleLaunch);
app.on('notificationAction', ({ data, action }) => {
  openFromNotification(data, action);
  render();
});

// Minimizing does not hide the frame for the browser: visibilityChange is the signal to pause work nobody sees.
app.on('visibilityChange', ({ visible }) => {
  if (visible) {
    startPolling();
  } else {
    stopPolling();
  }
});
app.on('connectionChange', ({ online }) => {
  if (online) {
    void refresh();
  }
});
app.on('sizeClassChange', render);

// Asked only while setDirty(true), because the app calls setDirty; the desktop waits 5 seconds.
app.answer('beforeClose', () => ({ allow: !dirty }));
// Sign-out, shutdown, restart or an update of Pithyx: cannot be refused, 2 seconds to save.
app.answer('beforeUnload', async () => {
  await save();
  return {};
});
app.on('appUpdated', ({ version }) => {
  app.log.info('New version installed', { version });
  if (!dirty) {
    location.reload();
  }
});

addEventListener('scroll', () => {
  state.scrollTop = document.scrollingElement?.scrollTop ?? 0;
});
await markDirty(false);
if (app.context.visible) {
  startPolling();
}
render();
