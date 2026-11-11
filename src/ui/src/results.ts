import { BatchMeta, Bridge, BridgeEvent, Column } from './bridge';
import { Grid, GridTheme, darkTheme, lightTheme } from './grid';
import { ResultStore, decodeBatch } from './store';

const MAX_OUTSTANDING = 3;
const LOOKAHEAD_ROWS = 100_000;

export const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;
const ms = (v: number) => (v < 0 ? '–' : `${v.toFixed(0)} ms`);

/** 480 ms, 1.07 s, 2 min 05 s. */
export function formatDuration(milliseconds: number): string {
  if (milliseconds < 1000) return `${Math.max(0, Math.round(milliseconds))} ms`;
  if (milliseconds < 60_000) return `${(milliseconds / 1000).toFixed(2)} s`;
  const s = Math.round(milliseconds / 1000);
  return `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, '0')} s`;
}

/**
 * A server error arrives as one string: "42703: column "id" does not exist", a blank line, then lines such as
 * "POSITION: 30" or "HINT: ...". The first line's message is what to read; the code and the other lines are the detail.
 */
export function parseError(message: string): { headline: string; extra: string } {
  const lines = message.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  if (lines.length === 0) return { headline: 'The statement failed.', extra: '' };
  const m = /^([0-9A-Z]{5}):\s*(.*)$/.exec(lines[0]);
  const headline = m ? m[2] : lines[0];
  const extra = [...(m ? [m[1]] : []), ...lines.slice(1)].join(' · ');
  return { headline, extra };
}

export type ResultState = 'idle' | 'running' | 'complete' | 'cancelled' | 'error';

const ICON: Record<ResultState, string> = { idle: '', running: '⟳', complete: '✓', cancelled: '⊘', error: '✖' };

/** Whether the timing details are open. Shared, so opening them on one result keeps them open on the next. */
let detailsOpen = false;

/** What a result tab needs from the sheet that owns it. */
export interface ResultsContext {
  bridge: Bridge;
  /** Whether to fetch the whole result eagerly rather than only what is scrolled into view. */
  fetchAll(): boolean;
  registerQuery(queryId: number, view: ResultView): void;
  unregisterQuery(queryId: number): void;
  /** The statement finished, failed or was cancelled: the sheet can offer Run again and refresh its result tabs. */
  onFinished(view: ResultView): void;
}

/** A short tab label from the start of the SQL: whitespace collapsed, clipped. */
export function sqlLabel(sql: string): string {
  const flat = sql.replace(/\s+/g, ' ').trim();
  return flat.length > 28 ? flat.slice(0, 27) + '…' : flat || '(empty)';
}

/**
 * One result tab: a grid fed by one running statement. It owns that statement's streaming (a few fetches in flight, more
 * as the user scrolls), the rows received, and the status bar above the grid. Engine events and row batches for its
 * query id are routed here by the app.
 *
 * The status bar says what happened in one line (rows and time, or the failure in red). The detailed timings are behind a
 * Details toggle on the same bar.
 */
export class ResultView {
  readonly panel = document.createElement('div');
  readonly grid: Grid;
  label = '';
  /** A pinned result survives the next run; an unpinned one is replaced by it. */
  pinned = false;
  queryId = 0;
  /** Where the statement stands. A failed one is shown red in the bar and marked on its tab. */
  state: ResultState = 'idle';
  /** The headline of a failure, for the tab's tooltip. */
  errorHeadline = '';

  private readonly statusEl = document.createElement('div');
  private readonly iconEl = document.createElement('span');
  private readonly headlineEl = document.createElement('span');
  private readonly extraEl = document.createElement('span');
  private readonly toggleEl = document.createElement('button');
  private readonly detailsEl = document.createElement('pre');
  private readonly gridEl = document.createElement('div');
  private store = new ResultStore();
  private columnCount = 0;
  private rowsAffected: number | null = null;
  private errorExtra = '';
  private connectionId = 0;
  private outstanding = 0;
  private done = true;
  private m = this.freshMetrics();
  /** The outcome of the last scroll benchmark, shown in the details. */
  bench = '';

  constructor(private ctx: ResultsContext, dark: boolean) {
    this.panel.className = 'resultpanel';
    this.gridEl.className = 'grid';

    this.statusEl.className = 'status';
    const main = document.createElement('div');
    main.className = 'statusMain';
    this.iconEl.className = 'statusIcon';
    this.iconEl.setAttribute('aria-hidden', 'true');
    const text = document.createElement('div');
    text.className = 'statusText';
    this.headlineEl.className = 'headline';
    this.extraEl.className = 'extra';
    text.append(this.headlineEl, this.extraEl);
    this.toggleEl.className = 'statusToggle';
    this.toggleEl.textContent = 'Details';
    this.toggleEl.title = 'Show the timings: first batch, first paint, engine split, drawing';
    this.toggleEl.onclick = () => { detailsOpen = !detailsOpen; this.refreshStats(); };
    main.append(this.iconEl, text, this.toggleEl);
    this.detailsEl.className = 'details';
    this.statusEl.append(main, this.detailsEl);

    this.panel.append(this.statusEl, this.gridEl);
    this.grid = new Grid(this.gridEl);
    this.setTheme(dark);
    this.grid.onViewChange = () => this.pump();
    this.grid.onFrame = () => {
      if (this.m.firstPaint < 0 && this.m.firstBatch >= 0 && this.store.loadedRows > 0) {
        this.m.firstPaint = performance.now() - this.m.t0;
      }
    };
    this.refreshStats();
  }

