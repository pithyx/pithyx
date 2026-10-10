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
