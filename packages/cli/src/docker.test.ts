import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { saveImages, type DockerCommand } from './docker';
import type { ImagesLock } from './lock';
import { tarBlocks } from './tar';

const config = `sha256:${'c'.repeat(64)}`;
const manifest = `sha256:${'d'.repeat(64)}`;
const hex = config.slice('sha256:'.length);

function lockFor(reference: string): ImagesLock {
  return { lockVersion: 1, images: { [reference]: { amd64: { manifest, config } } } };
}

/** Writes what `docker save` writes: an OCI layout whose blobs include the image config. */
function writeArchive(file: string, configHex: string) {
  const entry = (path: string, text: string) => {
    const bytes = Buffer.from(text);
    return { path, size: bytes.length, read: () => bytes };
  };
  writeFileSync(
    file,
    Buffer.concat([
      ...tarBlocks([entry('index.json', '{}'), entry(`blobs/sha256/${configHex}`, '{}')]),
    ]),
  );
}

/**
 * A Docker that knows images by the given references and answers like the real one: `save` writes the archive, an
 * unknown reference fails with "No such image", and an old Docker refuses `--platform`.
 */
function fakeDocker(
  known: readonly string[],
  options: { oldSave?: boolean; pullable?: boolean } = {},
) {
  const images = new Set(known);
  const calls: string[] = [];
  const answer = (args: readonly string[]): string => {
    calls.push(args.join(' '));
    if (args[0] === 'pull') {
      if (options.pullable !== true) {
        throw new Error(`docker pull failed (1): pull access denied for ${String(args.at(-1))}`);
      }
      images.add(String(args.at(-1)));
      return '';
    }
    if (args[0] === 'save') {
      if (options.oldSave === true && args.includes('--platform')) {
        throw new Error('docker save failed (125): unknown flag: --platform');
      }
      const reference = String(args.at(-1));
      if (!images.has(reference)) {
        throw new Error(`docker save failed (1): No such image: ${reference}`);
      }
      writeArchive(String(args[args.indexOf('--output') + 1]), hex);
      return '';
    }
    throw new Error(`unexpected docker ${args.join(' ')}`);
  };
  const docker: DockerCommand = (args) =>
    new Promise((resolve) => {
      resolve(answer(args));
    });
  return { docker, calls };
}

describe('saving the locked images', () => {
  let folder: string;
  beforeEach(() => {
    folder = mkdtempSync(join(tmpdir(), 'pithyx-save-'));
    vi.spyOn(console, 'log').mockImplementation(() => undefined);
  });
  afterEach(() => {
    rmSync(folder, { recursive: true, force: true });
    vi.restoreAllMocks();
  });

  it('takes the image id of the classic image store first', async () => {
    const { docker, calls } = fakeDocker([config]);
    const entries = await saveImages(lockFor(`ghcr.io/example/app@${manifest}`), folder, docker);
    expect(entries).toEqual([
      { path: `backend/images/amd64/${hex}.tar`, source: join(folder, `amd64-${hex}.tar`) },
    ]);
    expect(calls).toHaveLength(1);
  });

  it('saves a pithyx build image of the containerd image store by its manifest, without pulling', async () => {
    const reference = `pithyx.local/com.example.lists/server:1.0.0@${manifest}`;
    const { docker, calls } = fakeDocker([`pithyx.local/com.example.lists/server@${manifest}`]);
    await saveImages(lockFor(reference), folder, docker);
    expect(calls.some((call) => call.startsWith('pull'))).toBe(false);
  });

  it('falls back to a save without --platform on Docker before 27', async () => {
    const { docker, calls } = fakeDocker([config], { oldSave: true });
    await saveImages(lockFor(`ghcr.io/example/app@${manifest}`), folder, docker);
    expect(calls).toEqual([
      `save --platform linux/amd64 --output ${join(folder, `amd64-${hex}.tar`)} ${config}`,
      `save --output ${join(folder, `amd64-${hex}.tar`)} ${config}`,
    ]);
  });

  it('pulls an image from a registry by its manifest when Docker does not have it', async () => {
    const { docker, calls } = fakeDocker([], { pullable: true });
    await saveImages(lockFor(`ghcr.io/example/app:1@${manifest}`), folder, docker);
    expect(calls).toContain(`pull --quiet --platform linux/amd64 ghcr.io/example/app@${manifest}`);
  });

  it('never pulls a pithyx build image and names every attempt when it is missing', async () => {
    const { docker, calls } = fakeDocker([]);
    const saving = saveImages(
      lockFor(`pithyx.local/com.example.lists/server:1.0.0@${manifest}`),
      folder,
      docker,
    );
    await expect(saving).rejects.toThrow(/built for this machine.*run pithyx build again/s);
    await expect(saving).rejects.toThrow(`No such image: ${config}`);
    expect(calls.some((call) => call.startsWith('pull'))).toBe(false);
  });

  it('refuses an archive without the locked config', async () => {
    const { docker } = fakeDocker([`ghcr.io/example/app@${manifest}`]);
    const wrong = `sha256:${'e'.repeat(64)}`;
    const lock: ImagesLock = {
      lockVersion: 1,
      images: { [`ghcr.io/example/app@${manifest}`]: { amd64: { manifest, config: wrong } } },
    };
    await expect(saveImages(lock, folder, docker)).rejects.toThrow(/without the config/);
  });
});
