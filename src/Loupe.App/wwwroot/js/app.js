// Loupe — page logic. Host commands: Services/AppController.cs. Images: https://img.loupe/.
import { call, on, inHost } from './bridge.js';
import { initI18n, t, tn, currentLanguage } from './i18n.js';
import { hydrateIcons, icon } from './icons.js';
import { initSegmented, initRanges, paintRange } from './ui.js';
import { Viewer } from './viewer.js';

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
  return `${IMG}/thumb?p=${encodeURIComponent(it.path)}&s=${thumbPx()}&v=${it.modified}`;
}

// Thumbnail resolution from the tile width actually shown (Auto: the slider; N columns: grid width / N).
function tileWidth() {
  const n = state.settings.gridColumns || 0;
  if (!n) return state.settings.thumbSize || 180;
  return ($('#grid-wrap').clientWidth - 28 - (n - 1) * 10) / n;
}
const thumbPx = () => (tileWidth() * (window.devicePixelRatio || 1) > 300 ? 512 : 256);

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

function renderGrid() {
  const grid = $('#grid');
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
        ${it.edited ? `<span class="badge" title="${esc(t('Edited — the original is kept'))}">${icon('pencil-line', 12)}</span>` : ''}`;
      const img = tile.querySelector('img');
      img.dataset.src = thumbUrl(it);
      img.onload = () => img.classList.add('ok');
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
  $$('#grid .tile').forEach((el) => {
    const i = Number(el.dataset.i);
    el.classList.toggle('sel', state.selected.has(i));
    el.classList.toggle('focus', i === state.focus);
  });
  updateCount();
  refreshTarget();
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
  const e = edit && !state.before ? editParam() : '';
  return `${IMG}/view?p=${encodeURIComponent(it.path)}&max=${max}&v=${it.modified}${e ? `&e=${encodeURIComponent(e)}` : ''}`;
}

function openViewer(index) {
  if (index < 0 || index >= state.images.length) return;
  state.viewer = true;
  $('#viewer').hidden = false;
  showImage(index);
}

function closeViewer() {
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
    const long = info ? Math.max(info.width, info.height) : Math.max(probe.naturalWidth, probe.naturalHeight);
    const k = long / Math.max(probe.naturalWidth, probe.naturalHeight);
    viewer.setFileSize(Math.round(probe.naturalWidth * k), Math.round(probe.naturalHeight * k), keepView && !changed);
    prefetch(index);
  };
  probe.onerror = () => { if (token === viewToken) { $('#v-loading').hidden = true; toast(t('Could not open {name}', { name: it.name }), 'error'); } };
  probe.src = url;
}

function prefetch(index) {
  for (const k of [index + 1, index - 1, index + 2]) {
    const it = state.images[k];
    if (it) { const p = new Image(); p.src = viewUrl(it, { edit: false }); }
  }
}

function step(d) {
  if (!state.viewer) return;
  const n = state.vIndex + d;
  if (n >= 0 && n < state.images.length) showImage(n);
}

const refreshView = debounce(() => { if (state.viewer) showImage(state.vIndex, { keepView: true }); }, 120);

function onViewChange(v) {
  $('#v-zoom').textContent = `${Math.round(v.scale * 100)}%`;
  if (v.needFull && !wantFull) { wantFull = true; refreshView(); }
}

// ------------------------------------------------------------------ correction (edit) panel

const QUANTUM = { contrast: [-128, 255, 64], brightness: [-128, 127, 0], gamma: [-255, 255, 0] };
const STRIPLIGHT = { contrast: [-100, 100, 0], brightness: [-100, 100, 0], gamma: [10, 300, 100] };
const isQuantum = (m) => m === 1 || m === 4;
const isAdaptive = (m) => m >= 5 && m <= 8;
const orient = { rotation: 0, mirror: false, negative: false };

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
  };
}

// Same rule as ToneAdjust.IsNoOp in the engine.
function toneIsNoOp(e) {
  if (e.flags !== 0) return false;
  if (isQuantum(e.method)) return e.contrast === 64 && e.brightness === 0 && e.gamma === 0;
  if (e.method === 2 || e.method === 3 || e.method === 5) return false;
  if (e.method >= 6) return e.strength <= 0 && e.contrast === 0 && e.brightness === 0 && e.gamma === 100;
  return e.contrast === 0 && e.brightness === 0 && e.gamma === 100;
}

function isNeutral(e) {
  return toneIsNoOp(e) && e.sharpen <= 0 && !e.negative && e.rotation === 0 && !e.mirror;
}

function editParam() {
  const e = gatherEdit();
  return isNeutral(e) ? '' : JSON.stringify(e);
}

function resetEdit() {
  $('#tone-method').value = 0;
  applyRanges(0, true);
  setSlider('strength', 50);
  setSlider('sharpen', 0);
  $('#sharp-mode').value = 1;
  $$('#tone-flags input').forEach((c) => { c.checked = false; });
  Object.assign(orient, { rotation: 0, mirror: false, negative: false });
  paintOrient();
  editChanged();
}

function paintOrient() {
  $('#mirror-btn').setAttribute('aria-pressed', String(orient.mirror));
  $('#negative-btn').setAttribute('aria-pressed', String(orient.negative));
  const bits = [];
  if (orient.rotation) bits.push(`${orient.rotation}°`);
  if (orient.mirror) bits.push(t('mirrored'));
  if (orient.negative) bits.push(t('negative'));
  $('#orient-state').textContent = bits.join(' · ');
}

function editChanged() {
  const neutral = isNeutral(gatherEdit());
  $('#apply-btn').disabled = neutral || !targetPaths().length || state.job;
  refreshView();
}

function rotate(d) { orient.rotation = (orient.rotation + d + 360) % 360; paintOrient(); editChanged(); }

// "Apply to": the image in the viewer / focused tile, or every selected image.
function targetMode() { return $('#target').value; }

function targetPaths() {
  if (targetMode() === 'sel') return selectedImages().map((it) => it.path);
  const it = state.viewer ? state.images[state.vIndex] : focusItem();
  return it?.type === 'image' ? [it.path] : [];
}

function refreshTarget() {
  const n = selectedImages().length;
  $('#target-sel').textContent = n ? `${t('Selected')} (${n.toLocaleString()})` : t('Selected');
  $('#target-sel').disabled = n < 2 && targetMode() !== 'sel';
  if (targetMode() === 'sel' && n === 0) $('#target').value = 'one';
  const paths = targetPaths();
  $('#target-hint').textContent = targetMode() === 'sel'
    ? tn(paths.length, 'The correction is saved into {n} image. The preview shows the one in the viewer.', 'The correction is saved into {n} images. The preview shows the one in the viewer.')
    : '';
  $('#apply-label').textContent = paths.length > 1 ? tn(paths.length, 'Save to {n} file', 'Save to {n} files') : t('Save to file');
  const edited = targetMode() === 'sel' ? selectedImages().filter((it) => it.edited).length : (focusItem()?.edited ? 1 : 0);
  $('#restore-btn').disabled = !edited || state.job;
  $('#restore-label').textContent = edited > 1 ? tn(edited, 'Restore {n} original', 'Restore {n} originals') : t('Restore original');
  $('#apply-btn').disabled = isNeutral(gatherEdit()) || !paths.length || state.job;
}

async function applyEdit() {
  const paths = targetPaths();
  const edit = gatherEdit();
  if (!paths.length || isNeutral(edit) || state.job) return;
  const r = await run('edit.apply', { paths, edit });
  reportJob(r, 'Saved {n} file', 'Saved {n} files');
  if (!$('#keep-edit').checked) resetEdit();
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

// ------------------------------------------------------------------ settings dialog

async function openSettings() {
  const s = state.settings;
  $('#lang').value = currentLanguage();
  $('#set-quality').value = String(s.saveQuality ?? 0);
  $('#set-lossless').checked = s.losslessRotate ?? true;
  $('#set-workers').value = s.workers ?? 4;
  $('#about').innerHTML = `Loupe ${esc(state.init?.version ?? '')} · ${esc(state.init?.turboJpeg ?? '')}<br>Bad Ideas Works · ${esc(t('built on the Striplight PRO engine'))}`;
  $$('.theme-card').forEach((c) => c.setAttribute('aria-checked', String(c.dataset.theme === document.documentElement.dataset.theme)));
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
  mirror: () => { orient.mirror = !orient.mirror; paintOrient(); editChanged(); },
  negative: () => { orient.negative = !orient.negative; paintOrient(); editChanged(); },
  apply: applyEdit,
  reset: resetEdit,
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
  if (ctrl && k.toLowerCase() === 's') { e.preventDefault(); applyEdit(); return; }
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
    else if (k.toLowerCase() === 'n') commands.negative();
    return;
  }

  if (ctrl && k.toLowerCase() === 'a') { e.preventDefault(); commands.selectAll(); return; }
  if (ctrl && (k === '+' || k === '=' || k === '-')) { e.preventDefault(); zoomGrid(k !== '-'); return; }
  if (k === 'Backspace' || (e.altKey && k === 'ArrowUp')) { e.preventDefault(); commands.up(); return; }
  if (k === 'Enter') { e.preventDefault(); activate(state.focus); return; }
  if (k === 'Escape') { state.selected.clear(); paintSelection(); return; }
  if (k.toLowerCase() === 'r' && !ctrl) { rotate(e.shiftKey ? -90 : 90); return; }
  if (k.toLowerCase() === 'm' && !ctrl) { commands.mirror(); return; }
  if (k.toLowerCase() === 'n' && !ctrl) { commands.negative(); return; }
  const move = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: columns(), ArrowUp: -columns(), Home: -1e9, End: 1e9 }[k];
  if (move !== undefined && state.items.length) {
    e.preventDefault();
    const n = Math.max(0, Math.min(state.items.length - 1, (state.focus < 0 ? 0 : state.focus) + move));
    selectIndex(n, { shift: e.shiftKey, ctrl: false });
  }
});
document.addEventListener('keyup', (e) => { if (e.key.toLowerCase() === 'b') setBefore(false); });

// ------------------------------------------------------------------ panel wiring

$$('.slider').forEach((el) => {
  const [r, n] = sliderInputs(el);
  r.addEventListener('input', () => { n.value = r.value; editChanged(); });
  n.addEventListener('change', () => { r.value = n.value; paintRange(r); editChanged(); });
});
$('#tone-method').addEventListener('change', () => { applyRanges(Number($('#tone-method').value), true); editChanged(); });
$$('#tone-flags input, #sharp-mode').forEach((el) => el.addEventListener('change', editChanged));
$('#target').addEventListener('change', () => { refreshTarget(); });

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
  resetEdit();
  setUndo(init.undo);

  state.roots = await call('fs.roots');
  renderTree();
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
    setSlider('brightness', 25); editChanged();
    await sleep(500);
    await applyEdit();
    ok('edit', await until(() => state.images[0]?.edited));
    await restoreOriginals();
    ok('restore', await until(() => !state.images[0]?.edited));
    closeViewer();

    $('#target').value = 'one';
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

    await openFolder(sig);
    state.selected = new Set(state.items.map((it, i) => (isPartName(it.name) ? i : -1)).filter((i) => i >= 0));
    paintSelection();
    openMerge();
    ok('merge-plan', await until(() => !$('#mg-run').disabled));
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
  if (what === 'viewer') { setSlider('contrast', 18); editChanged(); openViewer(1); }
  if (what === 'rename') { closeViewer(); resetEdit(); commands.selectAll(); openRename(); }
};

start();
