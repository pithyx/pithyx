// Writes the corpus of update manifests and channels (tools/release/fixtures), which the release tools
// (tools/release/update-file.test.ts) and the daemon (UpdateCorpusTests) both run, so both judge alike. The keys are
// test keys derived from fixed seeds, so every run writes the same bytes; they only ever sign fixtures and are never
// trusted by a box.
// Usage: node tools/fixtures/update-signatures.ts
import { createHash, createPrivateKey, createPublicKey, sign } from 'node:crypto';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const corpus = join(dirname(fileURLToPath(import.meta.url)), '../release/fixtures');

interface TestKey {
  keyId: string;
  seed: string;
  publicKey: string;
  channels: string[];
}

function privateKey(seed: Buffer) {
  return createPrivateKey({
    key: Buffer.concat([Buffer.from('302e020100300506032b657004220420', 'hex'), seed]),
    format: 'der',
    type: 'pkcs8',
  });
}

function testKey(keyId: string, channels: string[]): TestKey {
  const seed = createHash('sha256').update(`pithyx update corpus test key ${keyId}`).digest();
  const jwk = createPublicKey(privateKey(seed)).export({ format: 'jwk' });
  return {
    keyId,
    seed: seed.toString('hex'),
    publicKey: Buffer.from(jwk.x ?? '', 'base64url').toString('base64'),
    channels,
  };
}

const release = testKey('test-release', ['stable', 'beta']);
const beta = testKey('test-beta', ['beta']);
const rogue = testKey('test-rogue', []);

function signWith(key: TestKey, bytes: Buffer): string {
  return sign(null, bytes, privateKey(Buffer.from(key.seed, 'hex'))).toString('base64');
}

