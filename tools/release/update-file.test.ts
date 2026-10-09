import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { checkChannel, checkManifest, type Channel, type TrustedKey } from './update-file';

const corpus = join(import.meta.dirname, 'fixtures');
const keyring = (
  JSON.parse(readFileSync(join(corpus, 'keys.json'), 'utf8')) as { keys: TrustedKey[] }
).keys;

interface Case {
  name: string;
  kind: 'manifest' | 'channel';
  document: string;
  signature: string | null;
  boxChannel: Channel;
  allowUnsigned?: boolean;
  knownSequence?: number;
  expected: unknown[];
}

const cases = JSON.parse(readFileSync(join(corpus, 'cases.json'), 'utf8')) as Case[];

describe('the update corpus', () => {
  it.each(cases.map((entry) => [entry.name, entry] as const))('%s', (_name, entry) => {
    const document = readFileSync(join(corpus, entry.document));
    const signature =
      entry.signature === null ? undefined : readFileSync(join(corpus, entry.signature));
    const { issues } =
      entry.kind === 'manifest'
        ? checkManifest(document, signature, keyring, {
            boxChannel: entry.boxChannel,
            allowUnsigned: entry.allowUnsigned,
          })
        : checkChannel(document, signature, keyring, {
            boxChannel: entry.boxChannel,
            knownSequence: entry.knownSequence,
          });
    expect(issues).toEqual(entry.expected);
  });
});
