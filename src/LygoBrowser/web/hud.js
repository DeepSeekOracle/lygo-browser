/* ==========================================================================
   LYGO BROWSER — shared HUD runtime
   Same-origin API client + formatters, toasts, pills and small DOM helpers.
   No dependencies, no network beyond the local bridge.
   ========================================================================== */
(function () {
  'use strict';

  var TOAST_MAX = 4;
  var HEALTH_MS = 12000;
  var toastWrap = null;
  var healthCache = null;
  var pillTimer = 0;

  function $(sel, root) { return (root || document).querySelector(sel); }
  function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

  var ESC_MAP = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  function esc(value) {
    return String(value === null || value === undefined ? '' : value).replace(/[&<>"']/g, function (ch) {
      return ESC_MAP[ch];
    });
  }

  function token() {
    return typeof window.LYGO_TOKEN === 'string' ? window.LYGO_TOKEN : '';
  }

  function el(tag, attrs) {
    var node = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (key) {
        var val = attrs[key];
        if (val === null || val === undefined || val === false) return;
        if (key === 'class') node.className = val;
        else if (key === 'text') node.textContent = String(val);
        else if (key === 'dataset') Object.assign(node.dataset, val);
        else if (key === 'value' && 'value' in node) node.value = val;
        else node.setAttribute(key, val === true ? '' : String(val));
      });
    }
    var kids = Array.prototype.slice.call(arguments, 2);
    appendAll(node, kids);
    return node;
  }

  function appendAll(node, kids) {
    kids.forEach(function (kid) {
      if (kid === null || kid === undefined || kid === false) return;
      if (Array.isArray(kid)) { appendAll(node, kid); return; }
      node.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
    });
  }

  function emptyNode(title, sub) {
    var node = el('div', { class: 'empty' }, el('b', { text: title }));
    if (sub) node.append(el('span', { text: sub }));
    return node;
  }

  function domain(url) {
    try {
      var u = new URL(String(url), window.location.href);
      return u.hostname.replace(/^www\./i, '');
    } catch (err) {
      return '';
    }
  }

  function fmtDate(value) {
    if (value === null || value === undefined || value === '') return '\u2014';
    var raw = String(value);
    if (/^\d{4}-\d{2}-\d{2}$/.test(raw)) return raw;
    var d;
    if (typeof value === 'number') d = new Date(value < 1e12 ? value * 1000 : value);
    else d = new Date(value);
    if (isNaN(d.getTime())) return raw.slice(0, 10);
    var p = function (n) { return String(n).padStart(2, '0'); };
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate());
  }

  function fmtBytes(value) {
    var n = Number(value);
    if (!isFinite(n) || n <= 0) return '0 B';
    if (n < 1024) return Math.round(n) + ' B';
    var units = ['KB', 'MB', 'GB', 'TB'];
    var i = -1;
    do { n = n / 1024; i += 1; } while (n >= 1024 && i < units.length - 1);
    return (n >= 100 ? n.toFixed(0) : n.toFixed(1)) + ' ' + units[i];
  }

  function fmtInt(value) {
    var n = Number(value);
    return isFinite(n) ? n.toLocaleString('en-US') : '\u2014';
  }

  function debounce(fn, wait) {
    var t = 0;
    return function () {
      var ctx = this;
      var args = arguments;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(ctx, args); }, wait);
    };
  }

  function ensureToastWrap() {
    if (toastWrap && toastWrap.isConnected) return toastWrap;
    toastWrap = el('div', { class: 'toast-wrap', role: 'status', 'aria-live': 'polite' });
    document.body.append(toastWrap);
    return toastWrap;
  }

  function toast(message, kind) {
    if (!document.body) return;
    var wrap = ensureToastWrap();
    var tone = kind === 'ok' ? 'ok' : kind === 'err' ? 'err' : 'info';
    var node = el('div', { class: 'toast toast--' + tone }, String(message === undefined ? '' : message));
    wrap.append(node);
    while (wrap.children.length > TOAST_MAX) wrap.firstElementChild.remove();
    setTimeout(function () {
      node.classList.add('is-out');
      setTimeout(function () { node.remove(); }, 200);
    }, tone === 'err' ? 5200 : 3400);
  }

  function api(path, opts) {
    var options = opts || {};
    var method = String(options.method || 'GET').toUpperCase();
    var headers = { Accept: 'application/json' };
    if (method !== 'GET') {
      headers['Content-Type'] = 'application/json';
      var t = token();
      if (t) headers['X-LYGO-Token'] = t;
    }
    return fetch(path, {
      method: method,
      headers: headers,
      cache: 'no-store',
      credentials: 'same-origin',
      body: options.body === undefined ? undefined : JSON.stringify(options.body)
    }).catch(function () {
      if (!options.silent) toast('Bridge unreachable: ' + path, 'err');
      var netErr = new Error('network');
      netErr.network = true;
      throw netErr;
    }).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (err) { data = null; }
        if (!res.ok) {
          var msg = (data && (data.error || data.message)) || 'HTTP ' + res.status;
          if (!options.silent) toast(msg, 'err');
          var err = new Error(msg);
          err.status = res.status;
          err.data = data;
          throw err;
        }
        if (data === null) {
          if (!options.silent) toast('Bridge returned no JSON: ' + path, 'err');
          throw new Error('bad-json');
        }
        return data;
      });
    });
  }

  function copy(text) {
    var value = String(text === undefined || text === null ? '' : text);
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(value).then(function () { return true; }, function () {
        return legacyCopy(value);
      });
    }
    return Promise.resolve(legacyCopy(value));
  }

  function legacyCopy(value) {
    try {
      var ta = el('textarea', { readonly: true });
      ta.value = value;
      ta.style.position = 'fixed';
      ta.style.top = '-1000px';
      ta.style.opacity = '0';
      document.body.append(ta);
      ta.select();
      var ok = document.execCommand('copy');
      ta.remove();
      return ok;
    } catch (err) {
      return false;
    }
  }

  function openUrl(url) {
    if (!url) return Promise.resolve(false);
    var body = { url: url, newTab: true };
    var done = function (res) {
      if (res && res.ok === false) {
        toast(res.error || 'Open failed', 'err');
        return false;
      }
      return true;
    };
    /* The HUD brief uses /api/open; the installed bridge exposes /api/agent/open.
       Try the documented path first, then the agent namespace. */
    return api('/api/open', { method: 'POST', body: body, silent: true })
      .then(done)
      .catch(function (err) {
        if (!err || err.status !== 404) {
          toast('Open failed: ' + url, 'err');
          return false;
        }
        return api('/api/agent/open', { method: 'POST', body: body })
          .then(done)
          .catch(function () { return false; });
      });
  }

  /* ---- pills ------------------------------------------------------------ */

  function setPill(key, value, state) {
    $$('[data-pill="' + key + '"]').forEach(function (node) {
      var v = node.querySelector('.v');
      if (v) v.textContent = value === null || value === undefined ? '\u2014' : String(value);
      var dot = node.querySelector('.dot');
      if (dot) dot.className = 'dot ' + (state === 'ok' ? 'dot--ok' : state === 'bad' ? 'dot--bad' : state === 'wait' ? 'dot--wait' : '');
    });
  }

  function bridgeOk(value) {
    if (value === true) return true;
    if (value === false || value === null || value === undefined) return false;
    if (typeof value === 'object') return !!value.ok;
    return ['0', 'false', 'down', 'off', 'offline', 'no', 'error'].indexOf(String(value).toLowerCase()) === -1;
  }

  function cdpLabel(value) {
    if (value === true) return 'ON';
    if (value === false || value === null || value === undefined || value === '') return '\u2014';
    if (typeof value === 'number') return ':' + value;
    var s = String(value);
    return /^\d+$/.test(s) ? ':' + s : s.toUpperCase();
  }

  function applyHealth(h) {
    if (!h || typeof h !== 'object') throw new Error('empty health');
    setPill('bridge', bridgeOk(h.bridge) ? 'OK' : 'DOWN', bridgeOk(h.bridge) ? 'ok' : 'bad');
    setPill('cdp', cdpLabel(h.cdp), h.cdp ? 'ok' : 'bad');
    setPill('archive', fmtInt(h.archiveCount), 'idle');
    setPill('vault', fmtInt(h.vaultCount), 'idle');
    setPill('record', fmtInt(h.recordCount), 'idle');
  }

  function offlinePills() {
    setPill('bridge', 'DOWN', 'bad');
    setPill('cdp', '\u2014', 'bad');
    setPill('archive', '\u2014', 'idle');
    setPill('vault', '\u2014', 'idle');
    setPill('record', '\u2014', 'idle');
  }

  function refreshHealth() {
    return api('/api/health', { silent: true }).then(function (h) {
      healthCache = h;
      applyHealth(h);
      return h;
    }).catch(function () {
      healthCache = null;
      offlinePills();
      return null;
    });
  }

  function startPills() {
    if (pillTimer) return;
    refreshHealth();
    pillTimer = window.setInterval(refreshHealth, HEALTH_MS);
  }

  function getHealth() {
    return api('/api/health', { silent: true }).then(function (h) {
      healthCache = h;
      return h;
    }).catch(function () { return healthCache; });
  }

  function boot() {
    if ($('[data-pill]')) startPills();
  }

  window.LYGO = {
    api: api,
    $: $,
    $$: $$,
    el: el,
    esc: esc,
    empty: emptyNode,
    domain: domain,
    fmtDate: fmtDate,
    fmtBytes: fmtBytes,
    fmtInt: fmtInt,
    debounce: debounce,
    toast: toast,
    copy: copy,
    openUrl: openUrl,
    getHealth: getHealth,
    refreshHealth: refreshHealth,
    pills: { set: setPill, start: startPills, refresh: refreshHealth }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot, { once: true });
  } else {
    boot();
  }
})();
