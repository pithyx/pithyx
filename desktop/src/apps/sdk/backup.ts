import { unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { daemon } from './daemonError';

export type BackupCategory = components['schemas']['BackupCategory'];
export type BackupSummary = components['schemas']['BackupSummary'];
export type RestoreResult = components['schemas']['RestoreResult'];

/** The configuration backup for administrators, outside the public SDK; the daemon checks the right on every request. */
interface BackupMethods {
  'internal.admin.backup.create': { params: { password: string }; result: { fileName: string } };
  'internal.admin.backup.inspect': {
    params: { file: Blob; password: string };
    result: BackupSummary;
  };
  'internal.admin.backup.restore': {
    params: { restoreId: string; categories: BackupCategory[] };
    result: RestoreResult;
  };
}

const CATEGORIES: readonly BackupCategory[] = [
  'users',
  'sharedFolders',
  'storage',
  'system',
  'certificates',
  'apps',
  'reverseProxy',
];

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** The file name from `Content-Disposition`, as the daemon suggests it. */
function fileNameOf(response: Response): string {
  const header = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(header);
  const name = match?.[1] ?? match?.[2];
  return name === undefined ? 'pithyx-config.pithyxbackup' : decodeURIComponent(name);
}

function save(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  // The download has its own copy once the click went through.
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}

async function base64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  // In chunks: String.fromCharCode takes its bytes as arguments, and a call has a limit on those.
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

const delay = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

async function healthy(): Promise<boolean> {
  try {
    const { response } = await api.GET('/api/health', { signal: AbortSignal.timeout(2000) });
    return response.ok;
  } catch {
    return false;
  }
}

/**
 * Waits until the daemon went away for its restart and answers again, then loads the desktop again: a restore may
 * have replaced the signed-in user, and every app should read the restored configuration. Checks once a second
 * while it waits, because nothing else can say when the daemon is back.
 */
async function reloadAfterRestart() {
  const deadline = Date.now() + 120_000;
  for (let waited = 0; waited < 15 && (await healthy()); waited++) {
    await delay(1000);
  }
  while (Date.now() < deadline && !(await healthy())) {
    await delay(1000);
  }
  window.location.reload();
}

/** The host side of the backup page of the Settings app. */
export function backupHost(): HostExtensions {
  return {
    'internal.admin.backup.create': {
      validate: (value) => isRecord(value) && typeof value.password === 'string',
      handle: (params) =>
        daemon(async () => {
          const { password } = params as { password: string };
          const result = await api.POST('/api/system/backup', {
            body: { password },
            parseAs: 'blob',
          });
          const blob = unwrap(result);
          const fileName = fileNameOf(result.response);
          save(blob, fileName);
          return { fileName };
        }),
    },
    'internal.admin.backup.inspect': {
      validate: (value) =>
        isRecord(value) && value.file instanceof Blob && typeof value.password === 'string',
      handle: (params) =>
        daemon(async () => {
          const { file, password } = params as { file: Blob; password: string };
          return unwrap(
            await api.POST('/api/system/backup/inspect', {
              body: { archive: await base64(file), password },
            }),
          );
        }),
    },
    'internal.admin.backup.restore': {
      validate: (value) =>
        isRecord(value) &&
        typeof value.restoreId === 'string' &&
        Array.isArray(value.categories) &&
        value.categories.every((category) => CATEGORIES.includes(category as BackupCategory)),
      handle: (params) =>
        daemon(async () => {
          const result = unwrap(
            await api.POST('/api/system/backup/restore', {
              body: params as { restoreId: string; categories: BackupCategory[] },
            }),
          );
          if (result.restarting) {
            void reloadAfterRestart();
          }
          return result;
        }),
    },
  };
}

export async function backupCall<M extends keyof BackupMethods>(
  app: PithyxApp,
  method: M,
  params: BackupMethods[M]['params'],
): Promise<BackupMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as BackupMethods[M]['result'];
}
