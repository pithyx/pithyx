import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';

const BLOCK = 512;

function text(block: Buffer, offset: number, length: number): string {
  const field = block.subarray(offset, offset + length);
  const end = field.indexOf(0);
  return field.subarray(0, end < 0 ? field.length : end).toString('utf8');
}

function paxPath(data: Buffer): string | undefined {
  let offset = 0;
  while (offset < data.length) {
    const space = data.indexOf(0x20, offset);
    if (space < 0) {
      return undefined;
    }
    const length = Number(data.subarray(offset, space).toString('ascii'));
    if (!Number.isSafeInteger(length) || length <= 0) {
      return undefined;
    }
    const record = data.subarray(space + 1, offset + length - 1).toString('utf8');
    const equals = record.indexOf('=');
    if (record.slice(0, equals) === 'path') {
      return record.slice(equals + 1);
    }
    offset += length;
  }
  return undefined;
}

/**
 * The regular files of a tar archive (ustar or pax, as `pithyx pack`, BuildKit and Docker write them) by path, without
 * a leading `./`. Links, folders and other entries are skipped. For archives the CLI wrote or built itself, not for
 * untrusted input: the box has its own hardened reader.
 */
export function readTar(archive: Buffer): Map<string, Buffer> {
  const files = new Map<string, Buffer>();
  let offset = 0;
  let longName: string | undefined;
  while (offset + BLOCK <= archive.length) {
    const header = archive.subarray(offset, offset + BLOCK);
    if (header.every((byte) => byte === 0)) {
      break;
    }
    const size = parseInt(text(header, 124, 12).trim() || '0', 8);
    const type = String.fromCharCode(header[156] ?? 0);
    const data = archive.subarray(offset + BLOCK, offset + BLOCK + size);
    offset += BLOCK + Math.ceil(size / BLOCK) * BLOCK;
    if (type === 'x') {
      longName = paxPath(data);
      continue;
    }
    if (type === 'L') {
      longName = text(data, 0, data.length);
      continue;
    }
    const prefix = text(header, 345, 155);
    const name =
      longName ?? (prefix === '' ? text(header, 0, 100) : `${prefix}/${text(header, 0, 100)}`);
    longName = undefined;
    if (type === '0' || type === '\0') {
      files.set(name.replace(/^\.\//, ''), data);
    }
  }
  return files;
}

/** The files of a `.pithyxapp` or another gzip-compressed tar archive. */
export function readTarGz(path: string): Map<string, Buffer> {
  return readTar(gunzipSync(readFileSync(path)));
}
