import { BatchMeta, Bridge, BridgeEvent, Column } from './bridge';
import { Grid, GridTheme, darkTheme, lightTheme } from './grid';
import { ResultStore, decodeBatch } from './store';

const MAX_OUTSTANDING = 3;
const LOOKAHEAD_ROWS = 100_000;

export const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;
const ms = (v: number) => (v < 0 ? '–' : `${v.toFixed(0)} ms`);

/** What a result tab needs from the sheet that owns it. */
export interface ResultsContext {
  bridge: Bridge;
  /** Whether to fetch the whole result eagerly rather than only what is scrolled into view. */
  fetchAll(): boolean;
  registerQuery(queryId: number, view: ResultView): void;
  unregisterQuery(queryId: number): void;
  /** The statement finished, failed or was cancelled: the sheet can offer Run again. */
  onFinished(view: ResultView): void;
}

/** A short tab label from the start of the SQL: whitespace collapsed, clipped. */
export function sqlLabel(sql: string): string {
  const flat = sql.replace(/\s+/g, ' ').trim();
  return flat.length > 28 ? flat.slice(0, 27) + '…' : flat || '(empty)';
}

/**
 * One result tab: a grid fed by one running statement. It owns that statement's streaming (a few fetches in flight, more
 * as the user scrolls), the rows received, and the numbers shown under the tab. Engine events and row batches for its
 * query id are routed here by the app.
 */
export class ResultView {
  readonly panel = document.createElement('div');
  readonly grid: Grid;
  label = '';
  /** A pinned result survives the next run; an unpinned one is replaced by it. */
  pinned = false;
  queryId = 0;

  private readonly statsEl = document.createElement('div');
  private readonly gridEl = document.createElement('div');
  private store = new ResultStore();
  private columnCount = 0;
  private outstanding = 0;
  private done = true;
  private m = this.freshMetrics();
  /** The outcome of the last scroll benchmark, shown under the tab. */
  bench = '';

  constructor(private ctx: ResultsContext, dark: boolean) {
    this.panel.className = 'resultpanel';
    this.statsEl.className = 'stats';
    this.gridEl.className = 'grid';
    this.panel.append(this.statsEl, this.gridEl);
    this.grid = new Grid(this.gridEl);
    this.setTheme(dark);
    this.grid.onViewChange = () => this.pump();
    this.grid.onFrame = () => {
      if (this.m.firstPaint < 0 && this.m.firstBatch >= 0 && this.store.loadedRows > 0) {
        this.m.firstPaint = performance.now() - this.m.t0;
      }
    };
  }

  get running(): boolean { return !this.done; }
  get gridElement(): HTMLElement { return this.gridEl; }

  setTheme(dark: boolean) {
    this.grid.theme = (dark ? darkTheme : lightTheme) as GridTheme;
    this.grid.invalidate();
  }

  private freshMetrics() {
    return {
      t0: 0, columns: -1, firstBatch: -1, firstPaint: -1, complete: -1,
      bytes: 0, batches: 0, rows: 0, engineFirst: -1, engineTotal: -1, engineSplit: '', status: '',
    };
  }

  /** Runs `sql` on a session, replacing whatever this tab showed. */
  async run(connectionId: number, sql: string) {
    if (this.queryId) this.ctx.unregisterQuery(this.queryId); // events for the statement it replaces no longer matter
    this.queryId = 0;
    this.label = sqlLabel(sql);
    this.panel.title = sql;
    const t0 = performance.now();
    this.m = { ...this.freshMetrics(), t0, status: 'running' };
    this.store = new ResultStore();
    this.columnCount = 0;
    this.outstanding = 0;
    this.done = false;
    this.bench = '';
    this.grid.setStore(this.store, []);

    const r = await this.ctx.bridge.request({ type: 'execute', connectionId, sql });
    if (r.type === 'error') {
      this.done = true;
      this.m.status = `error: ${r.message}`;
      this.ctx.onFinished(this);
      return;
    }
    this.queryId = r.queryId!;
    this.outstanding = 0; // nothing can be in flight for a query that has only just started
    this.ctx.registerQuery(this.queryId, this);
    this.m.columns = performance.now() - t0;
    this.columnCount = (r.columns as Column[]).length;
    this.grid.setStore(this.store, r.columns as Column[]);
    this.pump();
  }

  cancel() {
    if (!this.done && this.queryId) this.ctx.bridge.send({ type: 'cancel', queryId: this.queryId });
  }

