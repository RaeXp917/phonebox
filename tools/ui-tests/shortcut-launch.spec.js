// A shortcut with a `launch` key opens an app on the phone through the Android bridge. It must never be
// sent to the agent: that is a computer-side action, needs the PC awake and reachable, and on Windows it
// even opened a console window. A shortcut without `launch` keeps going to the agent, unchanged.
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [
  { id: 'ac', label: 'AC', icon: '📡', color: '#0ea5e9', launch: 'miremote' },
  { id: 'pc', label: 'Calculator', icon: '🧮', color: '#64748b' },
];

async function open(page) {
  // the Android app's bridge: only openApp, as on a phone that has the launch support
  await page.addInitScript(() => {
    window.__launches = [];
    window.Android = { openApp: (key, app) => { window.__launches.push(app); return true; } };
  });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], display: { vibrate: false }, shortcuts: SHORTCUTS,
    pages: [{ id: 'launch-test', title: 'Keys', widgets: [
      { id: 'w1', type: 'button', x: 0, y: 0, w: 6, h: 6, options: { id: 'ac' } },
      { id: 'w2', type: 'button', x: 6, y: 0, w: 6, h: 6, options: { id: 'pc' } },
    ] }],
  }));
  await page.goto('/panel/');
  await expect(page.locator('[data-id="launch-test"] .w-button')).toHaveCount(2);
  // the demo patches window.fetch to answer as the agent; wrap it to see what the panel asks for
  await page.evaluate(() => {
    window.__calls = [];
    const f = window.fetch;
    window.fetch = (input, init) => { window.__calls.push(String(input)); return f(input, init); };
  });
  return {
    launches: () => page.evaluate(() => window.__launches),
    calls: () => page.evaluate(() => window.__calls),
  };
}

test('a shortcut with launch opens the phone app and never calls the agent', async ({ page }) => {
  const { launches, calls } = await open(page);
  await page.locator('[data-id="launch-test"] .w-button').first().locator('.key').click();
  await expect.poll(launches).toEqual(['miremote']);
  expect((await calls()).filter((u) => u.includes('/run/')), 'a phone-side launch must not reach the agent').toEqual([]);
});

test('a shortcut without launch still runs on the computer', async ({ page }) => {
  const { launches, calls } = await open(page);
  await page.locator('[data-id="launch-test"] .w-button').nth(1).locator('.key').click();
  await expect.poll(async () => (await calls()).filter((u) => u.includes('/run/pc'))).not.toEqual([]);
  expect(await launches(), 'a PC shortcut must not open anything on the phone').toEqual([]);
});
