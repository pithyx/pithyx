---
title: 'Files and app storage'
description: 'Keep small values per user with app.storage, and read and write files in storage requests and shared folders.'
status: ready
audience: developer
sidebar:
  order: 1
---

A sandboxed app has no `localStorage`, IndexedDB or file system. It keeps small values per user with `app.storage`, on the box, and reaches files only through `app.files`: in a folder an administrator mapped to one of the manifest's storage requests (`{ storage: id }`), or in the shared folders the user can use (`{ share: id }`, permission `files.shares`). The daemon checks the app's permissions and the user's own rights on every call.

## What to use when

| Need                                                     | Use                                                                                  |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| Settings, drafts, small state per user, on every device  | `app.storage`                                                                        |
| State of one window (scroll position, open tab)          | `app.session`, see [Events and window context](/docs/sdk/guides/events-and-context/) |
| Files the app owns, in a place the administrator chooses | a storage request in `pithyx.json` and `{ storage: id }`                             |
| The user's own files in any shared folder                | `files.shares` and `{ share: id }`                                                   |
| Data only the backend reads                              | a Compose volume, see [Backends](/docs/developers/apps/backend/#storage)             |

## Example

A notes app keeps its settings per user and its notes as Markdown files in a storage request. `pithyx.json` declares the request:

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

## Per-user storage

`app.storage.get(key)` returns the stored value or `null`, `set(key, value)` replaces it, `remove(key)` deletes it (a missing key is no error) and `keys()` lists the keys sorted by name. Values are JSON: `undefined` is stored as `null`, a `Date` comes back as a string. Each user has their own values; other users, other apps and the app's backend cannot read them. No permission is needed.

**Limits:**

- Keys have 1 to 128 characters. An empty or longer key is refused by the desktop before it reaches the box, with [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid).
- A value is at most 64 KB as UTF-8 JSON, and keys plus values of one user and app are at most 1 MB. Both fail with [`app.storage.too_large`](/docs/reference/errors/#app.storage.too_large); `params.maxKb` is 64 or 1024.
- A call fails with [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout) after 15 seconds.

## Files in a storage request

A storage request names a place the app needs. At install an administrator maps it to a folder in a shared folder and can change that later. The app never learns where the folder is; it uses paths inside it.

- `app.context.storage` lists each request with its `state` (`ready`, `unmapped`, `locked`, `unavailable`) and `access` (`read`, `readWrite` or `null` while unmapped). The `storageChange` event replaces the list when a mapping changes or a pool is locked or comes back.
- A storage request needs no `files.*` permission. The app never gets more than the request's `access` and the mapping allow, and the user's own rights on the folder still apply.
- An unmapped request fails with [`app.storage.not_found`](/docs/reference/errors/#app.storage.not_found). Writing where the mapping or the user allows only reading fails with [`fs.share.read_only`](/docs/reference/errors/#fs.share.read_only). A locked or missing pool fails with [`fs.share.unavailable`](/docs/reference/errors/#fs.share.unavailable).
- A required request with `services` keeps the backend from starting until it is mapped: `app.context.backend` is then `unavailable` with `reason: 'storage'`. An optional one may stay `unmapped`, so check the state first.

## Files in shared folders

`app.files.shares()` lists the shared folders the user can use, with the user's `access` and, while its pool cannot be reached, `unavailable`. Listing a shared folder needs `files.shares`; reading a file's content also needs `files.read`, writing also `files.write`. A shared folder the user has no access to fails with [`fs.share.not_found`](/docs/reference/errors/#fs.share.not_found).

## File calls of a sandboxed window

A sandboxed app gets every file call of the SDK. The daemon checks the app's permissions and the user's own rights on each of them. Most calls name a place (`{ storage: id }` or `{ share: id }`):

| Call                                            | What it does                                                                                                                                                           |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `files.shares()`                                | the shared folders of the user                                                                                                                                         |
| `files.list(location, path, page)`              | one page of a folder: default 100 entries, at most 1000, natural name order unless `sort`; `query` keeps the names that contain it, ignoring case, in this folder only |
| `files.open(location, path)`                    | a `FileRef` for a file (a folder fails with `sdk.request.invalid`)                                                                                                     |
| `files.read(ref)`, `files.write(ref, blob)`     | the content of an opened file; `write` always replaces it                                                                                                              |
| `files.upload(location, folder, name, blob, o)` | a new file; an existing name fails with `fs.entry.exists` unless `overwrite: true`                                                                                     |
| `files.createFolder(location, path)`            | a folder; its parent must exist                                                                                                                                        |
| `files.rename(location, path, name)`            | renames within the folder; an existing name is never replaced                                                                                                          |
| `files.url(ref)`                                | a URL for `<img>`, `<video>` or `fetch` with range requests, valid 5 minutes, one file                                                                                 |
| `files.download(location, path)`                | a download through the browser, with progress in the taskbar                                                                                                           |
| `files.watch(location, path)` and `unwatch`     | `folderChanged` events while a folder is on screen                                                                                                                     |

**Dialogs.** `files.pick({ multiple, extensions })` (needs `files.read`) and `files.saveAs(suggestedName, blob)` (needs `files.write`) open the desktop's file dialog. The dialog opens only while the app's window has the focus (the window the user works in); otherwise `pick` resolves with `[]` and `saveAs` with `null`. A fresh click is not needed: calling them after an `await` in a click handler works, as long as the user did not switch to another window meanwhile. The mock desktop of `pithyx dev` follows the same rule (its frame must have the focus) and logs a refused call. The dialog looks like Files: it opens in the user's own home, lists the home and the shared folders on the left (an administrator finds other users' homes under **homes**, as in Files) and the folder as a table on the right; the app learns only the files the user chose, and it can read (and, with `files.write`, write) exactly those without `files.shares`. The same holds for the file a user opens with the app from the Files app (`launch.file`, `openFile`).

**The file manager calls** need `files.shares` and work on shared folders: `files.run(operation)` for copy, move and delete as a job, `files.versions` and `files.restoreVersion` (`files.read`, restore also `files.write`), `trash.list` (`files.read`) and `trash.restore`, `trash.delete` and `trash.empty` (`files.write`). The versions and the recycle bin take only a shared folder (`ShareLocation`). `files.run` also takes a storage request as source or target, so an app can copy a file from its folder into a shared folder the user picked: a shared folder needs `files.shares`, `files.read` where entries are copied or moved from and `files.write` where anything changes; a storage request needs only its mapping (for reading and writing where anything changes) and never reaches beyond the mapped folder. A delete in a storage request goes to its shared folder's recycle bin. `jobChanged` events follow a job; `jobs.cancel(id)` cancels only a job this app started for the user. `app.ui.showError(error)` shows the desktop's error dialog. `sharesChanged` reaches an app that holds `files.shares`.

Test with `app.supports(method)` before you offer a feature the desktop may not have: the mock desktop of `pithyx dev` serves fewer calls than the box.

## Edge cases

- **Paths** are relative to the location, with `/` between names and `""` for the root. A leading or doubled slash, `.`, `..` or a path over 4096 characters is refused with `sdk.request.invalid`. A name has 1 to 255 characters and no `/`.
- **File handles** belong to the window's connection to the desktop: closing the window, reloading the app (for example after `appUpdated`) or reloading the desktop ends them, also when the window comes back with its `app.session`. A stale handle fails with `sdk.request.invalid`. Store paths, never a `FileRef`. A file the user picked or opened with the app has no path the app learns, so such a file cannot be reopened after a reload; an app that must reopen files (an editor's tabs, recent files) asks for `files.shares` and lets the user browse with `files.list`, or keeps unsaved text in `app.session` and asks the user to open the file again.
- **Time limits.** Every call fails after 15 seconds with `sdk.request.timeout` except the ones that last as long as a transfer or as the user takes: `files.upload`, `files.read`, `files.write`, `files.pick` and `files.saveAs`.
- **Upload progress.** With an `upload` id, `files.upload` sends `uploadProgress` events with the bytes sent, and the upload shows in the taskbar with a cancel button.
- **Pages.** A `limit` above 1000 is cut to 1000. Pass `nextCursor` as `cursor` for the next page; it stays valid while the folder changes. Symbolic links are listed as `symlink` and never followed.
- **Mock desktop.** `pithyx dev` serves only `files.pick`, `files.saveAs`, `files.read` and `files.write` (on picked files) and keeps `app.storage` in the browser of the mock desktop. See [Testing](/docs/sdk/guides/testing/).

## Error codes

[`app.storage.too_large`](/docs/reference/errors/#app.storage.too_large), [`app.storage.not_found`](/docs/reference/errors/#app.storage.not_found), [`app.permission.denied`](/docs/reference/errors/#app.permission.denied), [`fs.entry.exists`](/docs/reference/errors/#fs.entry.exists), [`fs.entry.not_found`](/docs/reference/errors/#fs.entry.not_found), [`fs.path.invalid`](/docs/reference/errors/#fs.path.invalid), [`fs.share.not_found`](/docs/reference/errors/#fs.share.not_found), [`fs.share.read_only`](/docs/reference/errors/#fs.share.read_only), [`fs.share.unavailable`](/docs/reference/errors/#fs.share.unavailable), [`sdk.method.unknown`](/docs/reference/errors/#sdk.method.unknown), [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout).