  get running(): boolean { return !this.done; }
  get gridElement(): HTMLElement { return this.gridEl; }

  setTheme(dark: boolean) {
    this.grid.theme = (dark ? darkTheme : lightTheme) as GridTheme;
    this.grid.invalidate();
  }

  private freshMetrics() {
    return {
      t0: 0, columns: -1, firstBatch: -1, firstPaint: -1, complete: -1, ended: -1,
      bytes: 0, batches: 0, rows: 0, engineFirst: -1, engineTotal: -1, engineSplit: '',
    };
  }

  private setState(state: ResultState) {
    this.state = state;
    if (state !== 'complete' && state !== 'cancelled' && state !== 'error') return;
    this.m.ended = performance.now() - this.m.t0; // how long it took, for the bar
  }

  private fail(message: string) {
    const e = parseError(message);
    this.errorHeadline = e.headline;
    this.errorExtra = e.extra;
    this.setState('error');
  }

  /** Runs `sql` on a session, replacing whatever this tab showed. */
  async run(connectionId: number, sql: string) {
    if (this.queryId) this.ctx.unregisterQuery(this.queryId); // events for the statement it replaces no longer matter
    this.queryId = 0;
    this.label = sqlLabel(sql);
    this.panel.title = sql;
    const t0 = performance.now();
    this.m = { ...this.freshMetrics(), t0 };
    this.store = new ResultStore();
    this.columnCount = 0;
    this.rowsAffected = null;
    this.errorHeadline = '';
    this.errorExtra = '';
    this.outstanding = 0;
    this.done = false;
    this.bench = '';
    this.state = 'running';
    this.grid.setStore(this.store, []);
    this.refreshStats();

    this.connectionId = connectionId;
    const r = await this.ctx.bridge.request({ type: 'execute', connectionId, sql });
    if (r.type === 'cancelled') { // cancelled before the database returned anything
      this.done = true;
      this.setState('cancelled');
      this.refreshStats();
      this.ctx.onFinished(this);
      return;
    }
    if (r.type === 'error') {
      this.done = true;
      this.fail(r.message);
      this.refreshStats();
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

  /**
   * Cancels the statement. Once rows are flowing it has a query id. Before that (a slow aggregate, pg_sleep: nothing has come
   * back yet) it does not, and that is exactly when cancelling matters, so the session is named instead.
   */
  cancel() {
    if (this.done) return;
    if (this.queryId) this.ctx.bridge.send({ type: 'cancel', queryId: this.queryId });
    else if (this.connectionId) this.ctx.bridge.send({ type: 'cancel', connectionId: this.connectionId });
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
    this.refreshStats();
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
      this.rowsAffected = e.rowsAffected ?? null; // set by INSERT, UPDATE, DELETE; null for a SELECT
      this.grid.setRowHint(e.totalRows);
      this.setState('complete');
      this.finish();
    } else if (e.type === 'cancelled') {
      this.outstanding--;
      this.setState('cancelled');
      this.finish();
    } else if (e.type === 'error') {
      this.outstanding--;
      this.fail(e.message);
      this.finish();
    }
  }

  /** The one line the bar shows: what happened, and the detail beside it. */
  private describe(): { headline: string; extra: string } {
    const took = formatDuration(this.m.ended >= 0 ? this.m.ended : performance.now() - this.m.t0);
    switch (this.state) {
      case 'running':
        return { headline: 'Running…', extra: `${plural(this.store.loadedRows, 'row')} so far · ${took}` };
      case 'complete':
        // A statement with no result set (INSERT, UPDATE, DELETE) says what it did instead of "0 rows".
        return this.columnCount === 0 && this.rowsAffected !== null
          ? { headline: `${plural(this.rowsAffected, 'row')} affected`, extra: took }
          : { headline: plural(this.m.rows, 'row'), extra: took };
      case 'cancelled':
        return { headline: 'Cancelled', extra: `after ${took}` };
      case 'error':
        return { headline: this.errorHeadline, extra: this.errorExtra };
      default:
        return { headline: '', extra: '' };
    }
  }

  /** Redraws the status bar. Cheap; called a few times a second for the visible result, and on every state change. */
  refreshStats() {
    const { headline, extra } = this.describe();
    this.statusEl.dataset.state = this.state;
    this.statusEl.hidden = this.state === 'idle';
    // A failure interrupts: announce it. Everything else is polite.
    this.statusEl.setAttribute('role', this.state === 'error' ? 'alert' : 'status');
    this.iconEl.textContent = ICON[this.state];
    if (this.headlineEl.textContent !== headline) this.headlineEl.textContent = headline;
    if (this.extraEl.textContent !== extra) this.extraEl.textContent = extra;

    this.toggleEl.setAttribute('aria-expanded', String(detailsOpen));
    this.toggleEl.textContent = detailsOpen ? 'Hide details' : 'Details';
    this.detailsEl.hidden = !detailsOpen;
    if (!detailsOpen || this.panel.hidden) return;

    const m = this.m;
    const heap = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory?.usedJSHeapSize;
    const d = this.grid.drawStats;
    this.detailsEl.textContent =
      `loaded ${this.store.loadedRows.toLocaleString()} rows · ${m.batches} batches · ${(m.bytes / 1048576).toFixed(1)} MB` +
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
