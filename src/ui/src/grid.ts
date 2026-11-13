import { Column, Kind } from './bridge';
import { announce, toTsv, toast, writeClipboard } from './copy';
import { ExportFormat, ExportSource, exportAll } from './export';
import { showMenu } from './menu';
import { ResultStore, cellText } from './store';
import { CellStyler, rulesVersion, stylersFor } from './styles';
import { showValue } from './value-viewer';

const ROW_H = 22;
const HEADER_H = 28;
const GUTTER_W = 76;
const FONT = '13px Consolas, "Cascadia Mono", monospace';
const NULL_FONT = 'italic 12px Consolas, "Cascadia Mono", monospace';
const BOLD_FONT = 'bold 13px Consolas, "Cascadia Mono", monospace';
const BOLD_NULL_FONT = 'italic bold 12px Consolas, "Cascadia Mono", monospace';
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
/** Hovering a cut-off value this long shows it whole in a tooltip. */
const TIP_DELAY = 450;
/** Characters a tooltip shows; the value viewer shows the rest. */
const TIP_CHARS = 1500;
/** More cells than this are not put on the clipboard in one go: building the text would freeze the page. */
const MAX_COPY_CELLS = 2_000_000;

interface Cell { row: number; col: number }
/**
 * What is selected: one rectangle, from the anchor (where it started) to the focus (the cell that moves). Whole rows and
 * whole columns are kept as such, so a column selected while rows are still arriving covers the new rows too.
 */
interface Selection { anchor: Cell; focus: Cell; mode: 'cells' | 'rows' | 'cols' | 'all' }
/** Inclusive bounds. */
export interface CellRange { r0: number; r1: number; c0: number; c1: number }
type Area = 'cell' | 'header' | 'gutter' | 'corner';

/**
 * `sel` tints the selection while the grid has focus, `selIdle` when it does not, so it is clear where Ctrl+C goes. Both
 * are see-through, so a styled cell keeps its colours under them.
 */
export interface GridTheme {
  bg: string; altBg: string; fg: string; dim: string; headerBg: string; line: string;
  accent: string; sel: string; selIdle: string;
}

export const lightTheme: GridTheme = {
  bg: '#ffffff', altBg: '#f6f8fa', fg: '#1f2328', dim: '#8c959f', headerBg: '#eaeef2', line: '#d0d7de',
  accent: '#0969da', sel: 'rgba(9,105,218,0.16)', selIdle: 'rgba(110,119,129,0.18)',
};
export const darkTheme: GridTheme = {
  bg: '#0d1117', altBg: '#151b23', fg: '#e6edf3', dim: '#6e7681', headerBg: '#1c232c', line: '#30363d',
  accent: '#4493f8', sel: 'rgba(68,147,248,0.26)', selIdle: 'rgba(139,148,158,0.22)',
};

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/**
 * Read-only canvas grid. Only the visible rows are ever drawn; scrolling uses a native scroller + sticky canvas.
 *
 * Cells can be selected (click, drag, Shift+click, the row numbers, the headers, the keyboard) and copied as
 * tab-separated text. A value cut off with "…" shows whole in a tooltip on hover, and in the value viewer on a
 * double-click or Enter.
 */
export class Grid {
  private ctx: CanvasRenderingContext2D;
  private spacer = document.createElement('div');
  private canvas = document.createElement('canvas');
  private columns: Column[] = [];
  /** Per column, the styling rules that apply to it (null: none), matched once per result and again when the rules change. */
  private stylers: (CellStyler | null)[] = [];
  private stylersVersion = -1;
  private colX: number[] = [];
  /** Each column's width. Fitted to the content when the first rows arrive, unless the user has dragged it. */
  private widths: number[] = [];
  private manual: boolean[] = [];
  private fitted = false;
  /** Width of one character of the (monospaced) cell font: lets long values be cut without measuring all of them. */
  private charW = 8;
  private drag: { col: number; startX: number; startW: number } | null = null;
  private sel: Selection | null = null;
  /** A selection being dragged out with the mouse; `x`/`y` is the last pointer position, for scrolling at the edges. */
  private selDrag: { area: Area; x: number; y: number } | null = null;
  private autoScroll = 0;
  private focused = false;
  /** Set while a click gives the grid focus, so focusing does not pick a cell of its own first. */
  private clicking = false;
  private tip: HTMLDivElement | null = null;
  private tipTimer = 0;
  /** "row:col" of the cell the tooltip is for (or waiting on), so moving within one cell does not restart it. */
  private tipCell = '';
  private totalW = GUTTER_W;
  private store = new ResultStore();
  private rowHint = 0;
  private scheduled = false;
  private drawTimes: number[] = [];
  theme: GridTheme = lightTheme;
  onViewChange: () => void = () => {};
  /** Runs after each frame; used to time first paint. */
  onFrame: () => void = () => {};
  /** The menu's Export items: the whole result (null) or a rectangle of it. The result view owns exporting. */
  onExport: (range: CellRange | null) => void = () => {};
  /** Whether Export can be offered now (the statement has finished). */
  canExport: () => boolean = () => false;

