import { BatchMeta, Bridge, BridgeEvent, DatabaseSchema, connectBridge } from './bridge';
import { ResultView } from './results';
import { Sheet, SheetContext, TxState } from './sheet';
import { revealSelectedTab, scrollTabsWithWheel } from './tabs';
import { SchemaTree } from './tree';

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
/** The connection string the sheets share. Each sheet opens its own session on it. Null until a connect succeeds. */
let connectionString: string | null = null;
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
      if (e.defaultConnection) connEl.value = e.defaultConnection;
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
  renderTabs();
  sheet.focusEditor();
}

function addSheet(first = false): Sheet {
  const sheet = new Sheet(ctx, sheetsEl, `Sheet ${++sheetCounter}`, first ? INITIAL_SHEET : '', first ? 3 : 1);
  sheets.push(sheet);
  activate(sheet);
  sheet.setTheme(dark);
  // A new sheet gets its own session on the same connection, right away.
  if (connectionString) void sheet.connect(connectionString, false);
  return sheet;
}

async function closeSheet(sheet: Sheet) {
  if (sheet.txPending &&
      !window.confirm(`"${sheet.title}" has changes that are not committed. Closing it rolls them back.\n\nClose anyway?`)) return;
  const at = sheets.indexOf(sheet);
  sheets.splice(at, 1);
  const wasActive = active === sheet;
  await sheet.close();
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

async function connectAll() {
  const pending = sheets.filter((s) => s.txPending);
  if (pending.length &&
      !window.confirm(`Connecting again closes every sheet's session. ${pending.length} ` +
        `${pending.length === 1 ? 'sheet has' : 'sheets have'} changes that are not committed and would be rolled back.\n\nContinue?`)) return;

  const cs = connEl.value;
  connectBtn.disabled = true;
  tree.clear('Connecting…');
  await Promise.all(sheets.map((s) => s.disconnect()));

  // Every sheet opens its own session. Only the visible one sends its schema back for the sidebar.
  const results = await Promise.all(sheets.map((s) => s.connect(cs, s === active)));
  connectBtn.disabled = false;
  connectionString = results.some((r) => r.ok) ? cs : null;

  const mine = results[sheets.indexOf(active!)];
  if (mine?.ok && mine.schema) {
    showSchema(mine.schema);
  } else {
    tree.clear(`Connect failed: ${mine?.error ?? 'unknown error'}`);
    treeFilter.disabled = true;
    refreshBtn.disabled = true;
  }
  active?.focusEditor();
}

function showSchema(schema: DatabaseSchema) {
  treeFilter.disabled = false;
  refreshBtn.disabled = false;
  tree.setSchema(schema);
  tree.setFilter(treeFilter.value);
}

async function refreshSchema() {
  refreshBtn.disabled = true;
  tree.clear('Loading schema…');
  // Every session refreshes its own cache (completion reads it); the visible sheet also feeds the sidebar.
  const all = await Promise.all(sheets.map((s) => s.refreshSchema(s === active)));
  const mine = all[sheets.indexOf(active!)];
  refreshBtn.disabled = false;
  if (mine) showSchema(mine);
  else tree.clear('Schema unavailable.');
}

connectBtn.onclick = () => void connectAll();
refreshBtn.onclick = () => void refreshSchema();
treeFilter.addEventListener('input', () => tree.setFilter(treeFilter.value));
$('newSheet').onclick = () => addSheet();
$('newTab').onclick = () => addSheet();

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
