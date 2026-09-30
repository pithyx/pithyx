// The one password check of the desktop: setup, recovery, user management and password change use it. The hard
// rules (length and the list of common passwords) are the daemon's CredentialRules.ValidateNewPassword; the strength
// estimate only warns and never refuses a password.

export const MIN_PASSWORD_LENGTH = 8;
export const MAX_PASSWORD_LENGTH = 1024;

export type PasswordStrength = 'weak' | 'medium' | 'strong';

/** Why a password is refused (the first two) or weaker than it looks, most important first. */
export type PasswordIssue =
  'tooShort' | 'common' | 'dictionary' | 'repetition' | 'sequence' | 'length';

export interface PasswordLists {
  /** The most used passwords, lower case. The daemon checks its longer list on submit. */
  passwords: ReadonlySet<string>;
  /** Letters-only words from the same breach data with their rank, 0 for the most used. */
  words: ReadonlyMap<string, number>;
}

export interface PasswordAssessment {
  strength: PasswordStrength;
  /** False when the daemon would refuse the password. */
  allowed: boolean;
  issues: PasswordIssue[];
  /** The estimated guessing effort in bits. */
  bits: number;
}

const MEDIUM_BITS = 40;
const STRONG_BITS = 64;
// Beyond this every password is strong; the cap keeps the estimate fast for pasted walls of text.
const ESTIMATE_LIMIT = 128;
const MIN_WORD = 4;
const MAX_WORD = 16;
const MAX_BLOCK = 32;
const SEQUENCES = [
  '1234567890',
  'qwertyuiop',
  'qwertzuiop',
  'asdfghjkl',
  'yxcvbnm',
  'zxcvbnm',
  'abcdefghijklmnopqrstuvwxyz',
].flatMap((row) => [row, Array.from(row).reverse().join('')]);
const LEET: Readonly<Record<string, string>> = {
  '0': 'o',
  '1': 'i',
  '3': 'e',
  '4': 'a',
  '5': 's',
  '7': 't',
  '8': 'b',
  '@': 'a',
  $: 's',
  '!': 'i',
};

type Kind = 'brute' | 'word' | 'repeat' | 'sequence';

interface Step {
  bits: number;
  from: number;
  kind: Kind;
}

function charsetSize(chars: readonly string[]): number {
  let size = 0;
  if (chars.some((c) => /[a-z]/.test(c))) size += 26;
  if (chars.some((c) => /[A-Z]/.test(c))) size += 26;
  if (chars.some((c) => /[0-9]/.test(c))) size += 10;
  if (chars.some((c) => /[\x20-\x2f\x3a-\x40\x5b-\x60\x7b-\x7e]/.test(c))) size += 33;
  if (chars.some((c) => c > '\x7e')) size += 100;
  return size;
}

function caseBits(original: readonly string[]): number {
  const word = original.join('');
  if (word === word.toLowerCase()) return 0;
  if (word === word.toUpperCase() || /^[A-Z][^A-Z]*$/.test(word)) return 1;
  return original.length;
}

function isSequence(chars: readonly string[]): boolean {
  const text = chars.join('');
  return SEQUENCES.some((row) => row.includes(text));
}

/**
 * Estimates how many guesses a password needs by splitting it into the cheapest parts an attacker would try: words
 * from breach data (with capitals and look-alike digits), repeated characters or blocks, keyboard and alphabet
 * sequences, and everything else as random characters.
 */
function estimate(password: string, words: ReadonlyMap<string, number>) {
  const original = Array.from(password).slice(0, ESTIMATE_LIMIT);
  const lower = original.map((c) => c.toLowerCase());
  const unleet = lower.map((c) => LEET[c] ?? c);
  const bruteBits = Math.log2(Math.max(charsetSize(original), 10));
  const n = original.length;
  const best: Step[] = [{ bits: 0, from: 0, kind: 'brute' }];

  for (let end = 1; end <= n; end++) {
    const prev = best[end - 1];
    let step: Step = { bits: (prev?.bits ?? 0) + bruteBits, from: end - 1, kind: 'brute' };
    const offer = (from: number, bits: number, kind: Kind) => {
      const total = (best[from]?.bits ?? 0) + bits;
      if (total < step.bits) {
        step = { bits: total, from, kind };
      }
    };
    for (let from = Math.max(0, end - MAX_WORD); from <= end - MIN_WORD; from++) {
      const plain = lower.slice(from, end);
      const leet = unleet.slice(from, end);
      const plainRank = words.get(plain.join(''));
      const leetRank = words.get(leet.join(''));
      const rank = plainRank ?? leetRank;
      if (rank !== undefined && plain.some((c) => c !== plain[0])) {
        const substituted = plainRank === undefined ? 1 : 0;
        offer(
          from,
          Math.log2(rank + 2) + caseBits(original.slice(from, end)) + substituted,
          'word',
        );
      }
    }
    for (let from = end - 3; from >= 0; from--) {
      const run = lower.slice(from, end);
      if (run.some((c) => c !== run[0])) break;
      offer(from, bruteBits + Math.log2(run.length), 'repeat');
    }
    for (let from = end - 3; from >= 0; from--) {
      const run = lower.slice(from, end);
      if (!isSequence(run)) break;
      offer(from, Math.log2(26) + Math.log2(run.length) + 1, 'sequence');
    }
    for (let size = 2; size <= MAX_BLOCK && end - 2 * size >= 0; size++) {
      const block = lower.slice(end - size, end).join('');
      if (block === lower.slice(end - 2 * size, end - size).join('')) {
        offer(end - size, Math.log2(size) + 1, 'repeat');
      }
    }
    best[end] = step;
  }

  const covered: Record<Kind, number> = { brute: 0, word: 0, repeat: 0, sequence: 0 };
  for (let end = n; end > 0;) {
    const step = best[end];
    if (step === undefined) break;
    covered[step.kind] += end - step.from;
    end = step.from;
  }
  return { bits: best[n]?.bits ?? 0, covered, length: n };
}

export function assessPassword(
  password: string,
  lists: PasswordLists | undefined,
): PasswordAssessment {
  const issues: PasswordIssue[] = [];
  const common = lists?.passwords.has(password.toLowerCase()) ?? false;
  const allowed =
    password.length >= MIN_PASSWORD_LENGTH && password.length <= MAX_PASSWORD_LENGTH && !common;
  if (password.length < MIN_PASSWORD_LENGTH) issues.push('tooShort');
  if (common) issues.push('common');

  const { bits, covered, length } = estimate(password, lists?.words ?? new Map());
  if (!common && covered.word > 0 && covered.word * 2 >= length) issues.push('dictionary');
  if (covered.repeat * 4 >= length && covered.repeat > 0) issues.push('repetition');
  if (covered.sequence * 4 >= length && covered.sequence > 0) issues.push('sequence');

  const strength: PasswordStrength = !allowed
    ? 'weak'
    : bits >= STRONG_BITS
      ? 'strong'
      : bits >= MEDIUM_BITS
        ? 'medium'
        : 'weak';
  if (strength !== 'strong' && issues.length === 0) issues.push('length');
  return { strength, allowed, issues, bits };
}
