import { test as base, expect } from '@playwright/test';

/** Every test fails when a page of its browser context throws an uncaught error. */
export const test = base.extend<{ uncaughtErrors: undefined }>({
  uncaughtErrors: [
    async ({ context }, use) => {
      const errors: string[] = [];
      context.on('weberror', (error) => errors.push(error.error().message));
      await use(undefined);
      expect(errors, 'uncaught errors in the page').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
