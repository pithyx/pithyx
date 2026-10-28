import { afterEach, describe, expect, it } from 'vitest';
import { isAppAllowed, type SessionPermissions } from './access';
import { coreAppIds } from './ids';
import { setInstalledApps, type InstalledApp } from './installed';

const user: SessionPermissions = {
  admin: false,
  rights: ['right:desktopLogin'],
  protocols: [],
  apps: [coreAppIds.files],
};
const admin: SessionPermissions = { admin: true, rights: [], protocols: [], apps: [] };

const chat: InstalledApp = {
  id: 'com.example.chat',
  version: '1.0.0',
  name: { en: 'Chat' },
  description: null,
  entry: '/apps/com.example.chat/index.html',
  icon: '/api/apps/com.example.chat/icon?v=0123456789ab',
  window: {
    instances: 'single',
    defaultSize: { width: 800, height: 600 },
    minSize: { width: 320, height: 240 },
  },
  permissions: [],
  logLevel: 'info',
  fileHandlers: [],
  fileActions: [],
};

afterEach(() => {
  setInstalledApps(undefined);
});

describe('isAppAllowed', () => {
  it('gives administration apps to administrators only', () => {
    expect(isAppAllowed(coreAppIds.storage, user)).toBe(false);
    expect(isAppAllowed(coreAppIds.settings, admin)).toBe(true);
    expect(isAppAllowed(coreAppIds.store, user)).toBe(false);
    expect(isAppAllowed(coreAppIds.store, admin)).toBe(true);
  });

  it('gives granted core apps by their grant, and every app to administrators', () => {
    expect(isAppAllowed(coreAppIds.files, user)).toBe(true);
    expect(isAppAllowed(coreAppIds.viewer, user)).toBe(false);
    expect(isAppAllowed(coreAppIds.files, admin)).toBe(true);
  });

  it('gives an installed app while the topic apps lists it for the user', () => {
    expect(isAppAllowed(chat.id, admin)).toBe(false);
    setInstalledApps([chat]);
    expect(isAppAllowed(chat.id, user)).toBe(true);
    expect(isAppAllowed(chat.id, admin)).toBe(true);
    expect(isAppAllowed(chat.id, undefined)).toBe(false);
    setInstalledApps([]);
    expect(isAppAllowed(chat.id, admin)).toBe(false);
  });

  it('gives the rest to everybody and nothing unknown', () => {
    expect(isAppAllowed(coreAppIds.about, user)).toBe(true);
    expect(isAppAllowed(coreAppIds.about, undefined)).toBe(true);
    expect(isAppAllowed(coreAppIds.preferences, user)).toBe(true);
    expect(isAppAllowed(coreAppIds.files, undefined)).toBe(false);
    expect(isAppAllowed('nope', admin)).toBe(false);
  });

  it('knows the core apps only by their org.pithyx ids', () => {
    expect(isAppAllowed('about', admin)).toBe(false);
    expect(isAppAllowed('files', { ...user, apps: ['files'] })).toBe(false);
  });
});
