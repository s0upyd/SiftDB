import { BatchMeta, Bridge, BridgeEvent, Column } from './bridge';
import { toast } from './copy';
import { ExportFormat, exportText, suggestedName } from './export';
import { CellRange, CompareView, Grid, GridTheme, darkTheme, lightTheme } from './grid';
import { setIcon, spinner } from './icons';
import { ResultStore, decodeBatch } from './store';

/** The format and file of the last export in this run: the next export starts from them. */
let lastFormat: ExportFormat = 'csv';
let lastExportPath: string | null = null;

/** Batches requested ahead of the ones received, so the engine never waits on the page. */
const MAX_OUTSTANDING = 3;

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

/** Running has no character: it is the shared spinner (icons.ts). */
const ICON: Record<ResultState, string> = { idle: '', running: '', complete: '✓', cancelled: '⊘', error: '✖' };

/** Whether the timing details are open. Shared, so opening them on one result keeps them open on the next. */
let detailsOpen = false;

/** What a result tab needs from the sheet that owns it. */
export interface ResultsContext {
  bridge: Bridge;
  registerQuery(queryId: number, view: ResultView): void;
  unregisterQuery(queryId: number): void;
  /** The statement finished, failed or was cancelled: the sheet can offer Run again and refresh its result tabs. */
  onFinished(view: ResultView): void;
  /** "Run for real" on a dry run's bar: run the statement it tried, for real this time. */
  runForReal(sql: string): void;
}

/** How a dry run's rows are laid out (design section 14, F2), as the engine reports it. */
interface DryRun {
  shape: 'beforeAndAfter' | 'afterOnly' | 'deleted';
  /** Columns in one half: the table's columns. */
  width: number;
  /** Rows shown at most; the count of every affected row still comes back. */
  maxRows: number;
  /** Which half the grid shows, for an UPDATE with both. */
  showing: 'before' | 'after';
}

