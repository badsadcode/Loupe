// Loupe — page logic. Host commands: Services/AppController.cs. Images: https://img.loupe/.
import { call, on, inHost } from './bridge.js';
import { initI18n, t, tn, currentLanguage } from './i18n.js';
import { hydrateIcons, icon } from './icons.js';
import { initSegmented, initRanges, paintRange } from './ui.js';
import { Viewer } from './viewer.js';
import { Overlay } from './overlay.js';

const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const IMG = 'https://img.loupe';

// Themes: id → name and swatch (bg, surface, accent). Colours live in css/themes.css.
const THEMES = [
  { id: 'graphite', name: 'Graphite', sw: ['#0e0f12', '#22262d', '#f5a524'] },
  { id: 'daylight', name: 'Daylight', sw: ['#f4f5f7', '#ffffff', '#e8970f'] },
  { id: 'midnight', name: 'Midnight', sw: ['#0b1020', '#1d2846', '#38bdf8'] },
  { id: 'darkroom', name: 'Darkroom', sw: ['#0a0606', '#221212', '#e5484d'] },
  { id: 'sepia', name: 'Sepia', sw: ['#f3ecdf', '#fbf6ec', '#a0612b'] },
  { id: 'forest', name: 'Forest', sw: ['#0c120f', '#1d2b23', '#4ade80'] },
  { id: 'strawberry', name: 'Strawberry Milk', sw: ['#fde8ef', '#fff6f9', '#b8608a'] },
  { id: 'lavender', name: 'Lavender Dream', sw: ['#ece6ff', '#faf8ff', '#8670b8'] },
  { id: 'mint', name: 'Mint Macaron', sw: ['#e6f4ec', '#f6fffa', '#5a9a84'] },
  { id: 'orchid', name: 'Midnight Orchid', sw: ['#221b2e', '#3d3150', '#d8b0ec'] },
];

/**
 * Viewing background (thumbnails + viewer): 'theme' follows the theme, otherwise a colour.
 * Text, thumbnail plates and hover are derived from it so names stay readable on any colour,
 * and they stay neutral (grey), so nothing tints the scans.
 */
