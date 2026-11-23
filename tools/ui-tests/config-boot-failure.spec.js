// A panel that boots while the computer is unreachable used to stay on its saved layout until it was restarted: the
// first config read failed, the version stayed unknown, and the state poll only asked for a config when a version was
// already known. The next answer must bring the saved pages, including a widget the panel has never drawn.
const { test, expect } = require('@playwright/test');

const CACHED = {
  night: { enabled: false }, rules: [], shortcuts: [], display: { language: 'en' },
  pages: [{ id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} }] }],
};

const SAVED = {
  night: { enabled: false }, rules: [], shortcuts: [], display: { language: 'en' },
  pages: [{
    id: 'home', title: 'Home', enabled: true,
    widgets: [
      { id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} },
      { id: 'm', type: 'mirror', x: 0, y: 4, w: 6, h: 2, options: {} },
    ],
  }],
};

test('a config read that fails at boot is retried and its page is drawn', async ({ page }) => {
  await page.addInitScript(([cached, saved]) => {
    localStorage.clear();
    localStorage.setItem('cfg', JSON.stringify(cached));               // what the panel last applied
    localStorage.setItem('pb-demo-config', JSON.stringify(saved));     // what the computer holds now
    localStorage.setItem('pb-demo-version', '7');
    localStorage.setItem('pb-demo-config-fail', '2');                  // the computer is still starting
  }, [CACHED, SAVED]);

  await page.goto('/panel/');
  // booted from the saved copy: the clock is there, the new widget is not
  await expect(page.locator('.w-clock')).toHaveCount(1);
  await expect(page.locator('.w-mirror')).toHaveCount(0);

  // the read fails twice, then succeeds; the state poll must retry and the page must be redrawn
  await expect(page.locator('.w-mirror')).toHaveCount(1, { timeout: 20_000 });
  await expect(page.locator('.w-mirror .w-title')).toContainText('Notifications');
});
