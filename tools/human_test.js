/**
 * Human-use test for the LYGO Browser.
 *
 * Attaches to the LIVE browser over CDP (the same window Justin sees), then types, clicks,
 * searches, files links, saves a credential, downloads a file and takes a snap - the way a
 * person would. Asserts against the bridge API, and saves a screenshot per step.
 *
 *   set NODE_PATH=<USER>\source\LYGOapps\node_modules
 *   node tools\human_test.js
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const BASE = 'http://127.0.0.1:9337';
const CDP = 'http://127.0.0.1:9333';
const ROOT = path.join(__dirname, '..');
const TOKEN = fs.readFileSync(path.join(ROOT, 'data', 'state', 'bridge.token'), 'utf8').trim();
const OUT = path.join(ROOT, 'data', 'state', 'human-test');
fs.mkdirSync(OUT, { recursive: true });

const results = [];
let page;

function api(pathname, opts = {}) {
  return fetch(BASE + pathname, {
    method: opts.method || 'GET',
    headers: Object.assign({ 'X-LYGO-Token': TOKEN }, opts.body ? { 'Content-Type': 'application/json' } : {}),
    body: opts.body ? JSON.stringify(opts.body) : undefined
  }).then(async (r) => {
    const text = await r.text();
    try { return JSON.parse(text); } catch { return { raw: text }; }
  });
}

async function step(name, fn) {
  const started = Date.now();
  try {
    const detail = await fn();
    results.push({ name, ok: true, detail, ms: Date.now() - started });
    console.log(`  [PASS] ${name.padEnd(26)} ${detail || ''}`);
  } catch (err) {
    results.push({ name, ok: false, detail: err.message, ms: Date.now() - started });
    console.log(`  [FAIL] ${name.padEnd(26)} ${err.message}`);
    try { await page.screenshot({ path: path.join(OUT, `FAIL-${name.replace(/\W+/g, '-')}.png`) }); } catch (e) {}
  }
}

function expect(cond, message) { if (!cond) throw new Error(message); }

async function shot(name) {
  await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: false });
}

(async () => {
  const browser = await chromium.connectOverCDP(CDP);
  const ctx = browser.contexts()[0];
  const pages = ctx.pages();
  page = pages.find((p) => p.url().startsWith(BASE)) || pages[0];
  expect(page, 'no page found over CDP');
  console.log(`attached: ${page.url()}`);

  await step('open dashboard', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(600);
    await shot('01-dashboard');
    return await page.title();
  });

  await step('type in search box', async () => {
    const box = page.locator('#heroSearch, input[type="search"], .search-input, input.inp').first();
    await box.click();
    await box.fill('');
    await box.type('lightpanda', { delay: 45 });
    await page.waitForTimeout(700);
    const dropdown = await page.locator('.suggest, .sug, [class*="suggest"]').count();
    const hits = await page.locator('.suggest li, .sug li, [class*="suggest"] a').count();
    await shot('02-typed-search');
    expect(hits > 0 || dropdown > 0, 'no search suggestions appeared');
    return `${hits} suggestion(s)`;
  });

  await step('enter opens results', async () => {
    await page.keyboard.press('Enter');
    await page.waitForTimeout(900);
    await shot('03-bible-results');
    expect(page.url().includes('bible'), 'Enter did not open the Bible results: ' + page.url());
    const rows = await page.locator('#bibleRows tr, .rec, .rrow, tbody tr').count();
    return `${rows} row(s) · ${page.url().split('/').pop()}`;
  });

  await step('click a record', async () => {
    const row = page.locator('#bibleRows tr, .rec, .rrow, tbody tr').first();
    await row.click();
    await page.waitForTimeout(500);
    const detailText = await page.locator('#detail, .detail, .r-main').first().innerText();
    await shot('04-record');
    expect(detailText.length > 20, 'detail pane stayed empty');
    return detailText.split('\n')[0].slice(0, 60);
  });

  await step('type in add-link form', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    const url = 'https://example.com/human-test-' + Date.now();
    const input = page.locator('#addUrl, input[placeholder*="http"], .addbar input').first();
    await input.click();
    await input.type(url, { delay: 15 });
    const add = page.locator('#addBtn, .addbar button, button:has-text("ADD")').first();
    await add.click();
    await page.waitForTimeout(900);
    const hits = await api('/api/bible/search?q=human-test&limit=3');
    await shot('05-add-link');
    expect((hits.hits || []).length > 0, 'link was not filed in the Bible');
    return hits.hits[0].url.slice(0, 55);
  });

  await step('open a real website', async () => {
    await page.goto('https://example.com/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(800);
    const title = await page.title();
    await shot('06-example-com');
    expect(/example/i.test(title), 'example.com did not load: ' + title);
    return title;
  });

  await step('type in a page field', async () => {
    await page.goto(BASE + '/settings', { waitUntil: 'networkidle' });
    const field = page.locator('#sHome, #sDownload, input.inp').first();
    await field.click();
    await field.fill('https://excavationpro.ca/');
    await page.locator('#setForm button[type="submit"], button:has-text("SAVE SETTINGS")').first().click();
    await page.waitForTimeout(800);
    const s = await api('/api/settings');
    await shot('07-settings');
    expect(s.homepage === 'https://excavationpro.ca/', 'setting did not save: ' + s.homepage);
    return 'homepage saved';
  });

  await step('save a credential', async () => {
    await page.goto(BASE + '/vault', { waitUntil: 'networkidle' });
    await page.locator('#vLabel').fill('human test');
    await page.locator('#vUser').fill('justin');
    await page.locator('#vSecret').fill('test-secret-123');
    await page.locator('#vUrl').fill('https://example.com');
    await page.locator('#vSave').click();
    await page.waitForTimeout(900);
    const v = await api('/api/vault');
    await shot('08-vault');
    expect((v.items || []).some((i) => i.label === 'human test'), 'credential not listed');
    const row = v.items.find((i) => i.label === 'human test');
    await api('/api/vault/remove', { method: 'POST', body: { id: row.id } });
    return 'saved, listed, removed';
  });

  await step('take a snap', async () => {
    await page.goto(BASE + '/snaps', { waitUntil: 'networkidle' });
    const before = (await api('/api/snaps')).items.length;
    await page.locator('#snapView').click();
    await page.waitForTimeout(1600);
    const after = (await api('/api/snaps')).items;
    await shot('09-snaps');
    expect(after.length > before, 'snap was not recorded');
    return `${after.length} shots · ${after[0].w}x${after[0].h}`;
  });

  await step('download a file', async () => {
    const before = (await api('/api/downloads')).items.length;
    // Playwright intercepts navigations, so ask the browser itself to fetch the file
    await api('/api/agent/open', { method: 'POST', body: { url: BASE + '/selftest-sample.bin', newTab: true } });
    await new Promise((r) => setTimeout(r, 2400));
    const items = (await api('/api/downloads')).items;
    expect(items.length > before, 'download not tracked');
    return `${items[0].state} · ${items[0].bytes} bytes`;
  });

  await step("search a real site", async () => {
    if (!page || page.isClosed()) page = ctx.pages()[0];
    await page.goto("https://en.wikipedia.org/", { waitUntil: "domcontentloaded" });
    await page.waitForTimeout(900);
    const q = page.locator('input[name="search"]').first();
    await q.click();
    await q.type('Bookmark', { delay: 25 });
    await q.press('Enter');
    await page.waitForTimeout(2500);
    await shot('10-wikipedia');
    expect(page.url().toLowerCase().includes('q='), 'search did not submit: ' + page.url());
    return page.url().slice(0, 70);
  });

  const passed = results.filter((r) => r.ok).length;
  const failed = results.filter((r) => !r.ok);
  fs.writeFileSync(path.join(OUT, 'result.json'),
    JSON.stringify({ at: new Date().toISOString(), passed, failed: failed.length, results }, null, 1));
  console.log(`\nHUMAN TEST ${failed.length ? 'FAIL' : 'PASS'}  ${passed} passed · ${failed.length} failed`);
  console.log('screenshots: ' + OUT);
  // never browser.close() on a connectOverCDP session: it closes the user's real browser
  process.exit(failed.length ? 1 : 0);
})().catch((err) => { console.error('FATAL', err); process.exit(2); });
