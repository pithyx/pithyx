import { createHmac } from 'node:crypto';

const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

/** RFC 6238 code (SHA-1, 30 s, 6 digits) for a base32 secret, as an authenticator app computes it. */
export function totp(secret: string, time = Date.now()): string {
  let bits = '';
  for (const character of secret.replace(/=+$/, '').toUpperCase()) {
    bits += BASE32.indexOf(character).toString(2).padStart(5, '0');
  }
  const key = Buffer.from((bits.match(/.{8}/g) ?? []).map((byte) => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(time / 30_000)));
  const hmac = createHmac('sha1', key).update(counter).digest();
  const offset = (hmac.at(-1) ?? 0) & 0x0f;
  return String(hmac.readUInt32BE(offset) & 0x7fffffff)
    .slice(-6)
    .padStart(6, '0');
}
