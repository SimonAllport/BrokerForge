// What every scene shares: captions, a visible pointer with a click ring, title cards, and screenshots. The same look as
// the DataverseToCode videos (a dark caption box at the foot of the window). All of it is drawn into the page itself, so
// it works on any site, and it is put back after each navigation.
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { images, placeholders } from './env.mjs';

export const size = { width: 1440, height: 860 };

/** Runs in the page: draws (or clears) the caption, a title card, and hides real names. */
function overlayScript() {
  window.__bf = window.__bf || {};
  const bf = window.__bf;
  bf.caption = (text, title) => {
    let el = document.getElementById('bf-caption');
    if (!el) {
      el = document.createElement('div');
      el.id = 'bf-caption';
      Object.assign(el.style, {
        position: 'fixed', left: '50%', transform: 'translateX(-50%)', zIndex: '2147483646', pointerEvents: 'none',
        background: 'rgba(20, 24, 33, 0.88)', color: '#fff', borderRadius: '10px', fontFamily: '-apple-system, "Segoe UI", sans-serif',
        boxShadow: '0 6px 24px rgba(0,0,0,0.25)', textAlign: 'center', transition: 'opacity 250ms', maxWidth: '80%',
      });
      document.documentElement.appendChild(el);
    }
    el.textContent = text || '';
    // Over the dark console the usual dark box disappears: use VS Code's blue there.
    el.style.background = document.body?.dataset.dark ? 'rgba(14, 99, 156, 0.96)' : 'rgba(20, 24, 33, 0.88)';
    Object.assign(el.style, title
      ? { top: '42%', bottom: '', fontSize: '34px', fontWeight: '600', padding: '26px 44px' }
      : { top: '', bottom: '48px', fontSize: '21px', fontWeight: '400', padding: '14px 28px' });
    el.style.opacity = text ? '1' : '0';
  };
  bf.cover = (text) => {
    let el = document.getElementById('bf-cover');
    if (!text) { el?.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.id = 'bf-cover';
      Object.assign(el.style, { position: 'fixed', inset: '0', zIndex: '2147483645', background: '#f3f2f1', display: 'flex', alignItems: 'center',
        justifyContent: 'center', color: '#323130', fontFamily: '-apple-system, "Segoe UI", sans-serif', fontSize: '24px' });
      document.documentElement.appendChild(el);
    }
    el.textContent = text;
  };
  bf.mask = (pairs) => {
    const walk = node => {
      if (node.nodeType === 3) {
        let t = node.nodeValue, changed = false;
        for (const [real, shown] of pairs) if (t.includes(real)) { t = t.split(real).join(shown); changed = true; }
        if (changed) node.nodeValue = t;
      } else if (node.nodeType === 1 && !['SCRIPT', 'STYLE'].includes(node.tagName)) {
        // Never 'value': a form (the OAuth redirect_uri) would send the placeholder.
        for (const a of ['title', 'aria-label', 'placeholder']) {
          const v = node.getAttribute?.(a);
          if (v) for (const [real, shown] of pairs) if (v.includes(real)) node.setAttribute(a, v.split(real).join(shown));
        }
        for (const c of node.childNodes) walk(c);
      }
    };
    walk(document.documentElement);
  };
}

/**
 * Runs in every page of a recording context before its own scripts: real names replaced as the page changes, the
 * signed-in user's photo covered by a letter, and Microsoft's sign-in pages (which show the account) hidden behind a
 * plain screen; Playwright still clicks through them.
 */
export function initScript({ pairs, letter, orgHost }) {
  return `(() => {
    (${overlayScript})();
    const pairs = ${JSON.stringify(pairs)};
    const start = () => {
      window.__bf.mask(pairs);
      new MutationObserver(() => window.__bf.mask(pairs)).observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['title', 'aria-label'] });
      if (location.host === ${JSON.stringify(orgHost)}) {
        const a = document.createElement('div');
        a.textContent = ${JSON.stringify(letter)};
        Object.assign(a.style, { position: 'fixed', left: '1396px', top: '4px', width: '40px', height: '40px', borderRadius: '50%', background: '#5c2d91',
          color: '#fff', border: '2px solid #fff', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: '2147483640',
          font: '600 16px -apple-system, "Segoe UI", sans-serif', pointerEvents: 'none' });
        document.documentElement.appendChild(a);
      }
      if (/login\\.(microsoftonline|live)\\.com$/.test(location.host)) {
        window.__bf.cover('Signing in with Microsoft…');
        document.getElementById('bf-cover').style.pointerEvents = 'none';
      }
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  })();`;
}

