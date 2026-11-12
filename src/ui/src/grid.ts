import { Column, Kind } from './bridge';
import { ResultStore, cellText } from './store';

const ROW_H = 22;
const HEADER_H = 28;
const GUTTER_W = 76;
const FONT = '13px Consolas, "Cascadia Mono", monospace';
const NULL_FONT = 'italic 12px Consolas, "Cascadia Mono", monospace';
const HEADER_FONT ='600 13px "Segoe UI", sans-serif';

/** Column sizing: text padding each side, the narrowest a column gets, and caps for fitting (automatic, and on a double-click). */
const PAD = 8;
const MIN_W = 60;
const MAX_AUTO_W = 480;
const MAX_FIT_W = 1200;
/** Rows looked at when fitting: enough to be representative, few enough to be instant. */
const AUTO_SAMPLE = 1000;
const FIT_SAMPLE = 5000;
/** How close (px) to a column's edge in the header the pointer must be to resize it. */
const EDGE_HIT = 4;
const ELLIPSIS = '…';

export interface GridTheme { bg: string; altBg: string; fg: string; dim: string; headerBg: string; line: string }

export const lightTheme: GridTheme = { bg: '#ffffff', altBg: '#f6f8fa', fg: '#1f2328', dim: '#8c959f', headerBg: '#eaeef2', line: '#d0d7de' };
export const darkTheme: GridTheme = { bg: '#0d1117', altBg: '#151b23', fg: '#e6edf3', dim: '#6e7681', headerBg: '#1c232c', line: '#30363d' };

/** Read-only canvas grid. Only the visible rows are ever drawn; scrolling uses a native scroller + sticky canvas. */
export class Grid {
  private ctx: CanvasRenderingContext2D;
  private spacer = document.createElement('div');
  private canvas = document.createElement('canvas');
  private columns: Column[] = [];
  private colX: number[] = [];
  /** Each column's width. Fitted to the content when the first rows arrive, unless the user has dragged it. */
  private widths: number[] = [];
  private manual: boolean[] = [];
  private fitted = false;
  /** Width of one character of the (monospaced) cell font: lets long values be cut without measuring all of them. */
  private charW = 8;
  private drag: { col: number; startX: number; startW: number } | null = null;
  private totalW = GUTTER_W;
  private store = new ResultStore();
  private rowHint = 0;
  private scheduled = false;
  private drawTimes: number[] = [];
  theme: GridTheme = lightTheme;
  onViewChange: () => void = () => {};
  /** Runs after each frame; used to time first paint. */
  onFrame: () => void = () => {};

  constructor(private wrap: HTMLElement) {
    wrap.style.position = 'relative';
    wrap.style.overflow = 'auto';
    this.canvas.style.cssText = 'position:sticky;top:0;left:0;display:block';
    this.spacer.style.cssText = 'position:absolute;top:0;left:0;width:1px;height:1px;pointer-events:none';
    wrap.append(this.canvas, this.spacer);
    this.ctx = this.canvas.getContext('2d', { alpha: false })!;
    wrap.addEventListener('scroll', () => { this.invalidate(); this.onViewChange(); }, { passive: true });
    new ResizeObserver(() => { this.invalidate(); this.onViewChange(); }).observe(wrap);
    this.ctx.font = FONT;
    this.charW = this.ctx.measureText('0').width || 8;
    this.initResizing();
  }

  // ---- column widths ------------------------------------------------------------------------------------------

  /** Drag a column's right edge in the header to resize it; double-click the edge to fit it to the loaded rows. */
  private initResizing() {
    const c = this.canvas;
    c.addEventListener('pointermove', (e) => {
      if (this.drag) {
        this.setWidth(this.drag.col, this.drag.startW + e.clientX - this.drag.startX);
        return;
      }
      c.style.cursor = this.edgeAt(e.offsetX, e.offsetY) >= 0 ? 'col-resize' : '';
    });
    c.addEventListener('pointerdown', (e) => {
      const col = this.edgeAt(e.offsetX, e.offsetY);
      if (col < 0 || e.button !== 0) return;
      c.setPointerCapture(e.pointerId);
      this.drag = { col, startX: e.clientX, startW: this.widths[col] };
      e.preventDefault();
    });
    const end = (e: PointerEvent) => {
      if (!this.drag) return;
      this.drag = null;
      c.releasePointerCapture?.(e.pointerId);
    };
    c.addEventListener('pointerup', end);
    c.addEventListener('pointercancel', end);
    c.addEventListener('dblclick', (e) => {
      const col = this.edgeAt(e.offsetX, e.offsetY);
      if (col >= 0) this.setWidth(col, this.fitWidth(col, FIT_SAMPLE, MAX_FIT_W));
    });
  }