  constructor(private wrap: HTMLElement) {
    wrap.style.position = 'relative';
    wrap.style.overflow = 'auto';
    this.canvas.style.cssText = 'position:sticky;top:0;left:0;display:block';
    this.spacer.style.cssText = 'position:absolute;top:0;left:0;width:1px;height:1px;pointer-events:none';
    wrap.append(this.canvas, this.spacer);
    wrap.tabIndex = 0;
    wrap.setAttribute('aria-roledescription', 'results grid');
    wrap.setAttribute('aria-label',
      'Results. Arrow keys move, Shift extends the selection, Ctrl+C copies, Enter shows the whole value, Shift+F10 opens the menu.');
    this.ctx = this.canvas.getContext('2d', { alpha: false })!;
    wrap.addEventListener('scroll', () => { this.hideTip(); this.invalidate(); this.onViewChange(); }, { passive: true });
    new ResizeObserver(() => { this.invalidate(); this.onViewChange(); }).observe(wrap);
    this.ctx.font = FONT;
    this.charW = this.ctx.measureText('0').width || 8;
    this.initResizing();
    this.initSelecting();
    this.initTooltip();
    wrap.addEventListener('keydown', (e) => this.onKey(e));
    wrap.addEventListener('focus', () => {
      this.focused = true;
      // Tabbing in with nothing selected starts at the top-left cell in view, so the keys have something to move. A click
      // selects what it is on instead.
      if (!this.sel && !this.clicking && this.rowCount > 0 && this.columns.length > 0) {
        this.select({ row: Math.ceil(this.wrap.scrollTop / ROW_H), col: this.colAt(this.wrap.scrollLeft) });
      }
      this.invalidate();
    });
    wrap.addEventListener('blur', () => { this.focused = false; this.invalidate(); });
  }

  /** Rows the grid shows: all of them once the count is known, else those loaded so far. */
  private get rowCount() { return Math.max(this.store.loadedRows, this.rowHint); }

  // ---- column widths ------------------------------------------------------------------------------------------