function applyViewBg(bg) {
  const root = document.documentElement.style;
  const vars = ['--view-bg', '--view-text', '--view-text-2', '--view-thumb', '--view-hover'];
  if (!bg || bg === 'theme' || !/^#[0-9a-f]{6}$/i.test(bg)) vars.forEach((v) => root.removeProperty(v));
  else {
    const n = parseInt(bg.slice(1), 16), r = n >> 16, g = (n >> 8) & 255, b = n & 255;
    const lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    const dark = lum < 0.5;
    const shift = (k) => '#' + [r, g, b].map((c) => Math.max(0, Math.min(255, Math.round(c + k))).toString(16).padStart(2, '0')).join('');
    root.setProperty('--view-bg', bg);
    root.setProperty('--view-text', dark ? '#f2f2f2' : '#141414');
    root.setProperty('--view-text-2', dark ? 'rgba(242,242,242,.68)' : 'rgba(20,20,20,.68)');
    root.setProperty('--view-thumb', shift(dark ? 14 : -14));
    root.setProperty('--view-hover', dark ? 'rgba(255,255,255,.07)' : 'rgba(0,0,0,.06)');
  }
  $$('#view-bgs .bg-swatch').forEach((s) => s.setAttribute('aria-checked', String(s.dataset.bg === (bg || 'theme'))));
  if (/^#[0-9a-f]{6}$/i.test(bg || '')) $('#view-bg-custom').value = bg;
  state.settings.viewBackground = bg || 'theme';
  syncPopupTheme();
}
$$('#view-bgs .bg-swatch').forEach((s) => s.addEventListener('click', () => { applyViewBg(s.dataset.bg); saveSettings({ viewBackground: s.dataset.bg }); }));
$('#view-bg-custom').addEventListener('input', (e) => applyViewBg(e.target.value));
$('#view-bg-custom').addEventListener('change', (e) => saveSettings({ viewBackground: e.target.value }));

const state = {
  init: null,
  settings: {},
  roots: { drives: [], favorites: [] },
  path: '',
  parent: null,
  folders: [],
  images: [],
  items: [],            // folders then images, as shown in the grid
  selected: new Set(),  // indices into items
  focus: -1,
  anchor: -1,
  viewer: false,
  vIndex: -1,           // index into images while the viewer is open
  info: null,           // img.info of the focused image
  before: false,
  job: false,
};

// ------------------------------------------------------------------ small helpers

function toast(msg, kind = '') {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.textContent = msg;
  $('#toasts').append(el);
  setTimeout(() => el.remove(), kind === 'error' ? 6000 : 3200);
}

function status(msg) { $('#status').textContent = msg; }

async function run(cmd, args) {
  try { return await call(cmd, args); }
  catch (e) { toast(e.message, 'error'); throw e; }
}

const debounce = (fn, ms) => { let h; return (...a) => { clearTimeout(h); h = setTimeout(() => fn(...a), ms); }; };
const fmtBytes = (b) => b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(2)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`;
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const sepOf = (p) => (p.includes('\\') ? '\\' : '/');
const baseName = (p) => p.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || p;
const isPartName = (n) => /^.+_\d+$/.test(n);

function saveSettings(patch) {
  Object.assign(state.settings, patch);
  if (inHost) call('settings.set', patch).catch(() => {});
}

// ------------------------------------------------------------------ themes

function applyTheme(id) {
  if (!THEMES.some((th) => th.id === id)) id = 'graphite';
  document.documentElement.dataset.theme = id;
  const cs = getComputedStyle(document.documentElement);
  const dark = cs.getPropertyValue('--is-dark').trim() !== '0';
  const background = cs.getPropertyValue('--surface').trim() || '#14161a';
  if (inHost) call('app.theme', { theme: id, dark, background }).catch(() => {});
  $$('.theme-card').forEach((c) => c.setAttribute('aria-checked', String(c.dataset.theme === id)));
  state.settings.theme = id;
  syncPopupTheme();
}

function syncPopupTheme() {
  if (state.popupOpen) call('popup.themeSync', { theme: state.settings.theme, viewBackground: state.settings.viewBackground }).catch(() => {});
}

function buildThemes() {
  $('#themes').innerHTML = THEMES.map((th) => `
    <button class="theme-card" role="radio" data-theme="${th.id}">
      <span class="sw">${th.sw.map((c) => `<span style="background:${c}"></span>`).join('')}</span>
      <span>${esc(t(th.name))}</span>
    </button>`).join('');
  $$('.theme-card').forEach((c) => c.addEventListener('click', () => applyTheme(c.dataset.theme)));
}

// ------------------------------------------------------------------ tree

function treeNode({ name, path, kind, label, hasChildren = true, removable = false, missing = false }) {
  const node = document.createElement('div');
  node.className = 'node';
  node.dataset.path = path;
  const ico = kind === 'removable' ? 'usb' : kind === 'network' ? 'server' : kind === 'fixed' ? 'hard-drive' : kind === 'favorite' ? 'star' : 'folder';
  node.innerHTML = `<div class="row${missing ? ' missing' : ''}" role="treeitem" title="${esc(path)}">
      <span class="tw${hasChildren ? '' : ' leaf'}">${icon('chevron-right', 13)}</span>
      <span class="ico">${icon(ico, 15)}</span><span class="nm">${esc(name)}</span>
      ${label ? `<span class="lbl">${esc(label)}</span>` : ''}
      ${removable ? `<button class="icon-btn x" title="${esc(t('Unpin'))}">${icon('x', 13)}</button>` : ''}
    </div><div class="kids" hidden></div>`;
  const row = node.firstElementChild;
  row.querySelector('.tw').addEventListener('click', (e) => { e.stopPropagation(); toggleNode(node); });
  row.addEventListener('click', () => openFolder(path));
  row.addEventListener('dblclick', () => toggleNode(node, true));
  row.querySelector('.x')?.addEventListener('click', async (e) => {
    e.stopPropagation();
    state.roots = await run('fs.favoriteRemove', { path });
    renderTree();
  });
  return node;
}

async function toggleNode(node, forceOpen) {
  const kids = node.querySelector(':scope > .kids');
  if (node.classList.contains('open') && !forceOpen) { node.classList.remove('open'); kids.hidden = true; return; }
  if (!kids.dataset.loaded) {
    let subs = [];
    try { subs = await call('fs.subfolders', { path: node.dataset.path }); } catch (e) { toast(e.message, 'error'); return; }
    kids.innerHTML = '';
    for (const s of subs) kids.append(treeNode({ name: s.name, path: s.path, hasChildren: s.hasChildren }));
    kids.dataset.loaded = '1';
    if (!subs.length) node.querySelector('.tw').classList.add('leaf');
  }
  node.classList.add('open');
  kids.hidden = false;
}

function renderTree() {
  const tree = $('#tree');
  tree.innerHTML = '';
  const { drives, favorites } = state.roots;
  if (favorites.length) {
    tree.insertAdjacentHTML('beforeend', `<div class="tree-group">${esc(t('Pinned'))}</div>`);
    for (const f of favorites) tree.append(treeNode({ name: f.name, path: f.path, kind: 'favorite', removable: true, missing: !f.exists }));
  }
  tree.insertAdjacentHTML('beforeend', `<div class="tree-group">${esc(t('Drives'))}</div>`);
  for (const d of drives) tree.append(treeNode({ name: d.name, path: d.path, kind: d.kind, label: d.label }));
  markTree();
}

const samePath = (a, b) => a.replace(/[\\/]+$/, '').toLowerCase() === b.replace(/[\\/]+$/, '').toLowerCase();
const isUnder = (p, root) => { const r = root.replace(/[\\/]+$/, '').toLowerCase(); const x = p.toLowerCase(); return x === r || x.startsWith(r + '\\') || x.startsWith(r + '/'); };

function markTree() {
  $$('#tree .row.active').forEach((r) => r.classList.remove('active'));
  const hit = $$('#tree .node').filter((n) => samePath(n.dataset.path, state.path)).pop();
  if (hit) { hit.firstElementChild.classList.add('active'); hit.firstElementChild.scrollIntoView({ block: 'nearest' }); }
}

/** Expands the tree down to the current folder (under the longest matching root). */
async function revealInTree(path) {
  const roots = $$('#tree > .node').filter((n) => isUnder(path, n.dataset.path))
    .sort((a, b) => b.dataset.path.length - a.dataset.path.length);
  let node = roots[0];
  while (node && !samePath(node.dataset.path, path)) {
    await toggleNode(node, true);
    node = $$(':scope > .kids > .node', node).find((n) => isUnder(path, n.dataset.path));
  }
  markTree();
}

// ------------------------------------------------------------------ breadcrumb

function renderCrumbs() {
  const el = $('#crumbs');
  el.innerHTML = '';
  if (!state.path) return;
  const sep = sepOf(state.path);
  let parts, acc;
  if (state.path.startsWith('\\\\')) {
    const segs = state.path.slice(2).split('\\').filter(Boolean);
    acc = '\\\\' + segs.slice(0, 2).join('\\');
    parts = [{ name: acc, path: acc }];
    for (const s of segs.slice(2)) { acc += '\\' + s; parts.push({ name: s, path: acc }); }
  } else {
    const segs = state.path.split(/[\\/]/).filter(Boolean);
    acc = state.path.startsWith('/') ? '' : segs.shift();
    parts = [{ name: acc || '/', path: (acc || '') + sep }];
    for (const s of segs) { acc += sep + s; parts.push({ name: s, path: acc }); }
  }
  parts.forEach((p, i) => {
    if (i) el.insertAdjacentHTML('beforeend', `<span class="sep">${icon('chevron-right', 12)}</span>`);
    const b = document.createElement('button');
    b.textContent = p.name;
    b.title = p.path;
    b.addEventListener('click', () => openFolder(p.path));
    el.append(b);
  });
  el.scrollLeft = el.scrollWidth;
}

// ------------------------------------------------------------------ grid

let thumbObserver;

async function openFolder(path, { keepSelection = false, focusName = null } = {}) {
  if (!path) return;
  let r;
  try { r = await call('fs.list', { path }); } catch (e) { toast(e.message, 'error'); return; }
  if (!samePath(r.path, state.path) && pendingCount() &&
      !confirm(tn(pendingCount(), 'The correction of {n} image is not saved. Leave this folder and discard it?', 'The corrections of {n} images are not saved. Leave this folder and discard them?'))) return;
  if (!samePath(r.path, state.path)) { state.own.clear(); state.global = { ...NEUTRAL }; loadControls(state.global); $('#target').value = 'all'; }
  const prevNames = keepSelection ? new Set([...state.selected].map((i) => state.items[i]?.name)) : null;
  const sameFolder = samePath(r.path, state.path);
  state.path = r.path;
  state.parent = r.parent;
  state.folders = r.folders.map((f) => ({ ...f, type: 'folder' }));
  state.images = r.images.map((f) => ({ ...f, type: 'image' }));
  state.items = [...state.folders, ...state.images];
  state.selected = new Set();
  if (prevNames) state.items.forEach((it, i) => { if (prevNames.has(it.name)) state.selected.add(i); });
  const focusAt = focusName ? state.items.findIndex((it) => it.name === focusName) : -1;
  state.focus = focusAt >= 0 ? focusAt : sameFolder && keepSelection ? Math.min(state.focus, state.items.length - 1) : (state.images.length ? state.folders.length : state.items.length ? 0 : -1);
  state.anchor = state.focus;
  renderGrid();
  renderCrumbs();
  if (!sameFolder) revealInTree(state.path);
  const otherNote = r.otherFiles ? ' · ' + tn(r.otherFiles, '{n} other file', '{n} other files') : '';
  status(r.path + otherNote);
  refreshInspector();
}

function thumbUrl(it) {
  const e = editJson(effective(it));
  return `${IMG}/thumb?p=${encodeURIComponent(it.path)}&s=${thumbPx()}&v=${it.modified}${e ? `&e=${encodeURIComponent(e)}` : ''}`;
}

/** Corrections changed: visible thumbnails reload with the preview, the others when they scroll into view. */
const refreshThumbs = debounce(() => {
  const view = $('#grid-wrap').getBoundingClientRect();
  for (const tile of $$('#grid .tile.image')) {
    const it = state.items[Number(tile.dataset.i)];
    const img = tile.querySelector('img');
    if (!it || !img) continue;
    const url = thumbUrl(it);
    if (img.dataset.src === url) continue;
    img.dataset.src = url;
    const r = tile.getBoundingClientRect();
    if (r.bottom > view.top - 300 && r.top < view.bottom + 300) img.src = url;
    else if (img.getAttribute('src')) thumbObserver.observe(img);
  }
}, 180);

// Thumbnail resolution from the tile width actually shown (Auto: the slider; N columns: grid width / N).
function tileWidth() {
  const n = state.settings.gridColumns || 0;
  if (!n) return (state.settings.thumbSize || 180) * 1.25;   // Auto tiles stretch up to the next column
  return ($('#grid-wrap').clientWidth - 16 - (n - 1) * 6) / n;
}
const thumbPx = () => { const w = tileWidth() * (window.devicePixelRatio || 1); return w > 600 ? 1024 : w > 280 ? 512 : 256; };

/** Applies the column / size settings to the grid (Auto: as many columns as the size allows). */
function applyGridLayout(rerender) {
  const n = state.settings.gridColumns || 0;
  const before = $('#grid').dataset.px;
  document.documentElement.style.setProperty('--thumb', `${state.settings.thumbSize || 180}px`);
  if (n) $('#grid').style.setProperty('--cols', `repeat(${n}, minmax(0, 1fr))`); else $('#grid').style.removeProperty('--cols');
  $('#cols').value = String(n);
  $('#thumb-size').disabled = n > 0;
  $('#thumb-size').value = state.settings.thumbSize || 180;
  paintRange($('#thumb-size'));
  const px = String(thumbPx());
  $('#grid').dataset.px = px;
  if (rerender && before && before !== px) renderGrid();
}

const COLS = [1, 2, 3, 4, 5, 6, 8, 10, 12];
const saveLayout = debounce(() => saveSettings({ thumbSize: state.settings.thumbSize, gridColumns: state.settings.gridColumns }), 400);

/** Ctrl+wheel / Ctrl+plus/minus on the grid: bigger or smaller thumbnails (fewer or more columns). */
function zoomGrid(bigger) {
  const n = state.settings.gridColumns || 0;
  if (n) {
    const i = COLS.indexOf(n);
    state.settings.gridColumns = COLS[Math.max(0, Math.min(COLS.length - 1, (i < 0 ? 3 : i) + (bigger ? -1 : 1)))];
  } else {
    state.settings.thumbSize = Math.max(80, Math.min(520, (state.settings.thumbSize || 180) + (bigger ? 20 : -20)));
  }
  applyGridLayout(true);
  saveLayout();
}

// Tiles take the shape of the folder's typical image (landscape spreads, portrait pages), so the
// thumbnails fill them with as little frame as possible.
let aspects = [];
function noteAspect(r) {
  if (!r || aspects.length >= 15) return;
  aspects.push(r);
  if (aspects.length < 3 && aspects.length < state.images.length) return;
  const sorted = [...aspects].sort((a, b) => a - b);
  const med = Math.max(0.6, Math.min(1.8, sorted[Math.floor(sorted.length / 2)]));
  $('#grid').style.setProperty('--pic-aspect', med.toFixed(3));
}

function renderGrid() {
  const grid = $('#grid');
  aspects = [];
  grid.style.removeProperty('--pic-aspect');
  thumbObserver?.disconnect();
  grid.innerHTML = '';
  $('#empty').hidden = state.items.length > 0 || !state.path ? state.items.length > 0 : false;
  if (state.path && !state.items.length) {
    $('#empty').hidden = false;
    $('#empty h2').textContent = t('This folder is empty');
    $('#empty p').textContent = t('No subfolders and no JPEG images here.');
  }
  thumbObserver = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      const img = e.target;
      thumbObserver.unobserve(img);
      img.src = img.dataset.src;
    }
  }, { root: $('#grid-wrap'), rootMargin: '600px' });

  const frag = document.createDocumentFragment();
  state.items.forEach((it, i) => {
    const tile = document.createElement('div');
    tile.className = `tile ${it.type}`;
    tile.dataset.i = i;
    if (it.type === 'folder') {
      tile.innerHTML = `<div class="pic">${icon('folder', 64)}</div><div class="name" title="${esc(it.name)}">${esc(it.name)}</div>
        ${isPartName(it.name) ? `<span class="chip part">${esc(t('part'))}</span>` : ''}`;
    } else {
      tile.innerHTML = `<div class="pic"><img alt="" draggable="false"></div><div class="name" title="${esc(it.name)}">${esc(it.name)}</div>
        ${it.edited ? `<span class="badge" title="${esc(t('Edited — the original is kept'))}">${icon('pencil-line', 12)}</span>` : ''}
        <span class="own-dot" title="${esc(t('Own correction (not saved yet)'))}"></span>
        <span class="flag" title="${esc(t('Marked for rescanning'))}">${icon('flag', 12)}</span>
        <span class="crop-mark" title="${esc(t('Cropped (preview, not saved yet)'))}">${icon('crop', 12)}</span>`;
      tile.classList.toggle('rescan', !!it.rescan);
      const img = tile.querySelector('img');
      img.dataset.src = thumbUrl(it);
      img.onload = () => { img.classList.add('ok'); noteAspect(img.naturalWidth / img.naturalHeight); };
      img.onerror = () => img.parentElement.classList.add('err');
      thumbObserver.observe(img);
    }
    frag.append(tile);
  });
  grid.append(frag);
  paintSelection();
  updateCount();
}

function paintSelection() {
  pushPopup();
  $$('#grid .tile').forEach((el) => {
    const i = Number(el.dataset.i);
    el.classList.toggle('sel', state.selected.has(i));
    el.classList.toggle('focus', i === state.focus);
  });
  updateCount();
  selectionChanged();
}

function updateCount() {
  const nImg = state.images.length, nDir = state.folders.length;
  const parts = [];
  if (nDir) parts.push(tn(nDir, '{n} folder', '{n} folders'));
  parts.push(tn(nImg, '{n} image', '{n} images'));
  const sel = state.selected.size;
  if (sel) parts.push(tn(sel, '{n} selected', '{n} selected'));
  $('#count').textContent = state.path ? parts.join(' · ') : '';
}

const selectedImages = () => [...state.selected].map((i) => state.items[i]).filter((it) => it?.type === 'image');
const selectedFolders = () => [...state.selected].map((i) => state.items[i]).filter((it) => it?.type === 'folder');
const focusItem = () => state.items[state.focus];

function selectIndex(i, { ctrl = false, shift = false } = {}) {
  if (i < 0 || i >= state.items.length) return;
  if (shift && state.anchor >= 0) {
    const [a, b] = [Math.min(state.anchor, i), Math.max(state.anchor, i)];
    if (!ctrl) state.selected.clear();
    for (let k = a; k <= b; k++) state.selected.add(k);
  } else if (ctrl) {
    if (state.selected.has(i)) state.selected.delete(i); else state.selected.add(i);
    state.anchor = i;
  } else {
    state.selected = new Set([i]);
    state.anchor = i;
  }
  state.focus = i;
  paintSelection();
  $(`#grid .tile[data-i="${i}"]`)?.scrollIntoView({ block: 'nearest' });
  refreshInspector();
}

function activate(i) {
  const it = state.items[i];
  if (!it) return;
  if (it.type === 'folder') openFolder(it.path);
  else openViewer(state.images.indexOf(it));
}

function columns() {
  const tiles = $$('#grid .tile');
  if (tiles.length < 2) return 1;
  const top = tiles[0].offsetTop;
  const n = tiles.findIndex((el) => el.offsetTop !== top);
  return n < 0 ? tiles.length : n;
}

$('#grid').addEventListener('click', (e) => {
  const tile = e.target.closest('.tile');
  if (!tile) return;
  selectIndex(Number(tile.dataset.i), { ctrl: e.ctrlKey || e.metaKey, shift: e.shiftKey });
  $('#grid-wrap').focus({ preventScroll: true });
});
$('#grid').addEventListener('dblclick', (e) => {
  const tile = e.target.closest('.tile');
  if (tile) activate(Number(tile.dataset.i));
});
$('#grid-wrap').addEventListener('click', (e) => {
  if (e.target === $('#grid-wrap') || e.target === $('#grid')) { state.selected.clear(); paintSelection(); }
});

// ------------------------------------------------------------------ viewer

let viewer;
let viewToken = 0;

function viewUrl(it, { full = false, edit = true } = {}) {
  const r = $('#stage').getBoundingClientRect();
  const max = full ? 0 : Math.ceil(Math.max(r.width, r.height, 800) * (window.devicePixelRatio || 1));
  let e = '';
  if (edit && !state.before && tool !== 'slice') {
    const eff = effective(it);
    e = editJson(tool === 'crop' ? { ...eff, cropLeft: 0, cropTop: 0, cropRight: 0, cropBottom: 0 } : eff);
  }
  return `${IMG}/view?p=${encodeURIComponent(it.path)}&max=${max}&v=${it.modified}${e ? `&e=${encodeURIComponent(e)}` : ''}`;
}

function openViewer(index) {
  if (index < 0 || index >= state.images.length) return;
  state.viewer = true;
  $('#viewer').hidden = false;
  showImage(index);
}

function closeViewer() {
  if (tool) { tool = null; overlay.setMode(null); $('#crop-tool').setAttribute('aria-pressed', 'false'); $('#slice-tool').setAttribute('aria-pressed', 'false'); }
  state.viewer = false;
  $('#viewer').hidden = true;
  const it = state.images[state.vIndex];
  if (it) {
    const i = state.items.indexOf(it);
    state.selected = new Set([i]); state.focus = i; state.anchor = i;
    paintSelection();
    $(`#grid .tile[data-i="${i}"]`)?.scrollIntoView({ block: 'nearest' });
  }
  $('#grid-wrap').focus({ preventScroll: true });
}

let wantFull = false;

async function showImage(index, { keepView = false } = {}) {
  const it = state.images[index];
  if (!it) return;
  const changed = index !== state.vIndex;
  state.vIndex = index;
  if (changed) wantFull = false;
  const i = state.items.indexOf(it);
  if (changed) { state.focus = i; state.selected = new Set([i]); state.anchor = i; refreshInspector(); }
  $('#v-pos').textContent = `${index + 1} / ${state.images.length}`;
  $('#v-name').textContent = it.name;
  paintViewerFlag();
  pushPopup();
  const token = ++viewToken;
  const img = $('#v-img');
  const url = viewUrl(it, { full: wantFull });
  $('#v-loading').hidden = false;
  const probe = new Image();
  probe.onload = async () => {
    if (token !== viewToken) return;
    img.src = url;
    $('#v-loading').hidden = true;
    let info = state.info?.path === it.path ? state.info : null;
    if (!info) { try { info = await call('img.info', { path: it.path }); } catch { info = null; } }
    if (token !== viewToken) return;
    const long = info ? fileLongSide(info, it) : Math.max(probe.naturalWidth, probe.naturalHeight);
    const k = long / Math.max(probe.naturalWidth, probe.naturalHeight);
    viewer.setFileSize(Math.round(probe.naturalWidth * k), Math.round(probe.naturalHeight * k), keepView && !changed);
    prefetch(index);
  };
  probe.onerror = () => { if (token === viewToken) { $('#v-loading').hidden = true; toast(t('Could not open {name}', { name: it.name }), 'error'); } };
  probe.src = url;
}

/** Long side of the image as shown (after orientation and, outside the crop tool, after the crop). */
function fileLongSide(info, it) {
  const e = state.before || tool === 'slice' ? NEUTRAL : effective(it);
  let w = info.width, h = info.height;
  if (e.rotation === 90 || e.rotation === 270) [w, h] = [h, w];
  if (tool !== 'crop' && hasCrop(e)) { w *= 1 - e.cropLeft - e.cropRight; h *= 1 - e.cropTop - e.cropBottom; }
  return Math.max(w, h);
}

// ---- crop and slice tools in the viewer
let overlay = null;
let tool = null;             // 'crop' | 'slice' | null
const sliceCuts = { list: [0.5] };

function sliceSettings() {
  const parts = Math.max(2, Math.min(20, Math.round(Number($('#slice-parts').value) || 2)));
  if (sliceCuts.list.length !== parts - 1) sliceCuts.list = Array.from({ length: parts - 1 }, (_, i) => (i + 1) / parts);
  return { parts, vertical: $('#slice-dir').value === 'v', cuts: sliceCuts.list, reverse: $('#slice-reverse').checked };
}

function setTool(which) {
  if (which && !state.viewer) {
    const it = focusItem();
    if (it?.type !== 'image') { toast(t('Open an image first.')); return; }
    openViewer(state.images.indexOf(it));
  }
  tool = tool === which ? null : which;
  $('#crop-tool').setAttribute('aria-pressed', String(tool === 'crop'));
  $('#slice-tool').setAttribute('aria-pressed', String(tool === 'slice'));
  if (tool === 'slice') { const s = sliceSettings(); overlay.setCuts(s.cuts, s.vertical, s.reverse); }
  overlay.setMode(tool);
  if (state.viewer) showImage(state.vIndex, { keepView: false });
}

function paintSlice() {
  const s = sliceSettings();
  overlay?.setCuts(s.cuts, s.vertical, s.reverse);
}

function prefetch(index) {
  for (const k of [index + 1, index - 1, index + 2]) {
    const it = state.images[k];
    if (it) { const p = new Image(); p.src = viewUrl(it); }
  }
}

function step(d) {
  if (!state.viewer) return;
  const n = state.vIndex + d;
  if (n >= 0 && n < state.images.length) showImage(n);
}

const refreshView = debounce(() => { if (state.viewer) showImage(state.vIndex, { keepView: true }); }, 120);

function onViewChange(v) {
  overlay?.layout();
  $('#v-zoom').textContent = `${Math.round(v.scale * 100)}%`;
  if (v.needFull && !wantFull) { wantFull = true; refreshView(); }
}

// ------------------------------------------------------------------ correction (edit) panel
//
// As in Striplight PRO: a GLOBAL correction for every image in the folder, and OWN corrections
// for single images, locked against later global changes. Everything is a preview (thumbnails
// and viewer) until "Save changes" writes it into the files; the originals are kept.

const QUANTUM = { contrast: [-128, 255, 64], brightness: [-128, 127, 0], gamma: [-255, 255, 0] };
const STRIPLIGHT = { contrast: [-100, 100, 0], brightness: [-100, 100, 0], gamma: [10, 300, 100] };
const isQuantum = (m) => m === 1 || m === 4;
const isAdaptive = (m) => m >= 5 && m <= 8;
const NEUTRAL = Object.freeze({ method: 0, flags: 0, contrast: 0, brightness: 0, gamma: 100, strength: 50, sharpenMode: 1, sharpen: 0, negative: false, rotation: 0, mirror: false, cropLeft: 0, cropTop: 0, cropRight: 0, cropBottom: 0 });
const orient = { rotation: 0, mirror: false, negative: false };
state.global = { ...NEUTRAL };
state.own = new Map();        // path → own correction (not saved yet)
let loadingControls = false;

const slider = (key) => $(`.slider[data-key="${key}"]`);
const sliderInputs = (el) => [el.querySelector('input[type=range]'), el.querySelector('input[type=number]')];
function setSlider(key, value, range) {
  const [r, n] = sliderInputs(slider(key));
  if (range) for (const i of [r, n]) { i.min = range[0]; i.max = range[1]; }
  r.value = n.value = value;
  paintRange(r);
}
const getSlider = (key) => Number(sliderInputs(slider(key))[1].value) || 0;

function applyRanges(method, reset) {
  const ranges = isQuantum(method) ? QUANTUM : STRIPLIGHT;
  for (const k of ['contrast', 'brightness', 'gamma']) setSlider(k, reset ? ranges[k][2] : getSlider(k), ranges[k]);
  slider('strength').hidden = !isAdaptive(method);
  $('#tone-hint').textContent = isAdaptive(method)
    ? t('Adaptive methods analyse each image. The sliders apply after the adaptive step.')
    : isQuantum(method) ? t('Quantum ranges: contrast 64 and gamma 0 are neutral.') : '';
}

function gatherEdit() {
  return {
    method: Number($('#tone-method').value),
    flags: $$('#tone-flags input').reduce((a, c) => a | (c.checked ? Number(c.value) : 0), 0),
    contrast: getSlider('contrast'),
    brightness: getSlider('brightness'),
    gamma: getSlider('gamma'),
    strength: getSlider('strength'),
    sharpenMode: Number($('#sharp-mode').value),
    sharpen: getSlider('sharpen'),
    negative: orient.negative,
    rotation: orient.rotation,
    mirror: orient.mirror,
    cropLeft: cropInput('l'),
    cropTop: cropInput('t'),
    cropRight: cropInput('r'),
    cropBottom: cropInput('b'),
  };
}

const cropInput = (k) => Math.max(0, Math.min(95, Number($(`#crop-${k}`).value) || 0)) / 100;
const hasCrop = (e) => e.cropLeft > 0.0005 || e.cropTop > 0.0005 || e.cropRight > 0.0005 || e.cropBottom > 0.0005;
function setCropInputs(e) {
  for (const [k, f] of [['l', 'cropLeft'], ['t', 'cropTop'], ['r', 'cropRight'], ['b', 'cropBottom']])
    $(`#crop-${k}`).value = +((e[f] || 0) * 100).toFixed(2);
}

/** Shows a correction in the panel (without treating it as a change). */
function loadControls(e) {
  loadingControls = true;
  $('#tone-method').value = e.method;
  applyRanges(e.method, false);
  setSlider('contrast', e.contrast);
  setSlider('brightness', e.brightness);
  setSlider('gamma', e.gamma);
  setSlider('strength', e.strength);
  setSlider('sharpen', e.sharpen);
  $('#sharp-mode').value = e.sharpenMode;
  $$('#tone-flags input').forEach((c) => { c.checked = (e.flags & Number(c.value)) !== 0; });
  Object.assign(orient, { rotation: e.rotation, mirror: e.mirror, negative: e.negative });
  setCropInputs(e);
  overlay?.setCrop({ l: e.cropLeft || 0, t: e.cropTop || 0, r: e.cropRight || 0, b: e.cropBottom || 0 });
  paintOrient();
  loadingControls = false;
}

// Same rule as ToneAdjust.IsNoOp in the engine.
function toneIsNoOp(e) {
  if (e.flags !== 0) return false;
  if (isQuantum(e.method)) return e.contrast === 64 && e.brightness === 0 && e.gamma === 0;
  if (e.method === 2 || e.method === 3 || e.method === 5) return false;
  if (e.method >= 6) return e.strength <= 0 && e.contrast === 0 && e.brightness === 0 && e.gamma === 100;
  return e.contrast === 0 && e.brightness === 0 && e.gamma === 100;
}
const isNeutral = (e) => toneIsNoOp(e) && e.sharpen <= 0 && !e.negative && e.rotation === 0 && !e.mirror && !hasCrop(e);
const effective = (it) => state.own.get(it.path) ?? state.global;
const editJson = (e) => (e && !isNeutral(e) ? JSON.stringify(e) : '');

function paintOrient() {
  $('#mirror-btn').setAttribute('aria-pressed', String(orient.mirror));
  $('#negative-btn').setAttribute('aria-pressed', String(orient.negative));
  const bits = [];
  if (orient.rotation) bits.push(`${orient.rotation}°`);
  if (orient.mirror) bits.push(t('mirrored'));
  if (orient.negative) bits.push(t('negative'));
  $('#orient-state').textContent = bits.join(' · ');
}

/** A control changed: it becomes the global correction, or the own correction of the selected images. */
function controlsChanged() {
  if (loadingControls) return;
  const e = gatherEdit();
  if (targetMode() === 'sel') for (const it of selectedImages()) state.own.set(it.path, { ...e });
  else state.global = e;
  previewsChanged();
}

function previewsChanged() {
  paintQuickbar();
  pushPopup();
  refreshThumbs();
  refreshView();
  refreshTarget();
}

function rotate(d) { orient.rotation = (orient.rotation + d + 360) % 360; paintOrient(); controlsChanged(); }

function targetMode() { return $('#target').value; }

/** Switches between the global correction and own corrections of the selected images. */
function setTarget(mode) {
  if (mode === 'sel' && !selectedImages().length) mode = 'all';
  $('#target').value = mode;
  loadControls(mode === 'sel' ? (state.own.get(firstSelected()?.path) ?? state.global) : state.global);
  refreshTarget();
}

const firstSelected = () => {
  const f = focusItem();
  return f?.type === 'image' && state.selected.has(state.focus) ? f : selectedImages()[0];
};

/** Selection changed: in "Selected" mode the panel shows the (focused) selected image's correction. */
function selectionChanged() {
  if (targetMode() === 'sel') {
    if (!selectedImages().length) setTarget('all');
    else loadControls(state.own.get(firstSelected()?.path) ?? state.global);
  }
  refreshTarget();
}

/** Files that "Save changes" writes: own corrections, and the global one for every other image. */
function pendingItems() {
  const items = [];
  for (const it of state.images) {
    const e = effective(it);
    if (!isNeutral(e)) items.push({ path: it.path, edit: e });
  }
  return items;
}

function refreshTarget() {
  const n = selectedImages().length, sel = targetMode() === 'sel';
  $('#target-sel').textContent = n ? `${t('Selected')} (${n.toLocaleString()})` : t('Selected');
  $('#target-sel').disabled = !n;
  $('#target-hint').textContent = sel
    ? t('Changes give the selected images their own correction (orange); later global changes leave them as they are.')
    : t('Changes apply to every image in this folder; the thumbnails show the preview. Images with their own correction keep it.');
  $('[data-panel]')?.classList.toggle('own', sel);
  const items = pendingItems();
  const own = state.images.filter((it) => state.own.has(it.path) && !isNeutral(state.own.get(it.path))).length;
  const parts = [];
  if (!isNeutral(state.global)) parts.push(tn(state.images.length - state.images.filter((it) => state.own.has(it.path)).length, 'global correction on {n} image', 'global correction on {n} images'));
  if (own) parts.push(tn(own, '{n} own correction', '{n} own corrections'));
  $('#pending').textContent = parts.length ? `${t('Not saved yet')}: ${parts.join(' · ')}` : '';
  $('#apply-label').textContent = items.length > 1 ? tn(items.length, 'Save changes ({n} file)', 'Save changes ({n} files)') : t('Save changes');
  $('#apply-btn').disabled = !items.length || state.job;
  $('#discard-btn').hidden = !items.length && !state.own.size;
  $('#reset-label').textContent = sel ? t('Use global') : t('Reset');
  $('#reset-btn').title = sel ? t('The selected images follow the global correction again') : t('Back to neutral (no correction)');
  const edited = (sel ? selectedImages() : [focusItem()]).filter((it) => it?.edited).length;
  $('#restore-btn').disabled = !edited || state.job;
  $('#restore-label').textContent = edited > 1 ? tn(edited, 'Restore {n} original', 'Restore {n} originals') : t('Restore original');
  for (const tile of $$('#grid .tile.image')) {
    const it = state.items[Number(tile.dataset.i)];
    tile.classList.toggle('own', !!it && state.own.has(it.path));
    tile.classList.toggle('cropped', !!it && hasCrop(effective(it)));
  }
}

function resetCorrection() {
  if (targetMode() === 'sel') {
    for (const it of selectedImages()) state.own.delete(it.path);
    loadControls(state.global);
  } else {
    state.global = { ...NEUTRAL };
    loadControls(state.global);
  }
  previewsChanged();
}

function discardChanges() {
  state.own.clear();
  state.global = { ...NEUTRAL };
  loadControls(state.global);
  previewsChanged();
}

const pendingCount = () => pendingItems().length;

async function saveChanges() {
  const items = pendingItems();
  if (!items.length || state.job) return;
  if (items.length > 1 && !confirm(tn(items.length, 'Save the corrections into {n} file? The originals are kept.', 'Save the corrections into {n} files? The originals are kept.'))) return;
  const r = await run('edit.applyMany', { items });
  reportJob(r, 'Saved {n} file', 'Saved {n} files');
  const failed = new Set(r.failures.map((f) => f.path));
  for (const it of items) if (!failed.has(it.path)) state.own.delete(it.path);
  if (!failed.size) state.global = { ...NEUTRAL };
  loadControls(targetMode() === 'sel' ? (state.own.get(firstSelected()?.path) ?? state.global) : state.global);
  await reloadAfterChange();
}

async function restoreOriginals() {
  const paths = (targetMode() === 'sel' ? selectedImages() : [focusItem()]).filter((it) => it?.edited).map((it) => it.path);
  if (!paths.length) return;
  if (paths.length > 1 && !confirm(tn(paths.length, 'Put back the original of {n} image?', 'Put back the originals of {n} images?'))) return;
  const r = await run('edit.restore', { paths });
  reportJob(r, 'Restored {n} original', 'Restored {n} originals');
  await reloadAfterChange();
}

function reportJob(r, one, other) {
  const ok = r.done - r.failures.length;
  if (r.failures.length) {
    toast(tn(r.failures.length, '{n} file failed: ', '{n} files failed: ') + r.failures.slice(0, 3).map((f) => `${baseName(f.path)} (${f.error})`).join('; '), 'error');
  }
  if (ok > 0) toast(tn(ok, one, other) + (r.cancelled ? ` · ${t('stopped')}` : ''), 'ok');
}

async function reloadAfterChange() {
  const vName = state.viewer ? state.images[state.vIndex]?.name : null;
  state.info = null;
  await openFolder(state.path, { keepSelection: true, focusName: vName });
  if (state.viewer && vName) {
    const k = state.images.findIndex((it) => it.name === vName);
    if (k >= 0) { state.vIndex = -1; showImage(k, { keepView: true }); }
  }
}

// ------------------------------------------------------------------ info card

const refreshInspector = debounce(async () => {
  refreshTarget();
  const it = state.viewer ? state.images[state.vIndex] : focusItem();
  const card = $('#info');
  if (!it || it.type !== 'image') { card.hidden = true; state.info = null; return; }
  let info;
  try { info = await call('img.info', { path: it.path }); } catch { card.hidden = true; return; }
  state.info = info;
  const dpi = info.dpiX ? (info.dpiX === info.dpiY ? `${info.dpiX} dpi` : `${info.dpiX} × ${info.dpiY} dpi`) : '—';
  card.innerHTML = `<h4 title="${esc(info.path)}">${esc(info.name)}</h4>
    <dt>${esc(t('Size'))}</dt><dd>${info.width} × ${info.height} px</dd>
    <dt>${esc(t('Colour'))}</dt><dd>${esc(info.isGray ? t('grayscale') : t('colour'))}</dd>
    <dt>${esc(t('Quality'))}</dt><dd>${info.quality ? `≈ ${info.quality}` : '—'}</dd>
    <dt>${esc(t('Resolution'))}</dt><dd>${dpi}</dd>
    <dt>${esc(t('File'))}</dt><dd>${fmtBytes(info.size)}</dd>
    <dt>${esc(t('Modified'))}</dt><dd>${esc(info.modifiedText)}</dd>
    ${info.edited ? `<span class="edited">${esc(t('Edited — the original is kept'))}</span>` : ''}`;
  card.hidden = false;
}, 90);

// ------------------------------------------------------------------ rename dialog

let renamePaths = [];
let renamePlan = null;

function renameRules() {
  return {
    pattern: $('#rn-pattern').value,
    find: $('#rn-find').value,
    replace: $('#rn-replace').value,
    regex: $('#rn-regex').checked,
    matchCase: $('#rn-case-sens').checked,
    case: $('#rn-case').value,
    extCase: $('#rn-ext').value,
    start: Number($('#rn-start').value) || 0,
    step: Number($('#rn-step').value) || 1,
    digits: Math.max(0, Math.min(12, Number($('#rn-digits').value) || 0)),
    counterPerFolder: $('#rn-perfolder').checked,
  };
}

function loadRenameRules(r) {
  $('#rn-pattern').value = r.pattern ?? '{name}';
  $('#rn-find').value = r.find ?? '';
  $('#rn-replace').value = r.replace ?? '';
  $('#rn-regex').checked = !!r.regex;
  $('#rn-case-sens').checked = !!r.matchCase;
  $('#rn-case').value = r.case ?? 'keep';
  $('#rn-ext').value = r.extCase ?? 'lower';
  $('#rn-start').value = r.start ?? 1;
  $('#rn-step').value = r.step ?? 1;
  $('#rn-digits').value = r.digits ?? 5;
  $('#rn-perfolder').checked = r.counterPerFolder ?? true;
}

function openRename() {
  const sel = selectedImages();
  renamePaths = (sel.length ? sel : state.images).map((it) => it.path);
  if (!renamePaths.length) { toast(t('There are no images to rename here.')); return; }
  $('#rn-scope').textContent = sel.length ? tn(sel.length, '{n} selected image', '{n} selected images') : tn(renamePaths.length, 'all {n} image in this folder', 'all {n} images in this folder');
  loadRenameRules(state.settings.renameRules || {});
  $('#rename-dlg').showModal();
  planRename();
  $('#rn-pattern').focus();
}

const planRename = debounce(async () => {
  let plan;
  try { plan = await call('rename.plan', { paths: renamePaths, rules: renameRules() }); }
  catch (e) { $('#rn-summary').innerHTML = `<span class="bad">${esc(e.message)}</span>`; return; }
  renamePlan = plan;
  const sum = $('#rn-summary');
  if (plan.error) sum.innerHTML = `<span class="bad">${esc(t(plan.error))}</span>`;
  else {
    sum.innerHTML = `<b>${esc(tn(plan.changes, '{n} file will be renamed', '{n} files will be renamed'))}</b>` +
      (plan.problems ? ` · <span class="bad">${esc(tn(plan.problems, '{n} problem', '{n} problems'))}</span>` : '') +
      (plan.items.length - plan.changes - plan.problems ? ` · ${esc(tn(plan.items.length - plan.changes - plan.problems, '{n} unchanged', '{n} unchanged'))}` : '');
  }
  const tb = $('#rn-table');
  tb.tHead.innerHTML = `<tr><th>${esc(t('Current name'))}</th><th></th><th>${esc(t('New name'))}</th><th></th></tr>`;
  const rows = plan.items.slice(0, 3000).map((i) => {
    const bad = ['invalid', 'conflict', 'exists'].includes(i.status);
    return `<tr class="${bad ? 'bad' : i.status}"><td title="${esc(i.from)}">${esc(baseName(i.from))}</td><td class="arrow">→</td>
      <td class="to">${esc(i.newName)}</td><td class="why">${bad ? esc(t(i.message || '')) : ''}</td></tr>`;
  });
  if (plan.items.length > 3000) rows.push(`<tr class="same"><td colspan="4">… ${esc(tn(plan.items.length - 3000, '{n} more', '{n} more'))}</td></tr>`);
  tb.tBodies[0].innerHTML = rows.join('');
  $('#rn-run').disabled = !(plan.error == null && plan.problems === 0 && plan.changes > 0);
}, 140);

$$('#rename-dlg input, #rename-dlg select').forEach((el) => el.addEventListener('input', planRename));
$$('#rename-dlg [data-token]').forEach((b) => b.addEventListener('click', () => {
  const inp = $('#rn-pattern');
  const s = inp.selectionStart ?? inp.value.length, e = inp.selectionEnd ?? s;
  inp.value = inp.value.slice(0, s) + b.dataset.token + inp.value.slice(e);
  inp.focus();
  inp.setSelectionRange(s + b.dataset.token.length, s + b.dataset.token.length);
  planRename();
}));
$('#rename-dlg [data-preset="archive"]').addEventListener('click', () => {
  $('#rn-pattern').value = '{folder}_{n}'; $('#rn-digits').value = 5; $('#rn-start').value = 1; $('#rn-step').value = 1;
  $('#rn-perfolder').checked = true; planRename();
});
$('#rn-run').addEventListener('click', async () => {
  $('#rn-run').disabled = true;
  try {
    const r = await run('rename.run', { paths: renamePaths, rules: renameRules() });
    state.settings.renameRules = renameRules();
    $('#rename-dlg').close();
    toast(tn(r.count, 'Renamed {n} file', 'Renamed {n} files'), 'ok');
    setUndo(r.undo);
    const renamed = new Map(r.renamed.map((m) => [m.from, m.to]));
    const selNames = new Set(selectedImages().map((it) => baseName(renamed.get(it.path) ?? it.path)));
    await openFolder(state.path);
    state.items.forEach((it, i) => { if (selNames.has(it.name)) state.selected.add(i); });
    paintSelection();
  } catch { planRename(); }
});

// ------------------------------------------------------------------ merge dialog

let mergeFolders = [];

function openMerge() {
  let folders = selectedFolders().map((f) => f.path);
  if (folders.length < 2) {
    // Nothing selected: if this folder holds the parts of exactly one signature, take them.
    const groups = new Map();
    for (const f of state.folders) {
      const m = /^(.+)_\d+$/.exec(f.name);
      if (m) groups.set(m[1].toLowerCase(), [...(groups.get(m[1].toLowerCase()) || []), f.path]);
    }
    const multi = [...groups.values()].filter((g) => g.length >= 2);
    if (multi.length === 1) folders = multi[0];
  }
  if (folders.length < 2) {
    toast(t('Select the part folders to merge (Ctrl+click), for example 123_1 and 123_2.'));
    return;
  }
  mergeFolders = folders;
  $('#mg-delete').checked = state.settings.deleteEmptyParts ?? true;
  $('#mg-confirm').checked = state.settings.confirmDeleteParts ?? true;
  $('#merge-dlg').showModal();
  planMerge();
}

async function planMerge() {
  let p;
  try { p = await call('merge.plan', { folders: mergeFolders }); } catch (e) { toast(e.message, 'error'); return; }
  const flow = $('#mg-flow');
  if (p.error) {
    flow.innerHTML = '';
    $('#mg-problems').innerHTML = `<span>${esc(t(p.error))}</span>`;
    $('#mg-table').tHead.innerHTML = ''; $('#mg-table').tBodies[0].innerHTML = '';
    $('#mg-run').disabled = true;
    return;
  }
  flow.innerHTML = p.parts.map((x) => `<div class="part"><b>${esc(x.name)}</b><small>${esc(tn(x.files, '{n} file', '{n} files'))}</small></div>`)
    .join('<span class="plus">+</span>') +
    `<span class="to">→</span><div class="target"><b>${esc(p.baseName)}</b><small>${esc(tn(p.count, '{n} file', '{n} files'))} · ${esc(t('new folder'))}</small></div>`;
  $('#mg-problems').innerHTML = p.problems.map((x) => `<span>${esc(x)}</span>`).join('');
  $('#mg-table').tHead.innerHTML = `<tr><th>${esc(t('Part'))}</th><th>${esc(t('Current name'))}</th><th></th><th>${esc(t('New name'))}</th></tr>`;
  $('#mg-table').tBodies[0].innerHTML = p.sample.map((r) => `<tr class="ok"><td>${esc(r.folder)}</td><td>${esc(r.from)}</td><td class="arrow">→</td><td class="to">${esc(r.to)}</td></tr>`).join('');
  $('#mg-run').disabled = !p.canRun;
}

$('#mg-delete').addEventListener('change', (e) => saveSettings({ deleteEmptyParts: e.target.checked }));
$('#mg-confirm').addEventListener('change', (e) => saveSettings({ confirmDeleteParts: e.target.checked }));
$('#mg-run').addEventListener('click', async () => {
  let deleteEmpty = $('#mg-delete').checked;
  if (deleteEmpty && $('#mg-confirm').checked &&
      !confirm(t('Delete the part folders once they are empty?\n\n{names}', { names: mergeFolders.map(baseName).join(', ') }))) deleteEmpty = false;
  $('#mg-run').disabled = true;
  try {
    const r = await run('merge.run', { folders: mergeFolders, deleteEmpty });
    $('#merge-dlg').close();
    toast(t('Merged into {name}: {count} files', { name: baseName(r.target), count: r.count }), 'ok');
    setUndo(r.undo);
    await openFolder(state.path, { focusName: baseName(r.target) });
    const k = state.items.findIndex((it) => it.name === baseName(r.target));
    if (k >= 0) selectIndex(k);
  } catch { planMerge(); }
});

// ------------------------------------------------------------------ slice dialog

let slicePaths = [];

async function openSlice() {
  const its = state.viewer ? [state.images[state.vIndex]] : selectedImages();
  slicePaths = its.filter(Boolean).map((it) => it.path);
  if (!slicePaths.length) { toast(t('Select the images to slice.')); return; }
  const pend = pendingItems().filter((p) => slicePaths.includes(p.path));
  if (pend.length) {
    if (!confirm(t('These images have corrections that are not saved yet. Save them first?'))) return;
    await saveChanges();
  }
  $('#slice-dlg').showModal();
  let p;
  try { p = await call('slice.plan', { paths: slicePaths, settings: sliceSettings() }); } catch (e) { toast(e.message, 'error'); return; }
  const flow = $('#sl-flow');
  if (p.error) {
    flow.innerHTML = '';
    $('#sl-problems').innerHTML = `<span>${esc(t(p.error))}</span>`;
    $('#sl-table').tHead.innerHTML = ''; $('#sl-table').tBodies[0].innerHTML = '';
    $('#sl-run').disabled = true;
    return;
  }
  flow.innerHTML = `<div class="part"><b>${p.sources}</b><small>${esc(tn(p.sources, 'image to cut', 'images to cut'))}</small></div><span class="to">→</span>
    <div class="target"><b>${p.outputs}</b><small>${esc(tn(p.outputs, 'new file', 'new files'))}</small></div>
    <div class="part"><b>${p.renames}</b><small>${esc(tn(p.renames, 'file renumbered', 'files renumbered'))}</small></div>
    <div class="part"><b>${p.files} → ${p.after}</b><small>${esc(t('files in the folder'))}</small></div>`;
  $('#sl-problems').innerHTML = p.problems.map((x) => `<span>${esc(x)}</span>`).join('');
  $('#sl-table').tHead.innerHTML = `<tr><th>${esc(t('Current name'))}</th><th></th><th>${esc(t('New name'))}</th></tr>`;
  $('#sl-table').tBodies[0].innerHTML = p.sample.map((r) => `<tr class="${r.piece ? 'ok' : 'same'}"><td>${esc(r.from)}</td><td class="arrow">→</td><td class="to">${esc(r.to)}</td></tr>`).join('');
  $('#sl-run').disabled = !p.canRun;
}

$('#sl-run').addEventListener('click', async () => {
  $('#sl-run').disabled = true;
  try {
    const r = await run('slice.run', { paths: slicePaths, settings: sliceSettings() });
    $('#slice-dlg').close();
    toast(tn(r.outputs, 'Sliced into {n} file', 'Sliced into {n} files'), 'ok');
    setUndo(r.undo);
    if (tool === 'slice') setTool('slice');
    await reloadAfterChange();
  } catch { /* the toast says why */ }
});

// ------------------------------------------------------------------ rescan list

function setRescanCount(n) {
  $('#rescan-count').textContent = n;
  $('#rescan-count').hidden = !n;
}

/** Marks the selected images (or the one in the viewer) for rescanning; if all are marked already, unmarks them. */
async function toggleRescan() {
  const its = state.viewer ? [state.images[state.vIndex]] : (selectedImages().length ? selectedImages() : [focusItem()]);
  const list = its.filter((it) => it?.type === 'image');
  if (!list.length) return;
  const on = !list.every((it) => it.rescan);
  const r = await run('rescan.set', { paths: list.map((it) => it.path), on });
  for (const it of list) it.rescan = on;
  for (const tile of $$('#grid .tile.image')) tile.classList.toggle('rescan', !!state.items[Number(tile.dataset.i)]?.rescan);
  setRescanCount(r.entries.length);
  paintViewerFlag();
  pushPopup();
  toast(on ? tn(list.length, 'Marked {n} image for rescanning', 'Marked {n} images for rescanning') : tn(list.length, 'Unmarked {n} image', 'Unmarked {n} images'), 'ok');
}

function paintViewerFlag() {
  const it = state.viewer ? state.images[state.vIndex] : null;
  $('#v-flag').hidden = !it?.rescan;
  $('#v-flag-btn').setAttribute('aria-pressed', String(!!it?.rescan));
}

async function openRescan() {
  renderRescan(await run('rescan.list'));
  $('#rescan-dlg').showModal();
}

function renderRescan(r) {
  setRescanCount(r.entries.length);
  $('#rs-headers').checked = !!r.headers;
  $('#rs-count').textContent = tn(r.entries.length, '{n} image', '{n} images');
  $('#rs-empty').hidden = r.entries.length > 0;
  const tb = $('#rs-table');
  tb.tHead.innerHTML = r.entries.length ? `<tr><th>${esc(t('Microfilm number'))}</th><th>${esc(t('Folder'))}</th><th>${esc(t('File'))}</th><th>${esc(t('Reason'))}</th><th>${esc(t('Marked'))}</th><th></th></tr>` : '';
  tb.tBodies[0].innerHTML = r.entries.map((e, i) => `<tr class="${e.exists ? '' : 'gone'}" data-i="${i}">
      <td class="film"><input type="text" data-k="microfilm" value="${esc(e.microfilm)}" placeholder="${esc(t('e.g. 123-213'))}" spellcheck="false"></td>
      <td class="mono" title="${esc(e.path)}">${esc(e.folder)}</td>
      <td class="go" title="${esc(e.exists ? t('Show this image') : t('The file is no longer there'))}">${esc(e.file)}</td>
      <td class="why"><input type="text" data-k="reason" value="${esc(e.reason)}" list="rs-reasons" placeholder="${esc(t('Why?'))}"></td>
      <td class="mono">${esc(e.marked)}</td>
      <td><button class="icon-btn" data-remove title="${esc(t('Remove from the list'))}">${icon('x', 14)}</button></td></tr>`).join('');
  tb.dataset.paths = JSON.stringify(r.entries.map((e) => e.path));
  // Translate the reason suggestions once.
  $$('#rs-reasons option').forEach((o) => { if (!o.dataset.done) { o.value = t(o.value); o.dataset.done = '1'; } });
}

$('#rs-table').addEventListener('change', async (e) => {
  const input = e.target.closest('input[data-k]');
  if (!input) return;
  const path = JSON.parse($('#rs-table').dataset.paths)[Number(input.closest('tr').dataset.i)];
  renderRescan(await run('rescan.update', { path, [input.dataset.k]: input.value }));
});
$('#rs-table').addEventListener('click', async (e) => {
  const tr = e.target.closest('tr[data-i]');
  if (!tr) return;
  const path = JSON.parse($('#rs-table').dataset.paths)[Number(tr.dataset.i)];
  if (e.target.closest('[data-remove]')) {
    renderRescan(await run('rescan.set', { paths: [path], on: false }));
    const it = state.images.find((x) => x.path === path);
    if (it) { it.rescan = false; renderGrid(); }
  } else if (e.target.closest('td.go') && !tr.classList.contains('gone')) {
    $('#rescan-dlg').close();
    const sep = sepOf(path);
    await openFolder(path.slice(0, path.lastIndexOf(sep)) || path, { focusName: baseName(path) });
    const k = state.items.findIndex((it) => it.path === path);
    if (k >= 0) selectIndex(k);
  }
});
$('#rs-headers').addEventListener('change', (e) => saveSettings({ rescanHeaders: e.target.checked }));

// ------------------------------------------------------------------ undo

function setUndo(title) {
  const b = $('#undo-btn');
  b.disabled = !title;
  b.title = title ? t('Undo: {what}', { what: title }) : t('Undo the last rename or merge');
}

async function undo() {
  if ($('#undo-btn').disabled) return;
  if (!confirm(t('Undo: {what}?', { what: $('#undo-btn').title.replace(/^.*?: /, '') }))) return;
  const r = await run('undo.run');
  toast(t('Undone: {what}', { what: r.title }), 'ok');
  setUndo(r.next);
  await openFolder(state.path);
}

// ------------------------------------------------------------------ quick toolbar (customizable)

/** Every tool the quick toolbar can show. Settings → Toolbar picks which, and the order. */
const TOOLS = {
  rotL: { icon: 'rotate-ccw', label: 'Rotate left', run: () => rotate(-90) },
  rotR: { icon: 'rotate-cw', label: 'Rotate right', run: () => rotate(90) },
  mirror: { icon: 'flip-horizontal-2', label: 'Mirror', run: () => commands.mirror(), on: () => orient.mirror },
  negative: { icon: 'contrast', label: 'Negative', run: () => commands.negative(), on: () => orient.negative },
  darker: { icon: 'moon', label: 'Darker', run: () => nudge('brightness', -10) },
  brighter: { icon: 'sun', label: 'Brighter', run: () => nudge('brightness', 10) },
  lessContrast: { icon: 'circle-alert', label: 'Less contrast', run: () => nudge('contrast', -10) },
  moreContrast: { icon: 'circle-check', label: 'More contrast', run: () => nudge('contrast', 10) },
  autoLevels: { icon: 'wand-sparkles', label: 'Auto levels', run: () => { $('#tone-method').value = 5; applyRanges(5, true); controlsChanged(); } },
  crop: { icon: 'crop', label: 'Crop tool (K)', run: () => setTool('crop'), on: () => tool === 'crop' },
  cutLines: { icon: 'scissors', label: 'Cut lines (L)', run: () => setTool('slice'), on: () => tool === 'slice' },
  slice: { icon: 'square-split-horizontal', label: 'Slice…', run: () => openSlice() },
  rescan: { icon: 'flag', label: 'Mark for rescanning (Ctrl+R)', run: () => toggleRescan() },
  before: { icon: 'eye', label: 'Before (B)', run: () => setBefore(!state.before), on: () => state.before },
  resetCorr: { icon: 'rotate-ccw', label: 'Reset correction', run: () => resetCorrection() },
  save: { icon: 'save', label: 'Save changes (Ctrl+S)', run: () => saveChanges() },
  restore: { icon: 'history', label: 'Restore original', run: () => restoreOriginals() },
  undo: { icon: 'undo-2', label: 'Undo rename / merge / slice (Ctrl+Z)', run: () => undo() },
  rename: { icon: 'text-cursor-input', label: 'Rename (F2)', run: () => openRename() },
  merge: { icon: 'merge', label: 'Merge parts', run: () => openMerge() },
  rescanList: { icon: 'clipboard-list', label: 'Rescan list', run: () => openRescan() },
  selectAll: { icon: 'list-checks', label: 'Select all (Ctrl+A)', run: () => commands.selectAll() },
  zoomIn: { icon: 'zoom-in', label: 'Zoom in', run: () => (state.viewer ? viewer.zoomBy(1.25) : zoomGrid(true)) },
  zoomOut: { icon: 'zoom-out', label: 'Zoom out', run: () => (state.viewer ? viewer.zoomBy(1 / 1.25) : zoomGrid(false)) },
  fit: { icon: 'maximize', label: 'Fit (0)', run: () => state.viewer && viewer.fit() },
  refresh: { icon: 'refresh-cw', label: 'Refresh (F5)', run: () => commands.refresh() },
  popup: { icon: 'monitor', label: 'Pop-out viewer (Ctrl+P)', run: () => togglePopup(), on: () => state.popupOpen },
};
const DEFAULT_TOOLBAR = ['rotL', 'rotR', 'mirror', 'negative', '|', 'darker', 'brighter', 'autoLevels', '|', 'crop', 'cutLines', 'slice', '|', 'rescan', 'before', '|', 'save', 'restore', 'undo', '|', 'popup'];

function nudge(key, d) {
  const [r] = sliderInputs(slider(key));
  setSlider(key, Math.max(Number(r.min), Math.min(Number(r.max), getSlider(key) + d)));
  controlsChanged();
}

const toolbarIds = () => (Array.isArray(state.settings.toolbar) && state.settings.toolbar.length ? state.settings.toolbar : DEFAULT_TOOLBAR).filter((id) => id === '|' || TOOLS[id]);

function renderQuickbar() {
  $('#quickbar').innerHTML = toolbarIds().map((id) => (id === '|'
    ? '<span class="qsep"></span>'
    : `<button class="icon-btn" data-tool="${id}" title="${esc(t(TOOLS[id].label))}">${icon(TOOLS[id].icon, 17)}</button>`)).join('');
  paintQuickbar();
}

function paintQuickbar() {
  for (const b of $$('#quickbar [data-tool]')) {
    const tl = TOOLS[b.dataset.tool];
    if (tl.on) b.setAttribute('aria-pressed', String(!!tl.on()));
  }
}

$('#quickbar').addEventListener('click', (e) => {
  const b = e.target.closest('[data-tool]');
  if (!b) return;
  TOOLS[b.dataset.tool].run();
  setTimeout(paintQuickbar, 50);
});

function saveToolbar(list) {
  state.settings.toolbar = list;
  saveSettings({ toolbar: list });
  renderQuickbar();
  renderToolList();
}

/** Settings → Toolbar: every tool with a switch; shown tools can move up and down. */
function renderToolList() {
  const shown = toolbarIds();
  const hidden = Object.keys(TOOLS).filter((id) => !shown.includes(id));
  const arrows = `<button class="icon-btn" data-move="-1" title="${esc(t('Move up'))}">${icon('arrow-up', 13)}</button>
        <button class="icon-btn" data-move="1" title="${esc(t('Move down'))}">${icon('chevron-down', 13)}</button>`;
  const row = (id, i, on) => id === '|'
    ? `<div class="tool-row sep" data-i="${i}"><span class="lbl">— ${esc(t('separator'))} —</span>${arrows}
        <button class="icon-btn" data-del title="${esc(t('Remove'))}">${icon('x', 13)}</button></div>`
    : `<div class="tool-row" data-i="${on ? i : ''}" data-id="${id}">
        <label class="switch"><input type="checkbox" ${on ? 'checked' : ''}><span></span></label>${icon(TOOLS[id].icon, 15)}
        <span class="lbl">${esc(t(TOOLS[id].label))}</span>${on ? arrows : ''}</div>`;
  $('#tool-list').innerHTML = shown.map((id, i) => row(id, i, true)).join('') + hidden.map((id) => row(id, -1, false)).join('');
}

$('#tool-list').addEventListener('click', (e) => {
  const r = e.target.closest('.tool-row');
  if (!r || r.dataset.i === '') return;
  const list = [...toolbarIds()];
  const i = Number(r.dataset.i);
  const mv = e.target.closest('[data-move]');
  if (mv) {
    const j = i + Number(mv.dataset.move);
    if (j < 0 || j >= list.length) return;
    [list[i], list[j]] = [list[j], list[i]];
    saveToolbar(list);
  } else if (e.target.closest('[data-del]')) {
    list.splice(i, 1);
    saveToolbar(list);
  }
});
$('#tool-list').addEventListener('change', (e) => {
  const r = e.target.closest('.tool-row');
  if (!r?.dataset.id) return;
  const list = [...toolbarIds()];
  if (e.target.checked) list.push(r.dataset.id); else list.splice(list.indexOf(r.dataset.id), 1);
  saveToolbar(list);
});

// ------------------------------------------------------------------ pop-out viewer (second monitor)

state.popupOpen = false;

function togglePopup() { call(state.popupOpen ? 'popup.close' : 'popup.open').catch((e) => toast(e.message, 'error')); }

/** The image the pop-out shows: the one in the viewer, else the focused thumbnail. */
const popupItem = () => (state.viewer ? state.images[state.vIndex] : (focusItem()?.type === 'image' ? focusItem() : null));

const pushPopup = debounce(() => {
  if (!state.popupOpen) return;
  const it = popupItem();
  call('popup.update', it ? {
    path: it.path, modified: it.modified, name: it.name, index: state.images.indexOf(it), count: state.images.length,
    edit: editJson(effective(it)), before: state.before, rescan: !!it.rescan,
  } : {}).catch(() => {});
}, 60);

on('popup.wanted', () => pushPopup());
on('popup.state', (s) => {
  state.popupOpen = s.open;
  $('#popup-btn').setAttribute('aria-pressed', String(s.open));
  paintQuickbar();
  if (s.open) { pushPopup(); syncPopupTheme(); }
});
on('popup.action', ({ action }) => {
  const it = popupItem();
  const k = it ? state.images.indexOf(it) : -1;
  const go = (n) => {
    if (n < 0 || n >= state.images.length) return;
    if (state.viewer) showImage(n); else selectIndex(state.items.indexOf(state.images[n]));
  };
  if (action === 'next') go(k + 1);
  else if (action === 'prev') go(k - 1);
  else if (action === 'first') go(0);
  else if (action === 'last') go(state.images.length - 1);
  else if (action === 'before-on') setBefore(true);
  else if (action === 'before-off') setBefore(false);
  else if (action === 'rescan') toggleRescan();
});

// ------------------------------------------------------------------ settings dialog

async function openSettings() {
  const s = state.settings;
  $('#lang').value = currentLanguage();
  $('#set-quality').value = String(s.saveQuality ?? 0);
  $('#set-lossless').checked = s.losslessRotate ?? true;
  $('#set-workers').value = s.workers ?? 4;
  $('#about').innerHTML = `Loupe ${esc(state.init?.version ?? '')} · ${esc(state.init?.turboJpeg ?? '')}<br>Bad Ideas Works · ${esc(t('built on the Striplight PRO engine'))}`;
  $$('.theme-card').forEach((c) => c.setAttribute('aria-checked', String(c.dataset.theme === document.documentElement.dataset.theme)));
  renderToolList();
  $('#settings-dlg').showModal();
  refreshStorage();
}

async function refreshStorage() {
  if (!inHost) return;
  const st = await call('storage.info').catch(() => null);
  if (!st) return;
  $('#st-backup-path').textContent = st.backupRoot;
  $('#st-backup-size').textContent = `${tn(st.backupFiles, '{n} file', '{n} files')} · ${fmtBytes(st.backupBytes)}`;
  $('#st-thumb-path').textContent = st.thumbRoot;
  $('#st-thumb-size').textContent = `${tn(st.thumbFiles, '{n} file', '{n} files')} · ${fmtBytes(st.thumbBytes)}`;
}

$('#set-quality').addEventListener('change', (e) => saveSettings({ saveQuality: Number(e.target.value) }));
$('#set-lossless').addEventListener('change', (e) => saveSettings({ losslessRotate: e.target.checked }));
$('#set-workers').addEventListener('change', (e) => saveSettings({ workers: Math.max(1, Math.min(16, Number(e.target.value) || 4)) }));
$('#lang').addEventListener('change', async (e) => {
  await run('app.setLanguage', { lang: e.target.value });
  location.reload();
});

// ------------------------------------------------------------------ jobs

on('job.progress', (p) => {
  state.job = p.running;
  $('#job').hidden = !p.running;
  document.querySelector('.statusbar').classList.toggle('busy', p.running);
  $('#job-text').textContent = `${p.title} ${p.done}/${p.total}`;
  $('#job-bar').style.width = `${p.total ? (p.done / p.total) * 100 : 0}%`;
  if (!p.running) refreshTarget();
});

// ------------------------------------------------------------------ commands

const commands = {
  rename: openRename,
  merge: openMerge,
  undo,
  settings: openSettings,
  help: () => $('#help-dlg').showModal(),
  addFavorite: async () => {
    const path = await run('fs.pickFolder', { title: t('Pin a folder or network share'), start: state.path });
    if (!path) return;
    state.roots = await run('fs.favoriteAdd', { path });
    renderTree();
  },
  refreshTree: async () => { state.roots = await run('fs.roots'); renderTree(); },
  up: () => { if (state.parent) openFolder(state.parent, { focusName: baseName(state.path) }); },
  refresh: () => openFolder(state.path, { keepSelection: true }),
  selectAll: () => {
    state.selected = new Set(state.images.map((it) => state.items.indexOf(it)));
    paintSelection(); refreshInspector();
  },
  selectEdited: () => {
    state.selected = new Set(state.images.filter((it) => it.edited).map((it) => state.items.indexOf(it)));
    paintSelection(); refreshInspector();
    if (!state.selected.size) toast(t('No edited images in this folder.'));
  },
  closeViewer,
  prev: () => step(-1),
  next: () => step(1),
  zoomIn: () => viewer.zoomBy(1.25),
  zoomOut: () => viewer.zoomBy(1 / 1.25),
  fit: () => viewer.fit(),
  actual: () => viewer.actual(),
  before: () => setBefore(!state.before),
  rotL: () => rotate(-90),
  rotR: () => rotate(90),
  mirror: () => { orient.mirror = !orient.mirror; paintOrient(); controlsChanged(); },
  negative: () => { orient.negative = !orient.negative; paintOrient(); controlsChanged(); },
  apply: saveChanges,
  rescanToggle: toggleRescan,
  popup: togglePopup,
  toolbarSep: () => saveToolbar([...toolbarIds(), '|']),
  toolbarDefault: () => saveToolbar([...DEFAULT_TOOLBAR]),
  resetSettings: async () => {
    if (!confirm(t('Reset all settings to their defaults? Pinned folders, the language and the rescan list stay.'))) return;
    state.settings = await run('settings.reset');
    applyTheme(state.settings.theme);
    applyViewBg(state.settings.viewBackground);
    applyGridLayout(true);
    $('#show-names').checked = state.settings.showNames;
    $('#grid').classList.toggle('nonames', !state.settings.showNames);
    document.documentElement.style.setProperty('--tree-w', `${state.settings.treeWidth}px`);
    $('#slice-parts').value = 2; $('#slice-dir').value = 'v'; $('#slice-reverse').checked = false; paintSlice();
    discardChanges();
    renderQuickbar();
    $('#settings-dlg').close();
    openSettings();
    toast(t('All settings are back to their defaults.'), 'ok');
  },
  cropTool: () => setTool('crop'),
  cropClear: () => { for (const k of 'ltrb') $(`#crop-${k}`).value = 0; overlay.setCrop({ l: 0, t: 0, r: 0, b: 0 }); controlsChanged(); },
  sliceTool: () => setTool('slice'),
  slice: openSlice,
  rescanList: openRescan,
  rescanCopy: async () => {
    const n = await run('rescan.copy', { headers: $('#rs-headers').checked });
    toast(tn(n, 'Copied {n} row — paste it into Excel', 'Copied {n} rows — paste them into Excel'), 'ok');
  },
  rescanCsv: async () => { const f = await run('rescan.saveCsv'); if (f) toast(t('Saved {file}', { file: baseName(f) }), 'ok'); },
  rescanClear: async () => {
    if (!confirm(t('Remove every image from the rescan list?'))) return;
    renderRescan(await run('rescan.clear'));
    state.images.forEach((it) => { it.rescan = false; });
    renderGrid();
  },
  reset: resetCorrection,
  discard: () => { if (confirm(t('Discard every correction that is not saved yet?'))) discardChanges(); },
  restore: restoreOriginals,
  cancelJob: () => call('job.cancel').catch(() => {}),
  openBackups: () => run('storage.openBackups'),
  pickBackups: async () => { const p = await run('storage.pickBackupFolder'); if (p) refreshStorage(); },
  clearBackups: async () => {
    if (!confirm(t('Forget every kept original? Edited files can then no longer be restored.'))) return;
    await run('storage.clearBackups');
    refreshStorage();
    openFolder(state.path, { keepSelection: true });
  },
  clearThumbs: async () => { await run('storage.clearThumbs'); refreshStorage(); },
  openLang: () => run('i18n.openFolder'),
};

function setBefore(on) {
  if (state.before === on) return;
  state.before = on;
  $('#before-btn').setAttribute('aria-pressed', String(on));
  pushPopup();
  $('#v-badge').hidden = !on;
  if (state.viewer) showImage(state.vIndex, { keepView: true });
}

document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-cmd]');
  if (b && !b.disabled && commands[b.dataset.cmd]) commands[b.dataset.cmd]();
  const close = e.target.closest('[data-close]');
  if (close) close.closest('dialog')?.close();
});

