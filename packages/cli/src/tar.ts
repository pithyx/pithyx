import { closeSync, createWriteStream, openSync, renameSync, rmSync, writeSync } from 'node:fs';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { constants, createGzip } from 'node:zlib';

/** One regular file of an archive: a package path (ASCII, `/` as the separator), its size and how to read it. */
export interface TarFile {
  path: string;
  size: number;
  read(): Uint8Array;
}

const BLOCK = 512;
const NAME_LENGTH = 100;
const PREFIX_LENGTH = 155;
// 11 octal digits, the most a ustar size field holds.
const MAX_SIZE = 8 ** 11 - 1;

function octal(value: number, length: number): string {
  return `${value.toString(8).padStart(length - 1, '0')}\0`;
}

/** Splits a path into the ustar prefix and name fields, or null when it fits neither way. */
function splitName(path: string): { prefix: string; name: string } | null {
  if (path.length <= NAME_LENGTH) {
    return { prefix: '', name: path };
  }
  for (let slash = path.indexOf('/'); slash >= 0; slash = path.indexOf('/', slash + 1)) {
    const prefix = path.slice(0, slash);
    const name = path.slice(slash + 1);
    if (prefix.length <= PREFIX_LENGTH && name.length <= NAME_LENGTH && name.length > 0) {
      return { prefix, name };
    }
  }
  return null;
}

function header(name: string, prefix: string, size: number, type: '0' | 'x'): Buffer {
  const block = Buffer.alloc(BLOCK);
  const field = (offset: number, length: number, value: string) => {
    block.write(value, offset, length, 'ascii');
  };
  field(0, NAME_LENGTH, name);
  field(100, 8, octal(0o644, 8));
  field(108, 8, octal(0, 8));
  field(116, 8, octal(0, 8));
  field(124, 12, octal(size, 12));
  field(136, 12, octal(0, 12));
  field(148, 8, ' '.repeat(8));
  field(156, 1, type);
  field(257, 8, 'ustar\u000000');
  field(329, 8, octal(0, 8));
  field(337, 8, octal(0, 8));
  field(345, PREFIX_LENGTH, prefix);
  const sum = block.reduce((total, value) => total + value, 0);
  field(148, 8, `${sum.toString(8).padStart(6, '0')}\0 `);
  return block;
}

/** A pax record `<length> <key>=<value>\n`, whose length counts its own digits. */
function paxRecord(key: string, value: string): string {
  const body = ` ${key}=${value}\n`;
  let length = body.length + 1;
  while (String(length).length + body.length !== length) {
    length = String(length).length + body.length;
  }
  return `${String(length)}${body}`;
}

function padding(size: number): Buffer {
  return Buffer.alloc((BLOCK - (size % BLOCK)) % BLOCK);
}

/**
 * The blocks of a ustar archive of regular files in the given order, with fixed modes (0644), owners (0) and times
 * (0), and no folder entries. A path longer than the ustar fields allow gets a pax header with its `path`; the entry's
 * own name is then the start of that path, so a reader that ignores pax headers still lands in the same place.
 */
export function* tarBlocks(files: Iterable<TarFile>): Generator<Buffer> {
  for (const file of files) {
    if (file.size > MAX_SIZE) {
      throw new Error(`${file.path} is larger than a tar archive holds (8 GB)`);
    }
    const split = splitName(file.path);
    if (split === null) {
      const record = Buffer.from(paxRecord('path', file.path), 'ascii');
      const own = file.path.slice(0, NAME_LENGTH);
      yield Buffer.concat([header(own, '', record.length, 'x'), record, padding(record.length)]);
      yield header(own, '', file.size, '0');
    } else {
      yield header(split.name, split.prefix, file.size, '0');
    }
    const data = file.read();
    if (data.length !== file.size) {
      throw new Error(`${file.path} changed while it was packed`);
    }
    yield Buffer.from(data.buffer, data.byteOffset, data.length);
    yield padding(data.length);
  }
  yield Buffer.alloc(2 * BLOCK);
}

// Offset of the operating system byte in the gzip header (RFC 1952); zlib writes the platform it was built for.
const GZIP_OS = 9;
const GZIP_OS_UNKNOWN = 255;

/**
 * Writes the archive gzip-compressed at the best level to `target`, through a temporary file next to it. Node's gzip
 * header names no time and no file; the platform byte is set to "unknown", so the same input gives the same bytes on
 * every operating system with the same zlib.
 */
export async function writeTarGz(files: Iterable<TarFile>, target: string): Promise<void> {
  const temporary = `${target}.${String(process.pid)}.tmp`;
  try {
    await pipeline(
      Readable.from(tarBlocks(files)),
      createGzip({ level: constants.Z_BEST_COMPRESSION }),
      createWriteStream(temporary),
    );
    const handle = openSync(temporary, 'r+');
    try {
      writeSync(handle, Buffer.from([GZIP_OS_UNKNOWN]), 0, 1, GZIP_OS);
    } finally {
      closeSync(handle);
    }
    renameSync(temporary, target);
  } finally {
    rmSync(temporary, { force: true });
  }
}
