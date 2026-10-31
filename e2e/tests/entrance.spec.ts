import { expect, test } from './fixtures';
import { admin, choosePersonalSetting } from './session';

test.use({ reducedMotion: 'no-preference' });

test('the taskbar takes no presses while it slides in and slides in only once', async ({
  page,
}) => {
  const logouts: string[] = [];
  page.on('request', (request) => {
    if (request.url().endsWith('/api/auth/logout')) {
      logouts.push(request.url());
    }
  });
  await page.goto('/');
  await page.getByLabel('User name').fill(admin.username);
  await page.getByLabel('Password', { exact: true }).fill(admin.password);
  // Every animation holds still, so the taskbar stays at the start of its entrance.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Animation.enable');
  await cdp.send('Animation.setPlaybackRate', { playbackRate: 0 });
  await page.getByRole('button', { name: 'Sign in' }).click();
  const taskbar = page.getByRole('navigation', { name: 'Taskbar' });
  await expect(taskbar).toHaveCSS('pointer-events', 'none');

  const viewport = page.viewportSize() ?? { width: 1280, height: 720 };
  // Where the start button ends up; a press there now must reach nothing in the moving taskbar.
  await page.mouse.click(30, viewport.height - 26);
  await expect(page.getByRole('dialog', { name: 'Pithyx menu' })).toHaveCount(0);
  await expect(page.getByRole('menu')).toHaveCount(0);

  await cdp.send('Animation.setPlaybackRate', { playbackRate: 1 });
  await expect(taskbar).toHaveCSS('pointer-events', 'auto');
  await expect.poll(() => taskbar.evaluate((element) => element.getAnimations().length)).toBe(0);

  await choosePersonalSetting(page, 'Taskbar position', 'Left');
  // Moving the taskbar later does not play the entrance again.
  expect(await taskbar.evaluate((element) => element.getAnimations().length)).toBe(0);
  await expect(taskbar).toHaveCSS('pointer-events', 'auto');

  await choosePersonalSetting(page, 'Taskbar position', 'Bottom');
  expect(logouts).toEqual([]);
});