  /** Drag a column's right edge in the header to resize it; double-click the edge to fit it to the loaded rows. */
  private initResizing() {
    const c = this.canvas;
    c.addEventListener('pointermove', (e) => {
      if (this.drag) {
        this.setWidth(this.drag.col, this.drag.startW + e.clientX - this.drag.startX);
        return;
      }
      if (this.selDrag) return;
      c.style.cursor = this.edgeAt(e.offsetX, e.offsetY) >= 0 ? 'col-resize' : '';
    });
    c.addEventListener('pointerdown', (e) => {
      const col = this.edgeAt(e.offsetX, e.offsetY);
      if (col < 0 || e.button !== 0) return;
      c.setPointerCapture(e.pointerId);
      this.drag = { col, startX: e.clientX, startW: this.widths[col] };
      e.preventDefault();
      e.stopImmediatePropagation(); // a resize, not a selection
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
      if (col >= 0) { this.setWidth(col, this.fitWidth(col, FIT_SAMPLE, MAX_FIT_W)); return; }
      const hit = this.hit(e.offsetX, e.offsetY);
      if (hit.area === 'cell' && hit.row < this.store.loadedRows) void this.openValue({ row: hit.row, col: hit.col });
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

  private colWidth(c: number) { return (this.colX[c + 1] ?? this.totalW - GUTTER_W) - this.colX[c]; }

  // ---- where the pointer is -----------------------------------------------------------------------------------

  /** The column at x px into the columns (scrolled coordinates), clamped to the first and last. */
  private colAt(x: number): number {
    let lo = 0, hi = this.columns.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (this.colX[mid] <= x) lo = mid; else hi = mid - 1;
    }
    return lo;
  }

  /**
   * What is under a point on the canvas. Row and column are clamped into the grid, so a drag past the edge (or a click
   * below the last row) lands on the nearest cell.
   */
  private hit(px: number, py: number): Cell & { area: Area } {
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    const inHeader = py < HEADER_H, inGutter = px < GUTTER_W;
    const y = clamp(py, HEADER_H, h - 1) - HEADER_H + this.wrap.scrollTop;
    const x = clamp(px, GUTTER_W, w - 1) - GUTTER_W + this.wrap.scrollLeft;
    const row = clamp(Math.floor(y / ROW_H), 0, Math.max(0, this.rowCount - 1));
    const col = clamp(this.colAt(x), 0, Math.max(0, this.columns.length - 1));
    const area: Area = inHeader && inGutter ? 'corner' : inHeader ? 'header' : inGutter ? 'gutter' : 'cell';
    return { row, col, area };
  }

  // ---- selection ----------------------------------------------------------------------------------------------

  /** The selected rectangle, or null when nothing is selected. */
  private range(): CellRange | null {
    const s = this.sel;
    if (!s || this.columns.length === 0 || this.rowCount === 0) return null;
    const lastRow = this.rowCount - 1, lastCol = this.columns.length - 1;
    const rows = s.mode === 'cols' || s.mode === 'all'
      ? [0, lastRow]
      : [Math.min(s.anchor.row, s.focus.row), Math.min(lastRow, Math.max(s.anchor.row, s.focus.row))];
    const cols = s.mode === 'rows' || s.mode === 'all'
      ? [0, lastCol]
      : [Math.min(s.anchor.col, s.focus.col), Math.min(lastCol, Math.max(s.anchor.col, s.focus.col))];
    return { r0: rows[0], r1: rows[1], c0: cols[0], c1: cols[1] };
  }

  /** Selects from `anchor` to `focus` (one cell when only one is given), scrolls the focus into view and says so. */
  private select(focus: Cell, anchor: Cell = focus, mode: Selection['mode'] = 'cells') {
    if (this.columns.length === 0 || this.rowCount === 0) return;
    const fit = (c: Cell) => ({ row: clamp(c.row, 0, this.rowCount - 1), col: clamp(c.col, 0, this.columns.length - 1) });
    this.sel = { anchor: fit(anchor), focus: fit(focus), mode };
    this.scrollIntoView(this.sel.focus, mode);
    this.invalidate();
    this.describeSelection();
  }

  selectAll() {
    if (this.columns.length === 0 || this.rowCount === 0) return;
    const focus = this.sel?.focus ?? { row: 0, col: 0 };
    this.sel = { anchor: { row: 0, col: 0 }, focus, mode: 'all' };
    this.invalidate();
    this.describeSelection();
  }

  /** For screen readers: the cell that has focus, or how much is selected. */
  private describeSelection() {
    const r = this.range();
    if (!r || !this.focused) return;
    const n = (r.r1 - r.r0 + 1) * (r.c1 - r.c0 + 1);
    if (n > 1) { announce(`${this.sizeText(r)} selected`); return; }
    const { row, col } = this.sel!.focus;
    const v = this.valueAt(row, col);
    const said = v === undefined ? 'not loaded yet' : v === null ? 'NULL' : v === '' ? 'empty' : v.length > 200 ? v.slice(0, 200) + '…' : v;
    announce(`Row ${row + 1}, ${this.columns[col].name}: ${said}`);
  }

  private sizeText(r: CellRange) {
    const rows = r.r1 - r.r0 + 1, cols = r.c1 - r.c0 + 1;
    const p = (n: number, w: string) => `${n.toLocaleString()} ${w}${n === 1 ? '' : 's'}`;
    return rows === 1 && cols === 1 ? '1 value' : `${p(rows, 'row')} × ${p(cols, 'column')}`;
  }

  /** The cell's text, null for NULL, undefined when its row has not arrived yet. */
  private valueAt(row: number, col: number): string | null | undefined {
    const b = this.store.batchFor(row);
    return b ? cellText(b, col, row - b.startRow) : undefined;
  }

  private scrollIntoView(c: Cell, mode: Selection['mode']) {
    const w = this.wrap, viewH = w.clientHeight - HEADER_H, viewW = w.clientWidth - GUTTER_W;
    if (mode !== 'cols') {
      const y = c.row * ROW_H;
      if (y < w.scrollTop) w.scrollTop = y;
      else if (y + ROW_H > w.scrollTop + viewH) w.scrollTop = y + ROW_H - viewH;
    }
    if (mode !== 'rows') {
      const x = this.colX[c.col], cw = this.colWidth(c.col);
      if (x < w.scrollLeft || cw > viewW) w.scrollLeft = x;
      else if (x + cw > w.scrollLeft + viewW) w.scrollLeft = x + cw - viewW;
    }
  }

  /**
   * Click a cell to select it; drag or Shift+click to select a rectangle. Click the row numbers for whole rows, a header
   * for whole columns, the corner for everything. Right-click opens the menu.
   */
  private initSelecting() {
    const c = this.canvas;
    c.addEventListener('pointerdown', (e) => {
      if (e.button !== 0 || this.columns.length === 0 || this.rowCount === 0) return;
      const hit = this.hit(e.offsetX, e.offsetY);
      this.focusForClick();
      e.preventDefault(); // no text selection, and keep the focus just given
      this.extendTo(hit, hit.area, e.shiftKey && this.sel !== null);
      c.setPointerCapture(e.pointerId);
      this.selDrag = { area: hit.area, x: e.offsetX, y: e.offsetY };
    });
    c.addEventListener('pointermove', (e) => {
      if (!this.selDrag) return;
      this.selDrag.x = e.offsetX;
      this.selDrag.y = e.offsetY;
      this.dragTo();
      if (!this.autoScroll) this.autoScroll = requestAnimationFrame(() => this.scrollWhileDragging());
    });
    const end = (e: PointerEvent) => {
      if (!this.selDrag) return;
      this.selDrag = null;
      cancelAnimationFrame(this.autoScroll);
      this.autoScroll = 0;
      c.releasePointerCapture?.(e.pointerId);
    };
    c.addEventListener('pointerup', end);
    c.addEventListener('pointercancel', end);
    c.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      if (this.columns.length === 0 || this.rowCount === 0) return;
      const hit = this.hit(e.offsetX, e.offsetY);
      this.focusForClick();
      const r = this.range();
      // Right-clicking inside the selection keeps it (to copy it); anywhere else selects what was clicked first.
      const inside = r && hit.area === 'cell' && hit.row >= r.r0 && hit.row <= r.r1 && hit.col >= r.c0 && hit.col <= r.c1;
      if (!inside) this.extendTo(hit, hit.area, false);
      this.openMenu(e.clientX, e.clientY);
    });
  }

