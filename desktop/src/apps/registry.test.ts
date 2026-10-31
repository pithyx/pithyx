import { afterEach, describe, expect, it } from 'vitest';
import { coreAppIds } from './ids';
import { manifestText, setInstalledApps, type InstalledApp } from './installed';
import { allApps, appsFor, findApp } from './registry';

function installed(id: string, name: Record<string, string> = { en: id }): InstalledApp {
  return {
    id,
    version: '1.0.0',
    name,
    description: null,
    entry: `/apps/${id}/index.html`,
    icon: `/api/apps/${id}/icon?v=0123456789ab`,
    window: {
      instances: 'multi',
      defaultSize: { width: 640, height: 480 },
      minSize: { width: 320, height: 240 },
    },
    permissions: ['notifications'],
    logLevel: 'info',
    fileHandlers: [],
    fileActions: [],
  };
}

afterEach(() => {
  setInstalledApps(undefined);
});

describe('the app registry', () => {
  it('lists the core apps until the topic apps arrives', () => {
    expect(allApps().map((app) => app.id)).toContain(coreAppIds.store);
    expect(findApp('com.example.chat')).toBeUndefined();
  });

  it('adds the installed apps after the core apps, with their icon and window settings', () => {
    const chat = installed('com.example.chat');
    setInstalledApps([chat, installed('com.example.notes')]);
    const ids = allApps().map((app) => app.id);
    expect(ids.slice(-2)).toEqual(['com.example.chat', 'com.example.notes']);
    expect(ids.indexOf(coreAppIds.files)).toBeLessThan(ids.indexOf('com.example.chat'));
    const entry = findApp('com.example.chat');
    expect(entry).toMatchObject({
      icon: chat.icon,
      thirdParty: true,
      window: { instances: 'multi', defaultSize: { width: 640, height: 480 } },
    });
    // The same summary gives the same entry, so windows and menus keep their component.
    expect(findApp('com.example.chat')).toBe(entry);
  });

  it('drops an app the topic no longer lists', () => {
    setInstalledApps([installed('com.example.chat')]);
    setInstalledApps([]);
    expect(findApp('com.example.chat')).toBeUndefined();
  });

  it('never lists launch-only apps in the menu', () => {
    setInstalledApps([installed('com.example.chat')]);
    const ids = appsFor(() => true).map((app) => app.id);
    expect(ids).not.toContain(coreAppIds.viewer);
    expect(ids).toContain('com.example.chat');
  });
});

describe('manifestText', () => {
  it('takes the language, then its base language, then English', () => {
    const texts = { en: 'Chat', de: 'Plaudern' };
    expect(manifestText(texts, 'de')).toBe('Plaudern');
    expect(manifestText(texts, 'de-AT')).toBe('Plaudern');
    expect(manifestText(texts, 'fr')).toBe('Chat');
  });
});
