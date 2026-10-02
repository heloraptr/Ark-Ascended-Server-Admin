// Captures the README and guide screenshots from a running ARK Ascended Server Admin.
// It only reads and looks: it never starts or stops a server and never saves data.
// Configuration comes from environment variables; see README.md.
import { chromium } from 'playwright-core';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const need = name => {
  const value = process.env[name];
  if (!value) throw new Error(`Set ${name}. See README.md.`);
  return value;
};
const base = need('BASE').replace(/\/+$/, '');
const password = need('ARKADMIN_PASSWORD');
const out = resolve(process.env.OUT ?? resolve(here, '../../docs/images'));
const clusterId = process.env.CLUSTER_ID ?? '2';
const islandId = process.env.ISLAND_ID ?? '15';
const scorchedId = process.env.SCORCHED_ID ?? '16';
const searchTerm = process.env.MOD_SEARCH ?? 'spyglass';
const only = process.env.ONLY ? new Set(process.env.ONLY.split(',')) : null;
// True when a section that produces any of these pictures should run. Pictures named in
// `explicitOnly` are never part of a default run: they need ONLY=<name>.
const explicitOnly = new Set(['settings', 'settings-export']);
const want = (...names) => names.some(n => (only ? only.has(n) : !explicitOnly.has(n)));
if (only && [...explicitOnly].some(n => only.has(n))) {
  const host = new URL(base).hostname;
  if (!['localhost', '127.0.0.1', '::1', '[::1]'].includes(host)) {
    throw new Error('The Settings page shows the CurseForge API key. Photograph it only from a throwaway copy on localhost.');
  }
}
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
const page = await context.newPage();

