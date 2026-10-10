---
title: 'Ereignisse, Fensterkontext und Startargumente'
description: 'app.context lesen, den Ereignissen des Desktops folgen, Startargumente und Klicks auf Benachrichtigungen behandeln und den Fensterzustand speichern.'
status: ready
audience: developer
sidebar:
  order: 3
---

`app.context` enthält alles, was der Desktop dem Fenster beim Start mitgeteilt hat: den Benutzer, die Sprache, die Darstellung, die Größenklasse, die Startargumente, die Berechtigungen und den Zustand von Backend und Speicher. Das SDK hält ihn aktuell, und `app.on(event, handler)` sagt der App, wenn sich etwas geändert hat. Mit `app.answer` antwortet sie auf die zwei Fragen, die der Desktop stellt, bevor ein Fenster verschwindet. Dieser Leitfaden zeigt, wie ein Fenster startet, reagiert und seinen Zustand speichert.

## Beispiel

<!-- example: sdk/events-and-context.ts -->

```ts
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
```

## Der Kontext

`app.context` zu lesen braucht keine Berechtigung und keinen Aufruf. Das SDK aktualisiert diese Felder, bevor es deine Handler aufruft: `theme` (`themeChange`), `language`, `locale`, `timeZone` (`localeChange`), `sizeClass` (`sizeClassChange`), `permissions` (`permissionsChange`), `visible` (`visibilityChange`), `online` (`connectionChange`), `logLevel` (`logLevelChange`), `backend.status` (`backendStatus`) und `storage` (`storageChange`). Lies das Feld, statt eine eigene Kopie zu führen.

- `instance` ist die ID dieses Fensters, eindeutig pro Desktop-Tab.
- `user` hat `id`, `name`, `displayName`, `groups`, `isAdmin` und `twoFactor`. Das ist zur Anzeige da; ein Backend nutzt stattdessen die Header des Proxys.
- `sizeClass` ist `compact` unter 600 px, `medium` bis 1023 px und darüber `expanded`.
- `capabilities` listet die Methoden, die der Desktop bedient; `app.supports(method)` liest sie.

## Startargumente

`app.context.launch` sagt, warum das Fenster aufging: `{ args, file?, notification? }`. Ein Fenster, das der Benutzer vom Desktop oder aus dem Store öffnet, hat `{ args: null }`. Ein Klick auf eine Benachrichtigung der App, während kein Fenster der App offen ist, startet sie mit `launch.notification` (`notification`, `action`, `data`). Bei einer App mit `"instances": "single"` im Manifest holt ein erneuter Start das Fenster des Benutzers auf diesem Gerät nach vorn und sendet die neuen Argumente als Ereignis `launch`.

`launch.file` und das Ereignis `openFile` gehören zu Datei-Handlern; der Desktop sendet sie Drittanbieter-Apps noch nicht.

## Benachrichtigungen

Ein Klick auf eine Benachrichtigung oder einen ihrer Knöpfe, während ein Fenster der App offen ist, holt dieses Fenster nach vorn und sendet `notificationAction` mit der ID der Benachrichtigung, der `id` des Knopfs (oder `null` für die Benachrichtigung selbst) und den `data`, die du `notify` mitgegeben hast. Das Fenster, das die Benachrichtigung gesendet hat, bekommt es, wenn es noch verbunden ist, sonst das oberste Fenster der App.

## Ereignisse

| Ereignis                       | Wann                                                                               |
| ------------------------------ | ---------------------------------------------------------------------------------- |
| `focus`, `blur`                | das Fenster bekam oder verlor den Fokus                                            |
| `minimize`, `restore`          | das Fenster wurde minimiert oder wieder gezeigt                                    |
| `visibilityChange`             | das Fenster ist sichtbar oder nicht: minimiert, oder der Browser-Tab verborgen     |
| `connectionChange`             | der Desktop verlor oder bekam die Verbindung zur Box zurück                        |
| `sizeClassChange`              | das Fenster überschritt 600 oder 1024 px                                           |
| `themeChange`, `localeChange`  | siehe [Sprachen und Darstellung](/de/docs/sdk/guides/i18n-and-theming/)            |
| `permissionsChange`            | siehe [Berechtigungen](/de/docs/sdk/guides/permissions/)                           |
| `storageChange`                | eine Speicherzuordnung änderte sich oder ihr Pool wurde gesperrt oder kam zurück   |
| `backendStatus`                | das Backend ist `stopped`, `starting`, `running`, `updating` oder `unavailable`    |
| `logLevelChange`               | ein Administrator schaltete das Debug-Logging der App um                           |
| `appUpdated`                   | eine neue Version ist installiert; neu laden, um sie zu nutzen, nach dem Speichern |
| `launch`, `notificationAction` | siehe oben                                                                         |

`app.on` liefert eine Funktion, die den Handler wieder entfernt. Handler laufen in der Reihenfolge, in der sie hinzugefügt wurden. Ereignisse, die die App nicht kennt, werden ignoriert; neue Ereignisse brechen also keine App.

## Zustand speichern

- **`app.session.save(state)`** speichert bis zu 16 KB JSON für dieses Fenster mit dem Desktop-Layout. `app.session.restore()` liefert es nach einem Neuladen und auf einem anderen Gerät nach „Desktop holen von …“ zurück. Größerer Zustand scheitert mit [`app.storage.too_large`](/de/docs/reference/errors/#app.storage.too_large).
- **`beforeClose`** wird gefragt, wenn der Benutzer das Fenster schließt. Antworte `{ allow: false }`, um es offen zu halten, etwa um nach ungespeicherten Änderungen zu fragen, und ruf danach selbst `app.window.close()` auf, das nicht noch einmal fragt. Sobald die App `window.setDirty` aufgerufen hat, fragt der Desktop nur noch, solange das Fenster als geändert markiert ist, und schließt ein sauberes Fenster sofort. Der Desktop wartet 5 Sekunden und bietet dann an, das Fenster trotzdem zu schließen; ein Handler, der eine Ausnahme wirft, erlaubt das Schließen.
- **`beforeUnload`** kommt vor einer Abmeldung, einem Herunterfahren, einem Neustart oder einem Pithyx-Update (`reason`). Es lässt sich nicht ablehnen; der Desktop wartet 2 Sekunden.

## Sonderfälle

- Ereignisse, die passieren, während sich das Fenster verbindet, werden nicht nachgeliefert; der Startkontext enthält schon ihr Ergebnis.
- `visibilityChange` ist das Signal, Timer und Animationen anzuhalten: Der Frame eines minimierten Fensters bleibt für den Browser sichtbar.
- Nach `connectionChange` mit `online: false` scheitern Aufrufe, die die Box brauchen, bis sie zurück ist. Versuche es nicht in einer Schleife; warte auf `online: true`.
- `app.window.setTitle` nimmt höchstens 200 Zeichen; ein längerer Titel scheitert mit [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid). Fensteraufrufe wirken nur auf das aufrufende Fenster.
- Der Mock-Desktop von `pithyx dev` startet immer mit `launch: { args: null }`; sende `launch`, `notificationAction` oder jedes andere Ereignis mit eigenen Daten aus seinem Panel.

## Fehlercodes

[`app.storage.too_large`](/de/docs/reference/errors/#app.storage.too_large), [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout), [`sdk.transport.closed`](/de/docs/reference/errors/#sdk.transport.closed).
