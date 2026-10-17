import type { FileLocation, FileOperation } from '@pithyx/sdk';
import { describe, expect, it } from 'vitest';
import { operationBody, shareIdOf } from './fileMapping';

describe('share locations', () => {
  it('refuses a storage request as an invalid request of the method, a malformed id as an unknown share', () => {
    expect(() => shareIdOf({ storage: 'clips' }, 'files.versions')).toThrow(
      expect.objectContaining({
        code: 'sdk.request.invalid',
        params: { method: 'files.versions' },
      }),
    );
    expect(() => shareIdOf({ share: 'x' })).toThrow(
      expect.objectContaining({ code: 'fs.share.not_found' }),
    );
    expect(shareIdOf({ share: '3' })).toBe(3);
  });

  it('turns files.run into the daemon operation and refuses storage on either side', () => {
    const copy: FileOperation = {
      kind: 'copy',
      source: { location: { share: '3' }, folder: 'Clips', items: { names: ['a.md'] } },
      target: { location: { share: '4' }, path: 'Inbox' },
    };
    expect(operationBody(copy)).toEqual({
      kind: 'copy',
      items: [{ shareId: 3, path: 'Clips/a.md' }],
      target: { shareId: 4, path: 'Inbox' },
      permanent: false,
    });
    const storage = { storage: 'clips' } as FileLocation as { share: string };
    expect(() => operationBody({ ...copy, source: { ...copy.source, location: storage } })).toThrow(
      expect.objectContaining({ code: 'sdk.request.invalid', params: { method: 'files.run' } }),
    );
  });
});
