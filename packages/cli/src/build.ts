import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { arch, tmpdir } from 'node:os';
import { join, posix } from 'node:path';
import type { AppManifest } from '@pithyx/sdk';
import { isMap, isScalar, parse, parseDocument } from 'yaml';
import { devComposePath, validateCompose } from './compose';
import { dockerCredentials, run } from './docker';
import { describe, error, type Finding } from './findings';
import {
  ARCHITECTURES,
  formatLock,
  LOCK_FILE,
  RegistryClient,
  resolveLock,
  type Architecture,
  type ImagesLock,
} from './lock';
import { readOciArchive, type OciImage } from './oci';
import { readProject } from './project';

/** The registry prefix of images built for this machine only: they travel in the package, never through a registry. */
export const LOCAL_PREFIX = 'pithyx.local';

/** One service of `compose.dev.yaml` that `pithyx build` builds. */
export interface BuildService {
  name: string;
  /** The build context relative to the project root, with `/`. */
  context: string;
  /** The Dockerfile relative to the project root. */
  dockerfile: string;
  target?: string;
  args: Record<string, string>;
}

export interface BuildOptions {
  /** Build reproducibly for amd64 and arm64 into OCI archives `<service>.tar` in this folder, without pushing. */
  ociOut?: string | undefined;
  /** Pin `compose.yaml` and the lock to the archives in `ociOut` as pushed to `<pin>/<id>/<service>:<version>`. */
  pin?: string | undefined;
  /** Accept an app id in `org.pithyx.*`, for the apps of the official catalog (D381). */
  allowReservedId?: boolean | undefined;
}

/** The image reference of a built service: `<prefix>/<id>/<service>:<version>`. */
export function imageName(
  prefix: string,
  manifest: Pick<AppManifest, 'id' | 'version'>,
  service: string,
): string {
  return `${prefix}/${manifest.id}/${service}:${manifest.version}`;
}

/** The services of a checked `compose.dev.yaml` that have `build`, with their paths relative to the project root. */
export function buildServices(devCompose: Uint8Array, composePath: string): BuildService[] {
  const root = parse(new TextDecoder().decode(devCompose)) as {
    services: Record<string, { build?: unknown }>;
  };
  const folder = posix.dirname(composePath);
  const services: BuildService[] = [];
  for (const [name, service] of Object.entries(root.services)) {
    const build = service.build;
    if (build === undefined) {
      continue;
    }
    const spec =
      typeof build === 'string'
        ? { context: build }
        : (build as {
            context: string;
            dockerfile?: string;
            target?: string;
            args?: Record<string, unknown>;
          });
    const context = posix.normalize(posix.join(folder, spec.context));
    services.push({
      name,
      context,
      dockerfile: posix.join(context, spec.dockerfile ?? 'Dockerfile'),
      ...(spec.target === undefined ? {} : { target: spec.target }),
      args: Object.fromEntries(
        Object.entries(spec.args ?? {}).map(([key, value]) => [key, String(value)]),
      ),
    });
  }
  return services;
}

/**
 * `compose.yaml` from `compose.dev.yaml`: every `build` replaced by its image pinned by digest, everything else (and
 * the comments) as it was, under a line that says where the file comes from.
 */
export function pinnedCompose(devCompose: Uint8Array, images: ReadonlyMap<string, string>): string {
  const document = parseDocument(new TextDecoder().decode(devCompose));
  const services = document.get('services');
  if (!isMap(services)) {
    throw new Error('compose.dev.yaml has no services');
  }
  for (const pair of services.items) {
    const name = isScalar(pair.key) ? String(pair.key.value) : '';
    const service = pair.value;
    const image = images.get(name);
    if (image !== undefined && isMap(service)) {
      service.delete('build');
      service.set('image', image);
    }
  }
  document.commentBefore = ` Written by pithyx build from compose.dev.yaml; edit that file and build again.${
    document.commentBefore == null ? '' : `\n${document.commentBefore}`
  }`;
  return document.toString({ lineWidth: 0 });
}

/** The architecture of this machine as Docker names it. */
export function hostArchitecture(): Architecture {
  const name = arch() === 'x64' ? 'amd64' : arch();
  if (!ARCHITECTURES.includes(name as Architecture)) {
    throw new Error(`pithyx build runs on amd64 and arm64, not on ${name}`);
  }
  return name as Architecture;
}

/** `SOURCE_DATE_EPOCH` as the environment or the project's last git commit gives it. */
function sourceDateEpoch(root: string): string {
  const fromEnvironment = process.env.SOURCE_DATE_EPOCH;
  if (fromEnvironment !== undefined && /^\d+$/.test(fromEnvironment)) {
    return fromEnvironment;
  }
  try {
    return execFileSync('git', ['log', '-1', '--format=%ct'], {
      cwd: root,
      encoding: 'utf8',
    }).trim();
  } catch {
    throw new Error(
      'a reproducible build needs SOURCE_DATE_EPOCH or a git checkout to take the commit time from',
    );
  }
}

function buildArgs(root: string, service: BuildService): string[] {
  return [
    '--file',
    join(root, service.dockerfile),
    ...(service.target === undefined ? [] : ['--target', service.target]),
    ...Object.entries(service.args).flatMap(([key, value]) => ['--build-arg', `${key}=${value}`]),
    join(root, service.context),
  ];
}

interface Prepared {
  manifest: AppManifest;
  root: string;
  compose: string;
  devCompose: Uint8Array;
  services: BuildService[];
}

