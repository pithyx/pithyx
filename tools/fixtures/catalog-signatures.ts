// Writes the signature vectors of the catalog corpus (sdk/schema/fixtures/catalog/keys.json and signatures/).
// The keys are test keys derived from fixed seeds, so every run writes the same bytes; they only ever sign fixtures and
// are never trusted by a box.
// Usage: node tools/fixtures/catalog-signatures.ts
import { createHash, createPrivateKey, createPublicKey, sign } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const corpus = join(dirname(fileURLToPath(import.meta.url)), '../../sdk/schema/fixtures/catalog');
const out = join(corpus, 'signatures');

interface TestKey {
  keyId: string;
  seed: string;
  publicKey: string;
  channels: string[];
}

function testKey(keyId: string, channels: string[]): TestKey {
  const seed = createHash('sha256').update(`pithyx catalog corpus test key ${keyId}`).digest();
  const privateKey = createPrivateKey({
    key: Buffer.concat([Buffer.from('302e020100300506032b657004220420', 'hex'), seed]),
    format: 'der',
    type: 'pkcs8',
  });
  const jwk = createPublicKey(privateKey).export({ format: 'jwk' });
  return {
    keyId,
    seed: seed.toString('hex'),
    publicKey: Buffer.from(jwk.x ?? '', 'base64url').toString('base64'),
    channels,
  };
}

const stable = testKey('test-stable', ['stable', 'beta']);
const beta = testKey('test-beta', ['beta']);
const rogue = testKey('test-rogue', []);

function signWith(key: TestKey, bytes: Buffer): string {
  const privateKey = createPrivateKey({
    key: Buffer.concat([
      Buffer.from('302e020100300506032b657004220420', 'hex'),
      Buffer.from(key.seed, 'hex'),
    ]),
    format: 'der',
    type: 'pkcs8',
  });
  return sign(null, bytes, privateKey).toString('base64');
}

function json(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function signatureFile(signatures: { keyId: string; signature: string }[]): string {
  return json({ signatureVersion: 1, signatures });
}

mkdirSync(out, { recursive: true });
writeFileSync(
  join(corpus, 'keys.json'),
  json({
    $comment:
      'Test keys of the catalog corpus, derived from the seed sha256("pithyx catalog corpus test key <keyId>") by tools/fixtures/catalog-signatures.ts. They sign fixtures only and are never trusted by a box. The keyring of the corpus holds every key with its channels; test-rogue is trusted on no channel.',
    keys: [stable, beta, rogue],
  }),
);

/** The official index as a community index, without its app in the reserved namespace. */
function community(): unknown {
  const index = JSON.parse(readFileSync(join(corpus, 'index/valid/full.json'), 'utf8')) as {
    catalog: string;
    apps: Record<string, unknown>;
  };
  index.catalog = 'org.pithyx.community';
  delete index.apps['org.pithyx.example'];
  return index;
}

const indexes = {
  'stable.json': readFileSync(join(corpus, 'index/valid/full.json')),
  'beta.json': readFileSync(join(corpus, 'index/valid/beta.json')),
  'community.json': Buffer.from(json(community())),
};
const tampered = Buffer.from(
  indexes['stable.json'].toString('utf8').replace('"sequence": 7', '"sequence": 70'),
);
const trailingSpace = Buffer.concat([indexes['stable.json'], Buffer.from(' ')]);
for (const [name, bytes] of Object.entries({
  ...indexes,
  'stable-tampered.json': tampered,
  'stable-trailing-space.json': trailingSpace,
})) {
  writeFileSync(join(out, name), bytes);
}

const signatures: Record<string, string> = {
  'stable.sig': signatureFile([
    { keyId: stable.keyId, signature: signWith(stable, indexes['stable.json']) },
  ]),
  'beta.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, indexes['beta.json']) },
  ]),
  'beta-by-stable-key.sig': signatureFile([
    { keyId: stable.keyId, signature: signWith(stable, indexes['beta.json']) },
  ]),
  'stable-by-beta-key.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, indexes['stable.json']) },
  ]),
  'stable-by-rogue-key.sig': signatureFile([
    { keyId: rogue.keyId, signature: signWith(rogue, indexes['stable.json']) },
  ]),
  'stable-by-rogue-key-as-stable.sig': signatureFile([
    { keyId: stable.keyId, signature: signWith(rogue, indexes['stable.json']) },
  ]),
  'stable-rotation.sig': signatureFile([
    { keyId: 'test-next-key', signature: signWith(rogue, indexes['stable.json']) },
    { keyId: stable.keyId, signature: signWith(stable, indexes['stable.json']) },
  ]),
  'beta-one-bad.sig': signatureFile([
    { keyId: stable.keyId, signature: signWith(stable, indexes['beta.json']) },
    { keyId: beta.keyId, signature: signWith(beta, indexes['stable.json']) },
  ]),
  'community.sig': signatureFile([
    { keyId: stable.keyId, signature: signWith(stable, indexes['community.json']) },
  ]),
  'malformed-json.sig': '{"signatureVersion": 1,\n',
  'malformed-base64.sig': signatureFile([
    {
      keyId: stable.keyId,
      signature: 'not base64, but long enough to look like a signature'.padEnd(88, '!'),
    },
  ]),
  'malformed-short.sig': signatureFile([
    {
      keyId: stable.keyId,
      signature: signWith(stable, indexes['stable.json']).slice(0, 84) + '==',
    },
  ]),
  'malformed-empty.sig': signatureFile([]),
  'malformed-version.sig': json({
    signatureVersion: 2,
    signatures: [{ keyId: stable.keyId, signature: signWith(stable, indexes['stable.json']) }],
  }),
};
for (const [name, text] of Object.entries(signatures)) {
  writeFileSync(join(out, name), text);
}