// ------------------------------------------------------------------ keyboard

document.addEventListener('keydown', (e) => {
  if (document.querySelector('dialog[open]')) return;  // dialogs handle their own keys (Esc closes)
  const typing = e.target.matches?.('input[type=text], input[type=number], select');
  const k = e.key, ctrl = e.ctrlKey || e.metaKey;
  if (k === 'F1') { e.preventDefault(); commands.help(); return; }
  if (k === 'F2') { e.preventDefault(); openRename(); return; }
  if (k === 'F5') { e.preventDefault(); commands.refresh(); return; }
  if (ctrl && k.toLowerCase() === 's') { e.preventDefault(); saveChanges(); return; }
  if (ctrl && k.toLowerCase() === 'r') { e.preventDefault(); toggleRescan(); return; }
  if (ctrl && k.toLowerCase() === 'p') { e.preventDefault(); togglePopup(); return; }
  if (ctrl && e.shiftKey && k.toLowerCase() === 'z') { e.preventDefault(); restoreOriginals(); return; }
  if (ctrl && k.toLowerCase() === 'z') { e.preventDefault(); undo(); return; }
  if (typing) return;

  if (state.viewer) {
    if (k === 'Escape') { e.preventDefault(); closeViewer(); }
    else if (k === 'ArrowRight' || k.toLowerCase() === 'c' || k === 'PageDown' || k === ' ') { e.preventDefault(); step(1); }
    else if (k === 'ArrowLeft' || k.toLowerCase() === 'x' || k === 'PageUp') { e.preventDefault(); step(-1); }
    else if (k === 'Home') showImage(0);
    else if (k === 'End') showImage(state.images.length - 1);
    else if (k === '0') viewer.fit();
    else if (k === '1') viewer.actual();
    else if (k === '+' || k === '=') viewer.zoomBy(1.25);
    else if (k === '-') viewer.zoomBy(1 / 1.25);
    else if (k.toLowerCase() === 'b' && !e.repeat) setBefore(true);
    else if (k.toLowerCase() === 'r') rotate(e.shiftKey ? -90 : 90);
    else if (k.toLowerCase() === 'm') commands.mirror();
    else if (k.toLowerCase() === 'k') setTool('crop');
    else if (k.toLowerCase() === 'l') setTool('slice');
    else if (k.toLowerCase() === 'n') commands.negative();
    return;
  }

  if (ctrl && k.toLowerCase() === 'a') { e.preventDefault(); commands.selectAll(); return; }
  if (ctrl && (k === '+' || k === '=' || k === '-')) { e.preventDefault(); zoomGrid(k !== '-'); return; }
  if (k === 'Backspace' || (e.altKey && k === 'ArrowUp')) { e.preventDefault(); commands.up(); return; }
  if (k === 'Enter') { e.preventDefault(); activate(state.focus); return; }
  if (k === 'Escape') { state.selected.clear(); paintSelection(); return; }
  if (k.length === 1 && !ctrl && !e.altKey && /\S/.test(k)) { e.preventDefault(); typeAhead(k); return; }
  const move = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: columns(), ArrowUp: -columns(), Home: -1e9, End: 1e9 }[k];
  if (move !== undefined && state.items.length) {
    e.preventDefault();
    const n = Math.max(0, Math.min(state.items.length - 1, (state.focus < 0 ? 0 : state.focus) + move));
    selectIndex(n, { shift: e.shiftKey, ctrl: false });
  }
});
// Explorer-style: typing a name jumps to the folder or file; the same letter again goes to the next one.
let typed = '', typedAt = 0;
function typeAhead(ch) {
  const now = performance.now();
  typed = now - typedAt > 900 ? ch.toLowerCase() : typed + ch.toLowerCase();
  typedAt = now;
  const repeat = typed.length > 1 && [...typed].every((c) => c === typed[0]);
  const prefix = repeat ? typed[0] : typed;
  const n = state.items.length;
  const from = typed.length === 1 || repeat ? state.focus + 1 : Math.max(0, state.focus);
  for (let k = 0; k < n; k++) {
    const i = (from + k + n) % n;
    if (state.items[i].name.toLowerCase().startsWith(prefix)) { selectIndex(i); return; }
  }
}
document.addEventListener('keyup', (e) => { if (e.key.toLowerCase() === 'b') setBefore(false); });