// Text that must never reach an image. Applied to every text node and form value just before each shot.
// The host name of the page being shot is replaced with example.com, IPv4 addresses with 192.0.2.x
// (a documentation range), and values of keys that look like passwords or secrets with asterisks.
async function scrub() {
  await page.evaluate(host => {
    // A bare IP or localhost is not a name worth hiding; the IPv4 rule below handles other addresses.
    const hostRe = /^[\d.]+$|^localhost$/.test(host) ? /(?!)/g : new RegExp(host.split('.').join('[.]'), 'gi');
    const ipRe = /\b(?!127\.0\.0\.1\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b/g;
    const secretText = /((?:password|secret|apikey|api_key|token)[A-Za-z]*=)[^\s?&"]+/gi;
    const fix = s => s.replace(hostRe, 'example.com').replace(ipRe, '192.0.2.10').replace(secretText, '$1********');
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const next = fix(n.nodeValue);
      if (next !== n.nodeValue) n.nodeValue = next;
    }
    for (const el of document.querySelectorAll('[title],[href]')) {
      for (const a of ['title']) if (el.hasAttribute(a)) el.setAttribute(a, fix(el.getAttribute(a)));
    }
    const secret = /^(\s*[^=\n;#]*(password|secret|apikey|api_key|token)[^=\n]*=)(.*)$/gim;
    for (const box of document.querySelectorAll('textarea, input:not([type=password])')) {
      const next = fix(box.value.replace(secret, '$1********'));
      if (next !== box.value) box.value = next;
    }
  }, new URL(base).hostname);
}

async function settle(extra = 1500) {
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(extra);
}

async function open(path) {
  await page.goto(`${base}${path}`);
  await page.locator('.rz-tabview, main, .ark-page, body').first().waitFor();
  await settle(2500);
}

async function tab(name, scope = page) {
  const item = scope.locator(`.rz-tabview-nav li:has(:text-is("${name}"))`).first();
  await item.click();
  await page.waitForTimeout(1200);
}

// full = false: the viewport. clip = true: crop to the content (plus sidebar) when the page is short.
async function shot(name, { clip = false } = {}) {
  if (!want(name)) return;
  await scrub();
  let box;
  if (clip) {
    const bottom = await page.evaluate(() => {
      let max = 0;
      for (const el of document.querySelectorAll('main *, .ark-page *')) {
        const r = el.getBoundingClientRect();
        if (r.height > 0 && r.bottom < innerHeight) max = Math.max(max, r.bottom);
      }
      return max;
    });
    box = { x: 0, y: 0, width: 1440, height: Math.min(900, Math.ceil(bottom + 48)) };
  }
  await page.screenshot({ path: `${out}/${name}.png`, ...(box ? { clip: box } : {}) });
  console.log(`saved ${name}.png`);
}

await page.goto(`${base}/login`);
await page.fill('#password', password);
await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/login')), page.click('button[type=submit]')]);

// 1. Instances list
if (want('instances')) {
  await open('/');
  await shot('instances');
}

// 2. Console with the RCON command suggestions open, 10. and the full list (same view, grouped)
if (want('console')) {
  await open(`/instances/${islandId}`);
  await tab('Console');
  await page.waitForTimeout(3000);
  const rcon = page.locator('input[aria-label="RCON command"]:visible').first();
  if (await rcon.count()) {
    await page.locator('button.console-browse').first().click();
    await page.locator('.console-suggest').first().waitFor({ timeout: 5000 }).catch(() => {});
    await page.waitForTimeout(600);
  }
  await shot('console');
  await page.keyboard.press('Escape');
}

// 3. Mod library, 4. mod search (nothing is added)
if (want('mods-library', 'mods-search')) {
  await open('/mods');
  await shot('mods-library');
  const box = page.locator('input[placeholder^="Search CurseForge"]').first();
  await box.fill(searchTerm);
  await box.press('Enter');
  await page.waitForTimeout(500);
  await settle(3500);
  await shot('mods-search');
}

// 5. Cluster page and its tabs
if (want('cluster', 'cluster-mods', 'ini-editor', 'launch', 'cluster-settings', 'schedule')) {
  await open(`/clusters/${clusterId}`);
  await shot('cluster', { clip: true });
  await tab('Mods');
  await page.waitForTimeout(1000);
  await shot('cluster-mods');
  await tab('Config');
  await tab('GameUserSettings.ini');
  await page.locator('.ini-editor textarea').first().waitFor();
  await shot('ini-editor');
  await tab('Launch');
  await shot('launch', { clip: true });
  await tab('Settings');
  await shot('cluster-settings', { clip: true });
  await tab('Schedule');
  await page.waitForTimeout(1000);
  await shot('schedule', { clip: true });
}

// 7. Instance mod list (cluster mods locked, own map mod)
if (want('instance-mods')) {
  await open(`/instances/${scorchedId}`);
  await tab('Mods');
  await shot('instance-mods');
}

// 8. Players tab
if (want('players')) {
  await open(`/instances/${islandId}`);
  await tab('Players');
  await page.waitForTimeout(2500);
  await shot('players', { clip: true });
}

// 9. Backups tab
if (want('backups')) {
  await open(`/instances/${islandId}`);
  await tab('Backups');
  await page.waitForTimeout(1500);
  await shot('backups', { clip: true });
}

// 10. Instance tabs: launch (with the command line preview), settings (with the connection card), schedule
if (want('instance-launch', 'instance-launch-preview', 'instance-settings', 'instance-schedule')) {
  await open(`/instances/${islandId}`);
  await tab('Launch');
  await page.waitForTimeout(1500);
  await shot('instance-launch', { clip: true });
  await page.getByText('What a start would run').first().scrollIntoViewIfNeeded();
  await page.evaluate(() => window.scrollBy(0, 400));
  await page.waitForTimeout(500);
  await shot('instance-launch-preview');
  await tab('Settings');
  await page.waitForTimeout(2500);
  await shot('instance-settings');
  await tab('Schedule');
  await page.waitForTimeout(1000);
  await shot('instance-schedule', { clip: true });
}

// 11. Maps
if (want('maps')) {
  await open('/maps');
  await shot('maps');
}

// 12. Global Settings. Only with ONLY=settings and only on localhost: the page shows the CurseForge API key.
if (want('settings', 'settings-export')) {
  await open('/settings');
  await shot('settings');
  await page.getByText('Configuration data').first().scrollIntoViewIfNeeded();
  await page.waitForTimeout(500);
  await shot('settings-export');
}

// 13. Update page, as it is. This never starts a run; a mid-run picture has to be taken while one is going.
if (want('update')) {
  await open('/update');
  await page.waitForTimeout(1500);
  await shot('update');
}

await browser.close();
