import { BatchMeta, Bridge, BridgeEvent, DatabaseSchema, connectBridge } from './bridge';
import { ResultView } from './results';
import { Sheet, SheetContext, TxState } from './sheet';
import { revealSelectedTab, scrollTabsWithWheel } from './tabs';
import { SchemaTree, TreeRoot } from './tree';

// The app shell: connection, tabs, the schema sidebar, and routing of engine events to the sheet (or result tab) they
// belong to. Everything about one sheet lives in sheet.ts, and everything about one result in results.ts.

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const connEl = $<HTMLInputElement>('conn');
const connectBtn = $<HTMLButtonElement>('connect');
const tabsEl = $('tabs');
const sheetsEl = $('sheets');
const treeFilter = $<HTMLInputElement>('treeFilter');
const refreshBtn = $<HTMLButtonElement>('refresh');
const tree = new SchemaTree($('tree'), $('treeSummary'));
scrollTabsWithWheel(tabsEl);

const INITIAL_SHEET = [
  '-- Put the cursor in a statement and press F5 or Ctrl+Enter. Select text to run just that.',
  '-- Ctrl+. cancels. The ▶ in the margin runs a statement with the mouse.',
  'select i, i::bigint * 3 as big, random() as r, md5(i::text) as hash',
  'from generate_series(1, 1000000) as i;',
  '',
  'select * from "Person";',
  '',
].join('\n');

let bridge: Bridge;
let dark = false;
/**
 * One entry per distinct connection string in use. Sheets that connected with the same string share an entry, and so a
 * root in the schema tree; each of them still has its own session. The string (with its password) never reaches the DOM.
 */
interface ConnectionEntry { id: string; schema: DatabaseSchema | null }
const connectionsByString = new Map<string, ConnectionEntry>();
/** Connection strings with a connect in flight: they show in the tree as "loading…". */
const connecting = new Set<string>();
let connectionSeq = 0;
let connectNote = 'Not connected.';
const sheets: Sheet[] = [];
let active: Sheet | null = null;
let sheetCounter = 0;
let hostWarnedPending = false;

/** Engine events carry a query id or a connection id; these find the sheet or result they are for. */
const queries = new Map<number, ResultView>();
const connections = new Map<number, Sheet>();

const ctx: SheetContext = {
  get bridge() { return bridge; },
  isDark: () => dark,
  registerQuery: (id, view) => queries.set(id, view),
  unregisterQuery: (id) => queries.delete(id),
  registerConnection: (id, sheet) => connections.set(id, sheet),
  unregisterConnection: (id) => connections.delete(id),
  changed: () => { renderTabs(); updateHostState(); },
};

// ---- events from the engine --------------------------------------------------------------------------------------

function onEvent(e: BridgeEvent) {
  switch (e.type) {
    case 'init':
      dark = !!e.dark;
      document.documentElement.dataset.theme = dark ? 'dark' : 'light';
      sheets.forEach((s) => s.setTheme(dark));
      if (e.defaultConnection) {
        connEl.value = e.defaultConnection;
        for (const s of sheets) if (!s.draft && !s.connectionString) s.draft = e.defaultConnection;
      }
      return;
    case 'transaction':
      connections.get(e.connectionId)?.onTransaction(e as unknown as Parameters<Sheet['onTransaction']>[0]);
      return;
    case 'transactionUnknown':
      connections.get(e.connectionId)?.setMessage(`transaction state unavailable: ${e.message}`, 'warning');
      return;
  }
  if (e.queryId !== undefined) queries.get(e.queryId)?.onEvent(e);
}

function onBatch(meta: BatchMeta, data: ArrayBuffer) {
  queries.get(meta.queryId)?.onBatch(meta, data);
}

// ---- tabs --------------------------------------------------------------------------------------------------------

const TX_LABEL: Record<TxState, string> = {
  none: '', open: 'transaction open', changed: 'changes pending', failed: 'transaction aborted',
};

function renderTabs() {
  tabsEl.replaceChildren(...sheets.map((sheet) => {
    const tab = document.createElement('div');
    tab.className = 'tab';
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-selected', String(sheet === active));
    tab.onclick = () => activate(sheet);

    // A dot when this sheet's session is inside a transaction, so it can't be forgotten behind another tab.
    const dot = document.createElement('span');
    dot.className = `dot tx-${sheet.tx.state}`;
    dot.hidden = sheet.tx.state === 'none';
    dot.title = TX_LABEL[sheet.tx.state];

    const title = document.createElement('span');
    title.className = 'title';
    title.textContent = sheet.title;

    const close = document.createElement('button');
    close.className = 'close';
    close.textContent = '×';
    close.title = 'Close this sheet (Ctrl+W)';
    close.setAttribute('aria-label', `Close ${sheet.title}`);
    close.onclick = (e) => { e.stopPropagation(); void closeSheet(sheet); };

    tab.append(dot, title, close);
    return tab;
  }));
  revealSelectedTab(tabsEl);
}

