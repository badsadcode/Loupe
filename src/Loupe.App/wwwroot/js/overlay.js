// Tools drawn over the viewer image: the crop rectangle and the slice cut lines.
// Positions are fractions of the shown image, so they follow zoom and pan, and the same crop /
// cuts fit every image of a folder whatever its pixel size.

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

export class Overlay {
  constructor(stage, viewer, { onCrop, onCuts }) {
    this.stage = stage;
    this.viewer = viewer;
    this.onCrop = onCrop;
    this.onCuts = onCuts;
    this.mode = null;                       // 'crop' | 'slice' | null
    this.crop = { l: 0, t: 0, r: 0, b: 0 }; // fractions removed on each side
    this.cuts = [0.5];
    this.vertical = true;
    this.reverse = false;
    this.el = document.createElement('div');
    this.el.className = 'overlay';
    this.el.hidden = true;
    stage.append(this.el);
  }

  setMode(mode) {
    this.mode = mode;
    this.el.hidden = !mode;
    this.render();
  }

  setCrop(c) { this.crop = { ...c }; if (this.mode === 'crop') this.layout(); }

  setCuts(cuts, vertical, reverse) {
    this.cuts = [...cuts];
    this.vertical = vertical;
    this.reverse = reverse;
    if (this.mode === 'slice') this.render();
  }

  /** The shown image on screen (stage coordinates). */
  rect() {
    const v = this.viewer;
    return { x: v.x, y: v.y, w: v.fileW * v.scale, h: v.fileH * v.scale };
  }

  render() {
    this.el.innerHTML = '';
    if (this.mode === 'crop') {
      this.el.innerHTML = `<div class="crop-box" data-h="move">
        <span class="h n" data-h="n"></span><span class="h s" data-h="s"></span><span class="h w" data-h="w"></span><span class="h e" data-h="e"></span>
        <span class="h nw" data-h="nw"></span><span class="h ne" data-h="ne"></span><span class="h sw" data-h="sw"></span><span class="h se" data-h="se"></span>
        <span class="crop-size"></span></div>`;
    } else if (this.mode === 'slice') {
      this.cuts.forEach((_, i) => {
        const line = document.createElement('div');
        line.className = `cut ${this.vertical ? 'v' : 'h'}`;
        line.dataset.cut = i;
        this.el.append(line);
      });
      for (let i = 0; i <= this.cuts.length; i++) {
        const tag = document.createElement('span');
        tag.className = 'part-tag';
        tag.dataset.part = i;
        this.el.append(tag);
      }
    }
    this.#wire();
    this.layout();
  }

  layout() {
    if (!this.mode) return;
    const r = this.rect();
    if (this.mode === 'crop') {
      const box = this.el.querySelector('.crop-box');
      const c = this.crop;
      Object.assign(box.style, {
        left: `${r.x + r.w * c.l}px`, top: `${r.y + r.h * c.t}px`,
        width: `${r.w * (1 - c.l - c.r)}px`, height: `${r.h * (1 - c.t - c.b)}px`,
      });
      const fw = this.viewer.fileW, fh = this.viewer.fileH;
      box.querySelector('.crop-size').textContent = `${Math.round(fw * (1 - c.l - c.r))} × ${Math.round(fh * (1 - c.t - c.b))}`;
    } else {
      const edges = [0, ...this.cuts, 1];
      this.el.querySelectorAll('.cut').forEach((line) => {
        const f = this.cuts[Number(line.dataset.cut)];
        Object.assign(line.style, this.vertical
          ? { left: `${r.x + r.w * f}px`, top: `${r.y}px`, height: `${r.h}px`, width: '' }
          : { top: `${r.y + r.h * f}px`, left: `${r.x}px`, width: `${r.w}px`, height: '' });
      });
      const n = edges.length - 1;
      this.el.querySelectorAll('.part-tag').forEach((tag) => {
        const i = Number(tag.dataset.part);
        const mid = (edges[i] + edges[i + 1]) / 2;
        tag.textContent = String(this.reverse ? n - i : i + 1);
        Object.assign(tag.style, this.vertical
          ? { left: `${r.x + r.w * mid}px`, top: `${r.y + 16}px` }
          : { left: `${r.x + 16}px`, top: `${r.y + r.h * mid}px` });
      });
    }
  }

  #wire() {
    for (const el of this.el.querySelectorAll('[data-h], .cut')) {
      el.addEventListener('pointerdown', (e) => {
        if (e.button !== 0) return;
        e.stopPropagation();
        e.preventDefault();
        el.setPointerCapture(e.pointerId);
        const start = { x: e.clientX, y: e.clientY, crop: { ...this.crop }, cuts: [...this.cuts] };
        const handle = e.target.dataset.h || 'move';
        const move = (ev) => {
          const r = this.rect();
          const dx = (ev.clientX - start.x) / r.w, dy = (ev.clientY - start.y) / r.h;
          if (this.mode === 'crop') this.#dragCrop(handle, start.crop, dx, dy);
          else this.#dragCut(Number(el.dataset.cut), start.cuts, this.vertical ? dx : dy);
          this.layout();
        };
        const up = () => {
          el.removeEventListener('pointermove', move);
          el.removeEventListener('pointerup', up);
          if (this.mode === 'crop') this.onCrop({ ...this.crop }); else this.onCuts([...this.cuts]);
        };
        el.addEventListener('pointermove', move);
        el.addEventListener('pointerup', up);
      });
    }
  }

  #dragCrop(h, c0, dx, dy) {
    const MIN = 0.05;
    const c = { ...c0 };
    if (h === 'move') {
      const w = 1 - c0.l - c0.r, hh = 1 - c0.t - c0.b;
      c.l = clamp(c0.l + dx, 0, 1 - w); c.r = 1 - w - c.l;
      c.t = clamp(c0.t + dy, 0, 1 - hh); c.b = 1 - hh - c.t;
    } else {
      if (h.includes('w')) c.l = clamp(c0.l + dx, 0, 1 - c0.r - MIN);
      if (h.includes('e')) c.r = clamp(c0.r - dx, 0, 1 - c0.l - MIN);
      if (h.includes('n')) c.t = clamp(c0.t + dy, 0, 1 - c0.b - MIN);
      if (h.includes('s')) c.b = clamp(c0.b - dy, 0, 1 - c0.t - MIN);
    }
    this.crop = c;
  }

  #dragCut(i, cuts0, d) {
    const lo = (i === 0 ? 0 : cuts0[i - 1]) + 0.02, hi = (i === cuts0.length - 1 ? 1 : cuts0[i + 1]) - 0.02;
    this.cuts = [...cuts0];
    this.cuts[i] = clamp(cuts0[i] + d, lo, hi);
  }
}