// ------------------------------------------------------------------ panel wiring

$$('.slider').forEach((el) => {
  const [r, n] = sliderInputs(el);
  r.addEventListener('input', () => { n.value = r.value; controlsChanged(); });
  n.addEventListener('change', () => { r.value = n.value; paintRange(r); controlsChanged(); });
});
$('#tone-method').addEventListener('change', () => { applyRanges(Number($('#tone-method').value), true); controlsChanged(); });
$$('#crop-l, #crop-t, #crop-r, #crop-b').forEach((el) => el.addEventListener('change', () => {
  const e = gatherEdit();
  overlay.setCrop({ l: e.cropLeft, t: e.cropTop, r: e.cropRight, b: e.cropBottom });
  controlsChanged();
}));
$$('#slice-parts, #slice-dir, #slice-reverse').forEach((el) => el.addEventListener('change', paintSlice));
$('#slice-parts').addEventListener('input', paintSlice);
$$('#tone-flags input, #sharp-mode').forEach((el) => el.addEventListener('change', controlsChanged));
$('#target').addEventListener('change', () => setTarget($('#target').value));

$('#thumb-size').addEventListener('input', (e) => {
  state.settings.thumbSize = Number(e.target.value);
  document.documentElement.style.setProperty('--thumb', `${e.target.value}px`);
});
$('#thumb-size').addEventListener('change', () => { applyGridLayout(true); saveLayout(); });
$('#cols').addEventListener('change', (e) => { state.settings.gridColumns = Number(e.target.value); applyGridLayout(true); saveLayout(); });
$('#grid-wrap').addEventListener('wheel', (e) => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  zoomGrid(e.deltaY < 0);
}, { passive: false });
new ResizeObserver(debounce(() => { if (state.settings.gridColumns) applyGridLayout(true); }, 250)).observe($('#grid-wrap'));
$('#show-names').addEventListener('change', (e) => {
  $('#grid').classList.toggle('nonames', !e.target.checked);
  saveSettings({ showNames: e.target.checked });
});