function json(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function signatureFile(signatures: { keyId: string; signature: string }[]): string {
  return json({ signatureVersion: 1, signatures });
}

const digest = (text: string) => createHash('sha256').update(text).digest('hex');

function manifest(
  version: string,
  channel: string,
  extra: Record<string, unknown> = {},
): Record<string, unknown> {
  const deb = (name: string, debVersion: string) => `debs/${name}_${debVersion}_amd64.deb`;
  const debVersion = version.replace('-', '~');
  const files = [
    { path: 'debs/Packages', sha256: digest(`Packages ${version}`), size: 2048 },
    { path: 'debs/Release', sha256: digest(`Release ${version}`), size: 512 },
    { path: deb('pithyx', debVersion), sha256: digest(`pithyx ${version}`), size: 46284692 },
    {
      path: deb('pithyx-kernel', debVersion),
      sha256: digest(`pithyx-kernel ${version}`),
      size: 964,
    },
  ];
  return {
    updateVersion: 1,
    version,
    channel,
    minimumVersion: '0.1.0',
    architecture: 'amd64',
    commit: 'c2e871a0f3b1',
    publishedAt: '2026-10-09T21:00:00Z',
    kernel: { release: '6.12.111+deb13-amd64', zfs: '2.3.9-0+deb13u1' },
    files,
    packages: [
      { name: 'pithyx', version: debVersion, path: deb('pithyx', debVersion) },
      { name: 'pithyx-kernel', version: debVersion, path: deb('pithyx-kernel', debVersion) },
    ],
    changelog: '### Added\n\n- Updates with rollback.',
    ...extra,
  };
}

function channel(
  name: string,
  sequence: number,
  version: string,
  extra: Record<string, unknown> = {},
): unknown {
  const file = `pithyx-${version}-amd64.pithyxupdate`;
  return {
    channelVersion: 1,
    channel: name,
    sequence,
    publishedAt: '2026-10-09T21:05:00Z',
    version,
    minimumVersion: '0.1.0',
    changelog: '### Added\n\n- Updates with rollback.',
    file: {
      name: file,
      url: `../${name}-${version}/${file}`,
      size: 48146112,
      sha256: digest(file),
    },
    ...extra,
  };
}

const documents: Record<string, Buffer> = {
  'manifest-stable.json': Buffer.from(json(manifest('0.2.0', 'stable'))),
  'manifest-beta.json': Buffer.from(json(manifest('0.2.0-beta.3', 'beta'))),
  'manifest-unknown-field.json': Buffer.from(
    json(manifest('0.2.0', 'stable', { mirrors: ['later'] })),
  ),
  'manifest-bad-hash.json': Buffer.from(
    json(
      manifest('0.2.0', 'stable', { files: [{ path: 'debs/Packages', sha256: 'abc', size: 1 }] }),
    ),
  ),
  'manifest-path-escape.json': Buffer.from(
    json(
      manifest('0.2.0', 'stable', {
        files: [
          { path: 'debs/Packages', sha256: digest('a'), size: 1 },
          { path: 'debs/Release', sha256: digest('b'), size: 1 },
          { path: 'debs/../../etc/passwd', sha256: digest('c'), size: 1 },
        ],
      }),
    ),
  ),
  'manifest-version-2.json': Buffer.from(json(manifest('0.2.0', 'stable', { updateVersion: 2 }))),
  'manifest-duplicate-key.json': Buffer.from(
    json(manifest('0.2.0', 'stable')).replace(
      '"channel": "stable",',
      '"channel": "stable",\n  "channel": "beta",',
    ),
  ),
  'channel-stable.json': Buffer.from(json(channel('stable', 12, '0.2.0'))),
  'channel-beta.json': Buffer.from(json(channel('beta', 40, '0.2.0-beta.3'))),
  'channel-no-file.json': Buffer.from(json(channel('stable', 12, '0.2.0', { file: undefined }))),
  'channel-bad-name.json': Buffer.from(
    json(
      channel('stable', 12, '0.2.0', {
        file: { name: '../../evil.pithyxupdate', url: 'x', size: 1, sha256: digest('x') },
      }),
    ),
  ),
};
documents['manifest-stable-tampered.json'] = Buffer.from(
  (documents['manifest-stable.json'] ?? Buffer.alloc(0))
    .toString('utf8')
    .replace('"0.1.0"', '"0.0.1"'),
);
documents['channel-stable-trailing-space.json'] = Buffer.concat([
  documents['channel-stable.json'] ?? Buffer.alloc(0),
  Buffer.from(' '),
]);

const doc = (name: string): Buffer => documents[name] ?? Buffer.alloc(0);
const signatures: Record<string, string> = {
  'manifest-stable.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-stable.json')) },
  ]),
  'manifest-beta.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, doc('manifest-beta.json')) },
  ]),
  'manifest-stable-by-beta-key.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, doc('manifest-stable.json')) },
  ]),
  'manifest-beta-by-release-key.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-beta.json')) },
  ]),
  'manifest-stable-by-rogue-key.sig': signatureFile([
    { keyId: rogue.keyId, signature: signWith(rogue, doc('manifest-stable.json')) },
  ]),
  'manifest-stable-by-rogue-as-release.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(rogue, doc('manifest-stable.json')) },
  ]),
  'manifest-stable-rotation.sig': signatureFile([
    { keyId: 'test-release-2', signature: signWith(rogue, doc('manifest-stable.json')) },
    { keyId: release.keyId, signature: signWith(release, doc('manifest-stable.json')) },
  ]),
  'manifest-unknown-field.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-unknown-field.json')) },
  ]),
  'manifest-bad-hash.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-bad-hash.json')) },
  ]),
  'manifest-path-escape.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-path-escape.json')) },
  ]),
  'manifest-version-2.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-version-2.json')) },
  ]),
  'manifest-duplicate-key.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('manifest-duplicate-key.json')) },
  ]),
  'channel-stable.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('channel-stable.json')) },
  ]),
  'channel-beta.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, doc('channel-beta.json')) },
  ]),
  'channel-beta-by-release-key.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('channel-beta.json')) },
  ]),
  'channel-stable-by-beta-key.sig': signatureFile([
    { keyId: beta.keyId, signature: signWith(beta, doc('channel-stable.json')) },
  ]),
  'channel-no-file.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('channel-no-file.json')) },
  ]),
  'channel-bad-name.sig': signatureFile([
    { keyId: release.keyId, signature: signWith(release, doc('channel-bad-name.json')) },
  ]),
  'malformed-json.sig': '{"signatureVersion": 1,\n',
  'malformed-short.sig': signatureFile([
    {
      keyId: release.keyId,
      signature: signWith(release, doc('manifest-stable.json')).slice(0, 84) + '==',
    },
  ]),
  'malformed-empty.sig': signatureFile([]),
};