function activate(sheet: Sheet) {
  active = sheet;
  sheets.forEach((s) => s.setActive(s === sheet));
  connEl.value = sheet.draft; // the connection box always describes the visible sheet
  renderTabs();
  rebuildTree();
  sheet.focusEditor();
}

function addSheet(first = false): Sheet {
  // A new sheet starts on the connection of the sheet you were looking at, with a session of its own.
  const inherit = active?.connectionString ?? null;
  const sheet = new Sheet(ctx, sheetsEl, `Sheet ${++sheetCounter}`, first ? INITIAL_SHEET : '', first ? 3 : 1);
  sheet.draft = inherit ?? connEl.value;
  sheets.push(sheet);
  activate(sheet);
  sheet.setTheme(dark);
  if (inherit) void connectSheet(sheet, inherit);
  return sheet;
}

async function closeSheet(sheet: Sheet) {
  if (sheet.txPending &&
      !window.confirm(`"${sheet.title}" has changes that are not committed. Closing it rolls them back.\n\nClose anyway?`)) return;
  const at = sheets.indexOf(sheet);
  sheets.splice(at, 1);
  const wasActive = active === sheet;
  await sheet.close();
  rebuildTree(); // the last sheet on a connection takes its root with it
  if (sheets.length === 0) addSheet();
  else if (wasActive) activate(sheets[Math.min(at, sheets.length - 1)]);
  else renderTabs();
  updateHostState();
}

function cycle(step: 1 | -1) {
  if (sheets.length < 2 || !active) return;
  activate(sheets[(sheets.indexOf(active) + step + sheets.length) % sheets.length]);
}

/** The host warns before closing the window while any sheet has changes that would be thrown away. */
function updateHostState() {
  const pending = sheets.some((s) => s.txPending);
  if (pending === hostWarnedPending) return;
  hostWarnedPending = pending;
  bridge.send({ type: 'hostState', unsavedChanges: pending });
}

// ---- connection and schema ---------------------------------------------------------------------------------------

const parseConnection = (cs: string) => {
  const parts = new Map<string, string>();
  for (const part of cs.split(';')) {
    const i = part.indexOf('=');
    if (i > 0) parts.set(part.slice(0, i).trim().toLowerCase(), part.slice(i + 1).trim());
  }
  const pick = (...keys: string[]) => keys.map((k) => parts.get(k)).find(Boolean) ?? '';
  const host = pick('host', 'server', 'data source') || 'localhost';
  const port = pick('port');
  return { label: `${port ? `${host}:${port}` : host} / ${pick('database', 'initial catalog', 'db') || '(default)'}`, user: pick('username', 'user id', 'user', 'uid') };
};

/** Rebuilds the tree's roots from what the sheets are connected to. Roots nobody uses any more disappear. */
function rebuildTree() {
  const used = new Set<string>(connecting);
  for (const s of sheets) if (s.connectionString) used.add(s.connectionString);
  for (const cs of [...connectionsByString.keys()]) if (!used.has(cs)) connectionsByString.delete(cs);

  const infos = [...used].map((cs) => ({ cs, entry: connectionsByString.get(cs)!, ...parseConnection(cs) })).filter((i) => i.entry);
  const roots: TreeRoot[] = infos.map((i) => ({
    id: i.entry.id, label: i.label, schema: i.entry.schema,
    // Two connections that read the same (different users, say) are told apart by the user name.
    detail: infos.some((o) => o !== i && o.label === i.label) ? i.user : '',
    current: active?.connectionString === i.cs,
  }));
  tree.setRoots(roots, connectNote);
  const any = roots.some((r) => r.schema);
  treeFilter.disabled = !any;
  refreshBtn.disabled = !active?.connectionString;
  tree.setFilter(treeFilter.value);
}

/** Opens a session for one sheet and shows (or reuses) its connection's schema in the tree. */
async function connectSheet(sheet: Sheet, cs: string) {
  let entry = connectionsByString.get(cs);
  if (!entry) connectionsByString.set(cs, entry = { id: `c${++connectionSeq}`, schema: null });
  connecting.add(cs);
  rebuildTree();
  // Only the first session on a connection brings its schema over for the tree; later ones just fill their own cache.
  const r = await sheet.connect(cs, !entry.schema);
  connecting.delete(cs);
  if (r.ok) {
    sheet.draft = cs;
    if (r.schema) entry.schema = r.schema;
    connectNote = 'Not connected.';
  } else {
    connectNote = `Connect failed: ${r.error ?? 'unknown error'}`;
  }
  rebuildTree();
  return r;
}

