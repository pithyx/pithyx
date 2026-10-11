---
title: '@pithyx/sdk'
description: 'Die App-API: Identität, Dateien, Speicher, das Backend, Ereignisse, Logs und Berechtigungen.'
status: ready
audience: developer
sidebar:
  order: 2
---

`@pithyx/sdk` ist die API, mit der eine App mit dem Desktop spricht: `connect()` liefert ein `PithyxApp`-Objekt mit dem Benutzer- und Fensterkontext, Benachrichtigungen, Speicher pro Benutzer, freigegebenen Ordnern, dem Backend, Ereignissen und Logs. Es funktioniert in jedem Framework. Jede Methode wird vom Daemon auf Berechtigungen geprüft, und jeder Fehlschlag weist mit einem `PithyxError` ab, der einen stabilen Code trägt.

Die [API-Referenz](/de/docs/reference/sdk/) wird aus dem TSDoc erzeugt und listet jeden Export mit seinen Parametern, der Berechtigung, die er braucht, und den Fehlercodes, mit denen er ablehnt, eine Seite pro Modul: [App](/de/docs/reference/sdk/app/), [Dateien](/de/docs/reference/sdk/files/), [Backend](/de/docs/reference/sdk/backend/), [Ereignisse und Kontext](/de/docs/reference/sdk/events-context/), [i18n](/de/docs/reference/sdk/i18n/), [Theme](/de/docs/reference/sdk/theme/), [Host](/de/docs/reference/sdk/host/) und [Fehler](/de/docs/reference/sdk/errors/).

## Erste Schritte

```ts
import { connect } from '@pithyx/sdk';

const app = await connect();
console.log(app.context.user.displayName);
```

`connect()` wartet auf die Nachricht `pithyx:connect` des Desktops mit einem `MessagePort` von `window.parent`. Kommt innerhalb von 10 Sekunden nichts an (zum Beispiel weil die Seite außerhalb des Desktops geöffnet wurde), weist es mit `sdk.transport.closed` ab. Optionen: `themeRoot` (das Element, das die Theme-Eigenschaften erhält, `null` zum Überspringen), `timeout` (Aufruflimit in Millisekunden), `connectTimeout` (wie lange auf den Desktop gewartet wird, standardmäßig 10 Sekunden) und `origin` (die Box-Adresse für Backend-URLs).

- **Theme.** Das SDK setzt die CSS-Custom-Properties `--pithyx-color-background`, `-surface`, `-surface-raised`, `-hover`, `-selected`, `-text`, `-text-muted`, `-border`, `-accent`, `-accent-text`, `-focus`, `-success`, `-warning`, `-danger`, dazu `--pithyx-font-ui`, `--pithyx-font-mono`, `--pithyx-font-size`, `--pithyx-radius`, `--pithyx-space` und `--pithyx-control-height`. Es setzt außerdem `color-scheme`, `data-pithyx-theme` und `data-pithyx-density` und folgt Theme-Wechseln.
- **Zeitlimits.** Ein Aufruf schlägt nach 15 Sekunden mit `sdk.request.timeout` fehl; `files.upload`, `read`, `write`, `pick` und `saveAs` haben kein Zeitlimit.
- **Konstanten.** `SDK_VERSION`, `PROTOCOL_VERSION` und `MIN_PROTOCOL_VERSION`.
- `app.supports(method)` sagt, ob der Desktop eine Methode kennt. `app.on(event, handler)` liefert eine Funktion zum Abmelden.

## Leitfäden

- [Dateien und App-Speicher](/de/docs/sdk/guides/files-and-storage/)
- [Das Backend aufrufen](/de/docs/sdk/guides/backend/)
- [Ereignisse, Fensterkontext und Startargumente](/de/docs/sdk/guides/events-and-context/)
- [Berechtigungen und permissionsChange](/de/docs/sdk/guides/permissions/)
- [Sprachen und Darstellung in einer App](/de/docs/sdk/guides/i18n-and-theming/)
- [Logging und Fehler](/de/docs/sdk/guides/logging-and-errors/)
- [Testen mit pithyx dev und dem Mock-Desktop](/de/docs/sdk/guides/testing/)