const fileInvalid = (reason: string) => [
  { code: 'update.file.signature_invalid', params: { reason } },
];
const channelInvalid = (reason: string) => [
  { code: 'update.channel.signature_invalid', params: { reason } },
];
const cases = [
  {
    name: 'stable file on a stable box',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable.sig',
    boxChannel: 'stable',
    expected: [],
  },
  {
    name: 'stable file on a beta box',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable.sig',
    boxChannel: 'beta',
    expected: [],
  },
  {
    name: 'beta file on a beta box',
    kind: 'manifest',
    document: 'manifest-beta.json',
    signature: 'manifest-beta.sig',
    boxChannel: 'beta',
    expected: [],
  },
  {
    name: 'beta file by the release key',
    kind: 'manifest',
    document: 'manifest-beta.json',
    signature: 'manifest-beta-by-release-key.sig',
    boxChannel: 'beta',
    expected: [],
  },
  {
    name: 'beta file by the release key on a stable box',
    kind: 'manifest',
    document: 'manifest-beta.json',
    signature: 'manifest-beta-by-release-key.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.file.channel', params: { channel: 'beta', boxChannel: 'stable' } }],
  },
  {
    name: 'beta key on a stable box',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable-by-beta-key.sig',
    boxChannel: 'stable',
    expected: fileInvalid('untrusted'),
  },
  {
    name: 'beta key signs a stable file for a beta box',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable-by-beta-key.sig',
    boxChannel: 'beta',
    expected: [],
  },
  {
    name: 'unknown key',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable-by-rogue-key.sig',
    boxChannel: 'stable',
    expected: fileInvalid('untrusted'),
  },
  {
    name: 'wrong key under the release key id',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable-by-rogue-as-release.sig',
    boxChannel: 'stable',
    expected: fileInvalid('mismatch'),
  },
  {
    name: 'rotation with an unknown key',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'manifest-stable-rotation.sig',
    boxChannel: 'stable',
    expected: [],
  },
  {
    name: 'tampered manifest',
    kind: 'manifest',
    document: 'manifest-stable-tampered.json',
    signature: 'manifest-stable.sig',
    boxChannel: 'stable',
    expected: fileInvalid('mismatch'),
  },
  {
    name: 'unsigned',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: null,
    boxChannel: 'stable',
    expected: fileInvalid('missing'),
  },
  {
    name: 'unsigned on a development box',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: null,
    boxChannel: 'stable',
    allowUnsigned: true,
    expected: [],
  },
  {
    name: 'a bad signature on a development box',
    kind: 'manifest',
    document: 'manifest-stable-tampered.json',
    signature: 'manifest-stable.sig',
    boxChannel: 'stable',
    allowUnsigned: true,
    expected: fileInvalid('mismatch'),
  },
  {
    name: 'signature not json',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'malformed-json.sig',
    boxChannel: 'stable',
    expected: fileInvalid('malformed'),
  },
  {
    name: 'short signature',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'malformed-short.sig',
    boxChannel: 'stable',
    expected: fileInvalid('malformed'),
  },
  {
    name: 'no signatures',
    kind: 'manifest',
    document: 'manifest-stable.json',
    signature: 'malformed-empty.sig',
    boxChannel: 'stable',
    expected: fileInvalid('malformed'),
  },
  {
    name: 'unknown field',
    kind: 'manifest',
    document: 'manifest-unknown-field.json',
    signature: 'manifest-unknown-field.sig',
    boxChannel: 'stable',
    expected: [],
  },
  {
    name: 'bad hash and too few files',
    kind: 'manifest',
    document: 'manifest-bad-hash.json',
    signature: 'manifest-bad-hash.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.file.invalid', params: {} }],
  },
  {
    name: 'path outside debs',
    kind: 'manifest',
    document: 'manifest-path-escape.json',
    signature: 'manifest-path-escape.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.file.invalid', params: {} }],
  },
  {
    name: 'newer format',
    kind: 'manifest',
    document: 'manifest-version-2.json',
    signature: 'manifest-version-2.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.file.invalid', params: {} }],
  },
  {
    name: 'a key twice',
    kind: 'manifest',
    document: 'manifest-duplicate-key.json',
    signature: 'manifest-duplicate-key.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.file.invalid', params: {} }],
  },
  {
    name: 'stable channel',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: 'channel-stable.sig',
    boxChannel: 'stable',
    expected: [],
  },
  {
    name: 'same sequence again',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: 'channel-stable.sig',
    boxChannel: 'stable',
    knownSequence: 12,
    expected: [],
  },
  {
    name: 'older sequence',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: 'channel-stable.sig',
    boxChannel: 'stable',
    knownSequence: 13,
    expected: [{ code: 'update.channel.sequence_old', params: { sequence: 12, known: 13 } }],
  },
  {
    name: 'beta channel',
    kind: 'channel',
    document: 'channel-beta.json',
    signature: 'channel-beta.sig',
    boxChannel: 'beta',
    knownSequence: 39,
    expected: [],
  },
  {
    name: 'beta channel by the release key',
    kind: 'channel',
    document: 'channel-beta.json',
    signature: 'channel-beta-by-release-key.sig',
    boxChannel: 'beta',
    expected: [],
  },
  {
    name: 'beta key for the stable channel',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: 'channel-stable-by-beta-key.sig',
    boxChannel: 'stable',
    expected: channelInvalid('untrusted'),
  },
  {
    name: 'stable channel read as beta',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: 'channel-stable.sig',
    boxChannel: 'beta',
    expected: [
      { code: 'update.channel.mismatch', params: { channel: 'stable', expected: 'beta' } },
    ],
  },
  {
    name: 'one byte more',
    kind: 'channel',
    document: 'channel-stable-trailing-space.json',
    signature: 'channel-stable.sig',
    boxChannel: 'stable',
    expected: channelInvalid('mismatch'),
  },
  {
    name: 'unsigned channel',
    kind: 'channel',
    document: 'channel-stable.json',
    signature: null,
    boxChannel: 'stable',
    expected: channelInvalid('missing'),
  },
  {
    name: 'no file',
    kind: 'channel',
    document: 'channel-no-file.json',
    signature: 'channel-no-file.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.channel.invalid', params: {} }],
  },
  {
    name: 'file name with a path',
    kind: 'channel',
    document: 'channel-bad-name.json',
    signature: 'channel-bad-name.sig',
    boxChannel: 'stable',
    expected: [{ code: 'update.channel.invalid', params: {} }],
  },
];

