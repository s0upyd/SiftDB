// The record view (design section 14, F8): up to five rows of a result shown down the page, column name → value, one
// column per row, so a 60-column row reads top to bottom. With two or more rows it is a comparison: the columns whose
// values differ are marked and counted, and can be shown alone. Values keep the result's styling rules, foreign-key
// columns keep their links (F4), and long values open in the value viewer. The rows come from the result's store; this
// draws them and never changes the result.

import type { Column } from './bridge';
import type { Cell } from './grid';
import type { CellStyler } from './styles';
import { toTsv, toast, writeClipboard } from './copy';
import { showValue } from './value-viewer';

/** How many rows the record view shows at most: more no longer reads as one record per column. */
export const MAX_RECORD_ROWS = 5;

/** What the record view reads from its result. Rows and columns are the result's own indexes. */
export interface RecordSource {
  columns: Column[];
  stylers: (CellStyler | null)[];
  /** Rows loaded so far: how far ◀ ▶ can step. */
  rowCount: number;
  /** A value, null for NULL, undefined when not loaded. */
  value(row: number, col: number): string | null | undefined;
  /** Following foreign keys (design section 14, F4): which columns link out, and which are keys others point at. */
  isLink(col: number): boolean;
  isRef(col: number): boolean;
  peek(cell: Cell): Promise<HTMLElement | null>;
  open(cell: Cell): void;
  refs(cell: Cell, x: number, y: number): void;
}

const LONG_LINES = 6, LONG_CHARS = 400;
const isLong = (v: string) => v.length > LONG_CHARS || v.split('\n').length > LONG_LINES;

/**
 * The text a comparison uses: for Integer and Decimal columns, the number with leading zeros, trailing fraction zeros and
 * "+" taken off (so 1.0 = 1 and 007 = 7); anything else as it is. Exact, unlike parsing to a float.
 */
