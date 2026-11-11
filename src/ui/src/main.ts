import { BatchMeta, Bridge, BridgeEvent, Column, DatabaseSchema, connectBridge } from './bridge';
import { CompletionResponse, SheetAnalysis, SqlEditor } from './editor';
import { Grid, darkTheme, lightTheme } from './grid';
import { ResultStore, decodeBatch } from './store';
import { SchemaTree } from './tree';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const connEl = $<HTMLInputElement>('conn');
const statsEl = $('stats');
const runBtn = $<HTMLButtonElement>('run');
const cancelBtn = $<HTMLButtonElement>('cancel');
const benchBtn = $<HTMLButtonElement>('bench');
const fetchAll = $<HTMLInputElement>('fetchAll');

const INITIAL_SHEET = [
  '-- Put the cursor in a statement and press F5 or Ctrl+Enter. Select text to run just that.',
  '-- Ctrl+. cancels. The ▶ in the margin runs a statement with the mouse.',
  'select i, i::bigint * 3 as big, random() as r, md5(i::text) as hash',
  'from generate_series(1, 1000000) as i;',
  '',
  'select * from "Person";',
  '',
].join('\n');

const grid = new Grid($('grid'));
const treeFilter = $<HTMLInputElement>('treeFilter');
const refreshBtn = $<HTMLButtonElement>('refresh');
const tree = new SchemaTree($('tree'), $('treeSummary'));
const MAX_OUTSTANDING = 3;
const LOOKAHEAD_ROWS = 100_000;

let bridge: Bridge;
let connectionId = 0;
let queryId = 0;
let store = new ResultStore();
let outstanding = 0;
let done = true;

const m = {
  t0: 0, columns: -1, firstBatch: -1, firstPaint: -1, complete: -1,
  bytes: 0, batches: 0, rows: 0, engineFirst: -1, engineTotal: -1, engineSplit: '', status: '', bench: '',
};

function onEvent(e: BridgeEvent) {
  if (e.type === 'init') {
    document.documentElement.dataset.theme = e.dark ? 'dark' : 'light';
    grid.theme = e.dark ? darkTheme : lightTheme;
    grid.invalidate();
    sqlEditor.setTheme(!!e.dark);
    if (e.defaultConnection) connEl.value = e.defaultConnection;
    return;
  }
  if (e.queryId !== queryId) return; // stale query
  if (e.type === 'complete') {
    outstanding--;
    if (!done) {
      done = true;
      m.complete = performance.now() - m.t0;
      m.rows = e.totalRows;
      m.engineFirst = e.engineFirstBatchMs;
      m.engineTotal = e.engineTotalMs;
      m.engineSplit = `read ${e.engineReadMs.toFixed(0)} ms · encode ${e.engineAppendMs.toFixed(0)} ms · blocked on UI ${e.engineWriteWaitMs.toFixed(0)} ms`;
      m.status = 'complete';
      grid.setRowHint(e.totalRows);
      finish();
    }
  } else if (e.type === 'cancelled') {
    outstanding--; done = true; m.status = 'cancelled'; finish();
  } else if (e.type === 'error') {
    outstanding--; done = true; m.status = `error: ${e.message}`; finish();
  }
}

function onBatch(meta: BatchMeta, data: ArrayBuffer) {
  if (meta.queryId !== queryId) return;
  outstanding--;
  store.add(meta.seq, decodeBatch(data, meta.startRow));
  m.bytes += data.byteLength;
  m.batches++;
  if (m.firstBatch < 0) m.firstBatch = performance.now() - m.t0;
  grid.invalidate();
  pump();
}

/** Keep a few fetches in flight. When the user isn't "fetch all" and has scrolled nowhere near the end, stop. */
function pump() {
  while (!done && outstanding < MAX_OUTSTANDING &&
         (fetchAll.checked || store.loadedRows < grid.lastVisibleRow + LOOKAHEAD_ROWS)) {
    outstanding++;
    bridge.send({ type: 'fetchBatch', queryId });
  }
}

function finish() {
  runBtn.disabled = false;
  cancelBtn.disabled = true;
}

async function run(sql: string) {
  const t0 = performance.now();
  Object.assign(m, {
    t0, columns: -1, firstBatch: -1, firstPaint: -1, complete: -1,
    bytes: 0, batches: 0, rows: 0, engineFirst: -1, engineTotal: -1, engineSplit: '', status: 'running',
  });
  store = new ResultStore();
  outstanding = 0;
  done = false;
  runBtn.disabled = true;
  cancelBtn.disabled = false;

  const r = await bridge.request({ type: 'execute', connectionId, sql });
  if (r.type === 'error') { done = true; m.status = `error: ${r.message}`; finish(); return; }
  queryId = r.queryId!;
  m.columns = performance.now() - t0;
  grid.setStore(store, r.columns as Column[]);
  pump();
}

grid.onViewChange = () => pump();
grid.onFrame = () => {
  if (m.firstPaint < 0 && m.firstBatch >= 0 && store.loadedRows > 0) m.firstPaint = performance.now() - m.t0;
};

