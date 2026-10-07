import { createHash } from 'node:crypto';
import schema from '@pithyx/sdk/schema/images-lock.v1.schema.json' with { type: 'json' };
import { Ajv2020 } from 'ajv/dist/2020.js';
import { parse } from 'yaml';

/** Where the lock lives, in the project and in the package. */
export const LOCK_FILE = 'backend/images.lock.json';

/** The architectures of Pithyx, as Docker and the package folders `backend/images/<arch>/` name them. */
export const ARCHITECTURES = ['amd64', 'arm64'] as const;

export type Architecture = (typeof ARCHITECTURES)[number];

/** One image for one architecture: its platform manifest and its config digest, which Docker shows as the image id. */
export interface LockedPlatform {
  manifest: string;
  config: string;
}

/** `backend/images.lock.json` (D315). */
export interface ImagesLock {
  lockVersion: 1;
  images: Record<string, Partial<Record<Architecture, LockedPlatform>>>;
}

const validateSchema: (value: unknown) => boolean = new Ajv2020({
  allErrors: true,
  strict: true,
}).compile(schema);

/** Parses a lock file; null when it is not valid JSON of the lock schema. */
export function parseLock(bytes: Uint8Array): ImagesLock | null {
  try {
    const value: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    return validateSchema(value) ? (value as ImagesLock) : null;
  } catch {
    return null;
  }
}

/** The lock as `pithyx lock` writes it: keys sorted, two spaces, a final line break. */
export function formatLock(lock: ImagesLock): string {
  const images = Object.fromEntries(
    Object.keys(lock.images)
      .sort()
      .map((reference) => {
        const platforms = lock.images[reference] ?? {};
        return [
          reference,
          Object.fromEntries(
            ARCHITECTURES.flatMap((arch) => {
              const platform = platforms[arch];
              return platform === undefined
                ? []
                : [[arch, { manifest: platform.manifest, config: platform.config }]];
            }),
          ),
        ];
      }),
  );
  return `${JSON.stringify({ lockVersion: 1, images }, null, 2)}\n`;
}

/** The image references of a Compose file the validator accepted, sorted and without duplicates. */
export function composeImages(compose: Uint8Array): string[] {
  const root = parse(new TextDecoder().decode(compose)) as {
    services?: Record<string, { image?: unknown } | null>;
  } | null;
  const images = new Set<string>();
  for (const service of Object.values(root?.services ?? {})) {
    if (typeof service?.image === 'string') {
      images.add(service.image);
    }
  }
  return [...images].sort();
}

/** What is wrong with a lock for a Compose file's images: references without an entry, and entries without a reference. */
export function lockDifferences(
  lock: ImagesLock,
  images: readonly string[],
): { missing: string[]; unused: string[] } {
  const locked = Object.keys(lock.images);
  return {
    missing: images.filter((image) => !locked.includes(image)),
    unused: locked.filter((image) => !images.includes(image)).sort(),
  };
}

/** Where an image reference lives: the registry host, the repository and the digest it is pinned to. */
export interface ImageReference {
  registry: string;
  repository: string;
  digest: string;
}

const DOCKER_HUB = 'registry-1.docker.io';

/** Splits a reference the way Docker does: a first part with a dot, a colon or `localhost` is the registry. */
export function parseReference(reference: string): ImageReference {
  const at = reference.lastIndexOf('@');
  const digest = reference.slice(at + 1);
  let name = reference.slice(0, at);
  const slash = name.indexOf('/');
  const first = slash < 0 ? '' : name.slice(0, slash);
  let registry = DOCKER_HUB;
  if (first !== '' && (first.includes('.') || first.includes(':') || first === 'localhost')) {
    registry = first;
    name = name.slice(slash + 1);
  }
  // A tag is only a label once the reference is pinned; the digest decides.
  const tag = name.lastIndexOf(':');
  if (tag > name.lastIndexOf('/')) {
    name = name.slice(0, tag);
  }
  if (registry === DOCKER_HUB && !name.includes('/')) {
    name = `library/${name}`;
  }
  return { registry, repository: name, digest };
}

const INDEX_TYPES = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
];
const MANIFEST_TYPES = [
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.v2+json',
];

/** The part of `fetch` the registry client uses, so tests can answer for a registry. */
export type Fetch = (url: string, init?: { headers?: Record<string, string> }) => Promise<Response>;

/** Credentials for a registry host as `user:password`, or undefined for anonymous access. */
export type Credentials = (registry: string) => string | undefined;

interface IndexEntry {
  digest: string;
  platform?: { os?: string; architecture?: string; variant?: string };
}

function sha256(bytes: Uint8Array): string {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

/** The parameters of a `WWW-Authenticate: Bearer realm="...",service="..."` challenge. */
export function bearerChallenge(header: string | null): Record<string, string> | null {
  if (header === null || !/^bearer\s/i.test(header)) {
    return null;
  }
  const parameters: Record<string, string> = {};
  for (const match of header.slice(7).matchAll(/([a-z]+)="([^"]*)"/gi)) {
    const [, key, value] = match;
    if (key !== undefined && value !== undefined) {
      parameters[key.toLowerCase()] = value;
    }
  }
  return parameters;
}

/**
 * Reads image manifests from registries over the Distribution API: anonymously, or with the credentials of the
 * Docker configuration when the registry asks for them. Every document is checked against the digest it was asked
 * for, so a registry cannot hand out anything else.
 */
export class RegistryClient {
  private readonly tokens = new Map<string, string>();

  constructor(
    private readonly fetch: Fetch,
    private readonly credentials: Credentials = () => undefined,
  ) {}

