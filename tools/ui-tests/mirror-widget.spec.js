// The phone-notification widget: one row per active notification, the app's own mark on the left, the sender
// as the title, the app as a secondary label, the preview beneath and the age on the right.
const { test, expect } = require('@playwright/test');

const now = Date.now();
const ITEMS = [
  { id: 'a', app: 'Messenger', package: 'com.facebook.orca', sender: 'Alice', text: 'See you at eight', at: now - 120_000 },
  { id: 'b', app: 'Discord', package: 'com.discord', sender: 'Bob', text: 'ping', at: now - 30_000 },
  { id: 'c', app: 'Teams', package: 'com.microsoft.teams', sender: 'Work chat', text: 'Standup in 5', at: now - 10_000 },
  { id: 'd', app: 'Instagram', package: 'com.instagram.android', sender: 'dana', text: 'liked your photo', at: now - 5_000 },
];
const UNKNOWN = [
  { id: 'x', app: 'Test', package: 'com.example.unknown', sender: 'Someone', text: 'hello', at: now - 1_000 },
];
const UNICODE = [
  { id: 'u', app: 'Instagram', package: 'com.instagram.android', sender: 'Καλημέρα σου', text: 'Ça va très bien 👨‍👩‍👧‍👦', at: now - 10_000 },
];

async function open(page, items, extra = {}) {
  await page.addInitScript((data) => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], display: { vibrate: false }, shortcuts: [],
      pages: [{ id: 'mirror-test', title: 'Home', widgets: [{ id: 'm1', type: 'mirror', x: 0, y: 0, w: 6, h: 2, options: {} }] }],
    }));
    localStorage.setItem('pb-demo-mirror', JSON.stringify(data.items));
    localStorage.setItem('pb-demo-mirror-stale', JSON.stringify(data.stale === true));
    if (data.syncedAt != null) localStorage.setItem('pb-demo-mirror-synced', JSON.stringify(data.syncedAt));
    for (const [k, v] of Object.entries(data.seed || {})) localStorage.setItem(k, JSON.stringify(v));
  }, { items, stale: extra.stale, syncedAt: extra.syncedAt, seed: extra.seed });
  await page.goto('/panel/');
  await expect(page.locator('[data-id="mirror-test"] .w-mirror')).toHaveCount(1);
}

test('each of the four apps gets its own bundled mark, by package', async ({ page }) => {
  await open(page, ITEMS);
  const rows = page.locator('.w-mirror .mirror-row');
  await expect(rows).toHaveCount(4);

  // one SVG badge per row, and the brand colour comes from the package, not from the sender text
  const colours = [];
  for (let i = 0; i < 4; i++) {
    const svg = rows.nth(i).locator('.mirror-icon svg');
    await expect(svg).toHaveCount(1);
    colours.push(await svg.locator('rect').first().getAttribute('fill'));
  }
  expect(colours).toEqual(['#0084ff', '#5865f2', '#6264a7', 'url(#ig)']);
});

test('an unknown package falls back to an initial, never to a brand mark', async ({ page }) => {
  await open(page, UNKNOWN);
  const row = page.locator('.w-mirror .mirror-row').first();
  await expect(row.locator('.mirror-icon')).toHaveCount(0);
  await expect(row.locator('.mirror-dot')).toHaveText('T');
});

test('the sender is the title, the app is secondary, the preview sits beneath, the age on the right', async ({ page }) => {
  await open(page, ITEMS);
  const row = page.locator('.w-mirror .mirror-row').first();
  await expect(row.locator('.mirror-sender')).toHaveText('Alice');
  await expect(row.locator('.mirror-app')).toHaveText('Messenger');
  await expect(row.locator('.mirror-text')).toHaveText('See you at eight');
  await expect(row.locator('.mirror-time')).not.toHaveText('');

  const [sender, app, text, time] = await Promise.all([
    row.locator('.mirror-sender').boundingBox(), row.locator('.mirror-app').boundingBox(),
    row.locator('.mirror-text').boundingBox(), row.locator('.mirror-time').boundingBox(),
  ]);
  expect(sender.y).toBeLessThan(text.y);          // preview beneath the title
  expect(time.x).toBeGreaterThan(app.x);          // age stays on the right
  expect(text.x).toBeCloseTo(sender.x, 0);        // preview aligns with the title, not the icon
});

test('the card is named Notifications and always draws its own title', async ({ page }) => {
  await open(page, []);
  await expect(page.locator('.w-mirror .w-title')).toContainText('Notifications');
});

test('a connected phone with nothing to show says so instead of drawing a blank card', async ({ page }) => {
  await open(page, []);
  await expect(page.locator('.w-mirror .mirror-row')).toHaveCount(0);
  await expect(page.locator('.w-mirror .mirror-empty')).toHaveText('Nothing to show');
});

test('a phone that was never reached is disconnected, not "nothing to show"', async ({ page }) => {
  await open(page, [], { stale: true });
  await expect(page.locator('.w-mirror .mirror-empty')).toHaveText('Phone disconnected');
  await expect(page.locator('.w-mirror .mirror-empty')).not.toHaveText('Nothing to show');
});

test('while the first answer is still on its way the card says it is loading', async ({ page }) => {
  await open(page, [], { seed: { 'pb-demo-mirror-delay': 60_000 } });
  await expect(page.locator('.w-mirror .mirror-empty')).toHaveText('Loading notifications…');
});

test('an agent that cannot answer says the list is unavailable, not empty', async ({ page }) => {
  await open(page, [], { seed: { 'pb-demo-mirror-fail': true } });
  await expect(page.locator('.w-mirror .mirror-empty')).toHaveText('Notifications unavailable');
});

test('a disconnected phone keeps the last rows and marks them as old', async ({ page }) => {
  await open(page, ITEMS, { stale: true, syncedAt: Date.now() - 300_000 });
  await expect(page.locator('.w-mirror .mirror-row')).toHaveCount(4);
  await expect(page.locator('.w-mirror .w-sub')).toHaveText(/last synced/);
});

test('an empty feed from a disconnected phone says the phone is away', async ({ page }) => {
  await open(page, [], { stale: true, syncedAt: Date.now() - 60_000 });
  await expect(page.locator('.w-mirror .mirror-empty')).toHaveText('Phone disconnected');
});

test('Greek, accents and a multi-codepoint emoji render exactly as sent', async ({ page }) => {
  await open(page, UNICODE);
  const row = page.locator('.w-mirror .mirror-row').first();
  await expect(row.locator('.mirror-sender')).toHaveText('Καλημέρα σου');
  await expect(row.locator('.mirror-text')).toHaveText('Ça va très bien 👨‍👩‍👧‍👦');
  const text = await row.locator('.mirror-text').textContent();
  expect(text.startsWith('String (')).toBe(false);
  expect(text.includes('\uFFFD')).toBe(false);
});
