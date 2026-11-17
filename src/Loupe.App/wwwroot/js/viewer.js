// Zoom and pan for the big image view. The <img> is positioned with a transform:
// screen = offset + image_px * scale. "Fit" keeps the whole image visible; 1:1 shows real pixels.
// The source image may be a reduced view (served at screen size): naturalScale tells how many
// file pixels one served pixel stands for, so 1:1 means file pixels.

export class Viewer {
  constructor(stage, img, onChange) {
    this.stage = stage;
    this.img = img;
    this.onChange = onChange || (() => {});
    this.scale = 1;         // screen px per file px
    this.x = 0; this.y = 0; // image top-left on screen
    this.fileW = 0; this.fileH = 0;
    this.fitMode = true;
    this.#wire();
    new ResizeObserver(() => { if (this.fitMode) this.fit(); else this.#apply(); }).observe(stage);
  }

  /** Size of the image file in pixels (after orientation), so zoom is relative to real pixels. */
  setFileSize(w, h, keepView) {
    const changed = w !== this.fileW || h !== this.fileH;
    this.fileW = w; this.fileH = h;
    if (!keepView || changed || this.fitMode) this.fit(); else this.#apply();
  }

  get fitScale() {
    const r = this.stage.getBoundingClientRect();
    if (!this.fileW || !this.fileH) return 1;
    return Math.min((r.width - 24) / this.fileW, (r.height - 24) / this.fileH);
  }

  fit() {
    const r = this.stage.getBoundingClientRect();
    this.fitMode = true;
    this.scale = this.fitScale;
    this.x = (r.width - this.fileW * this.scale) / 2;
    this.y = (r.height - this.fileH * this.scale) / 2;
    this.#apply();
  }

  zoomTo(scale, cx, cy) {
    const r = this.stage.getBoundingClientRect();
    if (cx === undefined) { cx = r.width / 2; cy = r.height / 2; }
    scale = Math.max(Math.min(this.fitScale, 0.05), Math.min(16, scale));
    const ix = (cx - this.x) / this.scale, iy = (cy - this.y) / this.scale;
    this.scale = scale;
    this.x = cx - ix * scale;
    this.y = cy - iy * scale;
    this.fitMode = false;
    this.#clamp();
    this.#apply();
  }

  actual() { this.zoomTo(1); }
  zoomBy(f, cx, cy) { this.zoomTo(this.scale * f, cx, cy); }

  #clamp() {
    const r = this.stage.getBoundingClientRect();
    const w = this.fileW * this.scale, h = this.fileH * this.scale;
    this.x = w <= r.width ? (r.width - w) / 2 : Math.min(40, Math.max(r.width - w - 40, this.x));
    this.y = h <= r.height ? (r.height - h) / 2 : Math.min(40, Math.max(r.height - h - 40, this.y));
  }

  #apply() {
    const img = this.img;
    const nw = img.naturalWidth || this.fileW, nh = img.naturalHeight || this.fileH;
    // Draw the served image at file size × scale.
    img.style.width = `${this.fileW * this.scale}px`;
    img.style.height = `${this.fileH * this.scale}px`;
    img.style.transform = `translate(${Math.round(this.x)}px, ${Math.round(this.y)}px)`;
    img.classList.toggle('pixelated', this.scale >= 3);
    this.onChange({ scale: this.scale, fit: this.fitMode, served: nw / Math.max(1, this.fileW), needFull: this.scale * this.fileW > nw * 1.05 });
  }

  #wire() {
    const s = this.stage;
    s.addEventListener('wheel', (e) => {
      e.preventDefault();
      const r = s.getBoundingClientRect();
      this.zoomBy(e.deltaY < 0 ? 1.18 : 1 / 1.18, e.clientX - r.left, e.clientY - r.top);
    }, { passive: false });
    let drag = null;
    s.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      drag = { x: e.clientX, y: e.clientY, ox: this.x, oy: this.y };
      s.setPointerCapture(e.pointerId);
      s.classList.add('drag');
    });
    s.addEventListener('pointermove', (e) => {
      if (!drag) return;
      this.x = drag.ox + e.clientX - drag.x;
      this.y = drag.oy + e.clientY - drag.y;
      this.fitMode = false;
      this.#clamp();
      this.#apply();
    });
    const end = () => { drag = null; s.classList.remove('drag'); };
    s.addEventListener('pointerup', end);
    s.addEventListener('pointercancel', end);
    s.addEventListener('dblclick', (e) => {
      const r = s.getBoundingClientRect();
      if (this.fitMode) this.zoomTo(1, e.clientX - r.left, e.clientY - r.top); else this.fit();
    });
  }
}
