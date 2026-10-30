---
title: 'Testen mit pithyx dev und dem Mock-Desktop'
description: 'Eine App im Mock-Desktop von pithyx dev ausführen, Benutzer, Darstellung, Berechtigungen und Ereignisse im Panel ändern und App-Code gegen einen falschen Desktop testen.'
status: ready
audience: developer
sidebar:
  order: 7
---

Auf der Box gibt es keinen Entwicklungsmodus: Du entwickelst und testest auf deinem eigenen Rechner. `pithyx dev` (oder `vite` mit dem Pithyx-Plugin) startet einen Mock-Desktop, der dein Frontend in der Sandbox der Box mit den Headern der Box öffnet, das echte SDK-Protokoll spricht und dein Backend mit denselben Headern und Tokens wie die Box anbindet. Sein Panel wechselt Benutzer, Darstellung, Sprache, Berechtigungen und Backend-Zustand und sendet jedes Ereignis. Für Code ohne Browser verbindet der In-Process-Transport des SDK deinen App-Code in einem Unit-Test mit einem falschen Desktop.

## Den Mock-Desktop starten

```sh
npx pithyx dev                                       # das gebaute Frontend in dist/, Port 7400
npx pithyx dev --endpoint api=http://localhost:8080  # dein eigener Backend-Server statt Docker
npx pithyx dev --no-docker                           # kein Backend; seine Endpunkte antworten mit 503
```