export function stage(page, { mode = 'video', prefix = 'shot', hide = [] } = {}) {
  const video = mode === 'video';
  const pairs = [...placeholders, ...hide];
  const ensure = () => page.evaluate(`(${overlayScript})()`).catch(() => undefined);

  /** Replaces real names on the page now and whenever it changes. */
  async function mask() {
    await ensure();
    await page.evaluate(p => {
      window.__bf.mask(p);
      if (!window.__bfObserver) {
        window.__bfObserver = new MutationObserver(() => window.__bf.mask(p));
        window.__bfObserver.observe(document.documentElement, { subtree: true, childList: true, characterData: true });
      }
    }, pairs).catch(() => undefined);
  }
  const wait = ms => page.waitForTimeout(video ? ms : Math.min(ms, 400));

  const say = async (text, ms) => {
    if (!video) return;
    await ensure();
    await page.evaluate(t => window.__bf.caption(t, false), text);
    // Long enough to read: about 16 characters a second, never under 2.8 seconds.
    await page.waitForTimeout(ms ?? Math.max(2800, text.length * 62));
  };
  const hush = async () => { if (video) { await ensure(); await page.evaluate(() => window.__bf.caption('')); } };
  const title = async (text, ms = 3200) => {
    if (!video) return;
    await ensure();
    await page.evaluate(t => window.__bf.caption(t, true), text);
    await page.waitForTimeout(ms);
    await page.evaluate(() => window.__bf.caption(''));
  };
  /** A plain screen over the page while something not worth showing happens (a sign-in redirect). */
  const cover = async text => { await ensure(); await page.evaluate(t => window.__bf.cover(t), text ?? ''); };

  async function pointerTo(x, y) {
    if (!video) return;
    await ensure();
    await page.evaluate(({ x, y }) => {
      let p = document.getElementById('bf-pointer');
      if (!p) {
        p = document.createElement('div');
        p.id = 'bf-pointer';
        const ns = 'http://www.w3.org/2000/svg';
        const svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('width', '26'); svg.setAttribute('height', '26'); svg.setAttribute('viewBox', '0 0 24 24');
        const path = document.createElementNS(ns, 'path');
        for (const [k, v] of Object.entries({ d: 'M4 2l15 9-6.5 1.5L16 20l-3 1.5-3.5-7.5L4 19z', fill: '#111', stroke: '#fff', 'stroke-width': '1.5', 'stroke-linejoin': 'round' })) path.setAttribute(k, v);
        svg.appendChild(path);
        p.appendChild(svg);
        Object.assign(p.style, { position: 'fixed', left: `${window.__bfLastX ?? 700}px`, top: `${window.__bfLastY ?? 600}px`, zIndex: '2147483647',
          pointerEvents: 'none', transition: 'left 550ms ease-in-out, top 550ms ease-in-out' });
        document.documentElement.appendChild(p);
      }
      requestAnimationFrame(() => { p.style.left = `${x - 3}px`; p.style.top = `${y - 2}px`; });
      window.__bfLastX = x; window.__bfLastY = y;
    }, { x, y });
    await page.waitForTimeout(650);
  }
  async function ripple(x, y) {
    await page.evaluate(({ x, y }) => {
      const r = document.createElement('div');
      Object.assign(r.style, { position: 'fixed', left: `${x - 16}px`, top: `${y - 16}px`, width: '32px', height: '32px', borderRadius: '50%', zIndex: '2147483647',
        border: '3px solid rgba(0,120,212,0.85)', pointerEvents: 'none', transition: 'transform 400ms, opacity 400ms' });
      document.documentElement.appendChild(r);
      requestAnimationFrame(() => { r.style.transform = 'scale(1.8)'; r.style.opacity = '0'; });
      setTimeout(() => r.remove(), 450);
    }, { x, y });
  }
  /** Glides the pointer to a locator, rings, and clicks it. */
  async function tap(locator, options = {}) {
    await hush();
    await locator.waitFor({ state: 'visible', timeout: 60_000 });
    if (video) {
      const box = await locator.boundingBox();
      if (box) {
        const x = box.x + Math.min(box.width / 2, 60), y = box.y + box.height / 2;
        await pointerTo(x, y);
        await ripple(x, y);
      }
    }
    await locator.click(options);
    await wait(400);
  }
  /** Types like a person in video mode. */
  async function type(locator, text, { secret = false } = {}) {
    await tap(locator);
    if (secret) await locator.fill(text); // shown as dots either way, and fill cannot lose a keystroke
    else await locator.pressSequentially(text, { delay: video ? 45 : 5 });
  }
  /** Saves docs/guide/images/<prefix>-<name>.png (captions and pointer hidden), in shots mode only. */
  async function shot(name) {
    if (video) return;
    await mask();
    mkdirSync(images, { recursive: true });
    await page.evaluate(() => ['bf-caption', 'bf-pointer'].forEach(id => document.getElementById(id)?.remove())).catch(() => undefined);
    await page.screenshot({ path: join(images, `${prefix}-${name}.png`) });
  }

  return { video, say, hush, title, cover, tap, type, shot, mask, wait, pointerTo };
}
