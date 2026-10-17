import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { LOCAL_PREFIX } from './build';
import { ARCHITECTURES, parseReference, type ImagesLock } from './lock';
import type { PackageEntry } from './project';
import { readTar } from './untar';

/** Runs a command and resolves with its output; rejects with its error output when it fails. */
export function run(
  command: string,
  args: readonly string[],
  options: { inherit?: boolean; env?: NodeJS.ProcessEnv } = {},
): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      stdio: options.inherit === true ? 'inherit' : ['ignore', 'pipe', 'pipe'],
      env: options.env ?? process.env,
    });
    let stdout = '';
    let stderr = '';
    child.stdout?.on('data', (chunk: Buffer) => (stdout += chunk.toString()));
    child.stderr?.on('data', (chunk: Buffer) => (stderr += chunk.toString()));
    child.on('error', (failure) => {
      reject(new Error(`${command} could not be started: ${failure.message}`));
    });
    child.on('close', (code) => {
      if (code === 0) {
        resolve(stdout);
      } else {
        reject(
          new Error(`${command} ${args.join(' ')} failed (${String(code)}): ${stderr.trim()}`),
        );
      }
    });
  });
}

/**
 * Credentials for a registry from the Docker configuration (`~/.docker/config.json`, or `$DOCKER_CONFIG`), when it
 * stores them itself under `auths`. Credentials kept by a credential helper are not read; those registries are asked
 * anonymously.
 */
export function dockerCredentials(registry: string): string | undefined {
  const file = join(process.env.DOCKER_CONFIG ?? join(homedir(), '.docker'), 'config.json');
  if (!existsSync(file)) {
    return undefined;
  }
  try {
    const config = JSON.parse(readFileSync(file, 'utf8')) as {
      auths?: Record<string, { auth?: string }>;
    };
    const keys =
      registry === 'registry-1.docker.io'
        ? ['https://index.docker.io/v1/', 'index.docker.io', 'docker.io']
        : [registry, `https://${registry}`];
    for (const key of keys) {
      const auth = config.auths?.[key]?.auth;
      if (auth !== undefined && auth !== '') {
        return Buffer.from(auth, 'base64').toString('utf8');
      }
    }
  } catch {
    return undefined;
  }
  return undefined;
}

/** Runs a Docker command and resolves with its output; {@link saveImages} takes another one in tests. */
export type DockerCommand = (args: readonly string[]) => Promise<string>;

const docker: DockerCommand = (args) => run('docker', args);

function message(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure);
}

/** Whether a `docker save` archive holds the image config `hex`, in the OCI layout or the older Docker layout. */
function holdsConfig(file: string, hex: string): boolean {
  const files = readTar(readFileSync(file));
  return files.has(`blobs/sha256/${hex}`) || files.has(`${hex}.json`);
}

/**
 * Saves the first of `references` that the local Docker has and whose archive holds the locked config, asking for the
 * one platform where Docker knows `--platform`. Collects why each attempt failed.
 */
async function saveLocal(
  run: DockerCommand,
  references: readonly string[],
  arch: string,
  hex: string,
  file: string,
  failures: string[],
): Promise<boolean> {
  for (const reference of references) {
    try {
      try {
        await run(['save', '--platform', `linux/${arch}`, '--output', file, reference]);
      } catch (failure) {
        // Docker before 27 saves without choosing a platform.
        if (!/unknown flag/i.test(message(failure))) {
          throw failure;
        }
        await run(['save', '--output', file, reference]);
      }
      if (holdsConfig(file, hex)) {
        return true;
      }
      failures.push(`docker save ${reference} wrote an image without the config sha256:${hex}`);
    } catch (failure) {
      failures.push(message(failure));
    }
  }
  return false;
}

/**
 * Saves every locked image per architecture with the local Docker into `folder`, and returns them as package files
 * `backend/images/<arch>/<config digest>.tar`. It first saves what Docker already has, by the image id of the classic
 * image store (the config digest) or by the platform manifest's digest (the containerd image store, where the image id
 * is the manifest's or the index's digest), and checks that the archive holds the locked config. Anything else is
 * pulled by the manifest's digest first; an image `pithyx build` made for this machine (`pithyx.local/*`) is never
 * pulled. A failure names every attempt.
 */
export async function saveImages(
  lock: ImagesLock,
  folder: string,
  run: DockerCommand = docker,
): Promise<PackageEntry[]> {
  const entries: PackageEntry[] = [];
  for (const [reference, platforms] of Object.entries(lock.images)) {
    const { registry, repository } = parseReference(reference);
    const name = registry === 'registry-1.docker.io' ? repository : `${registry}/${repository}`;
    for (const arch of ARCHITECTURES) {
      const platform = platforms[arch];
      const hex = platform?.config.slice('sha256:'.length) ?? '';
      // Two references to one image share its file.
      if (
        platform === undefined ||
        entries.some((entry) => entry.path.endsWith(`/${arch}/${hex}.tar`))
      ) {
        continue;
      }
      const pinned = `${name}@${platform.manifest}`;
      const file = join(folder, `${arch}-${hex}.tar`);
      const failures: string[] = [];
      console.log(`Saving ${pinned} for ${arch}`);
      let saved = await saveLocal(
        run,
        [platform.config, pinned, platform.manifest],
        arch,
        hex,
        file,
        failures,
      );
      if (!saved && registry === LOCAL_PREFIX) {
        throw new Error(
          `${pinned} was built for this machine, but the local Docker does not have it; run pithyx build again.\n  ${failures.join('\n  ')}`,
        );
      }
      if (!saved) {
        try {
          await run(['pull', '--quiet', '--platform', `linux/${arch}`, pinned]);
          saved = await saveLocal(
            run,
            [pinned, platform.config, platform.manifest],
            arch,
            hex,
            file,
            failures,
          );
        } catch (failure) {
          failures.push(message(failure));
        }
      }
      if (!saved) {
        throw new Error(`${pinned} could not be saved for ${arch}:\n  ${failures.join('\n  ')}`);
      }
      entries.push({ path: `backend/images/${arch}/${hex}.tar`, source: file });
    }
  }
  return entries;
}