  private focusForClick() {
    this.clicking = true;
    this.wrap.focus({ preventScroll: true });
    this.clicking = false;
  }

  /** A click (or Shift+click when `extend`) on a cell, row number, header or the corner. */
  private extendTo(hit: Cell, area: Area, extend: boolean) {
    const s = this.sel;
    if (area === 'corner') { this.selectAll(); return; }
    if (area === 'gutter') {
      const col = s?.focus.col ?? 0;
      this.select({ row: hit.row, col }, extend ? { row: s!.anchor.row, col } : { row: hit.row, col }, 'rows');
    } else if (area === 'header') {
      const row = s?.focus.row ?? Math.floor(this.wrap.scrollTop / ROW_H);
      this.select({ row, col: hit.col }, extend ? { row, col: s!.anchor.col } : { row, col: hit.col }, 'cols');
    } else {
      this.select(hit, extend ? s!.anchor : hit, 'cells');
    }
  }

  /** While dragging: the focus follows the pointer along the kind of thing the drag started on. */
  private dragTo() {
    const d = this.selDrag, s = this.sel;
    if (!d || !s || d.area === 'corner') return;
    const hit = this.hit(d.x, d.y);
    const focus = d.area === 'gutter' ? { row: hit.row, col: s.focus.col }
      : d.area === 'header' ? { row: s.focus.row, col: hit.col }
      : hit;
    if (focus.row === s.focus.row && focus.col === s.focus.col) return;
    this.select(focus, s.anchor, s.mode);
  }