// Tree splitter
(() => {
  const sp = $('#splitter');
  let drag = null;
  sp.addEventListener('pointerdown', (e) => { drag = { x: e.clientX, w: $('.tree-pane').offsetWidth }; sp.setPointerCapture(e.pointerId); sp.classList.add('drag'); });
  sp.addEventListener('pointermove', (e) => {
    if (!drag) return;
    const w = Math.max(160, Math.min(700, drag.w + e.clientX - drag.x));
    document.documentElement.style.setProperty('--tree-w', `${w}px`);
  });
  sp.addEventListener('pointerup', () => {
    if (!drag) return;
    drag = null; sp.classList.remove('drag');
    saveSettings({ treeWidth: $('.tree-pane').offsetWidth });
  });
})();

window.addEventListener('error', (e) => { if (inHost) call('app.jsError', { message: `${e.message} @ ${e.filename}:${e.lineno}` }).catch(() => {}); });
window.addEventListener('unhandledrejection', (e) => {
  const m = e.reason?.message || String(e.reason);
  if (inHost && !/^(Unknown command|Folder not found)/.test(m)) call('app.jsError', { message: 'promise: ' + m }).catch(() => {});
});

// ------------------------------------------------------------------ start-up

async function start() {
  await initI18n();
  hydrateIcons();
  initRanges();
  ['#target', '#lang'].forEach((s) => initSegmented($(s)));
  buildThemes();
  viewer = new Viewer($('#stage'), $('#v-img'), onViewChange);
  overlay = new Overlay($('#stage'), viewer, {
    onCrop: (c) => { setCropInputs({ cropLeft: c.l, cropTop: c.t, cropRight: c.r, cropBottom: c.b }); controlsChanged(); },
    onCuts: (cuts) => { sliceCuts.list = cuts; },
  });

  if (!inHost) { status(t('Loupe must run inside its desktop window.')); return; }
  const init = await call('app.init');
  state.init = init;
  state.settings = init.settings;
  applyTheme(init.settings.theme);
  applyViewBg(init.settings.viewBackground);
  $('#version').textContent = `v${init.version} · ${init.turboJpeg}`;
  $('#tone-method').innerHTML = init.toneMethods.map((m, i) => `<option value="${i}">${esc(t(m))}</option>`).join('');
  document.documentElement.style.setProperty('--tree-w', `${init.settings.treeWidth || 270}px`);
  applyGridLayout(false);
  $('#show-names').checked = init.settings.showNames ?? true;
  $('#grid').classList.toggle('nonames', !$('#show-names').checked);
  loadControls(state.global);
  renderQuickbar();
  setUndo(init.undo);
  call('popup.isOpen').then((o) => { if (o) { state.popupOpen = true; $('#popup-btn').setAttribute('aria-pressed', 'true'); pushPopup(); } }).catch(() => {});

  state.roots = await call('fs.roots');
  renderTree();
  call('rescan.list').then((r) => setRescanCount(r.entries.length)).catch(() => {});
  const startPath = init.smokeFolder || init.settings.lastFolder;
  if (startPath) await openFolder(startPath).catch(() => {});
  $('#grid-wrap').focus();
  call('app.ready').catch(() => {});
}