/** What a dry run cannot undo or hide, said once, on its bar's info mark. */
const DRY_RUN_CAVEATS = [
  'A dry run really runs the statement, then rolls it back. Things to know:',
  '• Triggers fire, and are rolled back with everything else.',
  '• Sequences (nextval, serial and identity columns) are not rolled back: their numbers are used up.',
  '• Anything outside the database (dblink, foreign tables, COPY … TO PROGRAM) is not undone.',
  '• NOTIFY waits for a commit, so nothing is sent.',
  '• The rows are locked while it runs, as for the real statement.',
].join('\n');

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
  /**
   * Shown after a later run on the same sheet did not happen (blocked, or not connected): the bar then says it is from an
   * earlier run, so an old red error cannot be read as the outcome of what was just attempted. A new run clears it.
   */
  stale = false;
  /** The headline of a failure, for the tab's tooltip. */
  errorHeadline = '';

  private readonly statusEl = document.createElement('div');
  private readonly iconEl = document.createElement('span');
  private readonly staleEl = document.createElement('span');
  private readonly headlineEl = document.createElement('span');
  private readonly extraEl = document.createElement('span');
  private readonly toggleEl = document.createElement('button');
  private readonly exportEl = document.createElement('button');
  /** A dry run's controls: the Before | After switch, the caveats mark and Run for real. */
  private readonly dryEl = document.createElement('span');
  private readonly beforeBtn = document.createElement('button');
  private readonly afterBtn = document.createElement('button');
  private readonly realBtn = document.createElement('button');
  /** Set while this tab holds a dry run's result. */
  dry: DryRun | null = null;
  /** How far an export has got, beside its (then red, stop) icon. */
  private readonly exportPctEl = document.createElement('span');
  /** The export being written, if any. Its button then reads "Cancel export" with how far it has got. */
  private exporting: { cancelled: boolean } | null = null;
  private sql = '';
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
    this.staleEl.className = 'stalechip';
    this.staleEl.textContent = 'Earlier run';
    this.staleEl.title = 'This result is from an earlier run. The last attempt on this sheet did not run (see the message above).';
    this.staleEl.hidden = true;
    text.append(this.staleEl, this.headlineEl, this.extraEl);
    // Icon buttons, like the toolbar's: Export is green (it makes something), Details a chevron that points where the
    // panel will go. Both keep their words in the tooltip and the accessible name.
    this.toggleEl.className = 'ib statusbtn';
    this.toggleEl.onclick = () => { detailsOpen = !detailsOpen; this.refreshStats(); };
    this.exportEl.className = 'ib statusbtn ic-go';
    this.exportEl.append(this.exportPctEl);
    this.exportEl.onclick = () => {
      if (this.exporting) this.exporting.cancelled = true;
      else void this.export(null);
    };
    this.dryEl.className = 'dryctl';
    const modes = document.createElement('span');
    modes.className = 'modes';
    modes.setAttribute('role', 'group');
    modes.setAttribute('aria-label', 'Show the values');
    this.beforeBtn.textContent = 'Before';
    this.beforeBtn.title = 'Show the values as they are now';
    this.afterBtn.textContent = 'After';
    this.afterBtn.title = 'Show the values as the statement would leave them';
    this.beforeBtn.onclick = () => this.showHalf('before');
    this.afterBtn.onclick = () => this.showHalf('after');
    modes.append(this.beforeBtn, this.afterBtn);
    const info = document.createElement('span');
    info.className = 'dryinfo';
    info.textContent = 'ⓘ';
    info.tabIndex = 0;
    info.title = DRY_RUN_CAVEATS;
    info.setAttribute('aria-label', DRY_RUN_CAVEATS);
    this.realBtn.className = 'danger';
    this.realBtn.textContent = 'Run for real';
    this.realBtn.title = 'Run the statement now, for real. Nothing from the dry run is reused.';
    this.realBtn.onclick = () => this.ctx.runForReal(this.sql);
    this.dryEl.append(modes, info, this.realBtn);
    main.append(this.iconEl, text, this.dryEl, this.exportEl, this.toggleEl);
    this.detailsEl.className = 'details';
    this.statusEl.append(main, this.detailsEl);

    this.panel.append(this.statusEl, this.gridEl);
    this.grid = new Grid(this.gridEl);
    this.setTheme(dark);
    this.grid.onViewChange = () => this.pump();
    this.grid.onExport = (range) => void this.export(range);
    this.grid.canExport = () => this.canExport;
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

  /**
   * Runs `sql` on a session, replacing whatever this tab showed. `dry`: a dry run (design section 14, F2), which runs it
   * and rolls it back, showing the rows it would change.
   */
  async run(connectionId: number, sql: string, dry = false) {
    if (this.queryId) this.ctx.unregisterQuery(this.queryId); // events for the statement it replaces no longer matter
    this.queryId = 0;
    if (this.exporting) this.exporting.cancelled = true; // its rows are about to be replaced
    this.sql = sql;
    this.label = dry ? `Dry run: ${sqlLabel(sql)}` : sqlLabel(sql);
    this.dry = null;
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
    this.stale = false;
    this.state = 'running';
    this.grid.setStore(this.store, []);
    this.refreshStats();

    this.connectionId = connectionId;
    // The sheet asked "checkRun" before this, and the user confirmed any missing-WHERE warning (design section 14, F1). A
    // dry run needs no such check: nothing it does is kept.
    const r = await this.ctx.bridge.request(dry
      ? { type: 'dryRun', connectionId, sql }
      : { type: 'execute', connectionId, sql, allRowsChecked: true });
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
    const columns = r.columns as Column[];
    if (r.dryRun) {
      // An UPDATE with both halves opens on "after": what the statement would do.
      this.dry = { ...(r.dryRun as Omit<DryRun, 'showing'>), showing: 'after' };
      this.columnCount = this.dry.width;
      this.grid.setStore(this.store, columns.slice(this.dryBase('after'), this.dryBase('after') + this.dry.width), this.compareView());
    } else {
      this.columnCount = columns.length;
      this.grid.setStore(this.store, columns);
    }
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

  /**
   * Keep a few fetches in flight until the whole result has arrived. The statement holds its sheet's session until it is
   * drained, so the result is always fetched to the end (design section 11: fetch-on-scroll waits for spill-to-disk).
   */
  private pump() {
    // Until the engine has answered "execute" there is no query id to fetch from. (The grid's resize observer calls this
    // as soon as the tab exists, which is before that answer arrives.)
    if (!this.queryId) return;
    while (!this.done && this.outstanding < MAX_OUTSTANDING) {
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

  /** Where the shown half starts among the result's columns. */
  private dryBase(half: 'before' | 'after') {
    return this.dry?.shape === 'beforeAndAfter' && half === 'after' ? this.dry.width : 0;
  }

  /** The grid's layout for the dry run: the half shown, compared with the other when there is one. */
  private compareView(): CompareView {
    const d = this.dry!;
    const both = d.shape === 'beforeAndAfter';
    const base = this.dryBase(d.showing);
    const other = this.dryBase(d.showing === 'after' ? 'before' : 'after');
    return {
      base, partner: both ? other : null, deleted: d.shape === 'deleted',
      labels: d.showing === 'after' ? ['After', 'Before'] : ['Before', 'After'],
    };
  }

  private showHalf(half: 'before' | 'after') {
    if (!this.dry || this.dry.shape !== 'beforeAndAfter' || this.dry.showing === half) return;
    this.dry.showing = half;
    this.grid.setView(this.compareView());
    this.refreshStats();
  }

  /** A dry run's line: what would happen, and that it did not. */
  private describeDryRun(took: string): { headline: string; extra: string } {
    const d = this.dry!;
    const n = this.rowsAffected ?? this.m.rows;
    const verb = d.shape === 'deleted' ? 'delete' : 'change';
    const shown = this.m.rows < n ? ` · showing the first ${this.m.rows.toLocaleString()}` : '';
    const noBefore = d.shape === 'afterOnly' ? ' · the values before need PostgreSQL 18' : '';
    return {
      headline: `Dry run: would ${verb} ${plural(n, 'row')}`,
      extra: `rolled back, nothing changed${shown}${noBefore} · ${took}`,
    };
  }

  /** The one line the bar shows: what happened, and the detail beside it. */
  private describe(): { headline: string; extra: string } {
    const took = formatDuration(this.m.ended >= 0 ? this.m.ended : performance.now() - this.m.t0);
    if (this.dry && this.state === 'complete') return this.describeDryRun(took);
    if (this.dry && this.state === 'running') return { headline: 'Dry run…', extra: `${plural(this.store.loadedRows, 'row')} so far · ${took}` };
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
    this.statusEl.classList.toggle('stale', this.stale);
    // A dry run's bar is striped, so it is never taken for a real run's.
    this.statusEl.classList.toggle('dryrun', !!this.dry);
    this.dryEl.hidden = !this.dry || this.state !== 'complete';
    if (this.dry) {
      const both = this.dry.shape === 'beforeAndAfter';
      (this.beforeBtn.parentElement as HTMLElement).hidden = !both;
      this.beforeBtn.setAttribute('aria-pressed', String(this.dry.showing === 'before'));
      this.afterBtn.setAttribute('aria-pressed', String(this.dry.showing === 'after'));
    }
    this.staleEl.hidden = !this.stale;
    // A failure interrupts: announce it (once: not again when it becomes an earlier run's). Everything else is polite.
    this.statusEl.setAttribute('role', this.state === 'error' && !this.stale ? 'alert' : 'status');
    if (this.state === 'running') { if (!this.iconEl.querySelector('.spinner')) this.iconEl.replaceChildren(spinner()); }
    else this.iconEl.textContent = ICON[this.state];
    if (this.headlineEl.textContent !== headline) this.headlineEl.textContent = headline;
    if (this.extraEl.textContent !== extra) this.extraEl.textContent = extra;

    this.exportEl.hidden = !this.exporting && !this.canExport;
    if (!this.exporting) {
      setIcon(this.exportEl, 'export');
      this.exportEl.classList.replace('ic-stop', 'ic-go');
      this.exportPctEl.textContent = '';
      this.exportEl.title = 'Export the result to a CSV or JSON file';
      this.exportEl.setAttribute('aria-label', 'Export…');
    }
    this.toggleEl.setAttribute('aria-expanded', String(detailsOpen));
    setIcon(this.toggleEl, detailsOpen ? 'chevronUp' : 'chevronDown');
    this.toggleEl.title = detailsOpen ? 'Hide the timings' : 'Show the timings: first batch, first paint, engine split, drawing';
    this.toggleEl.setAttribute('aria-label', 'Details');
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
      `draw avg ${d.avg.toFixed(2)} ms, max ${d.max.toFixed(1)} ms`;
  }

  /** A finished result with columns can be exported (a cancelled one too: what arrived is what it shows). One at a time. */
  get canExport(): boolean {
    return !this.exporting && this.columnCount > 0 && (this.state === 'complete' || this.state === 'cancelled');
  }

  /**
   * Exports the whole result, or a rectangle of it, to a file chosen in the Save dialog, whose file type list picks CSV or
   * JSON. The page formats the rows and the engine writes them a piece at a time (export.ts, EngineHost.Export.cs), so a
   * large result neither freezes the page nor sits in memory twice. The file appears only when it is complete.
   */
  async export(range: CellRange | null) {
    if (!this.canExport) return;
    const bridge = this.ctx.bridge;
    const src = this.grid.exportSource(range ?? undefined);
    const d = await bridge.request({
      type: 'saveFileDialog', purpose: 'export', format: lastFormat, currentPath: lastExportPath,
      suggestedName: `${suggestedName(this.sql)}.${lastFormat}`,
    });
    if (!d.path || !this.canExport) return;
    const path = d.path as string;
    // A typed extension wins over the file type list, so "out.json" is JSON whichever type was showing.
    const format: ExportFormat = /\.json$/i.test(path) ? 'json' : /\.csv$/i.test(path) ? 'csv' : d.format === 'json' ? 'json' : 'csv';
    lastFormat = format;
    lastExportPath = path;

    const job = { cancelled: false };
    this.exporting = job;
    setIcon(this.exportEl, 'stop');
    this.exportEl.classList.replace('ic-go', 'ic-stop');
    this.exportPctEl.textContent = '0%';
    this.exportEl.title = 'Cancel the export. No file is written.';
    this.exportEl.setAttribute('aria-label', 'Cancel export');
    this.refreshStats();
    try {
      // Excel reads a CSV as UTF-8 only when it starts with the byte-order mark; JSON is plain UTF-8 by its standard.
      const begin = await bridge.request({ type: 'exportBegin', path, byteOrderMark: format === 'csv' });
      if (begin.type === 'error') { toast(this.gridEl, `Export failed: ${begin.message}`, 'warn'); return; }
      const exportId = begin.exportId as number;
      let row = src.r0;
      const counted = { ...src, cell: (r: number, c: number) => { row = r; return src.cell(r, c); } };
      for (const piece of exportText(format, counted)) {
        if (job.cancelled) break;
        const w = await bridge.request({ type: 'exportWrite', exportId, text: piece });
        // A failed write has already ended the export in the engine, and thrown the partial file away.
        if (w.type === 'error') { toast(this.gridEl, `Export failed: ${w.message}`, 'warn'); return; }
        const pct = Math.floor(((row - src.r0 + 1) / Math.max(1, src.rows)) * 100);
        this.exportPctEl.textContent = `${pct}%`;
      }
      const end = await bridge.request({ type: 'exportEnd', exportId, cancel: job.cancelled });
      if (end.type === 'error') toast(this.gridEl, `Export failed: ${end.message}`, 'warn');
      else if (job.cancelled) toast(this.gridEl, 'Export cancelled. No file was written.');
      else toast(this.gridEl, `Exported ${plural(src.rows, 'row')} to ${end.fileName}`);
    } finally {
      if (this.exporting === job) this.exporting = null;
      this.refreshStats();
    }
  }

  /** A later attempt on the sheet did not run: say this result is from before it. Nothing to say for an empty tab or a running one. */
  markStale() {
    if (this.state === 'idle' || this.state === 'running' || this.stale) return;
    this.stale = true;
    this.refreshStats();
  }

  dispose() {
    if (this.exporting) this.exporting.cancelled = true;
    this.cancel();
    if (this.queryId) this.ctx.unregisterQuery(this.queryId);
    this.queryId = 0;
    this.panel.remove();
  }
}
