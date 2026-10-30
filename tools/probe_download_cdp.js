const { chromium } = require('playwright');
const fs = require('fs');
const TOKEN = fs.readFileSync('<INSTALL_DIR>/data/state/bridge.token','utf8').trim();
(async () => {
  const b = await chromium.connectOverCDP('http://127.0.0.1:9333');
  console.log('attached');
  const r = await fetch('http://127.0.0.1:9337/api/agent/open', {
    method:'POST', headers:{'X-LYGO-Token':TOKEN,'Content-Type':'application/json'},
    body: JSON.stringify({url:'http://127.0.0.1:9337/selftest-sample.bin', newTab:true})});
  console.log('open ->', r.status);
  await new Promise(r => setTimeout(r, 3000));
  try {
    const h = await fetch('http://127.0.0.1:9337/api/health');
    const j = await h.json();
    console.log('BRIDGE ALIVE after download:', j.ok, 'downloads:', j.downloadCount);
  } catch (e) { console.log('BRIDGE DEAD:', e.message); }
  // deliberately do NOT browser.close() - that would close the user's browser
  process.exit(0);
})().catch(e => { console.log('FATAL', e.message); process.exit(2); });
