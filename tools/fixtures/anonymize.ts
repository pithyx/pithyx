// Replaces hardware identifiers in recorded JSON tool output with stable pseudonyms, so no real serial numbers or
// WWNs end up in the repository. The same input always yields the same pseudonym, so fixtures recorded at different
// times stay consistent. Serials of the dev VM's test disks (VYXTEST01 ...) are not hardware and stay readable.
//
// Usage: node tools/fixtures/anonymize.ts <file>   rewrites the file in place; files that are not JSON or hold no
// identifiers are left alone
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';

const SALT = 'pithyx-fixtures-v1';
const IDENTIFIER_KEYS = new Set(['serial', 'wwn', 'serial_number']);
const DERIVED_KEYS = new Set(['id', 'id-link']);
const TEST_DISK_SERIAL = /^VYXTEST\d+$/;

/** A pseudonym of the same length and shape: hex digits, with a kept 0x prefix for WWNs. */
export function pseudonym(value: string): string {
  if (TEST_DISK_SERIAL.test(value)) {
    return value;
  }
  const hex = createHash('sha256')
    .update(SALT + value)
    .digest('hex');
  if (/^0x[0-9a-f]+$/i.test(value)) {
    return `0x${hex.slice(0, Math.max(value.length - 2, 8))}`;
  }
  return hex.slice(0, Math.max(value.length, 8)).toUpperCase();
}

function collect(node: unknown, found: Map<string, string>): void {
  if (Array.isArray(node)) {
    node.forEach((child) => {
      collect(child, found);
    });
  } else if (typeof node === 'object' && node !== null) {
    for (const [key, value] of Object.entries(node)) {
      if (IDENTIFIER_KEYS.has(key) && typeof value === 'string' && value.trim() !== '') {
        found.set(value.trim(), pseudonym(value.trim()));
      } else {
        collect(value, found);
      }
    }
  }
}

function replace(node: unknown, found: Map<string, string>): unknown {
  if (Array.isArray(node)) {
    return node.map((child) => replace(child, found));
  }
  if (typeof node === 'object' && node !== null) {
    return Object.fromEntries(
      Object.entries(node).map(([key, value]) => {
        if (typeof value !== 'string') {
          return [key, replace(value, found)];
        }
        if (IDENTIFIER_KEYS.has(key)) {
          return [key, found.get(value.trim()) ?? value];
        }
        if (DERIVED_KEYS.has(key)) {
          let derived = value;
          for (const [original, alias] of found) {
            derived = derived.split(original).join(alias);
          }
          return [key, derived];
        }
        return [key, value];
      }),
    );
  }
  return node;
}

export function anonymize(document: unknown): unknown {
  const found = new Map<string, string>();
  collect(document, found);
  return replace(document, found);
}

const file = process.argv[2];
if (file !== undefined) {
  const text = readFileSync(file, 'utf8');
  let document: unknown;
  try {
    document = JSON.parse(text);
  } catch {
    process.exit(0);
  }
  const found = new Map<string, string>();
  collect(document, found);
  // Replaced as text, so the tool's formatting and numbers beyond 2^53 (ZFS GUIDs) stay exactly as recorded.
  let anonymized = text;
  for (const [original, alias] of found) {
    anonymized = anonymized.split(original).join(alias);
  }
  if (anonymized !== text) {
    writeFileSync(file, anonymized);
  }
}
