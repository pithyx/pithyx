import { describe, expect, expectTypeOf, it } from 'vitest';
import * as host from './host';
import * as sdk from './index';
import type { Permission } from './manifest';
import type { MethodName } from './protocol';

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
});
