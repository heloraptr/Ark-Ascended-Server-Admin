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
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
const page = await context.newPage();

// Text that must never reach an image. Applied to every text node and form value just before each shot.
// The host name of the page being shot is replaced with example.com, IPv4 addresses with 192.0.2.x
// (a documentation range), and values of keys that look like passwords or secrets with asterisks.
async function scrub() {
  await page.evaluate(host => {
    const hostRe = new RegExp(host.split('.').join('[.]'), 'gi');
    const ipRe = /\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b/g;
    const fix = s => s.replace(hostRe, 'example.com').replace(ipRe, '192.0.2.10');
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
  if (only && !only.has(name)) return;
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
await open('/');
await shot('instances');

// 2. Console with the RCON command suggestions open, 10. and the full list (same view, grouped)
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

// 3. Mod library
await open('/mods');
await shot('mods-library');

// 4. Mod search (nothing is added)
const box = page.locator('input[placeholder^="Search CurseForge"]').first();
await box.fill(searchTerm);
await box.press('Enter');
await page.waitForTimeout(500);
await settle(3500);
await shot('mods-search');

// 5. Cluster page
await open(`/clusters/${clusterId}`);
await shot('cluster', { clip: true });

// 5b. Cluster mod list
await tab('Mods');
await page.waitForTimeout(1000);
await shot('cluster-mods');

// 6. INI editor on the cluster's Config tab
await tab('Config');
await tab('GameUserSettings.ini');
await page.locator('.ini-editor textarea').first().waitFor();
await shot('ini-editor');

// 7. Instance mod list (cluster mods locked, own map mod)
await open(`/instances/${scorchedId}`);
await tab('Mods');
await shot('instance-mods');

// 8. Players tab
await open(`/instances/${islandId}`);
await tab('Players');
await page.waitForTimeout(2500);
await shot('players', { clip: true });

// 9. Backups tab
await open(`/instances/${islandId}`);
await tab('Backups');
await page.waitForTimeout(1500);
await shot('backups', { clip: true });

await browser.close();