## App und Benutzer

`app.context` wird durch Ereignisse aktuell gehalten und enthält die App (`id`, `version`), die Fensterinstanz, den Benutzer (`id`, `name`, `displayName`, `groups`, `isAdmin`, `twoFactor`), `language`, `locale` (zum Beispiel `de-AT`), `timeZone`, `theme`, `sizeClass` (`compact` unter 600 px, `medium` bis 1023 px, `expanded`), Startargumente, `permissions`, `capabilities`, die Box (`name`, `version`), `visible`, `online`, `logLevel`, den Status und die Endpunkte des Backends und den Zustand jeder Speicheranforderung.

- `app.window`: `setTitle` (bis zu 200 Zeichen), `close`, `setSize`, `setDirty(true)` (fragt die App vor dem Schließen) und `requestAttention`. Sie wirken nur auf das eigene Fenster der App.
- `app.notify({ title, body?, level?, actions?, data?, tag? })` braucht `notifications`. Der Titel hat 1 bis 200 Zeichen, der Text bis zu 1000, höchstens 2 Aktionen. Liefert `{ id }`.
- `app.setBadge(count | null)`, `app.openUrl(url)` (http, https oder mailto, in einem neuen Tab), `app.clipboard.writeText`, `app.contextMenu.show` und `app.ui.showError(error)`.
- `app.directory.users()` und `groups()` brauchen `directory.read`.
- `app.session.save(state)` und `restore()` behalten bis zu 16 KB JSON über Neuladen hinweg und ziehen mit dem Desktop-Layout um.
- `app.format` hat `date`, `time`, `dateTime`, `relative`, `number`, `bytes` und `list`, nach Sprache und Zeitzone des Benutzers.
- `@pithyx/sdk/i18n`: `createI18n(app, { en, de, ... })` liefert `{ language, t(key, params) }` mit ICU-Meldungen. Es wählt die genaue Locale, dann die Basissprache, dann `en`.

## Dateien und Speicher

