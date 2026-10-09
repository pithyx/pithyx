import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parse } from 'yaml';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { buildServices, imageName, pinnedCompose } from './build';
import { readOciArchive } from './oci';
import { tarBlocks } from './tar';
import { readTar } from './untar';

let scratch: string;

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'pithyx-build-test-'));
});

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true });
});

const devCompose = new TextEncoder().encode(`# The chat's backend.
services:
  server:
    build:
      context: server
      dockerfile: docker/Dockerfile.prod
      target: runtime
      args:
        PORT: 8080
    volumes:
      - data:/data
  web:
    build: ./web
  db:
    image: postgres:17.6@sha256:${'a'.repeat(64)}
volumes:
  data:
`);

describe('pithyx build', () => {
  it('finds the services to build with their paths from the project root', () => {
    expect(buildServices(devCompose, 'backend/compose.dev.yaml')).toEqual([
      {
        name: 'server',
        context: 'backend/server',
        dockerfile: 'backend/server/docker/Dockerfile.prod',
        target: 'runtime',
        args: { PORT: '8080' },
      },
      { name: 'web', context: 'backend/web', dockerfile: 'backend/web/Dockerfile', args: {} },
    ]);
  });

  it('replaces build by the pinned image and keeps the rest and the comments', () => {
    const server = `${imageName('ghcr.io/example/apps', { id: 'com.example.chat', version: '1.2.0' }, 'server')}@sha256:${'b'.repeat(64)}`;
    const text = pinnedCompose(devCompose, new Map([['server', server]]));
    expect(text).toContain("# The chat's backend.");
    expect(text.startsWith('# Written by pithyx build')).toBe(true);
    const parsed = parse(text) as { services: Record<string, Record<string, unknown>> };
    expect(parsed.services.server).toEqual({ volumes: ['data:/data'], image: server });
    expect(parsed.services.db?.image).toBe(`postgres:17.6@sha256:${'a'.repeat(64)}`);
    expect(server).toBe(
      `ghcr.io/example/apps/com.example.chat/server:1.2.0@sha256:${'b'.repeat(64)}`,
    );
  });
});

function blob(content: object | string): { digest: string; bytes: Buffer } {
  const bytes = Buffer.from(typeof content === 'string' ? content : JSON.stringify(content));
  return { digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}`, bytes };
}

function archive(files: Record<string, Buffer>): string {
  const path = join(scratch, 'image.tar');
  const blocks = [
    ...tarBlocks(
      Object.entries(files).map(([name, bytes]) => ({
        path: name,
        size: bytes.length,
        read: () => bytes,
      })),
    ),
    Buffer.alloc(1024),
  ];
  writeFileSync(path, Buffer.concat(blocks));
  return path;
}

describe('OCI archives', () => {
  it('reads the index digest and each platform of a multi-platform build', () => {
    const configs = {
      amd64: blob({ architecture: 'amd64', os: 'linux' }),
      arm64: blob({ architecture: 'arm64', os: 'linux' }),
    };
    const manifests = {
      amd64: blob({ schemaVersion: 2, config: { digest: configs.amd64.digest } }),
      arm64: blob({ schemaVersion: 2, config: { digest: configs.arm64.digest } }),
    };
    const attestation = blob({ schemaVersion: 2, config: { digest: configs.amd64.digest } });
    const index = blob({
      schemaVersion: 2,
      mediaType: 'application/vnd.oci.image.index.v1+json',
      manifests: [
        { digest: manifests.amd64.digest, platform: { os: 'linux', architecture: 'amd64' } },
        { digest: manifests.arm64.digest, platform: { os: 'linux', architecture: 'arm64' } },
        { digest: attestation.digest, platform: { os: 'unknown', architecture: 'unknown' } },
      ],
    });
    const files: Record<string, Buffer> = {
      'index.json': Buffer.from(
        JSON.stringify({
          manifests: [
            { mediaType: 'application/vnd.oci.image.index.v1+json', digest: index.digest },
          ],
        }),
      ),
    };
    for (const item of [
      index,
      attestation,
      ...Object.values(configs),
      ...Object.values(manifests),
    ]) {
      files[`blobs/sha256/${item.digest.slice(7)}`] = item.bytes;
    }
    const image = readOciArchive(archive(files));
    expect(image).toEqual({
      digest: index.digest,
      platforms: {
        amd64: { manifest: manifests.amd64.digest, config: configs.amd64.digest },
        arm64: { manifest: manifests.arm64.digest, config: configs.arm64.digest },
      },
    });
  });

  it('reads a single manifest as docker save writes it and checks every blob', () => {
    const config = blob({ architecture: 'amd64', os: 'linux' });
    const manifest = blob({ schemaVersion: 2, config: { digest: config.digest } });
    const files: Record<string, Buffer> = {
      'index.json': Buffer.from(JSON.stringify({ manifests: [{ digest: manifest.digest }] })),
      [`blobs/sha256/${config.digest.slice(7)}`]: config.bytes,
      [`blobs/sha256/${manifest.digest.slice(7)}`]: manifest.bytes,
    };
    expect(readOciArchive(archive(files))).toEqual({
      digest: manifest.digest,
      platforms: { amd64: { manifest: manifest.digest, config: config.digest } },
    });
    files[`blobs/sha256/${config.digest.slice(7)}`] = Buffer.from('tampered');
    expect(() => readOciArchive(archive(files))).toThrow(/does not match its digest/);
  });

  it('reads long names from pax headers', () => {
    const name = `${'folder/'.repeat(20)}file.txt`;
    const path = archive({ [name]: Buffer.from('x') });
    expect([...readTar(readFileSync(path)).keys()]).toEqual([name]);
  });
});