Öffne `http://127.0.0.1:7400/`. Nach einer Änderung baust du das Frontend neu und lädst neu; mit dem Vite-Plugin liefert `vite` denselben Mock-Desktop mit Hot Reload. Alle Optionen stehen auf der [CLI-Seite](/de/docs/sdk/cli/#pithyx-dev).

## Was das Panel ändert

- **Benutzer:** alice (Administratorin, Gruppe family), bob (Gruppen family und office) und carol (keine Gruppe). Ein Wechsel fragt die App `beforeUnload` mit `logout`, beendet die Tokens des alten Benutzers und öffnet das Fenster neu.
- **Darstellung, Akzent, Locale und Zeitzone:** sendet `themeChange` oder `localeChange`. Locales sind `en-US`, `en-GB`, `de-DE`, `de-AT`, `fr-FR` und `ja-JP`.
- **Größenklasse:** `compact`, `medium` oder `expanded`, mit `sizeClassChange`.
- **Online und Debug-Log:** `connectionChange` und `logLevelChange`.
- **Backend-Zustand und Speicherzustände:** `backendStatus` (auch `unavailable` mit den Gründen `storage` und `pool`) und `storageChange`.
- **Berechtigungen:** ein Schalter pro Berechtigung des Manifests, jeder sendet `permissionsChange`.
- **Fenster:** schließen (fragt `beforeClose`), neu laden, `focus`, `blur`, eine Datei öffnen (`openFile`), die vier Gründe von `beforeUnload` und minimieren in der Titelleiste.
- **Ereignisse:** jedes Ereignis mit bearbeitbaren Beispieldaten, um zu senden, was die Box senden würde.
- **Log:** Methodenaufrufe, Ereignisse, Benachrichtigungen und deine `app.log`-Einträge (`debug` nur, solange das Debug-Log an ist).

## Wo der Mock von der Box abweicht

| Bereich         | Mock-Desktop                                                                                                                                                                                                                                          | Box                                                                                         |
| --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `app.storage`   | im Browser des Mock-Desktops, pro Mock-Benutzer; prüft 64 KB pro Wert, kein Limit von 1 MB                                                                                                                                                            | im Daemon, pro Benutzer, 64 KB pro Wert und insgesamt 1 MB                                  |
| Dateien         | `pick` und `saveAs` (solange sein Frame den Fokus hat), `read` und `write` auf ausgewählten Dateien; keine Ordner-Aufrufe wie `files.shares`, `list`, `run` oder `upload` (`sdk.method.unknown`): teste sie gegen einen Fake-Desktop oder auf der Box | jeder Dateiaufruf, siehe [Dateien und App-Speicher](/de/docs/sdk/guides/files-and-storage/) |
| `ui.showError`  | ins Log geschrieben                                                                                                                                                                                                                                   | der Fehlerdialog des Desktops                                                               |
| `session.save`  | im Session Storage des Tabs, ohne Größenprüfung                                                                                                                                                                                                       | mit dem Desktop-Layout, höchstens 16 KB                                                     |
| Backend-Zustand | das Panel sendet nur das Ereignis; der Proxy leitet weiter                                                                                                                                                                                            | ein gestopptes Backend wacht für eine Anfrage auf; bei updating oder unavailable 503        |
| Start           | immer `{ args: null }`; sende `launch` aus dem Panel                                                                                                                                                                                                  | `{ args: null }` oder `launch.notification` nach einem Klick auf eine Benachrichtigung      |
| Tokens          | gültig 5 Minuten, Tickets 30 Sekunden und einmal, wie auf der Box                                                                                                                                                                                     | dasselbe, und bei jeder Nutzung gegen die Sitzung und die Freigabe der App geprüft          |

Prüfe `app.supports(method)` vor jedem Aufruf, der fehlen kann; dann weicht die App in beiden gleich aus.

## Unit-Tests gegen einen falschen Desktop

`createApp` verbindet App-Code über ein beliebiges Transportende; `inProcessPair` liefert zwei verbundene Enden in einem Prozess, und `serveApp` aus `@pithyx/sdk/host` ist die Desktop-Seite des Protokolls. Der Desktop nutzt es für jedes Fenster; in einem Test bedienst du damit genau die Methoden und den Kontext, die du willst, mit den Parameter- und Berechtigungsprüfungen des SDK. Dieser Test läuft mit `node --test` und prüft die Funktion `people` aus dem [Leitfaden zu Berechtigungen](/de/docs/sdk/guides/permissions/):

<!-- example: sdk/testing.test.ts -->

```ts
// A unit test without a browser: the app's code talks to a fake desktop over the SDK's in-process transport, with the
// same protocol, parameter checks and permission checks as a real window. Run with `node --test`.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  createApp,
  inProcessPair,
  themeTokenNames,
  type AppContext,
  type PithyxApp,
  type Theme,
} from '@pithyx/sdk';
import { serveApp, type AppConnection, type MethodHandlers } from '@pithyx/sdk/host';
import { people } from './permissions.ts';

/** A context like the desktop's init message, for a user without directory.read. */
function testContext(permissions: string[]): Omit<AppContext, 'capabilities'> {
  const theme: Theme = {
    scheme: 'light',
    density: 'comfortable',
    tokens: Object.fromEntries(themeTokenNames.map((name) => [name, '0'])) as Theme['tokens'],
  };
  return {
    app: { id: 'com.example.board', version: '1.0.0' },
    instance: 'test-1',
    user: { id: 2, name: 'bob', displayName: 'Bob', groups: [], isAdmin: false, twoFactor: false },
    language: 'en',
    locale: 'en-US',
    timeZone: 'UTC',
    theme,
    sizeClass: 'expanded',
    launch: { args: null },
    session: null,
    permissions,
    box: { name: 'test', version: '0.1.0' },
    visible: true,
    online: true,
    logLevel: 'info',
    backend: null,
    storage: [],
    host: { version: '1.0.0', protocol: 1 },
  };
}

/** Connects app code to a fake desktop that serves only the given methods. */
async function fakeDesktop(
  permissions: string[],
  methods: MethodHandlers,
): Promise<{ app: PithyxApp; desktop: AppConnection }> {
  const [appEnd, desktopEnd] = inProcessPair();
  const desktop = serveApp(desktopEnd, { context: testContext(permissions), methods });
  const app = await createApp(appEnd, { themeRoot: null });
  return { app, desktop };
}

const directory: MethodHandlers = {
  'directory.users': () => [
    { id: 1, name: 'alice', displayName: 'Alice', groups: [] },
    { id: 2, name: 'bob', displayName: 'Bob', groups: [] },
  ],
};

await test('without directory.read only the signed-in user can be picked', async () => {
  const { app } = await fakeDesktop([], directory);
  assert.deepEqual(await people(app), [{ id: 2, name: 'bob', displayName: 'Bob' }]);
});

await test('a granted directory.read lists everyone, a revoked one falls back', async () => {
  const { app, desktop } = await fakeDesktop(['directory.read'], directory);
  assert.equal((await people(app)).length, 2);

  desktop.emit('permissionsChange', { permissions: [] });
  // Events arrive asynchronously, like over a MessagePort.
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(app.context.permissions, []);
  assert.equal((await people(app)).length, 1);
});
```

- Gib `createApp` außerhalb eines Browsers `themeRoot: null` mit.
- Ereignisse und Antworten kommen asynchron an, wie über einen `MessagePort`: Warte nach `desktop.emit` einen Tick.
- Eine Methode, die du nicht bedienst, scheitert mit `sdk.method.unknown` und fehlt in `capabilities`, wie bei einem älteren Desktop.
- `@pithyx/sdk/host` ist die Seite des Desktops; nutze es nur in Tests, nie in der App.

## Sonderfälle

- Das Frontend direkt in einem Tab zu öffnen scheitert: `connect()` wartet 10 Sekunden auf einen Desktop und weist dann mit [`sdk.transport.closed`](/de/docs/reference/errors/#sdk.transport.closed) ab.
- Der Mock liefert die Content-Security-Policy der Box, also scheitert eine Anfrage an einen anderen Server ohne `network.internet` im Mock wie auf der Box.
- `pithyx dev` braucht eine gültige `pithyx.json`. Ein Backend läuft mit `docker compose up --wait --build` aus `backend/compose.dev.yaml`, falls vorhanden, sonst aus `backend/compose.yaml`, wobei der Port jedes Endpunkts für den Proxy auf `127.0.0.1` veröffentlicht wird; Strg+C führt `docker compose down` aus und behält die Volumes.
- Teste auf der Box, bevor du veröffentlichst: Installiere das Paket als Administrator mit **Aus Datei installieren** auf der Seite **Installiert** des Stores (siehe [Deine erste App](/de/docs/developers/apps/first-app/)) und prüfe die Aufrufe, die der Mock nicht bedient.

## Fehlercodes

[`sdk.method.unknown`](/de/docs/reference/errors/#sdk.method.unknown), [`sdk.transport.closed`](/de/docs/reference/errors/#sdk.transport.closed), [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid), [`app.backend.unavailable`](/de/docs/reference/errors/#app.backend.unavailable), [`request.route.not_found`](/de/docs/reference/errors/#request.route.not_found).