  /** A drag held past the grid's edge keeps scrolling that way, faster the further out the pointer is. */
  private scrollWhileDragging() {
    this.autoScroll = 0;
    const d = this.selDrag;
    if (!d) return;
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    const dx = d.area === 'gutter' ? 0 : d.x < GUTTER_W ? d.x - GUTTER_W : d.x > w ? d.x - w : 0;
    const dy = d.area === 'header' ? 0 : d.y < HEADER_H ? d.y - HEADER_H : d.y > h ? d.y - h : 0;
    if (dx === 0 && dy === 0) return;
    this.wrap.scrollLeft += clamp(dx, -60, 60);
    this.wrap.scrollTop += clamp(dy, -60, 60);
    this.dragTo();
    this.autoScroll = requestAnimationFrame(() => this.scrollWhileDragging());
  }

  // ---- keyboard -----------------------------------------------------------------------------------------------

  private onKey(e: KeyboardEvent) {
    const ctrl = e.ctrlKey && !e.altKey, key = e.key.toLowerCase();
    const done = () => { e.preventDefault(); e.stopPropagation(); this.hideTip(); };
    if (ctrl && key === 'c') { done(); void this.copy(e.shiftKey ? 'tsvHeaders' : 'tsv'); return; }
    if (ctrl && !e.shiftKey && key === 'a') { done(); this.selectAll(); return; }
    if ((e.shiftKey && e.key === 'F10') || e.key === 'ContextMenu') { done(); this.openMenuAtFocus(); return; }
    if (e.key === 'Enter' && !e.ctrlKey && !e.altKey && !e.shiftKey) {
      if (this.sel) { done(); void this.openValue(this.sel.focus); }
      return;
    }
    if (e.altKey || this.columns.length === 0 || this.rowCount === 0) return;

    const s = this.sel ?? { anchor: { row: 0, col: 0 }, focus: { row: 0, col: 0 }, mode: 'cells' as const };
    const f = s.focus, lastRow = this.rowCount - 1, lastCol = this.columns.length - 1;
    const page = Math.max(1, Math.floor((this.wrap.clientHeight - HEADER_H) / ROW_H) - 1);
    let to: Cell;
    switch (e.key) {
      case 'ArrowUp': to = { row: ctrl ? 0 : f.row - 1, col: f.col }; break;
      case 'ArrowDown': to = { row: ctrl ? lastRow : f.row + 1, col: f.col }; break;
      case 'ArrowLeft': to = { row: f.row, col: ctrl ? 0 : f.col - 1 }; break;
      case 'ArrowRight': to = { row: f.row, col: ctrl ? lastCol : f.col + 1 }; break;
      case 'Home': to = { row: ctrl ? 0 : f.row, col: 0 }; break;
      case 'End': to = { row: ctrl ? lastRow : f.row, col: lastCol }; break;
      case 'PageUp': to = { row: f.row - page, col: f.col }; break;
      case 'PageDown': to = { row: f.row + page, col: f.col }; break;
      case 'Escape':
        if (!this.sel || s.mode === 'cells' && f.row === s.anchor.row && f.col === s.anchor.col) return;
        to = f; // back to just the focused cell
        break;
      default: return;
    }
    done();
    if (!e.shiftKey || !this.sel) { this.select(to); return; }
    // Shift extends. Whole rows stay whole rows while moving up and down, whole columns while moving sideways.
    const vertical = to.col === f.col;
    const mode = s.mode === 'rows' && vertical ? 'rows' : s.mode === 'cols' && !vertical ? 'cols' : 'cells';
    this.select(to, mode === 'cells' && s.mode !== 'cells' ? this.farCorner() : s.anchor, mode);
  }

  /**
   * Turning whole rows, whole columns or everything into a plain rectangle: the anchor becomes the corner of the current
   * selection opposite the focus, so the rectangle starts out as what was selected.
   */
  private farCorner(): Cell {
    const r = this.range()!, f = this.sel!.focus;
    return { row: f.row === r.r0 ? r.r1 : r.r0, col: f.col === r.c0 ? r.c1 : r.c0 };
  }

  // ---- copy, the menu and the value viewer --------------------------------------------------------------------

