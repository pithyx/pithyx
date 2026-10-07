import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { ARCHITECTURES, parseReference, type ImagesLock } from './lock';
import type { PackageEntry } from './project';

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

/**
 * Saves every locked image per architecture with the local Docker (`docker pull --platform` by the platform
 * manifest's digest, then `docker save`) into `folder`, and returns them as package files
 * `backend/images/<arch>/<config digest>.tar`. The image id after the pull must be the config digest of the lock.
 */
export async function saveImages(lock: ImagesLock, folder: string): Promise<PackageEntry[]> {
  const entries: PackageEntry[] = [];
  for (const [reference, platforms] of Object.entries(lock.images)) {
    const { registry, repository } = parseReference(reference);
    const name = registry === 'registry-1.docker.io' ? repository : `${registry}/${repository}`;
    for (const arch of ARCHITECTURES) {
      const platform = platforms[arch];
      const hex = platform?.config.slice('sha256:'.length);
      // Two references to one image share its file.
      if (
        platform === undefined ||
        entries.some((entry) => entry.path.endsWith(`/${arch}/${String(hex)}.tar`))
      ) {
        continue;
      }
      const pinned = `${name}@${platform.manifest}`;
      console.log(`Saving ${pinned} for ${arch}`);
      await run('docker', ['pull', '--quiet', '--platform', `linux/${arch}`, pinned]);
      const id = (await run('docker', ['image', 'inspect', '--format', '{{.Id}}', pinned])).trim();
      if (id !== platform.config) {
        throw new Error(`${pinned} has the image id ${id}, but the lock says ${platform.config}`);
      }
      const file = join(folder, `${arch}-${String(hex)}.tar`);
      await run('docker', ['save', '--output', file, id]);
      entries.push({ path: `backend/images/${arch}/${String(hex)}.tar`, source: file });
    }
  }
  return entries;
}