  private base(registry: string): string {
    const host = registry.split(':')[0];
    const plain = host === 'localhost' || host === '127.0.0.1';
    return `${plain ? 'http' : 'https'}://${registry}/v2/`;
  }

  private async token(registry: string, repository: string, challenge: Record<string, string>) {
    const realm = challenge.realm;
    if (realm === undefined) {
      throw new Error(`${registry} asks for a token without naming where to get it`);
    }
    const url = new URL(realm);
    if (challenge.service !== undefined) {
      url.searchParams.set('service', challenge.service);
    }
    url.searchParams.set('scope', `repository:${repository}:pull`);
    const secret = this.credentials(registry);
    const response = await this.fetch(url.toString(), {
      headers:
        secret === undefined
          ? {}
          : { Authorization: `Basic ${Buffer.from(secret).toString('base64')}` },
    });
    if (!response.ok) {
      throw new Error(
        `${registry} refused a token for ${repository} (HTTP ${String(response.status)})`,
      );
    }
    const body = (await response.json()) as { token?: string; access_token?: string };
    const token = body.token ?? body.access_token;
    if (token === undefined) {
      throw new Error(`${registry} sent no token for ${repository}`);
    }
    return token;
  }

  /** A document of the repository by digest, checked against that digest. */
  async get(
    registry: string,
    repository: string,
    kind: 'manifests' | 'blobs',
    digest: string,
  ): Promise<{ type: string; body: Uint8Array }> {
    const url = `${this.base(registry)}${repository}/${kind}/${digest}`;
    const key = `${registry}/${repository}`;
    const request = (token: string | undefined) => {
      const headers: Record<string, string> = {
        Accept: [...INDEX_TYPES, ...MANIFEST_TYPES].join(', '),
      };
      if (token !== undefined) {
        headers.Authorization = `Bearer ${token}`;
      } else {
        const secret = this.credentials(registry);
        if (secret !== undefined && registry !== DOCKER_HUB) {
          headers.Authorization = `Basic ${Buffer.from(secret).toString('base64')}`;
        }
      }
      return this.fetch(url, { headers });
    };
    let response = await request(this.tokens.get(key));
    if (response.status === 401) {
      const challenge = bearerChallenge(response.headers.get('www-authenticate'));
      if (challenge !== null) {
        const token = await this.token(registry, repository, challenge);
        this.tokens.set(key, token);
        response = await request(token);
      }
    }
    if (!response.ok) {
      throw new Error(
        `${registry}/${repository} answered HTTP ${String(response.status)} for ${digest}`,
      );
    }
    const body = new Uint8Array(await response.arrayBuffer());
    if (sha256(body) !== digest) {
      throw new Error(`${registry}/${repository} sent a document that does not match ${digest}`);
    }
    return { type: (response.headers.get('content-type') ?? '').split(';')[0]?.trim() ?? '', body };
  }

  /** The platform manifest and config digest per Pithyx architecture of one pinned reference. */
  async resolve(reference: string): Promise<Partial<Record<Architecture, LockedPlatform>>> {
    const { registry, repository, digest } = parseReference(reference);
    const top = await this.get(registry, repository, 'manifests', digest);
    const document = JSON.parse(new TextDecoder().decode(top.body)) as {
      mediaType?: string;
      manifests?: IndexEntry[];
      config?: { digest?: string };
    };
    const type = document.mediaType ?? top.type;
    const result: Partial<Record<Architecture, LockedPlatform>> = {};
    if (INDEX_TYPES.includes(type) || Array.isArray(document.manifests)) {
      for (const arch of ARCHITECTURES) {
        const candidates = (document.manifests ?? []).filter(
          (entry) => entry.platform?.os === 'linux' && entry.platform.architecture === arch,
        );
        // arm64 images may name the variant v8; any other variant is not what Pithyx runs.
        const entry = candidates.find(
          (candidate) =>
            candidate.platform?.variant === undefined || candidate.platform.variant === 'v8',
        );
        if (entry !== undefined) {
          const manifest = await this.get(registry, repository, 'manifests', entry.digest);
          result[arch] = { manifest: entry.digest, config: configDigest(manifest.body, reference) };
        }
      }
      return result;
    }
    const config = configDigest(top.body, reference);
    const blob = await this.get(registry, repository, 'blobs', config);
    const image = JSON.parse(new TextDecoder().decode(blob.body)) as {
      os?: string;
      architecture?: string;
    };
    const arch = ARCHITECTURES.find((candidate) => candidate === image.architecture);
    if (image.os === 'linux' && arch !== undefined) {
      result[arch] = { manifest: digest, config };
    }
    return result;
  }
}

function configDigest(manifest: Uint8Array, reference: string): string {
  const document = JSON.parse(new TextDecoder().decode(manifest)) as {
    config?: { digest?: unknown };
  };
  const digest = document.config?.digest;
  if (typeof digest !== 'string' || !/^sha256:[a-f0-9]{64}$/.test(digest)) {
    throw new Error(`the manifest of ${reference} names no sha256 config`);
  }
  return digest;
}

/** Resolves a lock for the images; an image without a Pithyx architecture is an error. */
export async function resolveLock(
  images: readonly string[],
  client: RegistryClient,
): Promise<ImagesLock> {
  const lock: ImagesLock = { lockVersion: 1, images: {} };
  for (const image of images) {
    const platforms = await client.resolve(image);
    if (Object.keys(platforms).length === 0) {
      throw new Error(`${image} has no image for linux/amd64 or linux/arm64`);
    }
    lock.images[image] = platforms;
  }
  return lock;
}
