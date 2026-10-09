import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { ARCHITECTURES, type Architecture, type LockedPlatform } from './lock';
import { readTar } from './untar';

const INDEX_TYPES = new Set([
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
]);

interface Descriptor {
  mediaType?: string;
  digest: string;
  platform?: { os?: string; architecture?: string };
}

/** An image as an OCI image layout archive holds it: what its tag points to, and per architecture its image. */
export interface OciImage {
  /** The digest of the image index (or of the only manifest) that a push tags and a Compose file pins. */
  digest: string;
  platforms: Partial<Record<Architecture, LockedPlatform>>;
}

/**
 * Reads an OCI image layout archive, as `docker buildx build --output type=oci` and `docker save` write them, and
 * checks every blob it reads against its digest. Attestation manifests (platform `unknown`) are skipped.
 */
export function readOciArchive(path: string): OciImage {
  const files = readTar(readFileSync(path));
  const blob = (digest: string): Buffer => {
    const [algorithm, hex] = digest.split(':');
    const bytes = files.get(`blobs/${String(algorithm)}/${String(hex)}`);
    if (algorithm !== 'sha256' || bytes === undefined) {
      throw new Error(`${path} has no blob ${digest}`);
    }
    if (`sha256:${createHash('sha256').update(bytes).digest('hex')}` !== digest) {
      throw new Error(`${path}: the blob ${digest} does not match its digest`);
    }
    return bytes;
  };
  const json = (bytes: Buffer): unknown => JSON.parse(bytes.toString('utf8'));
  const layout = files.get('index.json');
  if (layout === undefined) {
    throw new Error(`${path} is not an OCI image layout (no index.json)`);
  }
  const top = (json(layout) as { manifests?: Descriptor[] }).manifests?.[0];
  if (top === undefined) {
    throw new Error(`${path} holds no image`);
  }
  const platforms: OciImage['platforms'] = {};
  const add = (manifestDigest: string, fallback?: string) => {
    const manifest = json(blob(manifestDigest)) as { config?: { digest?: string } };
    const config = manifest.config?.digest;
    if (config === undefined) {
      throw new Error(`${path}: the manifest ${manifestDigest} has no config`);
    }
    const architecture = fallback ?? (json(blob(config)) as { architecture?: string }).architecture;
    if (ARCHITECTURES.includes(architecture as Architecture)) {
      platforms[architecture as Architecture] = { manifest: manifestDigest, config };
    }
  };
  const topBytes = blob(top.digest);
  const parsed = json(topBytes) as { mediaType?: string; manifests?: Descriptor[] };
  if (INDEX_TYPES.has(top.mediaType ?? parsed.mediaType ?? '') || parsed.manifests !== undefined) {
    for (const entry of parsed.manifests ?? []) {
      const architecture = entry.platform?.architecture;
      if (entry.platform?.os === 'linux' && architecture !== undefined) {
        add(entry.digest, architecture);
      }
    }
  } else {
    add(top.digest);
  }
  if (Object.keys(platforms).length === 0) {
    throw new Error(`${path} has no image for ${ARCHITECTURES.join(' or ')}`);
  }
  return { digest: top.digest, platforms };
}