const official = 'org.pithyx.official';
const invalid = (reason: string) => [
  { code: 'store.catalog.signature_invalid', params: { reason } },
];
const cases = [
  {
    name: 'valid stable',
    index: 'stable.json',
    signature: 'stable.sig',
    channel: 'stable',
    expected: [],
  },
  {
    name: 'same sequence again',
    index: 'stable.json',
    signature: 'stable.sig',
    channel: 'stable',
    knownSequence: 7,
    expected: [],
  },
  {
    name: 'older sequence',
    index: 'stable.json',
    signature: 'stable.sig',
    channel: 'stable',
    knownSequence: 8,
    expected: [{ code: 'store.catalog.sequence_old', params: { sequence: 7, known: 8 } }],
  },
  {
    name: 'valid beta',
    index: 'beta.json',
    signature: 'beta.sig',
    channel: 'beta',
    knownSequence: 30,
    expected: [],
  },
  {
    name: 'stable key on beta',
    index: 'beta.json',
    signature: 'beta-by-stable-key.sig',
    channel: 'beta',
    expected: [],
  },
  {
    name: 'beta key on stable',
    index: 'stable.json',
    signature: 'stable-by-beta-key.sig',
    channel: 'stable',
    expected: invalid('untrusted'),
  },
  {
    name: 'unknown key',
    index: 'stable.json',
    signature: 'stable-by-rogue-key.sig',
    channel: 'stable',
    expected: invalid('untrusted'),
  },
  {
    name: 'wrong key',
    index: 'stable.json',
    signature: 'stable-by-rogue-key-as-stable.sig',
    channel: 'stable',
    expected: invalid('mismatch'),
  },
  {
    name: 'tampered',
    index: 'stable-tampered.json',
    signature: 'stable.sig',
    channel: 'stable',
    expected: invalid('mismatch'),
  },
  {
    name: 'one byte more',
    index: 'stable-trailing-space.json',
    signature: 'stable.sig',
    channel: 'stable',
    expected: invalid('mismatch'),
  },
  {
    name: 'rotation with an unknown key',
    index: 'stable.json',
    signature: 'stable-rotation.sig',
    channel: 'stable',
    expected: [],
  },
  {
    name: 'one bad signature of a trusted key',
    index: 'beta.json',
    signature: 'beta-one-bad.sig',
    channel: 'beta',
    expected: invalid('mismatch'),
  },
  {
    name: 'missing',
    index: 'stable.json',
    signature: null,
    channel: 'stable',
    expected: invalid('missing'),
  },
  {
    name: 'not json',
    index: 'stable.json',
    signature: 'malformed-json.sig',
    channel: 'stable',
    expected: invalid('malformed'),
  },
  {
    name: 'not base64',
    index: 'stable.json',
    signature: 'malformed-base64.sig',
    channel: 'stable',
    expected: invalid('malformed'),
  },
  {
    name: 'short signature',
    index: 'stable.json',
    signature: 'malformed-short.sig',
    channel: 'stable',
    expected: invalid('malformed'),
  },
  {
    name: 'no signatures',
    index: 'stable.json',
    signature: 'malformed-empty.sig',
    channel: 'stable',
    expected: invalid('malformed'),
  },
  {
    name: 'unknown format',
    index: 'stable.json',
    signature: 'malformed-version.sig',
    channel: 'stable',
    expected: invalid('malformed'),
  },
  {
    name: 'signed for beta, read on stable',
    index: 'beta.json',
    signature: 'beta-by-stable-key.sig',
    channel: 'stable',
    expected: [
      {
        code: 'store.catalog.mismatch',
        params: { path: '/channel', value: 'beta', expected: 'stable' },
      },
    ],
  },
  {
    name: 'another catalog',
    index: 'community.json',
    signature: 'community.sig',
    channel: 'stable',
    expected: [
      {
        code: 'store.catalog.mismatch',
        params: { path: '/catalog', value: 'org.pithyx.community', expected: official },
      },
    ],
  },
].map((entry) => ({ catalog: official, ...entry }));
writeFileSync(join(out, 'cases.json'), json(cases));