**Speicher pro Benutzer.** `app.storage.get(key)`, `set(key, value)`, `remove(key)` und `keys()`. Schlüssel haben bis zu 128 Zeichen, Werte bis zu 64 KB JSON, und jeder Benutzer bekommt 1 MB pro App. Das ersetzt `localStorage`, das eine App in der Sandbox nicht nutzen kann. Fehler: Ein leerer oder zu langer Schlüssel scheitert schon im Desktop mit [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), bevor er die Box erreicht, die für andere ungültige Schlüssel [`app.storage.key_invalid`](/de/docs/reference/errors/#app.storage.key_invalid) antwortet; [`app.storage.too_large`](/de/docs/reference/errors/#app.storage.too_large).

**Übergebene Dateien** (`files.read` und `files.write`): `app.files.pick({ multiple?, extensions? })`, `saveAs(suggestedName, blob)`, `read(ref)`, `write(ref, blob)` und `url(ref)` (5 Minuten gültig). Ein `FileRef` hat `ref`, `name`, `type`, `size`, `modified` und `writable`. Auswählen und Speichern müssen einer Benutzeraktion folgen.

**Ordner** (`FileLocation` ist `{ storage: id }` für eine Speicheranforderung im Manifest oder `{ share: id }` für einen freigegebenen Ordner, was `files.shares` braucht): `open(location, path)` (ein `FileRef` für eine Datei), `shares()`, `list(location, path, { cursor, limit, sort, order })` (standardmäßig 100 Einträge, höchstens 1000), `watch`, `upload`, `download`, `createFolder`, `rename`, `run(operation)` für Kopieren, Verschieben und Löschen als Job (Löschen geht in den Papierkorb, außer bei `permanent`), `versions` und `restoreVersion`. Hochladen über einen vorhandenen Namen schlägt mit `fs.entry.exists` fehl. `app.trash` listet den Papierkorb, stellt wieder her, löscht und leert ihn, und `app.jobs.cancel(id)` bricht einen Job ab.

:::note[Dateien in der Sandbox]
Eine App aus einem Paket oder einem Katalog bekommt jeden Dateiaufruf des SDK. `pick` und `saveAs` öffnen den Dateidialog des Desktops und brauchen eine Benutzeraktion im Fenster; eine Datei, die der Benutzer auswählt oder mit der App öffnet, lässt sich ohne `files.shares` lesen. `read`, `write` und `upload` haben keine Zeitgrenze. Der [Leitfaden](/de/docs/sdk/guides/files-and-storage/) hat die Einzelheiten.
:::

## Backend

- `app.backend.fetch(path, init)` ist `fetch` mit hinzugefügtem Bearer-Token; es löst mit jedem Status auf und weist mit `sdk.backend.unreachable` ab, wenn die Box nicht erreichbar ist.
- `app.backend.url(path)` liefert eine URL mit `?pithyx-token=`.
- `app.backend.socket(path)` öffnet einen WebSocket mit einem Einmal-Ticket (`wss` bei HTTPS, `ws` bei HTTP).
- `app.backend.token()` liefert das Token und sein Ablaufdatum.
- Jede akzeptiert `{ endpoint: 'name' }`; der Standard ist `api`. Ein unbekannter Endpunkt ist `sdk.backend.endpoint_unknown`. Endpunktnamen im SDK dürfen länger sein als die 32 Zeichen, die die Box in einem Manifest akzeptiert; nutze bis zu 32.
- `app.context.backend.status` und das Ereignis `backendStatus` melden `stopped`, `starting`, `running`, `updating` oder `unavailable`.

Siehe [Backends](/de/docs/developers/apps/backend/) für Routen und Header.

## Ereignisse

Abonniere mit `app.on(name, handler)`: `launch`, `focus`, `blur`, `minimize`, `restore`, `themeChange`, `localeChange`, `sizeClassChange`, `openFile`, `appUpdated` (neu laden, um die neue Version zu nutzen), `visibilityChange`, `connectionChange`, `backendStatus`, `permissionsChange` (die vollständige Liste), `storageChange`, `notificationAction`, `logLevelChange`, `sharesChanged`, `folderChanged`, `uploadProgress` und `jobChanged`.

Beantworte Anfragen mit `app.answer(query, handler)`:

- `beforeClose` liefert `{ allow: boolean }`. Es wird nur gefragt, solange `setDirty(true)` gilt; der Desktop wartet 5 Sekunden und bietet dann an, trotzdem zu schließen.
- `beforeUnload` (Grund `logout`, `shutdown`, `restart` oder `update`) kann nicht abgelehnt werden und bekommt 2 Sekunden.

## Logs

`app.log.debug`, `info`, `warn`, `error` und `log(message, data?)` werfen nie. Jeder Eintrag geht auch in die Browser-Konsole. `info`, `warn` und `error` gehen an den Daemon; `debug` nur, solange ein Administrator das Debug-Logging für die App eingeschaltet hat (`context.logLevel`). Einträge erscheinen im Protokoll-Center unter der Quelle `app/<id>` mit dem angemeldeten Benutzer.

Eine Meldung wird bei 4000 Zeichen abgeschnitten; `data` enthält bis zu 20 Felder mit Schlüsseln bis zu 100 und Werten bis zu 1000 Zeichen. Einträge werden gebündelt und ratenbegrenzt (eine Spitze von 200, dann 20 pro Sekunde; der Rest wird verworfen). Logge nie Geheimnisse.

## Fehler

Fehlgeschlagene Aufrufe weisen mit `PithyxError` ab (`code`, `params`, `detail`, `requestId`). `isProblem(value)` prüft auf die Form. Probleme des Daemons werden unverändert durchgereicht. Die SDK-Transportcodes sind `sdk.method.unknown`, `sdk.request.invalid`, `sdk.request.timeout`, `sdk.transport.closed`, `sdk.protocol.unsupported`, `sdk.transfer.cancelled`, `sdk.backend.endpoint_unknown` und `sdk.backend.unreachable`. Apps übersetzen Codes selbst; `detail` ist ein englischer Fallback. Häufige Daemon-Codes sind `app.permission.denied`, `app.permission.not_requested`, `app.token.invalid`, `app.backend.unavailable` und die `fs.*`-Codes. Siehe die [Fehlerreferenz](/de/docs/reference/errors/).