  /** Keep a few fetches in flight. When not "fetch all" and nowhere near the end of what has loaded, stop. */
  private pump() {
    // Until the engine has answered "execute" there is no query id to fetch from. (The grid's resize observer calls this
    // as soon as the tab exists, which is before that answer arrives.)
    if (!this.queryId) return;
    while (!this.done && this.outstanding < MAX_OUTSTANDING &&
           (this.ctx.fetchAll() || this.store.loadedRows < this.grid.lastVisibleRow + LOOKAHEAD_ROWS)) {
      this.outstanding++;
      this.ctx.bridge.send({ type: 'fetchBatch', queryId: this.queryId });
    }
  }

  private finish() {
    this.done = true;
    this.ctx.onFinished(this);
  }

  onBatch(meta: BatchMeta, data: ArrayBuffer) {
    this.outstanding--;
    this.store.add(meta.seq, decodeBatch(data, meta.startRow));
    this.m.bytes += data.byteLength;
    this.m.batches++;
    if (this.m.firstBatch < 0) this.m.firstBatch = performance.now() - this.m.t0;
    this.grid.invalidate();
    this.pump();
  }

  onEvent(e: BridgeEvent) {
    if (e.type === 'complete') {
      this.outstanding--;
      if (this.done) return;
      this.m.complete = performance.now() - this.m.t0;
      this.m.rows = e.totalRows;
      this.m.engineFirst = e.engineFirstBatchMs;
      this.m.engineTotal = e.engineTotalMs;
      this.m.engineSplit =
        `read ${e.engineReadMs.toFixed(0)} ms · encode ${e.engineAppendMs.toFixed(0)} ms · blocked on UI ${e.engineWriteWaitMs.toFixed(0)} ms`;
      // A statement with no result set (INSERT, UPDATE, DELETE) says what it did instead.
      this.m.status = e.rowsAffected != null && this.columnCount === 0 ? `complete · ${plural(e.rowsAffected, 'row')} affected` : 'complete';
      this.grid.setRowHint(e.totalRows);
      this.finish();
    } else if (e.type === 'cancelled') {
      this.outstanding--;
      this.m.status = 'cancelled';
      this.finish();
    } else if (e.type === 'error') {
      this.outstanding--;
      this.m.status = `error: ${e.message}`;
      this.finish();
    }
  }

  /** Redraws the numbers under the tab. Cheap; called a few times a second for the visible result only. */
  refreshStats() {
    if (this.panel.hidden) return;
    const m = this.m;
    const heap = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory?.usedJSHeapSize;
    const d = this.grid.drawStats;
    this.statsEl.textContent =
      `status: ${m.status || 'idle'} · loaded ${this.store.loadedRows.toLocaleString()} rows · ${m.batches} batches · ${(m.bytes / 1048576).toFixed(1)} MB` +
      (heap ? ` · JS heap ${(heap / 1048576).toFixed(0)} MB` : '') + '\n' +
      `columns ${ms(m.columns)} · first batch ${ms(m.firstBatch)} · first paint ${ms(m.firstPaint)} · transfer complete ${ms(m.complete)}` +
      ` · engine first/total ${ms(m.engineFirst)}/${ms(m.engineTotal)}\n` +
      (m.engineSplit ? `engine split: ${m.engineSplit}\n` : '') +
      `draw avg ${d.avg.toFixed(2)} ms, max ${d.max.toFixed(1)} ms` + (this.bench ? ` · scroll: ${this.bench}` : '');
  }

  /** Scrolls top to bottom over a fixed time and records frame intervals. */
  runScrollBenchmark() {
    const wrap = this.gridEl;
    const span = wrap.scrollHeight - wrap.clientHeight;
    if (span <= 0) { this.bench = 'nothing to scroll'; return; }
    const DURATION = 6000;
    const frames: number[] = [];
    const start = performance.now();
    let prev = start;
    this.bench = 'running…';
    const step = (now: number) => {
      frames.push(now - prev);
      prev = now;
      const t = Math.min(1, (now - start) / DURATION);
      wrap.scrollTop = span * t;
      if (t < 1) { requestAnimationFrame(step); return; }
      frames.shift();
      frames.sort((a, b) => a - b);
      const q = (p: number) => frames[Math.min(frames.length - 1, Math.floor(frames.length * p))];
      const slow = frames.filter((f) => f > 20).length;
      this.bench = `${frames.length} frames over ${DURATION / 1000}s · p50 ${q(0.5).toFixed(1)}ms · p95 ${q(0.95).toFixed(1)}ms · ` +
        `max ${frames[frames.length - 1].toFixed(1)}ms · >20ms: ${slow}`;
    };
    requestAnimationFrame(step);
  }

  dispose() {
    this.cancel();
    if (this.queryId) this.ctx.unregisterQuery(this.queryId);
    this.queryId = 0;
    this.panel.remove();
  }
}
