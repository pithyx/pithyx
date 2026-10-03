import { describe, expect, it } from 'vitest';
import { isAppAllowed, type SessionPermissions } from './access';

const user: SessionPermissions = {
  admin: false,
  rights: ['right:desktopLogin'],
  protocols: [],
  apps: ['files'],
};
const admin: SessionPermissions = { admin: true, rights: [], protocols: [], apps: [] };

describe('isAppAllowed', () => {
  it('gives administration apps to administrators only', () => {
    expect(isAppAllowed('storage', user)).toBe(false);
    expect(isAppAllowed('settings', admin)).toBe(true);
  });

  it('gives granted apps by their grant, and every app to administrators', () => {
    expect(isAppAllowed('files', user)).toBe(true);
    expect(isAppAllowed('demo', user)).toBe(false);
    expect(isAppAllowed('demo', admin)).toBe(true);
  });

  it('gives the rest to everybody and nothing unknown', () => {
    expect(isAppAllowed('about', user)).toBe(true);
    expect(isAppAllowed('about', undefined)).toBe(true);
    expect(isAppAllowed('files', undefined)).toBe(false);
    expect(isAppAllowed('nope', admin)).toBe(false);
  });
});