  /** The column whose right edge is under the pointer, in the header; -1 if none. */
  private edgeAt(px: number, py: number): number {
    if (py > HEADER_H) return -1;
    const sx = this.wrap.scrollLeft;
    for (let c = 0; c < this.columns.length; c++) {
      const edge = GUTTER_W + this.colX[c] + this.widths[c] - sx;
      if (edge > GUTTER_W && Math.abs(px - edge) <= EDGE_HIT) return c;
    }
    return -1;
  }

  /** A width the user chose: kept from now on, never refitted. */
  private setWidth(col: number, width: number) {
    this.widths[col] = Math.max(MIN_W, Math.round(width));
    this.manual[col] = true;
    this.layout();
    this.invalidate();
  }

  private layout() {
    this.colX = [];
    let x = 0;
    for (const w of this.widths) { this.colX.push(x); x += w; }
    this.totalW = GUTTER_W + x;
  }

  /** The width that shows the header and the first `sample` loaded rows whole, within [MIN_W, cap]. */
  private fitWidth(col: number, sample: number, cap: number): number {
    const ctx = this.ctx;
    ctx.font = HEADER_FONT;
    let widest = ctx.measureText(this.columns[col].name).width;
    ctx.font = NULL_FONT;
    const nullW = ctx.measureText('NULL').width;
    ctx.font = FONT;
    const maxChars = Math.ceil(cap / this.charW) + 1; // anything longer is capped anyway: never measure a huge value whole
    const rows = Math.min(this.store.loadedRows, sample);
    for (let r = 0; r < rows && widest < cap; r++) {
      const batch = this.store.batchFor(r);
      if (!batch) break;
      const v = cellText(batch, col, r - batch.startRow);
      const w = v === null ? nullW : ctx.measureText(v.length > maxChars ? v.slice(0, maxChars) : v).width;
      if (w > widest) widest = w;
    }
    return Math.min(cap, Math.max(MIN_W, Math.ceil(widest) + PAD * 2));
  }

  /**
   * Text cut to fit `avail` px, ending in "…" when anything was cut. `minCharW` is the narrowest a character can be in the
   * current font, so at most avail / minCharW characters can fit: longer text is never measured whole.
   */
  private fitText(text: string, avail: number, minCharW = this.charW): string {
    const ctx = this.ctx;
    const est = Math.floor(avail / minCharW);
    if (text.length <= est && ctx.measureText(text).width <= avail) return text;
    let n = Math.min(text.length, est);
    while (n > 0 && ctx.measureText(text.slice(0, n) + ELLIPSIS).width > avail) n--;
    return n > 0 ? text.slice(0, n) + ELLIPSIS : '';
  }

  setStore(store: ResultStore, columns: Column[]) {
    this.store = store;
    this.columns = columns;
    this.manual = columns.map(() => false);
    this.fitted = false;
    // Until rows arrive, each column is as wide as its header; then they are fitted to the content (see draw).
    this.widths = columns.map((_, c) => this.fitWidth(c, 0, MAX_AUTO_W));
    this.layout();
    this.rowHint = 0;
    this.wrap.scrollTop = 0;
    this.wrap.scrollLeft = 0;
    this.invalidate();
  }

  /** Total rows if known (query finished), otherwise the loaded count drives the scroll height. */
  setRowHint(rows: number) { this.rowHint = rows; this.invalidate(); }


  get drawStats() {
    const t = this.drawTimes;
    if (!t.length) return { avg: 0, max: 0 };
    return { avg: t.reduce((a, b) => a + b, 0) / t.length, max: Math.max(...t) };
  }

  invalidate() {
    if (this.scheduled) return;
    this.scheduled = true;
    requestAnimationFrame(() => { this.scheduled = false; this.draw(); this.onFrame(); });
  }

  private draw() {
    const t0 = performance.now();
    const dpr = window.devicePixelRatio || 1;
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
      this.canvas.width = Math.round(w * dpr);
      this.canvas.height = Math.round(h * dpr);
      this.canvas.style.width = w + 'px';
      this.canvas.style.height = h + 'px';
    }
    // Fit the columns to the first rows once they are here. Columns the user has resized keep their width.
    if (!this.fitted && this.store.loadedRows > 0) {
      this.fitted = true;
      for (let c = 0; c < this.columns.length; c++) if (!this.manual[c]) this.widths[c] = this.fitWidth(c, AUTO_SAMPLE, MAX_AUTO_W);
      this.layout();
    }
    const rows = Math.max(this.store.loadedRows, this.rowHint);
    this.spacer.style.width = this.totalW + 'px';
    this.spacer.style.height = HEADER_H + rows * ROW_H + 'px';

