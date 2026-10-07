import { randomBytes } from 'node:crypto';
import type { MockUser } from './users';

/** How long a backend token is valid, as on the box (D303). */
export const TOKEN_LIFETIME = 5 * 60_000;

/** How long a WebSocket ticket is valid, as on the box (D303). */
export const TICKET_LIFETIME = 30_000;

/** Who a token or ticket was given to: a user in one window of the app. */
export interface Holder {
  user: MockUser;
  instance: string;
}

interface Grant extends Holder {
  expires: number;
}

/** 256 random bits, URL-safe. */
function secret(): string {
  return randomBytes(32).toString('base64url');
}

/**
 * The tokens and tickets of `pithyx dev`, kept in memory like the box keeps them: a backend token is valid for every
 * endpoint for 5 minutes, a ticket once, for one endpoint, within 30 seconds.
 */
export class TokenStore {
  private readonly tokens = new Map<string, Grant>();
  private readonly tickets = new Map<string, Grant & { endpoint: string }>();

  constructor(private readonly now: () => number = Date.now) {}

  private prune(): void {
    const now = this.now();
    for (const [key, grant] of this.tokens) {
      if (grant.expires <= now) {
        this.tokens.delete(key);
      }
    }
    for (const [key, grant] of this.tickets) {
      if (grant.expires <= now) {
        this.tickets.delete(key);
      }
    }
  }

  issueToken(holder: Holder): { token: string; expiresAt: string } {
    this.prune();
    const token = secret();
    const expires = this.now() + TOKEN_LIFETIME;
    this.tokens.set(token, { ...holder, expires });
    return { token, expiresAt: new Date(expires).toISOString() };
  }

  issueTicket(holder: Holder, endpoint: string): { ticket: string; expiresAt: string } {
    this.prune();
    const ticket = secret();
    const expires = this.now() + TICKET_LIFETIME;
    this.tickets.set(ticket, { ...holder, endpoint, expires });
    return { ticket, expiresAt: new Date(expires).toISOString() };
  }

  /** The holder of a valid token, or undefined. */
  checkToken(token: string): Holder | undefined {
    const grant = this.tokens.get(token);
    return grant !== undefined && grant.expires > this.now() ? grant : undefined;
  }

  /** Uses up a ticket: the holder when it is valid for the endpoint, otherwise undefined. Either way it is gone. */
  takeTicket(ticket: string, endpoint: string): Holder | undefined {
    const grant = this.tickets.get(ticket);
    this.tickets.delete(ticket);
    return grant !== undefined && grant.expires > this.now() && grant.endpoint === endpoint
      ? grant
      : undefined;
  }

  /** Ends every token and ticket of a user, as a sign-out does on the box. */
  revoke(userId: number): void {
    for (const [key, grant] of this.tokens) {
      if (grant.user.id === userId) {
        this.tokens.delete(key);
      }
    }
    for (const [key, grant] of this.tickets) {
      if (grant.user.id === userId) {
        this.tickets.delete(key);
      }
    }
  }
}