  /**
   * Copies the selection. Tab-separated by default (Excel pastes it into cells), with the column names first if asked;
   * or as CSV or JSON, the same text an export writes.
   */
  async copy(as: 'tsv' | 'tsvHeaders' | ExportFormat) {
    const r = this.range();
    if (!r) return;
    const loadedLast = Math.min(r.r1, this.store.loadedRows - 1);
    const rows = loadedLast - r.r0 + 1, cols = r.c1 - r.c0 + 1;
    if (rows <= 0) { toast(this.wrap, 'Nothing to copy yet: those rows have not arrived', 'warn'); return; }
    if (rows * cols > MAX_COPY_CELLS) {
      toast(this.wrap, `Too much to copy at once (${(rows * cols).toLocaleString()} values). Select up to ${MAX_COPY_CELLS.toLocaleString()}, or export.`, 'warn');
      return;
    }
    const src = this.exportSource({ ...r, r1: loadedLast });
    let text: string;
    if (as === 'csv' || as === 'json') {
      text = exportAll(as, src);
    } else {
      const out: (string | null)[][] = [];
      for (let row = r.r0; row <= loadedLast; row++) {
        const line: (string | null)[] = [];
        for (let c = r.c0; c <= r.c1; c++) line.push(src.cell(row, c));
        out.push(line);
      }
      text = toTsv(out, as === 'tsvHeaders' ? this.columns.slice(r.c0, r.c1 + 1).map((c) => c.name) : undefined);
    }
    if (!(await writeClipboard(text))) {
      toast(this.wrap, 'Could not copy: the clipboard is not available', 'warn');
      return;
    }
    const what = this.sizeText({ ...r, r1: loadedLast });
    const how = as === 'tsvHeaders' ? ' with column names' : as === 'csv' ? ' as CSV' : as === 'json' ? ' as JSON' : '';
    const partial = loadedLast < r.r1 ? ' (the rows loaded so far)' : '';
    toast(this.wrap, `Copied ${what}${how}${partial}`);
  }

  /**
   * The loaded rows of a rectangle (the whole result when none is given), for export. Read while it is written, so rows
   * still arriving are not part of it: the range is fixed when it is made.
   */
  exportSource(r?: CellRange): ExportSource & { rows: number } {
    const store = this.store, columns = this.columns;
    const range = r ?? { r0: 0, r1: store.loadedRows - 1, c0: 0, c1: columns.length - 1 };
    const r1 = Math.min(range.r1, store.loadedRows - 1);
    return {
      columns, r0: range.r0, r1, c0: range.c0, c1: range.c1, rows: Math.max(0, r1 - range.r0 + 1),
      cell: (row, col) => { const b = store.batchFor(row)!; return cellText(b, col, row - b.startRow); },
    };
  }

  /** The selected rectangle, for exporting just that. Null when nothing is selected. */
  get selection(): CellRange | null { return this.range(); }

  private openMenu(x: number, y: number) {
    const focus = this.sel?.focus;
    const r = this.range();
    const several = !!r && (r.r1 > r.r0 || r.c1 > r.c0);
    showMenu(x, y, [
      { label: 'Copy', shortcut: 'Ctrl+C', action: () => void this.copy('tsv') },
      { label: 'Copy with column names', shortcut: 'Ctrl+Shift+C', action: () => void this.copy('tsvHeaders') },
      { label: 'Copy as CSV', action: () => void this.copy('csv') },
      { label: 'Copy as JSON', action: () => void this.copy('json') },
      { label: 'Select all', shortcut: 'Ctrl+A', action: () => this.selectAll(), separatorBefore: true },
      {
        label: 'Show whole value', shortcut: 'Enter',
        disabled: !focus || focus.row >= this.store.loadedRows,
        action: () => { if (focus) void this.openValue(focus); },
      },
      { label: 'Export result…', separatorBefore: true, disabled: !this.canExport(), action: () => this.onExport(null) },
      { label: 'Export selection…', disabled: !several || !this.canExport(), action: () => this.onExport(this.range()) },
    ], 'Result actions');
  }

  /** The keyboard's way to the menu: at the focused cell. */
  private openMenuAtFocus() {
    const f = this.sel?.focus;
    const box = this.wrap.getBoundingClientRect();
    if (!f) { this.openMenu(box.left + GUTTER_W, box.top + HEADER_H); return; }
    const x = GUTTER_W + this.colX[f.col] - this.wrap.scrollLeft + 8;
    const y = HEADER_H + (f.row + 1) * ROW_H - this.wrap.scrollTop;
    this.openMenu(box.left + clamp(x, GUTTER_W, this.wrap.clientWidth - 8), box.top + clamp(y, HEADER_H, this.wrap.clientHeight - 8));
  }

