import { Column, Kind } from './bridge';
import { ResultStore, cellText } from './store';

const ROW_H = 22;
const HEADER_H = 28;
const GUTTER_W = 76;
const FONT = '13px Consolas, "Cascadia Mono", monospace';
const NULL_FONT = 'italic 12px Consolas, "Cascadia Mono", monospace';
const HEADER_FONT ='600 13px "Segoe UI", sans-serif';

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
  }

  setStore(store: ResultStore, columns: Column[]) {
    this.store = store;
    this.columns = columns;
    this.colX = [];
    let x = 0;
    for (const c of columns) {
      this.colX.push(x);
      x += c.kind === Kind.Int32 ? 100 : c.kind === Kind.Int64 ? 140 : c.kind === Kind.Float64 ? 170 : 260;
    }
    this.totalW = GUTTER_W + x;
    this.rowHint = 0;
    this.wrap.scrollTop = 0;
    this.wrap.scrollLeft = 0;
    this.invalidate();
  }

  /** Total rows if known (query finished), otherwise the loaded count drives the scroll height. */
  setRowHint(rows: number) { this.rowHint = rows; this.invalidate(); }

  get lastVisibleRow(): number {
    return Math.ceil((this.wrap.scrollTop + this.wrap.clientHeight - HEADER_H) / ROW_H);
  }

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
          ctx.fillText(value, right ? x + cw - 8 : x + 8, y + ROW_H / 2);
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
      ctx.fillText(this.columns[c].name, x + 8, HEADER_H / 2);
      ctx.restore();
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
