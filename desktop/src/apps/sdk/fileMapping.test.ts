import type { FileOperation } from '@pithyx/sdk';
import { describe, expect, it } from 'vitest';
import { appOperationBody } from '../sandbox/appApi';
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
    expect(() =>
      operationBody({ ...copy, source: { ...copy.source, location: { storage: 'clips' } } }),
    ).toThrow(
      expect.objectContaining({ code: 'sdk.request.invalid', params: { method: 'files.run' } }),
    );
  });

  it('sends files.run of a third-party app with storage requests as places of their own', () => {
    expect(
      appOperationBody({
        kind: 'copy',
        source: { location: { storage: 'clips' }, folder: '', items: { names: ['a.md'] } },
        target: { location: { share: '4' }, path: 'Inbox' },
      }),
    ).toEqual({
      kind: 'copy',
      items: [{ storage: 'clips', path: 'a.md' }],
      target: { share: '4', path: 'Inbox' },
      permanent: false,
    });
    expect(
      appOperationBody({
        kind: 'delete',
        source: {
          location: { share: '3' },
          folder: 'Old',
          items: { all: true, except: ['keep.md'] },
        },
      }),
    ).toEqual({
      kind: 'delete',
      allIn: { folder: { share: '3', path: 'Old' }, except: ['keep.md'] },
      permanent: false,
    });
  });
});