// ------------------------------------------------------------------ self-test (CI smoke test, Loupe.exe --smoke)

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms = 15000) {
  const t0 = performance.now();
  while (performance.now() - t0 < ms) { if (await fn()) return true; await sleep(100); }
  return false;
}

window.__loupeSelfTest = async () => {
  const r = { steps: [] };
  const ok = (name, cond, extra) => { r.steps.push({ name, ok: !!cond, ...(extra || {}) }); return !!cond; };
  try {
    const root = state.init.smokeFolder;
    const sep = sepOf(root);
    const sig = [root, '73', '524', '0', '0'].join(sep);
    await openFolder(sig + sep + '80');
    ok('list', state.images.length === 6, { images: state.images.length });
    ok('thumbnails', await until(() => $$('#grid img.ok').length >= 6));
    openViewer(0);
    ok('viewer', await until(() => $('#v-img').complete && $('#v-img').naturalWidth > 0 && $('#v-img').src.includes('/view')));
    const conf0 = window.confirm; window.confirm = () => true;
    setTarget('all');
    setSlider('brightness', 25); controlsChanged();
    ok('global-preview', await until(() => $$('#grid img').every((im) => im.dataset.src.includes('&e='))));
    resetCorrection();
    setTarget('sel');
    setSlider('brightness', 25); controlsChanged();
    ok('own-preview', state.own.size === 1 && pendingItems().length === 1);
    await sleep(400);
    await saveChanges();
    window.confirm = conf0;
    ok('edit', await until(() => state.images[0]?.edited));
    await restoreOriginals();
    ok('restore', await until(() => !state.images[0]?.edited));
    // crop: own correction on image 1 (drawn values), previewed and saved
    for (const [k, v] of [['l', 10], ['t', 0], ['r', 10], ['b', 0]]) $(`#crop-${k}`).value = v;
    $('#crop-l').dispatchEvent(new Event('change'));
    ok('crop-preview', await until(() => state.own.size === 1 && hasCrop(pendingItems()[0].edit)));
    discardChanges();
    await toggleRescan();
    ok('rescan-mark', await until(() => state.images[0]?.rescan && !$('#rescan-count').hidden));
    await openRescan();
    const film = $('#rs-table input[data-k=microfilm]');
    film.value = '123-213'; film.dispatchEvent(new Event('change', { bubbles: true }));
    ok('rescan-list', await until(() => $('#rs-table input[data-k=microfilm]')?.value === '123-213'));
    $('#rescan-dlg').close();
    closeViewer();

    setTarget('all');
    commands.selectAll();
    openRename();
    $('#rn-pattern').value = '{folder}_{n}'; $('#rn-digits').value = 5; $('#rn-start').value = 1; planRename();
    ok('rename-plan', await until(() => renamePlan && renamePlan.changes === 6 && !$('#rn-run').disabled));
    $('#rn-run').click();
    ok('rename', await until(() => state.images.length === 6 && state.images[0].name === '80_00001.jpg'));
    $('#undo-btn').disabled = false;
    const conf = window.confirm; window.confirm = () => true;
    await undo();
    ok('rename-undo', await until(() => state.images[0]?.name === '73_524_0_0_80_00001.jpg'));

    // slice image 1 into two pages: 6 → 7 files, renumbered; then undo
    const before6 = state.images.map((it) => it.name);
    selectIndex(state.items.indexOf(state.images[0]));
    $('#slice-parts').value = '2'; paintSlice();
    await openSlice();
    ok('slice-plan', await until(() => !$('#sl-run').disabled));
    $('#sl-run').click();
    ok('slice', await until(() => state.images.length === 7 && state.images[6].name === '73_524_0_0_80_00007.jpg'));
    await undo();
    ok('slice-undo', await until(() => state.images.length === 6 && state.images.map((it) => it.name).join() === before6.join()));

    await openFolder(sig);
    ok('open-parent', state.folders.length === 3, { path: state.path });
    state.selected = new Set(state.items.map((it, i) => (isPartName(it.name) ? i : -1)).filter((i) => i >= 0));
    paintSelection();
    openMerge();
    ok('merge-plan', await until(() => !$('#mg-run').disabled), { problems: $('#mg-problems').textContent });
    $('#mg-run').click();
    ok('merge', await until(() => state.folders.some((f) => f.name === '123') && !state.folders.some((f) => f.name === '123_1')));
    await undo();
    ok('merge-undo', await until(() => state.folders.some((f) => f.name === '123_1') && !state.folders.some((f) => f.name === '123')));
    window.confirm = conf;
    await openFolder(sig + sep + '80');
    selectIndex(0);
  } catch (e) {
    ok('exception', false, { error: String(e?.message || e) });
  }
  r.ok = r.steps.every((s) => s.ok);
  await call('app.selfTestResult', r);
  return r;
};

window.__loupeShow = (what) => {
  if (what === 'viewer') { setTarget('all'); setSlider('contrast', 18); controlsChanged(); openViewer(1); }
  if (what === 'rename') { closeViewer(); discardChanges(); commands.selectAll(); openRename(); }
};

start();