export function comparable(value: string | null, type: Column['type']): string | null {
  if (value === null || (type !== 'integer' && type !== 'decimal')) return value;
  const m = /^\s*([+-]?)(\d*)(?:\.(\d*))?\s*$/.exec(value);
  if (!m || (m[2] === '' && (m[3] ?? '') === '')) return value; // NaN, Infinity, an exponent: as written
  const whole = m[2].replace(/^0+(?=\d)/, '') || '0';
  const fraction = (m[3] ?? '').replace(/0+$/, '');
  const zero = /^0*$/.test(whole) && fraction === '';
  return `${m[1] === '-' && !zero ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
}

/** The values are not all the same, by `comparable` (two NULLs are equal; NULL and "" are not). */
export function differs(values: (string | null | undefined)[], type: Column['type']): boolean {
  const seen = values.map((v) => (v === undefined ? undefined : comparable(v, type)));
  return seen.some((v) => v !== seen[0]);
}

const SVG = 'http://www.w3.org/2000/svg';
/** The grid's link arrows, drawn rather than typed: ↗ follows the link, ↙ asks what references the row. */
function arrow(kind: 'link' | 'refs'): SVGSVGElement {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 10 10');
  svg.setAttribute('width', '10');
  svg.setAttribute('height', '10');
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(SVG, 'path');
  p.setAttribute('d', kind === 'link' ? 'M2 8L8 2M3.5 2H8v4.5' : 'M8 2L2 8M6.5 8H2V3.5');
  p.setAttribute('fill', 'none');
  p.setAttribute('stroke', 'currentColor');
  p.setAttribute('stroke-width', '1.5');
  p.setAttribute('stroke-linecap', 'round');
  svg.append(p);
  return svg;
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className = '', text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

/** A label for a generic type, greyed beside the column's name. */
const TYPE_LABEL: Record<string, string> = { text: 'text', integer: 'integer', decimal: 'decimal', boolean: 'boolean', date: 'date', dateTime: 'date & time' };

export class RecordView {
  readonly el = el('div', 'recordview');
  private src: RecordSource | null = null;
  private rows: number[] = [];
  private filter = '';
  private onlyDiff = false;
  private tip: HTMLElement | null = null;
  private tipTimer = 0;

  /** `toGrid`: Space or Escape here goes back to the grid. */
  constructor(private readonly toGrid: () => void) {
    this.el.tabIndex = 0;
    this.el.setAttribute('role', 'region');
    this.el.setAttribute('aria-label', 'Record view');
    this.el.addEventListener('keydown', (e) => this.onKey(e));
  }

  /** The result's rows shown now. */
  get shownRows(): number[] { return this.rows; }

  show(src: RecordSource, rows: number[]) {
    this.src = src;
    this.rows = rows.slice(0, MAX_RECORD_ROWS);
    this.render();
  }

  /** The styling rules or the result changed: draw again with what is there now. */
  refresh(src: RecordSource) { if (this.src) { this.src = src; this.render(); } }

  focus() { this.el.focus({ preventScroll: true }); }

  dispose() { this.hideTip(); this.el.remove(); }

  // ---- stepping -------------------------------------------------------------------------------------------------

  /** ◀ ▶ and PgUp / PgDn: the previous or next rows of the result, as many as are shown. */
  private step(direction: -1 | 1) {
    const src = this.src;
    if (!src || !this.rows.length) return;
    const n = this.rows.length;
    const first = Math.max(0, Math.min(this.rows[0] + direction * n, src.rowCount - n));
    if (first === this.rows[0]) return;
    this.rows = Array.from({ length: n }, (_, i) => first + i);
    this.render();
  }

  private onKey(e: KeyboardEvent) {
    if (e.target instanceof HTMLInputElement) return; // typing in the filter box
    const plain = !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey;
    if (plain && e.key === 'PageUp') { e.preventDefault(); this.step(-1); }
    else if (plain && e.key === 'PageDown') { e.preventDefault(); this.step(1); }
    else if (plain && (e.key === ' ' || e.key === 'Escape')) { e.preventDefault(); this.toGrid(); }
    else if (e.ctrlKey && !e.altKey && e.key.toLowerCase() === 'c' && !window.getSelection()?.toString()) { e.preventDefault(); void this.copy(); }
  }

  // ---- drawing --------------------------------------------------------------------------------------------------

  private render() {
    const src = this.src;
    if (!src) return;
    this.hideTip();
    const rows = this.rows;
    const several = rows.length > 1;
    const columns = src.columns.map((c, i) => ({ c, i }));
    const changed = new Set(several
      ? columns.filter(({ c, i }) => differs(rows.map((r) => src.value(r, i)), c.type)).map(({ i }) => i)
      : []);
    const needle = this.filter.trim().toLowerCase();
    const shown = columns.filter(({ c, i }) => (!needle || c.name.toLowerCase().includes(needle)) && (!this.onlyDiff || changed.has(i)));

    // The bar: stepping, where these rows are, the filter, the differences, Copy.
    const bar = el('div', 'recordbar');
    const prev = el('button', 'ib', '◀');
    prev.title = `Previous ${several ? `${rows.length} rows` : 'row'} (PgUp)`;
    prev.setAttribute('aria-label', prev.title);
    prev.disabled = rows[0] === 0;
    prev.onclick = () => this.step(-1);
    const next = el('button', 'ib', '▶');
    next.title = `Next ${several ? `${rows.length} rows` : 'row'} (PgDn)`;
    next.setAttribute('aria-label', next.title);
    next.disabled = rows[rows.length - 1] >= src.rowCount - 1;
    next.onclick = () => this.step(1);
    const where = el('span', 'recordwhere',
      `${several ? `Rows ${rows[0] + 1}–${rows[rows.length - 1] + 1}` : `Row ${rows[0] + 1}`} of ${src.rowCount.toLocaleString()}`);
    const filter = el('input', 'recordfilter');
    filter.type = 'search';
    filter.placeholder = 'Filter columns';
    filter.setAttribute('aria-label', 'Show only the columns whose name contains');
    filter.value = this.filter;
    filter.oninput = () => {
      this.filter = filter.value;
      const at = filter.selectionStart;
      this.render();
      const again = this.el.querySelector<HTMLInputElement>('.recordfilter');
      again?.focus();
      if (at !== null) again?.setSelectionRange(at, at);
    };
    bar.append(prev, next, where, filter);
    if (several) {
      const count = el('span', 'recorddiff', `${changed.size} of ${columns.length} columns differ`);
      const only = el('label', 'recordonly');
      const box = el('input');
      box.type = 'checkbox';
      box.checked = this.onlyDiff;
      box.onchange = () => { this.onlyDiff = box.checked; this.render(); };
      only.append(box, ' Only differences');
      bar.append(count, only);
    }
    bar.append(el('span', 'grow'));
    const copy = el('button', '', 'Copy');
    copy.title = several ? 'Copy as a table: the column names, then one column per row (Ctrl+C)' : 'Copy as name: value lines (Ctrl+C)';
    copy.onclick = () => void this.copy();
    bar.append(copy);

    // The table: the name (with its type greyed), then one column per row, headed with its row number.
    const table = el('table', 'recordtable');
    const head = table.createTHead().insertRow();
    head.append(el('th', 'recordname', 'Column'), ...rows.map((r) => {
      const th = el('th', '', `Row ${r + 1}`);
      th.scope = 'col';
      return th;
    }));
    const body = table.createTBody();
    for (const { c, i } of shown) {
      const tr = body.insertRow();
      if (changed.has(i)) {
        tr.className = 'differs';
        tr.title = 'The values differ';
      }
      const name = el('th', 'recordname');
      name.scope = 'row';
      name.append(el('span', '', c.name));
      if (c.type) name.append(el('span', 'recordtype', TYPE_LABEL[c.type] ?? c.type));
      tr.append(name);
      for (const r of rows) tr.append(this.cell(src, r, i));
    }
    if (!shown.length) {
      const td = body.insertRow().insertCell();
      td.colSpan = rows.length + 1;
      td.className = 'recordnone';
      td.textContent = this.onlyDiff && !needle ? 'No differences: every column has the same values.' : 'No column matches the filter.';
    }
    this.el.replaceChildren(bar, el('div', 'recordscroll'));
    (this.el.lastElementChild as HTMLElement).append(table);
  }

  private cell(src: RecordSource, row: number, col: number): HTMLTableCellElement {
    const td = el('td');
    const v = src.value(row, col);
    const box = el('div', 'recordval');
    if (v === undefined) { box.textContent = 'not loaded yet'; box.classList.add('null'); td.append(box); return td; }
    if (v === null) { box.textContent = 'NULL'; box.classList.add('null'); }
    else box.textContent = v;
    // Result styling (design section 10): the cell's own colours, as in the grid. The difference mark is the bar in the
    // margin, so it never clashes with them.
    const style = src.stylers[col]?.(v) ?? null;
    if (style) {
      td.style.color = style.text;
      td.style.background = style.background;
      if (style.bold) td.style.fontWeight = '600';
    }
    td.append(box);
    const name = src.columns[col].name;
    if (v !== null && isLong(v)) {
      box.classList.add('long');
      const more = el('button', 'linkbtn recordmore', 'Show whole value');
      more.title = 'Open the whole value (JSON is laid out)';
      more.onclick = () => void showValue({ column: name, row: row + 1, value: v });
      td.append(more);
    }
    // Following foreign keys (design section 14, F4), as in the grid: hover for the row it points at, ↗ to open it, ↙ to
    // ask what references this row. NULLs point nowhere.
    if (v !== null && (src.isLink(col) || src.isRef(col))) {
      td.classList.add('haslinks'); // room for the arrows, so they never cover the value
      const acts = el('span', 'recordlinks');
      const cell = { row, col };
      if (src.isLink(col)) {
        box.classList.add('link');
        box.addEventListener('pointerenter', (e) => this.peekSoon(src, cell, e.clientX, e.clientY));
        box.addEventListener('pointerleave', () => this.hideTip());
        box.addEventListener('click', (e) => { if (e.ctrlKey) { e.preventDefault(); src.open(cell); } });
        const b = el('button', 'ib recordlink');
        b.title = 'Open the row this points at (Ctrl+click the value)';
        b.setAttribute('aria-label', `Open the row ${name} points at`);
        b.append(arrow('link'));
        b.onclick = () => src.open(cell);
        acts.append(b);
      }
      if (src.isRef(col)) {
        const b = el('button', 'ib recordlink');
        b.title = 'What references this row?';
        b.setAttribute('aria-label', 'What references this row?');
        b.append(arrow('refs'));
        b.onclick = (e) => src.refs(cell, e.clientX, e.clientY);
        acts.append(b);
      }
      td.append(acts);
    }
    return td;
  }

  // ---- the link card --------------------------------------------------------------------------------------------

  private peekSoon(src: RecordSource, cell: Cell, x: number, y: number) {
    this.hideTip();
    this.tipTimer = window.setTimeout(async () => {
      const card = await src.peek(cell);
      if (!card || !this.el.isConnected) return;
      document.body.append(card);
      const r = card.getBoundingClientRect();
      card.style.left = `${x + 12 + r.width > window.innerWidth - 4 ? Math.max(4, x - 12 - r.width) : x + 12}px`;
      card.style.top = `${y + 18 + r.height > window.innerHeight - 4 ? Math.max(4, y - 8 - r.height) : y + 18}px`;
      this.tip = card;
    }, 400);
  }

  private hideTip() {
    window.clearTimeout(this.tipTimer);
    this.tip?.remove();
    this.tip = null;
  }

  // ---- copy -----------------------------------------------------------------------------------------------------

  /** One row: "name: value" lines. Several: a table, the names first, then one column per row (pastes into a sheet). */
  private async copy() {
    const src = this.src;
    if (!src) return;
    const cols = src.columns.map((c, i) => ({ c, i }));
    let text: string;
    if (this.rows.length === 1) {
      const r = this.rows[0];
      text = cols.map(({ c, i }) => { const v = src.value(r, i); return `${c.name}: ${v === null ? 'NULL' : v ?? ''}`; }).join('\n');
    } else {
      text = toTsv(cols.map(({ c, i }) => [c.name, ...this.rows.map((r) => src.value(r, i) ?? null)]),
        ['column', ...this.rows.map((r) => `Row ${r + 1}`)]);
    }
    const ok = await writeClipboard(text);
    toast(this.el, ok ? `Copied ${this.rows.length === 1 ? 'the record' : `${this.rows.length} records`}` : 'Could not copy', ok ? 'info' : 'warn');
  }
}
