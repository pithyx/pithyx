import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { unreachableError } from '@pithyx/api-client';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { toPithyxError } from './daemonError';

/** Text files are shown up to this size; the rest is cut off with a note. */
export const MAX_TEXT_BYTES = 1024 * 1024;

export interface TextContent {
  text: string;
  /** True when the file is larger than what is shown. */
  truncated: boolean;
}

/** The URL that streams a file for `<img>`, `<audio>`, `<video>` and pdf.js, with range requests and the sandbox CSP. */
export function contentUrl(share: string, folder: string, name: string): string {
  const path = folder === '' ? name : `${folder}/${name}`;
  return `/api/files/${share}/content?${new URLSearchParams({ path, inline: 'true' }).toString()}`;
}

function isTextRequest(value: unknown): boolean {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const { share, folder, name } = value as Record<string, unknown>;
  return typeof share === 'string' && typeof folder === 'string' && typeof name === 'string';
}

/**
 * The viewer's own call: the first megabyte of a text file as text. The daemon sends every text type, HTML
 * included, as text/plain, and the viewer only ever puts it into a text node.
 */
export function viewerHost(): { extensions: HostExtensions; close: () => void } {
  return {
    extensions: {
      'internal.viewer.text': {
        validate: isTextRequest,
        handle: async (params) => {
          const { share, folder, name } = params as { share: string; folder: string; name: string };
          const response = await fetch(contentUrl(share, folder, name), {
            headers: { Range: `bytes=0-${String(MAX_TEXT_BYTES - 1)}` },
            credentials: 'same-origin',
          }).catch((error: unknown) => {
            throw toPithyxError(unreachableError(error));
          });
          if (!response.ok) {
            const problem = (await response.json().catch(() => null)) as {
              code?: string;
              params?: Record<string, unknown>;
              requestId?: string;
            } | null;
            throw new PithyxError({
              code: problem?.code ?? 'fs.entry.not_found',
              params: problem?.params ?? {},
              ...(problem?.requestId === undefined ? {} : { requestId: problem.requestId }),
            });
          }
          const total = Number(response.headers.get('Content-Range')?.split('/')[1] ?? Number.NaN);
          const bytes = await response.arrayBuffer();
          return {
            // Invalid UTF-8 shows as replacement characters instead of failing.
            text: new TextDecoder('utf-8', { fatal: false }).decode(bytes),
            truncated: Number.isFinite(total) && total > bytes.byteLength,
          } satisfies TextContent;
        },
      },
    },
    close: () => undefined,
  };
}

export async function readText(
  app: PithyxApp,
  share: string,
  folder: string,
  name: string,
): Promise<TextContent> {
  // The SDK's call is typed to the public methods; the internal name only exists here.
  return (await app.call(
    'internal.viewer.text' as unknown as MethodName,
    {
      share,
      folder,
      name,
    } as never,
  )) as TextContent;
}