/** Reads the project and checks `compose.dev.yaml` as a catalog does; throws with every problem. */
function prepare(folder: string, allowReservedId: boolean): Prepared {
  const project = readProject(folder, { allowReservedId });
  const manifest = project.manifest;
  const compose = manifest?.backend?.compose;
  if (manifest === undefined || compose === undefined) {
    throw new Error(
      manifest === undefined ? 'pithyx.json is not valid' : 'the app has no backend to build',
    );
  }
  const path = devComposePath(compose);
  const file = join(project.root, path);
  if (!existsSync(file)) {
    throw new Error(`${path} is missing: pithyx build reads the backend's source form from it`);
  }
  const devCompose = readFileSync(file);
  const findings: Finding[] = validateCompose(devCompose, manifest, { dev: true }).issues.map(
    error,
  );
  if (findings.length > 0) {
    throw new Error(`${path}:\n${findings.map((finding) => describe(finding)).join('\n')}`);
  }
  return {
    manifest,
    root: project.root,
    compose,
    devCompose,
    services: buildServices(devCompose, path),
  };
}

/**
 * Writes `compose.yaml` and the lock: the built services from their images, the other services' images resolved from
 * their registries. The result must pass the box's own check.
 */
async function writePinned(
  prepared: Prepared,
  built: ReadonlyMap<string, { reference: string; image: OciImage }>,
): Promise<void> {
  const { manifest, root, compose, devCompose } = prepared;
  const pinned = pinnedCompose(
    devCompose,
    new Map([...built].map(([service, { reference }]) => [service, reference])),
  );
  const pinnedBytes = new TextEncoder().encode(pinned);
  const issues = validateCompose(pinnedBytes, manifest).issues.map(error);
  if (issues.length > 0) {
    throw new Error(`the pinned ${compose}:\n${issues.map((issue) => describe(issue)).join('\n')}`);
  }
  const builtReferences = new Set([...built.values()].map(({ reference }) => reference));
  const others = [
    ...new Set(
      Object.values(
        (parse(pinned) as { services: Record<string, { image: string }> }).services,
      ).map((service) => service.image),
    ),
  ].filter((image) => !builtReferences.has(image));
  const lock: ImagesLock =
    others.length === 0
      ? { lockVersion: 1, images: {} }
      : await resolveLock(others, new RegistryClient(fetch, dockerCredentials));
  for (const { reference, image } of built.values()) {
    lock.images[reference] = image.platforms;
  }
  writeFileSync(join(root, compose), pinned);
  mkdirSync(join(root, posix.dirname(LOCK_FILE)), { recursive: true });
  writeFileSync(join(root, LOCK_FILE), formatLock(lock));
}

/**
 * `pithyx build`. Without options it builds every service of `compose.dev.yaml` that has `build` with the local
 * Docker for this machine, tags it `pithyx.local/<id>/<service>:<version>` and pins `compose.yaml` and the lock to it,
 * so `pithyx pack --images` puts it into the package. `ociOut` instead builds reproducibly for amd64 and arm64 with
 * buildx into OCI archives (the commit time as `SOURCE_DATE_EPOCH`, timestamps rewritten, no attestations) and pushes
 * nothing; `pin` then pins `compose.yaml` and the lock to those archives as a registry holds them after a push.
 */
export async function build(folder: string, options: BuildOptions = {}): Promise<string[]> {
  const prepared = prepare(folder, options.allowReservedId === true);
  const { manifest, root, services } = prepared;
  const report: string[] = [];
  if (options.pin !== undefined) {
    if (options.ociOut === undefined) {
      throw new Error('--pin reads the archives of --oci-out: name that folder');
    }
    const built = new Map<string, { reference: string; image: OciImage }>();
    for (const service of services) {
      const image = readOciArchive(join(options.ociOut, `${service.name}.tar`));
      const reference = `${imageName(options.pin, manifest, service.name)}@${image.digest}`;
      built.set(service.name, { reference, image });
      report.push(`Pinned ${service.name} to ${reference}`);
    }
    await writePinned(prepared, built);
    return report;
  }
  if (options.ociOut !== undefined) {
    mkdirSync(options.ociOut, { recursive: true });
    const env = { ...process.env, SOURCE_DATE_EPOCH: sourceDateEpoch(root) };
    for (const service of services) {
      const target = join(options.ociOut, `${service.name}.tar`);
      await run(
        'docker',
        [
          'buildx',
          'build',
          '--platform',
          ARCHITECTURES.map((name) => `linux/${name}`).join(','),
          '--provenance=false',
          '--sbom=false',
          '--output',
          `type=oci,dest=${target},rewrite-timestamp=true`,
          ...buildArgs(root, service),
        ],
        { inherit: true, env },
      );
      const image = readOciArchive(target);
      report.push(
        `Built ${service.name} for ${Object.keys(image.platforms).join(', ')} into ${target} (${image.digest})`,
      );
    }
    return report;
  }
  const host = hostArchitecture();
  const scratch = mkdtempSync(join(tmpdir(), 'pithyx-build-'));
  try {
    const built = new Map<string, { reference: string; image: OciImage }>();
    for (const service of services) {
      const tag = imageName(LOCAL_PREFIX, manifest, service.name);
      await run('docker', ['build', '--tag', tag, ...buildArgs(root, service)], { inherit: true });
      // Docker 25 and later save an OCI image layout, which names the manifest digest the lock needs.
      const saved = join(scratch, `${service.name}.tar`);
      await run('docker', ['save', '--output', saved, tag]);
      const image = readOciArchive(saved);
      if (image.platforms[host] === undefined) {
        throw new Error(`docker built ${tag} for another architecture than ${host}`);
      }
      built.set(service.name, { reference: `${tag}@${image.digest}`, image });
      report.push(`Built ${tag} for ${host}`);
    }
    await writePinned(prepared, built);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
  return report;
}