    const ctx = this.ctx, th = this.theme;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = th.bg;
    ctx.fillRect(0, 0, w, h);
    ctx.font = FONT;
    ctx.textBaseline = 'middle';

    const sx = this.wrap.scrollLeft, sy = this.wrap.scrollTop;
    const first = Math.max(0, Math.floor(sy / ROW_H));
    const last = Math.min(rows - 1, Math.ceil((sy + h - HEADER_H) / ROW_H));
    const nCols = this.columns.length;

    for (let r = first; r <= last; r++) {
      const y = HEADER_H + r * ROW_H - sy;
      if (r % 2) { ctx.fillStyle = th.altBg; ctx.fillRect(0, y, w, ROW_H); }
      const batch = this.store.batchFor(r);
      ctx.fillStyle = th.dim;
      if (!batch) { ctx.fillText('loading…', GUTTER_W + 8 - sx, y + ROW_H / 2); continue; }
      const local = r - batch.startRow;
      ctx.fillStyle = th.fg;
      for (let c = 0; c < nCols; c++) {
        const x = GUTTER_W + this.colX[c] - sx;
        const cw = (this.colX[c + 1] ?? this.totalW - GUTTER_W) - this.colX[c];
        if (x + cw < GUTTER_W || x > w) continue;
        ctx.save();
        ctx.beginPath();
        ctx.rect(Math.max(x, GUTTER_W), y, cw, ROW_H);
        ctx.clip();
        const right = this.columns[c].kind !== Kind.Text;
        ctx.textAlign = right ? 'right' : 'left';
        const value = cellText(batch, c, local);
        if (value === null) {
          // NULL is a value of its own: dim and italic so it can never be mistaken for the text "NULL", 0 or ''.
          ctx.fillStyle = th.dim;
          ctx.font = NULL_FONT;
          ctx.fillText('NULL', right ? x + cw - 8 : x + 8, y + ROW_H / 2);
          ctx.font = FONT;
          ctx.fillStyle = th.fg;
        } else {
          ctx.fillText(this.fitText(value, cw - PAD * 2), right ? x + cw - PAD : x + PAD, y + ROW_H / 2);
        }
        ctx.restore();
      }
    }

    // Sticky row-number gutter.
    ctx.textAlign = 'right';
    ctx.fillStyle = th.headerBg;
    ctx.fillRect(0, HEADER_H, GUTTER_W, h);
    ctx.fillStyle = th.dim;
    for (let r = first; r <= last; r++) ctx.fillText(String(r + 1), GUTTER_W - 8, HEADER_H + r * ROW_H - sy + ROW_H / 2);

    // Sticky header.
    ctx.fillStyle = th.headerBg;
    ctx.fillRect(0, 0, w, HEADER_H);
    ctx.font = HEADER_FONT;
    ctx.textAlign = 'left';
    ctx.fillStyle = th.fg;
    for (let c = 0; c < nCols; c++) {
      const x = GUTTER_W + this.colX[c] - sx;
      if (x + 40 < GUTTER_W || x > w) continue;
      ctx.save();
      ctx.beginPath();
      ctx.rect(Math.max(x, GUTTER_W), 0, (this.colX[c + 1] ?? this.totalW - GUTTER_W) - this.colX[c], HEADER_H);
      ctx.clip();
      const cw = (this.colX[c + 1] ?? this.totalW - GUTTER_W) - this.colX[c];
      ctx.fillText(this.fitText(this.columns[c].name, cw - PAD * 2, 3), x + PAD, HEADER_H / 2); // proportional font: 'i' is narrow
      ctx.restore();
      // A faint divider at each column's edge: it is also where the column is dragged to resize.
      ctx.strokeStyle = th.line;
      ctx.beginPath();
      ctx.moveTo(x + cw - 0.5, 6); ctx.lineTo(x + cw - 0.5, HEADER_H - 6);
      ctx.stroke();
    }
    ctx.fillStyle = th.headerBg;
    ctx.fillRect(0, 0, GUTTER_W, HEADER_H);
    ctx.strokeStyle = th.line;
    ctx.beginPath();
    ctx.moveTo(0, HEADER_H - 0.5); ctx.lineTo(w, HEADER_H - 0.5);
    ctx.moveTo(GUTTER_W - 0.5, 0); ctx.lineTo(GUTTER_W - 0.5, h);
    ctx.stroke();

    this.drawTimes.push(performance.now() - t0);
    if (this.drawTimes.length > 240) this.drawTimes.shift();
  }
}
