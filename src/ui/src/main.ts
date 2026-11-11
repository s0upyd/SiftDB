import { BatchMeta, Bridge, BridgeEvent, Column, DatabaseSchema, connectBridge } from './bridge';
import { CompletionResponse, QueryMode, RunTarget, SheetAnalysis, SqlEditor } from './editor';
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
let lastColumnCount = 0;

const txBar = $('txbar');
const txIcon = $('txIcon');
const txLabel = $('txLabel');
const txDetail = $('txDetail');
const txEnd = $<HTMLButtonElement>('txEnd');
const txCommit = $<HTMLButtonElement>('txCommit');
const txRollback = $<HTMLButtonElement>('txRollback');
const beginBtn = $<HTMLButtonElement>('begin');

type TxState = 'none' | 'open' | 'changed' | 'failed';
/** The server's word on the session's transaction, as last reported by the engine. */
const tx = { state: 'none' as TxState, startedAt: null as string | null, statements: 0, rowsChanged: 0 };
let hostWarnedPending = false;

const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;

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
  if (e.type === 'transaction') {
    tx.state = e.state;
    tx.startedAt = e.startedAt ?? null;
    tx.statements = e.statements;
    tx.rowsChanged = e.rowsChanged;
    renderTx();
    return;
  }
  if (e.type === 'transactionUnknown') {
    m.status = `transaction state unavailable: ${e.message}`;
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
      // A statement with no result set (INSERT, UPDATE, DELETE) says what it did instead.
      m.status = e.rowsAffected != null && lastColumnCount === 0 ? `complete · ${plural(e.rowsAffected, 'row')} affected` : 'complete';
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
  lastColumnCount = (r.columns as Column[]).length;
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
async function analyze(text: string, selectionStart: number, selectionEnd: number, mode: QueryMode): Promise<SheetAnalysis | null> {
  if (!connectionId) return null;
  const r = await bridge.request({ type: 'analyzeSheet', connectionId, text, selectionStart, selectionEnd, mode });
  return r.type === 'sheetAnalysis' ? (r as unknown as SheetAnalysis) : null;
}

function handleTarget(target: RunTarget) {
  switch (target.kind) {
    case 'run': void run(target.sql); break;
    case 'blocked': m.status = `not run: ${target.reason}`; break;
    case 'none': m.status = 'nothing to run: put the cursor in a statement, or select some text'; break;
    case 'disconnected': statsEl.textContent = 'Connect first. The connection decides which SQL dialect splits your statements.'; break;
  }
}

async function runFromEditor() {
  handleTarget(await sqlEditor.statementToRun());
}

// ---- Transaction bar (design section 7) ---------------------------------------------------------------------------
// The state is whatever the server last said. The Begin button and a typed BEGIN end up in the same place because
// both are observed afterwards, never assumed.

function elapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, '0')} s`;
  return `${Math.floor(s / 3600)} h ${String(Math.floor((s % 3600) / 60)).padStart(2, '0')} min`;
}

function renderTx() {
  const s = tx.state;
  txBar.hidden = s === 'none';
  txBar.className = s === 'none' ? '' : `tx-${s}`;
  beginBtn.disabled = !connectionId || s !== 'none';
  if (s !== 'none') {
    txIcon.textContent = s === 'open' ? 'ⓘ' : s === 'changed' ? '✎' : '✖';
    txLabel.textContent = s === 'open' ? 'Transaction open' : s === 'changed' ? 'Changes pending' : 'Transaction aborted';
    txEnd.hidden = s !== 'open';      // nothing changed: ending it is just a commit
    txCommit.hidden = s !== 'changed';
    txRollback.hidden = false;        // the only way out of a failed transaction
    updateTxDetail();
  }
  // The host warns before closing the window while changes would be thrown away.
  const pending = s === 'changed' || s === 'failed';
  if (pending !== hostWarnedPending) {
    hostWarnedPending = pending;
    bridge.send({ type: 'hostState', unsavedChanges: pending });
  }
}

function updateTxDetail() {
  if (tx.state === 'none') return;
  const parts: string[] = [];
  // The count runs from the first statement in the transaction, whether or not anything has changed yet.
  if (tx.state === 'open') parts.push(`${plural(tx.statements, 'statement')} · nothing changed yet`);
  else if (tx.state === 'changed') parts.push(`${plural(tx.statements, 'statement')} · ${plural(tx.rowsChanged, 'row')} changed`);
  else parts.push('an error aborted it: roll back to continue');
  if (tx.startedAt) parts.push(`open ${elapsed(Date.now() - Date.parse(tx.startedAt))}`);
  txDetail.textContent = parts.join(' · ');
}
setInterval(updateTxDetail, 1000);

async function txAction(action: 'begin' | 'commit' | 'rollback') {
  if (!connectionId) return;
  const r = await bridge.request({ type: 'transaction', connectionId, action });
  if (r.type === 'error') m.status = `${action} failed: ${r.message}`;
}
beginBtn.onclick = () => void txAction('begin');
txEnd.onclick = () => void txAction('commit');
txCommit.onclick = () => void txAction('commit');
txRollback.onclick = () => void txAction('rollback');

// Capture phase, and stopPropagation: Monaco binds Ctrl+Shift+Enter itself ("insert line above"), so it must not also see it.
window.addEventListener('keydown', (e) => {
  if (!e.ctrlKey || !e.shiftKey) return;
  const commit = e.key === 'Enter' && (tx.state === 'open' || tx.state === 'changed');
  const rollback = (e.key === 'R' || e.key === 'r') && tx.state !== 'none';
  if (!commit && !rollback) return;
  e.preventDefault();
  e.stopPropagation();
  void txAction(commit ? 'commit' : 'rollback');
}, true);

// F5 runs the statement from anywhere on the page. Never let it reload the page (it would drop the connection);
// when the editor has focus it handles F5 itself and this never sees the key.
window.addEventListener('keydown', (e) => {
  if (e.key !== 'F5') return;
  e.preventDefault();
  void runFromEditor();
});

// Strict: the text you write is the text that runs. Assisted: the editor shows (and runs) the quoting the database needs.
const modeBtn = $<HTMLButtonElement>('mode');
modeBtn.onclick = () => {
  const next: QueryMode = sqlEditor.getMode() === 'assisted' ? 'strict' : 'assisted';
  sqlEditor.setMode(next);
  modeBtn.textContent = next === 'assisted' ? 'Assisted' : 'Strict';
  modeBtn.setAttribute('aria-pressed', String(next === 'strict'));
};

/** Suggestions are computed by the engine from its cached schema, so this is one cheap round trip per request. */
async function complete(text: string, offset: number): Promise<CompletionResponse | null> {
  if (!connectionId) return null;
  const r = await bridge.request({ type: 'complete', connectionId, text, offset });
  return r.type === 'completions' ? (r as unknown as CompletionResponse) : null;
}

const sqlEditor = new SqlEditor($('editor'), analyze, {
  run: () => void runFromEditor(),
  cancel: cancelQuery,
  runSql: handleTarget,
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