rmSync(corpus, { recursive: true, force: true });
mkdirSync(corpus, { recursive: true });
writeFileSync(
  join(corpus, 'keys.json'),
  json({
    $comment:
      'Test keys of the update corpus, derived from the seed sha256("pithyx update corpus test key <keyId>") by tools/fixtures/update-signatures.ts. They sign fixtures only and are never trusted by a box. test-release signs for stable and beta like pithyx-release-1, test-beta for beta like pithyx-beta-1, test-rogue for nothing.',
    keys: [release, beta, rogue],
  }),
);
for (const [name, bytes] of Object.entries(documents)) {
  writeFileSync(join(corpus, name), bytes);
}
for (const [name, text] of Object.entries(signatures)) {
  writeFileSync(join(corpus, name), text);
}
writeFileSync(join(corpus, 'cases.json'), json(cases));

/** One entry of a ustar archive: the 512-byte header, the content and its padding to 512 bytes. */
function tarEntry(name: string, content: Buffer | null): Buffer {
  const header = Buffer.alloc(512);
  const field = (offset: number, length: number, value: string) => {
    header.write(value, offset, length, 'ascii');
  };
  const octal = (value: number, length: number) => value.toString(8).padStart(length - 1, '0');
  field(0, 100, name);
  field(100, 8, octal(content === null ? 0o755 : 0o644, 8));
  field(108, 8, octal(0, 8));
  field(116, 8, octal(0, 8));
  field(124, 12, octal(content?.length ?? 0, 12));
  field(136, 12, octal(1791580518, 12));
  field(148, 8, '        ');
  field(156, 1, content === null ? '5' : '0');
  field(257, 6, 'ustar');
  field(263, 2, '00');
  let sum = 0;
  for (const byte of header) {
    sum += byte;
  }
  field(148, 8, `${octal(sum, 7)}\0`);
  if (content === null) {
    return header;
  }
  return Buffer.concat([header, content, Buffer.alloc((512 - (content.length % 512)) % 512)]);
}

// A whole update file signed by test-beta, laid out as tools/release/build-update.sh lays one out, for the E2E suite
// (simulation trusts the corpus keys). It installs from any version from 0.0.1 on.
const debs: Record<string, Buffer> = {
  'debs/Packages': Buffer.from('Package: pithyx\nVersion: 0.2.0~beta.3\n'),
  'debs/Release': Buffer.from('Origin: Pithyx\nLabel: Pithyx\n'),
  'debs/pithyx-kernel_0.2.0~beta.3_amd64.deb': Buffer.from('a test package, not a real one'),
  'debs/pithyx_0.2.0~beta.3_amd64.deb': Buffer.from('another test package, not a real one'),
};
const updateManifest = Buffer.from(
  json(
    manifest('0.2.0-beta.3', 'beta', {
      minimumVersion: '0.0.1',
      files: Object.entries(debs).map(([path, content]) => ({
        path,
        sha256: createHash('sha256').update(content).digest('hex'),
        size: content.length,
      })),
    }),
  ),
);
writeFileSync(
  join(corpus, 'pithyx-0.2.0-beta.3-amd64.pithyxupdate'),
  Buffer.concat([
    tarEntry('manifest.json', updateManifest),
    tarEntry(
      'manifest.json.sig',
      Buffer.from(
        signatureFile([{ keyId: beta.keyId, signature: signWith(beta, updateManifest) }]),
      ),
    ),
    tarEntry('debs/', null),
    ...Object.entries(debs).map(([path, content]) => tarEntry(path, content)),
    Buffer.alloc(1024),
  ]),
);