  private async openValue(c: Cell) {
    const value = this.valueAt(c.row, c.col);
    if (value === undefined) return;
    this.hideTip();
    await showValue({ column: this.columns[c.col].name, row: c.row + 1, value });
    this.wrap.focus({ preventScroll: true });
  }

  // ---- tooltip ------------------------------------------------------------------------------------------------

  /** Hovering a value that does not fit (cut off with "…", or on several lines) shows it whole after a moment. */
  private initTooltip() {
    const c = this.canvas;
    c.addEventListener('pointermove', (e) => {
      if (this.drag || this.selDrag || e.buttons) { this.hideTip(); return; }
      const hit = this.hit(e.offsetX, e.offsetY);
      const over = hit.area === 'cell' && e.offsetY >= HEADER_H && e.offsetX >= GUTTER_W
        && e.offsetY - HEADER_H + this.wrap.scrollTop < this.rowCount * ROW_H
        && e.offsetX - GUTTER_W + this.wrap.scrollLeft < this.totalW - GUTTER_W;
      const key = over ? `${hit.row}:${hit.col}` : '';
      if (key === this.tipCell) return;
      this.hideTip();
      if (!key) return;
      this.tipCell = key;
      const { clientX, clientY } = e;
      this.tipTimer = window.setTimeout(() => this.showTip(hit, clientX, clientY), TIP_DELAY);
    });
    c.addEventListener('pointerleave', () => this.hideTip());
    c.addEventListener('pointerdown', () => this.hideTip());
  }

  private showTip(cell: Cell, x: number, y: number) {
    const v = this.valueAt(cell.row, cell.col);
    if (v === undefined || v === null) return;
    this.ctx.font = FONT;
    const cut = v.includes('\n') || this.fitText(v, this.colWidth(cell.col) - PAD * 2) !== v;
    if (!cut) return;
    const tip = document.createElement('div');
    tip.className = 'gridtip';
    tip.setAttribute('role', 'tooltip');
    const text = document.createElement('div');
    text.className = 'tiptext';
    text.textContent = v.length > TIP_CHARS ? v.slice(0, TIP_CHARS) + ELLIPSIS : v;
    tip.append(text);
    if (v.length > TIP_CHARS || v.split('\n').length > 16) {
      const more = document.createElement('div');
      more.className = 'tipmore';
      more.textContent = `${v.length.toLocaleString()} characters. Double-click to see all of it.`;
      tip.append(more);
    }
    document.body.append(tip);
    const r = tip.getBoundingClientRect();
    tip.style.left = `${x + 12 + r.width > window.innerWidth - 4 ? Math.max(4, x - 12 - r.width) : x + 12}px`;
    tip.style.top = `${y + 18 + r.height > window.innerHeight - 4 ? Math.max(4, y - 8 - r.height) : y + 18}px`;
    this.tip = tip;
  }

  private hideTip() {
    clearTimeout(this.tipTimer);
    this.tipCell = '';
    this.tip?.remove();
    this.tip = null;
  }

  // ---- the result ---------------------------------------------------------------------------------------------