$('connect').onclick = async () => {
  statsEl.textContent = 'Connecting…';
  const r = await bridge.request({ type: 'connect', provider: 'postgres', connectionString: connEl.value });
  if (r.type === 'error') { statsEl.textContent = `Connect failed: ${r.message}`; return; }
  connectionId = r.connectionId;
  runBtn.disabled = false;
  benchBtn.disabled = false;
  statsEl.textContent = 'Connected. Press F5 in the editor.';
  treeFilter.disabled = false;
  refreshBtn.disabled = false;
  sqlEditor.refresh();
  sqlEditor.focus();
  await loadSchema();
};

async function loadSchema() {
  tree.clear('Loading schema…');
  refreshBtn.disabled = true;
  const t0 = performance.now();
  const r = await bridge.request({ type: 'introspect', connectionId });
  refreshBtn.disabled = false;
  if (r.type === 'error') { tree.clear(`Schema failed: ${r.message}`); return; }
  tree.setSchema(r.schema as DatabaseSchema);
  tree.setFilter(treeFilter.value);
  console.log(`schema loaded in ${(performance.now() - t0).toFixed(0)} ms`);
}

refreshBtn.onclick = loadSchema;
treeFilter.addEventListener('input', () => tree.setFilter(treeFilter.value));
runBtn.onclick = () => void runFromEditor();
cancelBtn.onclick = cancelQuery;

function cancelQuery() {
  if (!cancelBtn.disabled) bridge.send({ type: 'cancel', queryId });
}

/** The engine owns the dialect-aware parts of "what does F5 run": splitting the sheet and picking the statement. */
async function analyze(text: string, selectionStart: number, selectionEnd: number): Promise<SheetAnalysis | null> {
  if (!connectionId) return null;
  const r = await bridge.request({ type: 'analyzeSheet', connectionId, text, selectionStart, selectionEnd });
  return r.type === 'sheetAnalysis' ? (r as unknown as SheetAnalysis) : null;
}

async function runFromEditor() {
  const target = await sqlEditor.statementToRun();
  switch (target.kind) {
    case 'run': void run(target.sql); break;
    case 'none': m.status = 'nothing to run: put the cursor in a statement, or select some text'; break;
    case 'disconnected': statsEl.textContent = 'Connect first. The connection decides which SQL dialect splits your statements.'; break;
  }
}

/** Suggestions are computed by the engine from its cached schema, so this is one cheap round trip per request. */
async function complete(text: string, offset: number): Promise<CompletionResponse | null> {
  if (!connectionId) return null;
  const r = await bridge.request({ type: 'complete', connectionId, text, offset });
  return r.type === 'completions' ? (r as unknown as CompletionResponse) : null;
}

const sqlEditor = new SqlEditor($('editor'), analyze, {
  run: () => void runFromEditor(),
  cancel: cancelQuery,
  runSql: (sql) => void run(sql),
  complete,
}, INITIAL_SHEET);
sqlEditor.setCursor(3); // on the first statement

/** Scrolls top to bottom over a fixed time and records frame intervals. */
benchBtn.onclick = () => {
  const wrap = $('grid');
  const span = wrap.scrollHeight - wrap.clientHeight;
  if (span <= 0) { m.bench = 'nothing to scroll'; return; }
  const DURATION = 6000;
  const frames: number[] = [];
  const start = performance.now();
  let prev = start;
  m.bench = 'running…';
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
    m.bench = `${frames.length} frames over ${DURATION / 1000}s · p50 ${q(0.5).toFixed(1)}ms · p95 ${q(0.95).toFixed(1)}ms · ` +
      `max ${frames[frames.length - 1].toFixed(1)}ms · >20ms: ${slow}`;
  };
  requestAnimationFrame(step);
};

const ms = (v: number) => (v < 0 ? '–' : `${v.toFixed(0)} ms`);
setInterval(() => {
  if (!connectionId) return;
  const heap = (performance as any).memory?.usedJSHeapSize as number | undefined;
  const d = grid.drawStats;
  statsEl.textContent =
    `status: ${m.status || 'idle'} · loaded ${store.loadedRows.toLocaleString()} rows · ${m.batches} batches · ${(m.bytes / 1048576).toFixed(1)} MB` +
    (heap ? ` · JS heap ${(heap / 1048576).toFixed(0)} MB` : '') + '\n' +
    `columns ${ms(m.columns)} · first batch ${ms(m.firstBatch)} · first paint ${ms(m.firstPaint)} · transfer complete ${ms(m.complete)}` +
    ` · engine first/total ${ms(m.engineFirst)}/${ms(m.engineTotal)}\n` +
    (m.engineSplit ? `engine split: ${m.engineSplit}\n` : '') +
    `draw avg ${d.avg.toFixed(2)} ms, max ${d.max.toFixed(1)} ms` + (m.bench ? ` · scroll: ${m.bench}` : '');
}, 250);

bridge = connectBridge(onEvent, onBatch);
