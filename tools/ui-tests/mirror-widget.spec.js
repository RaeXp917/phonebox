// The phone-notification widget: it shows what the agent mirrored from the personal phone, one row per
// active notification, and it must not invent rows when the feed is empty.
const { test, expect } = require('@playwright/test');

const ITEMS = [
  { id: '0|com.facebook.orca|1|null|1', app: 'Messenger', package: 'com.facebook.orca', sender: 'Alice', text: 'See you at eight', at: Date.now() - 120_000 },
  { id: '0|com.discord|2|null|1', app: 'Discord', package: 'com.discord', sender: 'Bob', text: 'ping', at: Date.now() - 30_000 },
];

async function open(page, items) {
  await page.addInitScript((data) => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], display: { vibrate: false }, shortcuts: [],
      pages: [{ id: 'mirror-test', title: 'Home', widgets: [{ id: 'm1', type: 'mirror', x: 0, y: 0, w: 6, h: 2, options: {} }] }],
    }));
    localStorage.setItem('pb-demo-mirror', JSON.stringify(data));
  }, items);
  await page.goto('/panel/');
  await expect(page.locator('[data-id="mirror-test"] .w-mirror')).toHaveCount(1);
}

test('each active notification is one row with app, sender, preview and age', async ({ page }) => {
  await open(page, ITEMS);
  const rows = page.locator('.w-mirror .mirror-row');
  await expect(rows).toHaveCount(2);
  const first = rows.first();
  await expect(first.locator('.mirror-top b')).toHaveText('Alice');
  await expect(first.locator('.mirror-app')).toHaveText('Messenger');
  await expect(first.locator('.mirror-text')).toHaveText('See you at eight');
  await expect(first.locator('.mirror-time')).not.toHaveText('');
  // the badge takes the app's initial and its brand colour
  await expect(first.locator('.mirror-dot')).toHaveText('M');
  const dotColor = await first.locator('.mirror-dot').evaluate((el) => getComputedStyle(el).getPropertyValue('--d').trim());
  expect(dotColor).toBe('#0084ff');
});

test('an empty feed says so instead of showing a blank card', async ({ page }) => {
  await open(page, []);
  await expect(page.locator('.w-mirror .mirror-row')).toHaveCount(0);
  await expect(page.locator('.w-mirror .mirror-empty')).toBeVisible();
});
