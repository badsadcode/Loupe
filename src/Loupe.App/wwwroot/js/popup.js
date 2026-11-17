// The pop-out viewer (popup.html, a second window for a second monitor). It shows what the main
// window sends ("popup.image": the focused image and its correction) and sends key presses back
// ("popup.action"). Zoom / pan are its own.
import { call, on } from './bridge.js';
import { initI18n, t } from './i18n.js';
import { hydrateIcons } from './icons.js';
import { Viewer } from './viewer.js';

const $ = (s) => document.querySelector(s);
const IMG = 'https://img.loupe';
let cur = null, viewer, token = 0, wantFull = false, infoCache = new Map();

function applyViewBg(bg) {
  const root = document.documentElement.style;
  if (!bg || bg === 'theme' || !/^#[0-9a-f]{6}$/i.test(bg)) { root.removeProperty('--view-bg'); return; }
  root.setProperty('--view-bg', bg);
}

/** Long side as shown: after rotation and crop of the correction (same rule as the main viewer). */
function longSide(info, e) {
  let w = info.width, h = info.height;
  if (e && (e.rotation === 90 || e.rotation === 270)) [w, h] = [h, w];
  if (e) { w *= 1 - (e.cropLeft || 0) - (e.cropRight || 0); h *= 1 - (e.cropTop || 0) - (e.cropBottom || 0); }
  return Math.max(w, h);
}

async function show(p, keepView) {
  const changed = !cur || !p || p.path !== cur.path;
  cur = p;
  $('#empty').hidden = !!p?.path;
  $('#img').hidden = !p?.path;
  $('#flag').hidden = !p?.rescan;
  $('#badge').hidden = !p?.before;
  if (!p?.path) { $('#pos').textContent = ''; $('#name').textContent = ''; document.title = t('Loupe — viewer'); return; }
  if (changed) wantFull = false;
  $('#pos').textContent = `${p.index + 1} / ${p.count}`;
  $('#name').textContent = p.name;
  document.title = `${p.name} — Loupe`;
  const r = $('#stage').getBoundingClientRect();
  const max = wantFull ? 0 : Math.ceil(Math.max(r.width, r.height, 800) * (window.devicePixelRatio || 1));
  const e = !p.before && p.edit ? `&e=${encodeURIComponent(p.edit)}` : '';
  const url = `${IMG}/view?p=${encodeURIComponent(p.path)}&max=${max}&v=${p.modified}${e}`;
  const my = ++token;
  $('#loading').hidden = false;
  const probe = new Image();
  probe.onload = async () => {
    if (my !== token) return;
    $('#img').src = url;
    $('#loading').hidden = true;
    const key = p.path + '|' + p.modified;
    let info = infoCache.get(key);
    if (!info) { try { info = await call('img.info', { path: p.path }); infoCache.set(key, info); } catch { info = null; } }
    if (my !== token) return;
    const edit = !p.before && p.edit ? JSON.parse(p.edit) : null;
    const long = info ? longSide(info, edit) : Math.max(probe.naturalWidth, probe.naturalHeight);
    const k = long / Math.max(probe.naturalWidth, probe.naturalHeight);
    viewer.setFileSize(Math.round(probe.naturalWidth * k), Math.round(probe.naturalHeight * k), keepView && !changed);
  };
  probe.onerror = () => { if (my === token) $('#loading').hidden = true; };
  probe.src = url;
}

const action = (a) => call('popup.action', { action: a }).catch(() => {});

const acts = {
  zoomIn: () => viewer.zoomBy(1.25),
  zoomOut: () => viewer.zoomBy(1 / 1.25),
  fit: () => viewer.fit(),
  actual: () => viewer.actual(),
  topmost: async () => paintTopmost(await call('popup.topmost', {})),
  fullscreen: () => call('popup.fullscreen', {}),
  close: () => call('popup.close'),
};

function paintTopmost(on) { $('#topmost').classList.toggle('on', !!on); $('#topmost').setAttribute('aria-pressed', String(!!on)); }

document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-act]');
  if (b) acts[b.dataset.act]();
});

document.addEventListener('keydown', (e) => {
  const k = e.key, ctrl = e.ctrlKey || e.metaKey;
  if (k === 'F11' || (k.toLowerCase() === 'f' && !ctrl)) { e.preventDefault(); acts.fullscreen(); }
  else if (k === 'Escape') { if (document.body.classList.contains('fs')) acts.fullscreen(); else acts.close(); }
  else if (ctrl && k.toLowerCase() === 'r') { e.preventDefault(); action('rescan'); }
  else if (k === 'ArrowRight' || k.toLowerCase() === 'c' || k === 'PageDown' || k === ' ') { e.preventDefault(); action('next'); }
  else if (k === 'ArrowLeft' || k.toLowerCase() === 'x' || k === 'PageUp') { e.preventDefault(); action('prev'); }
  else if (k === 'Home') action('first');
  else if (k === 'End') action('last');
  else if (k === '0') viewer.fit();
  else if (k === '1') viewer.actual();
  else if (k === '+' || k === '=') viewer.zoomBy(1.25);
  else if (k === '-') viewer.zoomBy(1 / 1.25);
  else if (k.toLowerCase() === 'b' && !e.repeat) action('before-on');
});
document.addEventListener('keyup', (e) => { if (e.key.toLowerCase() === 'b') action('before-off'); });

// In full screen the bar hides; it comes back when the mouse is near the top.
let hideTimer;
document.addEventListener('mousemove', (e) => {
  if (!document.body.classList.contains('fs')) return;
  document.body.classList.toggle('bar-shown', e.clientY < 60);
  clearTimeout(hideTimer);
  hideTimer = setTimeout(() => document.body.classList.remove('bar-shown'), 2500);
});

on('popup.image', (p) => show(p, true));
on('popup.fullscreen', ({ on: fs }) => { document.body.classList.toggle('fs', fs); setTimeout(() => viewer.fit(), 80); });
on('popup.theme', (th) => { document.documentElement.dataset.theme = th.theme; applyViewBg(th.viewBackground); });

(async () => {
  await initI18n();
  hydrateIcons();
  viewer = new Viewer($('#stage'), $('#img'), (v) => {
    $('#zoom').textContent = `${Math.round(v.scale * 100)}%`;
    if (v.needFull && !wantFull && cur) { wantFull = true; show(cur, true); }
  });
  const r = await call('popup.ready');
  document.documentElement.dataset.theme = r.theme;
  applyViewBg(r.viewBackground);
  paintTopmost(r.topmost);
  document.body.classList.toggle('fs', r.fullscreen);
})();