  setStore(store: ResultStore, columns: Column[]) {
    this.store = store;
    this.columns = columns;
    this.stylersVersion = -1;
    this.manual = columns.map(() => false);
    this.fitted = false;
    this.sel = null;
    this.hideTip();
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
    const rows = this.rowCount;
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
    if (this.stylersVersion !== rulesVersion()) {
      this.stylers = stylersFor(this.columns);
      this.stylersVersion = rulesVersion();
    }

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
        const cw = this.colWidth(c);
        if (x + cw < GUTTER_W || x > w) continue;
        ctx.save();
        ctx.beginPath();
        ctx.rect(Math.max(x, GUTTER_W), y, cw, ROW_H);
        ctx.clip();
        const right = this.columns[c].kind !== Kind.Text;
        ctx.textAlign = right ? 'right' : 'left';
        const value = cellText(batch, c, local);
        // A styled cell carries its own background and text colour, so it reads the same in either theme.
        const style = this.stylers[c]?.(value) ?? null;
        if (style) {
          ctx.fillStyle = style.background;
          ctx.fillRect(x, y, cw, ROW_H);
        }
        if (value === null) {
          // NULL is a value of its own: dim and italic so it can never be mistaken for the text "NULL", 0 or ''.
          ctx.fillStyle = style?.text ?? th.dim;
          ctx.font = style?.bold ? BOLD_NULL_FONT : NULL_FONT;
          ctx.fillText('NULL', right ? x + cw - 8 : x + 8, y + ROW_H / 2);
        } else {
          if (style) { ctx.fillStyle = style.text; if (style.bold) ctx.font = BOLD_FONT; }
          ctx.fillText(this.fitText(value, cw - PAD * 2), right ? x + cw - PAD : x + PAD, y + ROW_H / 2);
        }
        ctx.font = FONT;
        ctx.fillStyle = th.fg;
        ctx.restore();
      }
    }

    // The selection: a see-through tint over the cells (styled cells keep their colours), an outline round it, and a
    // stronger outline on the focused cell. Grey while the grid does not have focus.
    const sel = this.range();
    if (sel) {
      const x0 = GUTTER_W + this.colX[sel.c0] - sx, x1 = GUTTER_W + this.colX[sel.c1] + this.colWidth(sel.c1) - sx;
      const y0 = HEADER_H + sel.r0 * ROW_H - sy, y1 = HEADER_H + (sel.r1 + 1) * ROW_H - sy;
      ctx.save();
      ctx.beginPath();
      ctx.rect(GUTTER_W, HEADER_H, w - GUTTER_W, h - HEADER_H);
      ctx.clip();
      ctx.fillStyle = this.focused ? th.sel : th.selIdle;
      ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
      ctx.strokeStyle = this.focused ? th.accent : th.dim;
      ctx.lineWidth = 1;
      ctx.strokeRect(x0 + 0.5, y0 + 0.5, x1 - x0 - 1, y1 - y0 - 1);
      if (this.focused) {
        const f = this.sel!.focus;
        const fx = GUTTER_W + this.colX[f.col] - sx, fy = HEADER_H + f.row * ROW_H - sy;
        ctx.lineWidth = 2;
        ctx.strokeRect(fx + 1, fy + 1, this.colWidth(f.col) - 2, ROW_H - 2);
      }
      ctx.restore();
    }

    // Sticky row-number gutter. Selected rows' numbers are tinted, so the selection can be read from the edge.
    ctx.textAlign = 'right';
    ctx.fillStyle = th.headerBg;
    ctx.fillRect(0, HEADER_H, GUTTER_W, h);
    if (sel) {
      ctx.fillStyle = this.focused ? th.sel : th.selIdle;
      const y0 = Math.max(HEADER_H, HEADER_H + sel.r0 * ROW_H - sy);
      const y1 = Math.min(h, HEADER_H + (sel.r1 + 1) * ROW_H - sy);
      if (y1 > y0) ctx.fillRect(0, y0, GUTTER_W, y1 - y0);
    }
    ctx.fillStyle = th.dim;
    for (let r = first; r <= last; r++) ctx.fillText(String(r + 1), GUTTER_W - 8, HEADER_H + r * ROW_H - sy + ROW_H / 2);

    // Sticky header.
    ctx.fillStyle = th.headerBg;
    ctx.fillRect(0, 0, w, HEADER_H);
    if (sel) {
      ctx.fillStyle = this.focused ? th.sel : th.selIdle;
      const x0 = Math.max(GUTTER_W, GUTTER_W + this.colX[sel.c0] - sx);
      const x1 = Math.min(w, GUTTER_W + this.colX[sel.c1] + this.colWidth(sel.c1) - sx);
      if (x1 > x0) ctx.fillRect(x0, 0, x1 - x0, HEADER_H);
    }
    ctx.font = HEADER_FONT;
    ctx.textAlign = 'left';
    ctx.fillStyle = th.fg;
    for (let c = 0; c < nCols; c++) {
      const x = GUTTER_W + this.colX[c] - sx;
      if (x + 40 < GUTTER_W || x > w) continue;
      const cw = this.colWidth(c);
      ctx.save();
      ctx.beginPath();
      ctx.rect(Math.max(x, GUTTER_W), 0, cw, HEADER_H);
      ctx.clip();
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
