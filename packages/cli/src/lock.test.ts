import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import {
  bearerChallenge,
  composeImages,
  formatLock,
  parseLock,
  parseReference,
  RegistryClient,
  resolveLock,
  type Fetch,
} from './lock';

const digestOf = (text: string) => `sha256:${createHash('sha256').update(text).digest('hex')}`;

describe('image references', () => {
  const digest = `sha256:${'a'.repeat(64)}`;

  it.each([
    [`nginx@${digest}`, 'registry-1.docker.io', 'library/nginx'],
    [`nginx:1.27@${digest}`, 'registry-1.docker.io', 'library/nginx'],
    [`grafana/grafana:11@${digest}`, 'registry-1.docker.io', 'grafana/grafana'],
    [`ghcr.io/example/chat-server:1.2.0@${digest}`, 'ghcr.io', 'example/chat-server'],
    [`localhost:5000/team/app@${digest}`, 'localhost:5000', 'team/app'],
  ])('%s lives in %s as %s', (reference, registry, repository) => {
    expect(parseReference(reference)).toEqual({ registry, repository, digest });
  });

  it('reads the images of a Compose file once each, sorted', () => {
    const compose = `services:\n  b:\n    image: two@${digest}\n  a:\n    image: one@${digest}\n  c:\n    image: two@${digest}\n`;
    expect(composeImages(new TextEncoder().encode(compose))).toEqual([
      `one@${digest}`,
      `two@${digest}`,
    ]);
  });

  it('writes the lock with sorted keys and reads it back', () => {
    const lock = {
      lockVersion: 1 as const,
      images: {
        [`z@${digest}`]: {
          arm64: { config: digest, manifest: digest },
          amd64: { config: digest, manifest: digest },
        },
        [`a@${digest}`]: { amd64: { config: digest, manifest: digest } },
      },
    };
    const text = formatLock(lock);
    expect(text.indexOf(`a@${digest}`)).toBeLessThan(text.indexOf(`z@${digest}`));
    expect(text.indexOf('amd64')).toBeLessThan(text.indexOf('arm64'));
    expect(parseLock(new TextEncoder().encode(text))).toEqual(lock);
    expect(parseLock(new TextEncoder().encode('{"lockVersion":1,"images":{}}'))).toBeNull();
    expect(parseLock(new TextEncoder().encode(text.replace('amd64', 'riscv64')))).toBeNull();
  });
});

/** A registry that wants an anonymous token and serves the given documents by digest. */
function registry(documents: Record<string, { type: string; body: string }>) {
  const requests: { url: string; headers: Record<string, string> }[] = [];
  const fetch: Fetch = (url, init) => {
    const headers = init?.headers ?? {};
    requests.push({ url, headers });
    if (url.startsWith('https://auth.example/token')) {
      return Promise.resolve(Response.json({ token: 'anonymous' }));
    }
    if (headers.Authorization !== 'Bearer anonymous') {
      return Promise.resolve(
        new Response(null, {
          status: 401,
          headers: {
            'WWW-Authenticate':
              'Bearer realm="https://auth.example/token",service="registry.example"',
          },
        }),
      );
    }
    const digest = url.split('/').pop() ?? '';
    const document = documents[digest];
    return Promise.resolve(
      document === undefined
        ? new Response(null, { status: 404 })
        : new Response(document.body, { headers: { 'Content-Type': document.type } }),
    );
  };
  return { fetch, requests };
}

describe('pithyx lock', () => {
  const amd64Config = digestOf('amd64 config');
  const arm64Config = digestOf('arm64 config');
  const amd64 = JSON.stringify({
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: { digest: amd64Config },
  });
  const arm64 = JSON.stringify({
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: { digest: arm64Config },
  });
  const index = JSON.stringify({
    mediaType: 'application/vnd.oci.image.index.v1+json',
    manifests: [
      { digest: digestOf(amd64), platform: { os: 'linux', architecture: 'amd64' } },
      { digest: digestOf('armv7'), platform: { os: 'linux', architecture: 'arm', variant: 'v7' } },
      { digest: digestOf(arm64), platform: { os: 'linux', architecture: 'arm64', variant: 'v8' } },
      { digest: digestOf('attestation'), platform: { os: 'unknown', architecture: 'unknown' } },
    ],
  });
  const reference = `registry.example/team/app:1.0@${digestOf(index)}`;

  it('resolves every architecture of a multi-platform image with an anonymous token', async () => {
    const { fetch, requests } = registry({
      [digestOf(index)]: { type: 'application/vnd.oci.image.index.v1+json', body: index },
      [digestOf(amd64)]: { type: 'application/vnd.oci.image.manifest.v1+json', body: amd64 },
      [digestOf(arm64)]: { type: 'application/vnd.oci.image.manifest.v1+json', body: arm64 },
    });
    const lock = await resolveLock([reference], new RegistryClient(fetch));
    expect(lock.images[reference]).toEqual({
      amd64: { manifest: digestOf(amd64), config: amd64Config },
      arm64: { manifest: digestOf(arm64), config: arm64Config },
    });
    expect(requests[0]?.url).toBe(
      `https://registry.example/v2/team/app/manifests/${digestOf(index)}`,
    );
    const token = requests.find((request) => request.url.startsWith('https://auth.example'));
    expect(token?.url).toBe(
      'https://auth.example/token?service=registry.example&scope=repository%3Ateam%2Fapp%3Apull',
    );
  });

  it('reads the architecture of a single-platform image from its config', async () => {
    const config = JSON.stringify({ os: 'linux', architecture: 'arm64' });
    const manifest = JSON.stringify({
      mediaType: 'application/vnd.docker.distribution.manifest.v2+json',
      config: { digest: digestOf(config) },
    });
    const { fetch } = registry({
      [digestOf(manifest)]: {
        type: 'application/vnd.docker.distribution.manifest.v2+json',
        body: manifest,
      },
      [digestOf(config)]: { type: 'application/octet-stream', body: config },
    });
    const single = `registry.example/team/app@${digestOf(manifest)}`;
    expect(await new RegistryClient(fetch).resolve(single)).toEqual({
      arm64: { manifest: digestOf(manifest), config: digestOf(config) },
    });
  });

  it('refuses a document that does not match its digest', async () => {
    const { fetch } = registry({
      [digestOf(index)]: { type: 'application/vnd.oci.image.index.v1+json', body: `${index} ` },
    });
    await expect(new RegistryClient(fetch).resolve(reference)).rejects.toThrow(/does not match/);
  });

  it('refuses an image without an architecture of Pithyx', async () => {
    const windows = JSON.stringify({
      mediaType: 'application/vnd.oci.image.index.v1+json',
      manifests: [{ digest: digestOf('w'), platform: { os: 'windows', architecture: 'amd64' } }],
    });
    const { fetch } = registry({
      [digestOf(windows)]: { type: 'application/vnd.oci.image.index.v1+json', body: windows },
    });
    await expect(
      resolveLock([`registry.example/w@${digestOf(windows)}`], new RegistryClient(fetch)),
    ).rejects.toThrow(/no image for linux/);
  });

  it('reads bearer challenges', () => {
    expect(bearerChallenge('Bearer realm="https://a/token",service="b",scope="c"')).toEqual({
      realm: 'https://a/token',
      service: 'b',
      scope: 'c',
    });
    expect(bearerChallenge('Basic realm="x"')).toBeNull();
  });
});
