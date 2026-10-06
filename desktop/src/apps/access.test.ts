import { describe, expect, it } from 'vitest';
import { isAppAllowed, type SessionPermissions } from './access';
import { coreAppIds } from './ids';

const user: SessionPermissions = {
  admin: false,
  rights: ['right:desktopLogin'],
  protocols: [],
  apps: [coreAppIds.files],
};
const admin: SessionPermissions = { admin: true, rights: [], protocols: [], apps: [] };

describe('isAppAllowed', () => {
  it('gives administration apps to administrators only', () => {
    expect(isAppAllowed(coreAppIds.storage, user)).toBe(false);
    expect(isAppAllowed(coreAppIds.settings, admin)).toBe(true);
  });

  it('gives granted apps by their grant, and every app to administrators', () => {
    expect(isAppAllowed(coreAppIds.files, user)).toBe(true);
    expect(isAppAllowed(coreAppIds.demo, user)).toBe(false);
    expect(isAppAllowed(coreAppIds.demo, admin)).toBe(true);
  });

  it('gives the rest to everybody and nothing unknown', () => {
    expect(isAppAllowed(coreAppIds.about, user)).toBe(true);
    expect(isAppAllowed(coreAppIds.about, undefined)).toBe(true);
    expect(isAppAllowed(coreAppIds.files, undefined)).toBe(false);
    expect(isAppAllowed('nope', admin)).toBe(false);
  });

  it('knows the core apps only by their org.pithyx ids', () => {
    expect(isAppAllowed('about', admin)).toBe(false);
    expect(isAppAllowed('files', { ...user, apps: ['files'] })).toBe(false);
  });
});
