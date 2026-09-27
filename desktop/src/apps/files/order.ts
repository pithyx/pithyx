import type { FileEntry, SortKey, SortOrder } from '@vyxis/sdk';

/**
 * The daemon's natural order (`NaturalSort.Key` in the daemon): digit runs compare by value, letters ignore case, and
 * the name itself breaks ties, so the order is total and matches the server's pages exactly.
 */
export function naturalKey(name: string): string {
  let key = '';
  for (let i = 0; i < name.length; i++) {
    const c = name.charCodeAt(i);
    if (c >= 48 && c <= 57) {
      let end = i;
      while (end + 1 < name.length && isDigit(name.charCodeAt(end + 1))) {
        end++;
      }
      const digits = name.slice(i, end + 1).replace(/^0+/, '');
      key += '0' + String.fromCharCode(48 + digits.length) + digits;
      i = end;
    } else {
      key +=
        c < 0x80
          ? c >= 97 && c <= 122
            ? String.fromCharCode(c - 32)
            : name[i]
          : name.charAt(i).toUpperCase();
    }
  }
  return `${key}\u0000${name}`;
}

function isDigit(c: number): boolean {
  return c >= 48 && c <= 57;
}

/** Ordinal comparison of UTF-16 code units, like the daemon's string.CompareOrdinal. */
function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Compares two entries in the order of a listing, the same way the daemon sorts its pages. */
export function compareEntries(
  a: FileEntry,
  b: FileEntry,
  sort: SortKey,
  order: SortOrder,
): number {
  let result: number;
  if (sort === 'name') {
    result = ordinal(naturalKey(a.name), naturalKey(b.name));
  } else {
    const value = (entry: FileEntry) =>
      sort === 'size' ? (entry.kind === 'folder' ? -1 : entry.size) : Date.parse(entry.modified);
    result = value(a) - value(b) || ordinal(naturalKey(a.name), naturalKey(b.name));
  }
  return order === 'asc' ? result : -result;
}
