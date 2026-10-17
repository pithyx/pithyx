---
title: 'Dateien und App-Speicher'
description: 'Kleine Werte pro Benutzer mit app.storage ablegen und Dateien in Speicheranfragen und freigegebenen Ordnern lesen und schreiben.'
status: ready
audience: developer
sidebar:
  order: 1
---

Eine App in der Sandbox hat kein `localStorage`, kein IndexedDB und kein Dateisystem. Kleine Werte pro Benutzer legt sie mit `app.storage` auf der Box ab, und Dateien erreicht sie nur über `app.files`: in einem Ordner, den ein Administrator einer Speicheranfrage des Manifests zugeordnet hat (`{ storage: id }`), oder in den freigegebenen Ordnern, die der Benutzer nutzen darf (`{ share: id }`, Berechtigung `files.shares`). Der Daemon prüft bei jedem Aufruf die Berechtigungen der App und die Rechte des Benutzers.

## Was wofür

| Bedarf                                                                 | Nimm                                                                                          |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Einstellungen, Entwürfe, kleiner Zustand pro Benutzer, auf jedem Gerät | `app.storage`                                                                                 |
| Zustand eines Fensters (Scrollposition, offener Tab)                   | `app.session`, siehe [Ereignisse und Fensterkontext](/de/docs/sdk/guides/events-and-context/) |
| Dateien der App an einem Ort, den der Administrator wählt              | eine Speicheranfrage in `pithyx.json` und `{ storage: id }`                                   |
| Die eigenen Dateien des Benutzers in einem freigegebenen Ordner        | `files.shares` und `{ share: id }`                                                            |
| Daten, die nur das Backend liest                                       | ein Compose-Volume, siehe [Backends](/de/docs/developers/apps/backend/#speicher)              |

## Beispiel

Eine Notizen-App speichert ihre Einstellungen pro Benutzer und die Notizen als Markdown-Dateien in einer Speicheranfrage. `pithyx.json` deklariert die Anfrage:

```json
"storage": [{ "id": "notes", "name": { "en": "Notes" }, "access": "readWrite", "required": false }]
```

<!-- example: sdk/files-and-storage.ts -->

```ts
// A notes app: settings per user in app.storage, notes as files in a storage request of the manifest.
// pithyx.json declares:
//   "storage": [{ "id": "notes", "name": { "en": "Notes" }, "access": "readWrite", "required": false }]
import { PithyxError, type FileEntry, type FileLocation, type PithyxApp } from '@pithyx/sdk';

const NOTES: FileLocation = { storage: 'notes' };

interface Settings {
  sort: 'name' | 'modified';
  lastOpened: string | null;
}

const DEFAULTS: Settings = { sort: 'name', lastOpened: null };

/** The user's settings; a key nobody stored yet reads as null. */
export async function loadSettings(app: PithyxApp): Promise<Settings> {
  const stored = await app.storage.get<Partial<Settings>>('settings');
  return { ...DEFAULTS, ...stored };
}

export async function saveSettings(app: PithyxApp, settings: Settings): Promise<void> {
  try {
    await app.storage.set('settings', settings);
  } catch (error) {
    // 64 KB per value, 1 MB per user and app; params.maxKb names the limit that was hit.
    if (error instanceof PithyxError && error.code === 'app.storage.too_large') {
      app.log.warn('Settings not saved', { maxKb: String(error.params.maxKb) });
      return;
    }
    throw error;
  }
}

/** Whether the notes folder can be used now: mapped by an administrator, its pool unlocked and present. */
export function notesReady(app: PithyxApp): boolean {
  return app.context.storage.find((storage) => storage.id === 'notes')?.state === 'ready';
}

export function notesWritable(app: PithyxApp): boolean {
  return app.context.storage.find((storage) => storage.id === 'notes')?.access === 'readWrite';
}

/** Every note in the folder, page by page (at most 1000 entries per page). */
export async function listNotes(app: PithyxApp, sort: Settings['sort']): Promise<FileEntry[]> {
  const notes: FileEntry[] = [];
  let cursor: string | null = null;
  do {
    const page = await app.files.list(NOTES, '', {
      limit: 1000,
      sort,
      order: sort === 'modified' ? 'desc' : 'asc',
      ...(cursor === null ? {} : { cursor }),
    });
    notes.push(
      ...page.entries.filter((entry) => entry.kind === 'file' && entry.name.endsWith('.md')),
    );
    cursor = page.nextCursor;
  } while (cursor !== null);
  return notes;
}

export async function readNote(app: PithyxApp, name: string): Promise<string> {
  const file = await app.files.open(NOTES, name);
  return (await app.files.read(file)).text();
}

/** Writes a note, replacing an existing one of the same name. */
export async function writeNote(app: PithyxApp, name: string, text: string): Promise<void> {
  const data = new Blob([text], { type: 'text/markdown' });
  await app.files.upload(NOTES, '', name, data, { overwrite: true });
}

/** Creates the archive folder unless it exists already. */
export async function ensureArchive(app: PithyxApp): Promise<void> {
  try {
    await app.files.createFolder(NOTES, 'Archive');
  } catch (error) {
    if (!(error instanceof PithyxError && error.code === 'fs.entry.exists')) {
      throw error;
    }
  }
}

/** Opens a note and remembers it for this user, on every device. */
export async function openNote(app: PithyxApp, settings: Settings, name: string): Promise<string> {
  const text = await readNote(app, name);
  await saveSettings(app, { ...settings, lastOpened: name });
  return text;
}

/** Wires it together: list the notes, and again whenever the mapping or its pool changes. */
export async function start(
  app: PithyxApp,
  render: (notes: FileEntry[] | null) => void,
): Promise<Settings> {
  const settings = await loadSettings(app);
  if (!app.supports('files.list')) {
    // An older desktop, or the mock desktop of pithyx dev, which serves no folder calls.
    render(null);
    return settings;
  }
  const refresh = async () => {
    render(notesReady(app) ? await listNotes(app, settings.sort) : null);
  };
  app.on('storageChange', () => {
    void refresh();
  });
  await refresh();
  return settings;
}
```

## Speicher pro Benutzer

`app.storage.get(key)` liefert den gespeicherten Wert oder `null`, `set(key, value)` ersetzt ihn, `remove(key)` löscht ihn (ein fehlender Schlüssel ist kein Fehler) und `keys()` listet die Schlüssel nach Namen sortiert. Werte sind JSON: `undefined` wird als `null` gespeichert, ein `Date` kommt als Text zurück. Jeder Benutzer hat seine eigenen Werte; andere Benutzer, andere Apps und das Backend der App können sie nicht lesen. Es braucht keine Berechtigung.

**Grenzen:**

- Schlüssel haben 1 bis 128 Zeichen. Einen leeren oder längeren Schlüssel lehnt schon der Desktop ab, bevor er die Box erreicht, mit [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid).
- Ein Wert hat höchstens 64 KB als UTF-8-JSON, und Schlüssel plus Werte eines Benutzers und einer App höchstens 1 MB. Beides scheitert mit [`app.storage.too_large`](/de/docs/reference/errors/#app.storage.too_large); `params.maxKb` ist 64 oder 1024.
- Ein Aufruf scheitert nach 15 Sekunden mit [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout).

## Dateien in einer Speicheranfrage

Eine Speicheranfrage benennt einen Ort, den die App braucht. Bei der Installation ordnet ein Administrator ihr einen Ordner in einem freigegebenen Ordner zu und kann das später ändern. Die App erfährt nie, wo der Ordner liegt; sie nutzt Pfade darin.

- `app.context.storage` listet jede Anfrage mit `state` (`ready`, `unmapped`, `locked`, `unavailable`) und `access` (`read`, `readWrite` oder `null`, solange nichts zugeordnet ist). Das Ereignis `storageChange` ersetzt die Liste, wenn sich eine Zuordnung ändert oder ein Pool gesperrt wird oder zurückkommt.
- Eine Speicheranfrage braucht keine `files.*`-Berechtigung. Die App bekommt nie mehr, als `access` der Anfrage und die Zuordnung erlauben, und die eigenen Rechte des Benutzers auf den Ordner gelten weiterhin.
- Eine nicht zugeordnete Anfrage scheitert mit [`app.storage.not_found`](/de/docs/reference/errors/#app.storage.not_found). Schreiben, wo Zuordnung oder Benutzer nur Lesen erlauben, scheitert mit [`fs.share.read_only`](/de/docs/reference/errors/#fs.share.read_only). Ein gesperrter oder fehlender Pool scheitert mit [`fs.share.unavailable`](/de/docs/reference/errors/#fs.share.unavailable).
- Eine Pflichtanfrage (`required`) mit `services` verhindert den Start des Backends, bis sie zugeordnet ist: `app.context.backend` ist dann `unavailable` mit `reason: 'storage'`. Eine optionale darf `unmapped` bleiben, prüfe also zuerst den Zustand.

## Dateien in freigegebenen Ordnern

`app.files.shares()` listet die freigegebenen Ordner, die der Benutzer nutzen darf, mit seinem `access` und, solange der Pool nicht erreichbar ist, `unavailable`. Einen freigegebenen Ordner zu listen braucht `files.shares`; den Inhalt einer Datei zu lesen zusätzlich `files.read`, zu schreiben zusätzlich `files.write`. Ein freigegebener Ordner ohne Zugriff des Benutzers scheitert mit [`fs.share.not_found`](/de/docs/reference/errors/#fs.share.not_found).

## Dateiaufrufe eines Sandbox-Fensters

Eine App in der Sandbox bekommt jeden Dateiaufruf des SDK. Der Daemon prüft bei jedem die Berechtigungen der App und die eigenen Rechte des Benutzers. Die meisten Aufrufe nennen einen Ort (`{ storage: id }` oder `{ share: id }`):

| Aufruf                                          | Was er tut                                                                                                 |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| `files.shares()`                                | die freigegebenen Ordner des Benutzers                                                                     |
| `files.list(location, path, page)`              | eine Seite eines Ordners: standardmäßig 100 Einträge, höchstens 1000, natürliche Namensordnung ohne `sort` |
| `files.open(location, path)`                    | eine `FileRef` für eine Datei (ein Ordner scheitert mit `sdk.request.invalid`)                             |
| `files.read(ref)`, `files.write(ref, blob)`     | der Inhalt einer geöffneten Datei; `write` ersetzt ihn immer                                               |
| `files.upload(location, folder, name, blob, o)` | eine neue Datei; ein vorhandener Name scheitert mit `fs.entry.exists`, außer mit `overwrite: true`         |
| `files.createFolder(location, path)`            | ein Ordner; sein Elternordner muss existieren                                                              |
| `files.rename(location, path, name)`            | benennt im Ordner um; ein vorhandener Name wird nie ersetzt                                                |
| `files.url(ref)`                                | eine URL für `<img>`, `<video>` oder `fetch` mit Range-Anfragen, 5 Minuten gültig, eine Datei              |
| `files.download(location, path)`                | ein Download durch den Browser, mit Fortschritt in der Taskleiste                                          |
| `files.watch(location, path)` und `unwatch`     | `folderChanged`-Ereignisse, solange ein Ordner zu sehen ist                                                |

**Dialoge.** `files.pick({ multiple, extensions })` (braucht `files.read`) und `files.saveAs(suggestedName, blob)` (braucht `files.write`) öffnen den Dateidialog des Desktops. Der Dialog öffnet sich nur, solange das Fenster der App den Fokus hat (das Fenster, in dem der Benutzer arbeitet); sonst liefert `pick` `[]` und `saveAs` `null`. Ein frischer Klick ist nicht nötig: Ein Aufruf nach einem `await` in einem Klick-Handler funktioniert, solange der Benutzer nicht inzwischen in ein anderes Fenster gewechselt ist. Der Mock-Desktop von `pithyx dev` folgt derselben Regel (sein Frame muss den Fokus haben) und protokolliert einen abgelehnten Aufruf. Der Dialog zeigt die Freigaben des Benutzers; die App erfährt nur die Dateien, die der Benutzer wählt, und kann genau diese ohne `files.shares` lesen (und mit `files.write` schreiben). Dasselbe gilt für die Datei, die ein Benutzer in der Dateien-App mit der App öffnet (`launch.file`, `openFile`).

**Die Dateiverwaltungs-Aufrufe** brauchen `files.shares` und arbeiten auf Freigaben: `files.run(operation)` für Kopieren, Verschieben und Löschen als Auftrag (`files.read` und `files.write` für Kopieren und Verschieben, `files.write` für Löschen), `files.versions` und `files.restoreVersion` (`files.read`, Wiederherstellen auch `files.write`), `trash.list` (`files.read`) sowie `trash.restore`, `trash.delete` und `trash.empty` (`files.write`). Sie nehmen nur eine Freigabe (`ShareLocation`): Eine Speicheranfrage als Quelle oder Ziel scheitert mit [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid). Um eine Datei aus einer Speicheranfrage in eine Freigabe zu kopieren, öffne und lies sie (`files.open`, `files.read`) und lade sie dort mit `files.upload` hoch. `jobChanged`-Ereignisse begleiten einen Auftrag; `jobs.cancel(id)` bricht nur einen Auftrag ab, den diese App für den Benutzer gestartet hat. `app.ui.showError(error)` zeigt den Fehlerdialog des Desktops. `sharesChanged` bekommt eine App mit `files.shares`.

Prüfe mit `app.supports(method)`, bevor du eine Funktion anbietest, die dem Desktop fehlen kann: Der Mock-Desktop von `pithyx dev` bedient weniger Aufrufe als die Box.

## Sonderfälle

- **Pfade** sind relativ zum Ort, mit `/` zwischen den Namen und `""` für die Wurzel. Ein führender oder doppelter Schrägstrich, `.`, `..` oder ein Pfad über 4096 Zeichen wird mit `sdk.request.invalid` abgelehnt. Ein Name hat 1 bis 255 Zeichen und kein `/`.
- **Datei-Handles** gehören dem Fenster, das sie geöffnet hat, und enden mit ihm. Speichere Pfade, nie eine `FileRef`; eine veraltete scheitert mit `sdk.request.invalid`.
- **Zeitgrenzen.** Jeder Aufruf scheitert nach 15 Sekunden mit `sdk.request.timeout`, außer denen, die so lange dauern wie eine Übertragung oder der Benutzer: `files.upload`, `files.read`, `files.write`, `files.pick` und `files.saveAs`.
- **Upload-Fortschritt.** Mit einer `upload`-ID sendet `files.upload` Ereignisse `uploadProgress` mit den gesendeten Bytes, und der Upload erscheint mit einem Abbrechen-Knopf in der Taskleiste.
- **Seiten.** Ein `limit` über 1000 wird auf 1000 gekürzt. Gib `nextCursor` als `cursor` für die nächste Seite mit; er bleibt gültig, während sich der Ordner ändert. Symbolische Links werden als `symlink` gelistet und nie verfolgt.
- **Mock-Desktop.** `pithyx dev` bedient nur `files.pick`, `files.saveAs`, `files.read` und `files.write` (für ausgewählte Dateien) und hält `app.storage` im Browser des Mock-Desktops. Siehe [Testen](/de/docs/sdk/guides/testing/).

## Fehlercodes

[`app.storage.too_large`](/de/docs/reference/errors/#app.storage.too_large), [`app.storage.not_found`](/de/docs/reference/errors/#app.storage.not_found), [`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied), [`fs.entry.exists`](/de/docs/reference/errors/#fs.entry.exists), [`fs.entry.not_found`](/de/docs/reference/errors/#fs.entry.not_found), [`fs.path.invalid`](/de/docs/reference/errors/#fs.path.invalid), [`fs.share.not_found`](/de/docs/reference/errors/#fs.share.not_found), [`fs.share.read_only`](/de/docs/reference/errors/#fs.share.read_only), [`fs.share.unavailable`](/de/docs/reference/errors/#fs.share.unavailable), [`sdk.method.unknown`](/de/docs/reference/errors/#sdk.method.unknown), [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout).
