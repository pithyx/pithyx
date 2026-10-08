const NUMERIC = /^[0-9]+$/;

function sign(a: bigint, b: bigint): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function compareIdentifiers(a: string, b: string): number {
  const aNumeric = NUMERIC.test(a);
  const bNumeric = NUMERIC.test(b);
  if (aNumeric && bNumeric) {
    return sign(BigInt(a), BigInt(b));
  }
  if (aNumeric !== bNumeric) {
    return aNumeric ? -1 : 1;
  }
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Compares two SemVer 2.0 versions without build metadata, as the box does: below zero when `left` is older. */
export function compareVersions(left: string, right: string): number {
  const split = (version: string) => {
    const dash = version.indexOf('-');
    const core = (dash < 0 ? version : version.slice(0, dash))
      .split('.')
      .map((part) => BigInt(part));
    return { core, pre: dash < 0 ? undefined : version.slice(dash + 1).split('.') };
  };
  const a = split(left);
  const b = split(right);
  for (let index = 0; index < 3; index++) {
    const compared = sign(a.core[index] ?? 0n, b.core[index] ?? 0n);
    if (compared !== 0) {
      return compared;
    }
  }
  if (a.pre === undefined || b.pre === undefined) {
    return (a.pre === undefined ? 1 : 0) - (b.pre === undefined ? 1 : 0);
  }
  for (let index = 0; index < Math.min(a.pre.length, b.pre.length); index++) {
    const compared = compareIdentifiers(a.pre[index] ?? '', b.pre[index] ?? '');
    if (compared !== 0) {
      return compared;
    }
  }
  return Math.sign(a.pre.length - b.pre.length);
}
