import { describe, expect, expectTypeOf, it } from 'vitest';
import packageJson from '../package.json';
import * as host from './host';
import * as sdk from './index';
import type { Permission } from './manifest';
import type { EventName, MethodName, QueryName } from './protocol';

// Administrative calls (storage, users, network) are internal to the desktop's core apps, never part of the SDK.
const INTERNAL = /^(internal|admin)/i;

describe('the public SDK', () => {
  it('exports no internal or administrative names', () => {
    for (const name of [...Object.keys(sdk), ...Object.keys(host)]) {
      expect(name).not.toMatch(INTERNAL);
    }
  });

  it('has no internal or administrative methods or permissions', () => {
    expectTypeOf<Extract<MethodName, `internal.${string}` | `admin.${string}`>>().toBeNever();
    expectTypeOf<Extract<Permission, `internal.${string}` | `admin.${string}`>>().toBeNever();
  });

  it('takes its version from package.json', () => {
    expect(sdk.SDK_VERSION).toBe(packageJson.version);
  });

  it('has the v1 additions and none of what they replaced or what was refused (D300)', () => {
    expectTypeOf<
      | 'backend.token'
      | 'backend.ticket'
      | 'directory.users'
      | 'directory.groups'
      | 'setBadge'
      | 'window.requestAttention'
      | 'window.setDirty'
      | 'contextMenu.show'
      | 'clipboard.writeText'
      | 'openUrl'
    >().toExtend<MethodName>();
    expectTypeOf<
      | 'visibilityChange'
      | 'connectionChange'
      | 'backendStatus'
      | 'permissionsChange'
      | 'storageChange'
      | 'notificationAction'
      | 'logLevelChange'
    >().toExtend<EventName>();
    expectTypeOf<'beforeClose' | 'beforeUnload'>().toEqualTypeOf<QueryName>();

    expectTypeOf<Extract<MethodName, 'auth.token'>>().toBeNever();
    expectTypeOf<
      Extract<
        EventName,
        | 'backendUnavailable'
        | 'backendRestored'
        | 'permissionRevoked'
        | 'resize'
        | 'idle'
        | 'clipboardChange'
        | 'filesDropped'
      >
    >().toBeNever();
    expect(sdk.BEFORE_UNLOAD_TIMEOUT).toBe(2000);
    expect(Object.keys(sdk)).not.toContain('BEFORE_LOGOUT_TIMEOUT');
    expect(sdk.sdkErrorCodes).toEqual(
      expect.arrayContaining(['sdk.backend.endpoint_unknown', 'sdk.backend.unreachable']),
    );
  });
});