/** Connect acts on the visible sheet only: other sheets keep their sessions, on whatever they were connected to. */
async function connectActive() {
  const sheet = active;
  const cs = connEl.value.trim();
  if (!sheet || !cs) return;
  if (sheet.txPending &&
      !window.confirm(`"${sheet.title}" has changes that are not committed. Connecting again closes its session and rolls them back.

Continue?`)) return;
  sheet.draft = cs;
  connectBtn.disabled = true;
  await sheet.disconnect();
  rebuildTree();
  await connectSheet(sheet, cs);
  connectBtn.disabled = false;
  sheet.focusEditor();
}

/** Reloads the visible sheet's connection: every session on it refreshes its cache, and the tree takes the new schema. */
async function refreshSchema() {
  const cs = active?.connectionString;
  const entry = cs ? connectionsByString.get(cs) : undefined;
  if (!active || !cs || !entry) return;
  refreshBtn.disabled = true;
  const same = sheets.filter((s) => s.connectionString === cs);
  const all = await Promise.all(same.map((s) => s.refreshSchema(s === active)));
  const mine = all[same.indexOf(active)];
  if (mine) entry.schema = mine;
  rebuildTree();
}

connEl.addEventListener('input', () => { if (active) active.draft = connEl.value; });
connectBtn.onclick = () => void connectActive();
refreshBtn.onclick = () => void refreshSchema();
treeFilter.addEventListener('input', () => tree.setFilter(treeFilter.value));
$('newSheet').onclick = () => addSheet();
$('newTab').onclick = () => addSheet();

// ---- schema tree width -------------------------------------------------------------------------------------------

/** Dragging (or arrow keys) resizes the tree; the sheets take the rest. The width is remembered between runs. */
function initSideSplitter() {
  const side = $('side'), split = $('sidesplit');
  const DEFAULT = 300, MIN = 160;
  const KEY = 'siftdb.sideWidth';
  const setWidth = (px: number, save = true) => {
    const max = Math.max(MIN, $('main').clientWidth - 640); // leave the sheets room for their toolbar
    const w = Math.round(Math.max(MIN, Math.min(px, max)));
    side.style.width = `${w}px`;
    if (save) try { localStorage.setItem(KEY, String(w)); } catch { /* private window or blocked storage: fine */ }
  };
  try { const saved = Number(localStorage.getItem(KEY)); if (saved) setWidth(saved, false); } catch { /* ignore */ }

  let startX = 0, startW = 0;
  split.onpointerdown = (e) => {
    split.setPointerCapture(e.pointerId);
    split.classList.add('dragging');
    startX = e.clientX; startW = side.offsetWidth;
    e.preventDefault();
  };
  split.onpointermove = (e) => { if (split.hasPointerCapture(e.pointerId)) setWidth(startW + e.clientX - startX); };
  const end = (e: PointerEvent) => { split.classList.remove('dragging'); split.releasePointerCapture?.(e.pointerId); };
  split.onpointerup = end;
  split.onpointercancel = end;
  split.ondblclick = () => setWidth(DEFAULT);
  split.onkeydown = (e) => {
    const step = e.shiftKey ? 80 : 20;
    if (e.key === 'ArrowLeft') setWidth(side.offsetWidth - step);
    else if (e.key === 'ArrowRight') setWidth(side.offsetWidth + step);
    else return;
    e.preventDefault();
  };
}
initSideSplitter();

// ---- keyboard ----------------------------------------------------------------------------------------------------
// Capture phase and stopPropagation: Monaco binds some of these itself (Ctrl+Shift+Enter is "insert line above"), so it
// must not also see them.

window.addEventListener('keydown', (e) => {
  const key = e.key.toLowerCase();
  const handled = (fn: () => void) => { e.preventDefault(); e.stopPropagation(); fn(); };

  if (e.ctrlKey && !e.shiftKey && !e.altKey) {
    if (key === 't') return handled(() => addSheet());
    if (key === 'w' && active) return handled(() => void closeSheet(active!));
  }
  if (e.ctrlKey && !e.altKey && e.key === 'Tab') return handled(() => cycle(e.shiftKey ? -1 : 1));

  if (e.ctrlKey && e.shiftKey && active) {
    const s = active.tx.state;
    if (e.key === 'Enter' && (s === 'open' || s === 'changed')) return handled(() => void active!.txAction('commit'));
    if (key === 'r' && s !== 'none') return handled(() => void active!.txAction('rollback'));
  }
}, true);

// F5 runs the statement from anywhere on the page. Never let it reload the page (it would drop every session); when
// an editor has focus it handles F5 itself and this never sees the key.
window.addEventListener('keydown', (e) => {
  if (e.key !== 'F5') return;
  e.preventDefault();
  void active?.runFromEditor();
});

// ---- go ----------------------------------------------------------------------------------------------------------

bridge = connectBridge(onEvent, onBatch);
addSheet(true);
setInterval(() => active?.tick(), 250);
