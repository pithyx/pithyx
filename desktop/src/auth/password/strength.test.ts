import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, test } from 'vitest';
import { assessPassword, MIN_PASSWORD_LENGTH, type PasswordLists } from './strength';

const repo = join(import.meta.dirname, '..', '..', '..', '..');
const lines = (path: string) =>
  readFileSync(join(repo, path), 'utf8')
    .split('\n')
    .filter((line) => line !== '');
const clientPasswords = lines('desktop/src/auth/password/common-passwords.txt');
const clientWords = lines('desktop/src/auth/password/common-words.txt');
const lists: PasswordLists = {
  passwords: new Set(clientPasswords),
  words: new Map(clientWords.map((word, rank) => [word, rank])),
};

describe('assessPassword', () => {
  test.each([
    ['k7#mQz2', 'weak', false, ['tooShort']],
    ['password1', 'weak', false, ['common']],
    ['PassWord1', 'weak', false, ['common']],
    ['sunshine2024', 'weak', true, ['dictionary']],
    ['Sunshine2024!', 'weak', true, ['dictionary']],
    ['P@ssw0rd2024', 'weak', true, ['dictionary']],
    ['aaaaaaaaaaaa', 'weak', true, ['repetition']],
    ['hunter2hunter2', 'weak', true, ['repetition']],
    ['qwertz123456', 'weak', true, ['sequence']],
    ['k7#mQz2p', 'medium', true, ['length']],
    ['tulip-oar', 'medium', true, ['length']],
    ['Xk9mPq2wLr7v', 'strong', true, []],
    ['correct horse battery staple', 'strong', true, []],
    ['river-candle-orbit-7', 'strong', true, []],
  ] as const)('%s is %s', (password, strength, allowed, issues) => {
    const result = assessPassword(password, lists);
    expect(result.strength).toBe(strength);
    expect(result.allowed).toBe(allowed);
    expect(result.issues).toEqual(issues);
  });

  test('weak passwords from the minimum length on are allowed', () => {
    expect(assessPassword('zzzzqqqq', lists)).toMatchObject({ strength: 'weak', allowed: true });
  });

  test('only the length rule applies before the lists have loaded', () => {
    expect(assessPassword('password1', undefined).allowed).toBe(true);
    expect(assessPassword('short', undefined).issues).toEqual(['tooShort']);
  });

  test('a long pasted text stays fast', () => {
    const started = performance.now();
    assessPassword('x9'.repeat(512), lists);
    expect(performance.now() - started).toBeLessThan(200);
  });
});

describe('the lists match the daemon', () => {
  test('the minimum length is the daemon constant', () => {
    const rules = readFileSync(
      join(repo, 'daemon/src/Pithyx.Daemon/Auth/CredentialRules.cs'),
      'utf8',
    );
    expect(rules).toContain(`public const int MinPasswordLength = ${String(MIN_PASSWORD_LENGTH)};`);
  });

  test('every password the desktop refuses is refused by the daemon', () => {
    const daemon = new Set(lines('daemon/src/Pithyx.Daemon/Auth/common-passwords.txt'));
    expect(clientPasswords.filter((password) => !daemon.has(password))).toEqual([]);
    expect(clientPasswords.every((password) => password.length >= MIN_PASSWORD_LENGTH)).toBe(true);
  });

  test('the dictionary holds lower-case words only', () => {
    expect(clientWords.filter((word) => !/^[a-z]{4,}$/.test(word))).toEqual([]);
  });
});
