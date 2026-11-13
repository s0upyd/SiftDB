import { BatchMeta, Bridge, BridgeEvent, DatabaseSchema, connectBridge } from './bridge';
import { ConnectionCatalog, EditResult, askPassword, editConnection } from './connections';
import { askSaveChanges } from './dialogs';
import { lockIcon, spinner } from './icons';
import { ResultView } from './results';
import { Settings, editSettings } from './settings';
import { FileState, Sheet, SheetContext, Target, TxState, targetKey } from './sheet';
import type { QueryMode } from './editor';
import { revealSelectedTab, scrollTabsWithWheel } from './tabs';
import { SchemaTree, TreeRoot } from './tree';

// The app shell: the connection picker, tabs, the schema sidebar, and routing of engine events to the sheet (or result
// tab) they belong to. Everything about one sheet lives in sheet.ts, one result in results.ts, saved connections in
// connections.ts.

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const pickEl = $<HTMLSelectElement>('connPick');
const connEl = $<HTMLInputElement>('conn');
const connectBtn = $<HTMLButtonElement>('connect');
const editBtn = $<HTMLButtonElement>('editConn');
const newConnBtn = $<HTMLButtonElement>('newConn');
const saveAsBtn = $<HTMLButtonElement>('saveAs');
const tabsEl = $('tabs');
const sheetsEl = $('sheets');
const treeFilter = $<HTMLInputElement>('treeFilter');
const refreshBtn = $<HTMLButtonElement>('refresh');
const tree = new SchemaTree($('tree'), $('treeSummary'));
scrollTabsWithWheel(tabsEl);

const INITIAL_SHEET = [
  '-- Put the cursor in a statement and press F5 or Ctrl+Enter. Select text to run just that.',
  '-- Ctrl+. cancels. The ▶ in the margin runs a statement with the mouse.',
  'select version();', // works on any Postgres database
  '',
].join('\n');

let bridge: Bridge;
let catalog: ConnectionCatalog;
let settings: Settings;
let dark = false;
/**
 * One entry per distinct target in use (a saved connection, or a pasted string), by targetKey. Sheets on the same target
 * share an entry, and so a root in the schema tree; each of them still has its own session. A pasted string (with its
 * password) never reaches the DOM.
 */
interface ConnectionEntry { id: string; schema: DatabaseSchema | null }
const entries = new Map<string, ConnectionEntry>();
/** Targets with a connect in flight: they show in the tree as "loading…". */
const connecting = new Map<string, Target>();
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
  askPassword,
  persist: () => scheduleAutosave(),
};

// ---- events from the engine --------------------------------------------------------------------------------------

function onEvent(e: BridgeEvent) {
  switch (e.type) {
    case 'init':
      dark = !!e.dark;
      document.documentElement.dataset.theme = dark ? 'dark' : 'light';
      sheets.forEach((s) => s.setTheme(dark));
      if (e.version) $('settings').title = `Settings (Ctrl+,) · SiftDB ${e.version}`;
      return;
    case 'updateReady': {
      // Downloaded in the background by the host. Restarting goes through the usual close (warning, last autosave).
      const button = $('update');
      button.textContent = `Restart to update to ${e.version}`;
      button.title = `SiftDB ${e.version} is downloaded. Restart now to use it: your sheets come back as they were.\n`
        + 'Or keep working: it is installed the next time SiftDB starts.';
      button.hidden = false;
      return;
    }
    case 'flushBeforeClose':
      // The window is closing: write the last edits now, then let the host close it.
      void autosaveNow().finally(() => bridge.send({ type: 'flushed' }));
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
    tab.title = sheet.file?.path ?? '';

    // A dot after the name while the sheet has edits that are not in its file (design section 6).
    const unsaved = document.createElement('span');
    unsaved.className = 'unsaved';
    unsaved.textContent = '●';
    unsaved.hidden = !sheet.dirty;
    unsaved.title = 'Unsaved changes';
    unsaved.setAttribute('role', 'img');
    unsaved.setAttribute('aria-label', 'unsaved changes');

    // A lock on a read-only sheet: a refused write is then never a mystery, whichever tab you are looking at.
    const lock = document.createElement('span');
    lock.className = 'lock';
    lock.append(lockIcon());
    lock.hidden = !sheet.readOnly;
    lock.title = 'Read-only connection: the server refuses changes';
    lock.setAttribute('role', 'img');
    lock.setAttribute('aria-label', 'read-only');

    const close = document.createElement('button');
    close.className = 'close';
    close.textContent = '×';
    close.title = 'Close this sheet (Ctrl+W)';
    close.setAttribute('aria-label', `Close ${sheet.title}`);
    close.onclick = (e) => { e.stopPropagation(); void closeSheet(sheet); };

    // While a statement runs: a small spinner, so you can work on another sheet and still see it going. When a long one
    // finished while you were elsewhere: its outcome, until you look at the sheet.
    const run = document.createElement('span');
    run.setAttribute('role', 'img');
    if (sheet.isRunning) {
      run.className = 'running';
      run.append(spinner());
      run.title = 'Running';
      run.setAttribute('aria-label', 'running');
    } else if (sheet.finished) {
      const f = sheet.finished;
      run.className = `tabdone ${f.outcome}`;
      run.textContent = f.outcome === 'complete' ? '✓' : f.outcome === 'error' ? '✖' : '■';
      run.title = Sheet.describeFinished(f);
      run.setAttribute('aria-label', run.title.toLowerCase());
    } else run.hidden = true;

    tab.append(dot, lock, run, title, unsaved, close);
    return tab;
  }));
  revealSelectedTab(tabsEl);
}

function activate(sheet: Sheet) {
  active = sheet;
  sheets.forEach((s) => s.setActive(s === sheet));
  renderPicker(); // the connection picker always describes the visible sheet
  renderTabs();
  rebuildTree();
  sheet.focusEditor();
  scheduleAutosave(); // which sheet is active comes back after a restart
}

/** A sheet added to the strip, not yet shown or connected. */
function createSheet(untitled: string, text = '', cursorLine = 1): Sheet {
  const sheet = new Sheet(ctx, sheetsEl, untitled, text, cursorLine);
  sheet.setTheme(dark);
  sheets.push(sheet);
  return sheet;
}

function addSheet(first = false): Sheet {
  // A new sheet starts on the connection of the sheet you were looking at, with a session of its own.
  const inherit = active?.target ?? null;
  const sheet = createSheet(`Sheet ${++sheetCounter}`, first ? INITIAL_SHEET : '', first ? 3 : 1);
  sheet.setMode(settings.values.newSheetMode); // the preferred mode (user settings)
  sheet.draftChoice = inherit ? choiceFor(inherit) : active?.draftChoice ?? defaultChoice();
  sheet.draftText = inherit?.kind === 'string' ? inherit.cs : active?.draftText ?? '';
  activate(sheet);
  if (inherit) void connectSheet(sheet, inherit);
  return sheet;
}

async function closeSheet(sheet: Sheet) {
  // Unsaved text would be gone with the tab (the window closing never asks: autosave brings everything back).
  if (sheet.dirty && sheet.text.trim() !== '') {
    activate(sheet);
    const answer = await askSaveChanges(sheet.title);
    if (answer === 'cancel') return;
    if (answer === 'save' && !(await saveSheet(sheet))) return;
  }
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
  scheduleAutosave(); // its autosaved text goes with it
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

// ---- the connection picker ---------------------------------------------------------------------------------------
// One choice per saved connection ('saved:<id>'), plus 'string' for a pasted connection string. Each sheet remembers
// its own choice, so switching tabs shows that sheet's.

const STRING_CHOICE = 'string';
/** No connection chosen: a file named one that is not saved here, and SiftDB never guesses (design section 6). */
const NONE_CHOICE = 'none';
const choiceFor = (t: Target) => (t.kind === 'saved' ? `saved:${t.id}` : STRING_CHOICE);
const savedIdOf = (choice: string) => (choice.startsWith('saved:') ? choice.slice(6) : null);

/** At startup and for new sheets: the last connection used, else the first saved one, else a pasted string. */
function defaultChoice(): string {
  const last = catalog?.lastUsed && catalog.byId(catalog.lastUsed);
  if (last) return `saved:${last.id}`;
  return catalog?.saved[0] ? `saved:${catalog.saved[0].id}` : STRING_CHOICE;
}

/** What Connect would open for this sheet, or null when there is nothing to open (an empty string). */
function draftTarget(sheet: Sheet): Target | null {
  const id = savedIdOf(sheet.draftChoice);
  if (id) return catalog.byId(id) ? { kind: 'saved', id } : null;
  const cs = sheet.draftText.trim();
  return cs ? { kind: 'string', cs } : null;
}

function renderPicker() {
  const option = (value: string, text: string) => Object.assign(document.createElement('option'), { value, textContent: text });
  const sheet = active;
  let choice = sheet?.draftChoice ?? defaultChoice();
  const none = option(NONE_CHOICE, sheet?.connectionName ? `Pick a connection (the file names ${sheet.connectionName})` : 'Pick a connection');
  none.disabled = true;
  pickEl.replaceChildren(
    ...(choice === NONE_CHOICE ? [none] : []),
    ...catalog.saved.map((c) => option(`saved:${c.id}`, `${c.name}${c.readOnly ? ' (read-only)' : ''} — ${catalog.describe(c)}`)),
    option(STRING_CHOICE, 'Connection string…'));

  const id = savedIdOf(choice);
  if (id && !catalog.byId(id)) { // deleted since this sheet last showed it
    choice = defaultChoice();
    if (sheet) sheet.draftChoice = choice;
  }
  pickEl.value = choice;
  connEl.value = sheet?.draftText ?? '';
  const isString = choice === STRING_CHOICE;
  connEl.hidden = !isString;
  saveAsBtn.hidden = !isString;
  editBtn.disabled = isString || choice === NONE_CHOICE || !!catalog.error;
  newConnBtn.disabled = !!catalog.error || catalog.providers.length === 0;
  const picked = id ? catalog.byId(id) : null;
  pickEl.title = picked
    ? `${picked.name}: ${catalog.provider(picked.provider)?.displayName ?? picked.provider}, ${catalog.describe(picked)}${picked.readOnly ? '. Read-only: the server refuses changes.' : picked.autoTransaction ? '. Auto-transaction: always inside a transaction; changes wait for Commit.' : ''}`
    : 'Type a connection string, or save a connection to pick it here';
}

pickEl.addEventListener('change', () => {
  if (!active) return;
  active.draftChoice = pickEl.value;
  renderPicker();
  if (pickEl.value === STRING_CHOICE) connEl.focus();
  scheduleAutosave();
});
connEl.addEventListener('input', () => { if (active) active.draftText = connEl.value; });
connEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') void connectActive(); });

/** After the form closes: reload the list, and point the visible sheet at a connection it just saved. */
async function afterEdit(r: EditResult) {
  if (r.kind === 'cancelled') return;
  await catalog.load();
  if (r.kind === 'saved' && active) active.draftChoice = `saved:${r.id}`;
  if (r.kind === 'deleted') for (const s of sheets) if (s.draftChoice === `saved:${r.id}`) s.draftChoice = defaultChoice();
  renderPicker();
  rebuildTree(); // a rename shows in the tree straight away
}

editBtn.onclick = async () => {
  const id = savedIdOf(pickEl.value);
  const c = id ? catalog.byId(id) : undefined;
  if (c) await afterEdit(await editConnection(bridge, catalog, c));
};
newConnBtn.onclick = async () => afterEdit(await editConnection(bridge, catalog, null));
/** Saving a pasted string: the engine takes it apart into fields. Its password stays in the engine. */
saveAsBtn.onclick = async () => {
  const cs = connEl.value.trim();
  if (!cs) { active?.setMessage('Type a connection string first, then save it.', 'warning'); return; }
  const r = await bridge.request({ type: 'parseConnectionString', provider: 'postgres', connectionString: cs });
  if (r.type === 'error') { active?.setMessage(`That connection string cannot be read: ${r.message}`, 'error'); return; }
  await afterEdit(await editConnection(bridge, catalog, null, { fields: r.fields, hasPassword: r.hasPassword, fromConnectionString: cs }));
};

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

/** How a target reads in the tree: a saved connection by its name, a pasted string by host / database. */
function describeTarget(t: Target): { label: string; detail: string; user: string } {
  if (t.kind === 'saved') {
    const c = catalog.byId(t.id);
    return c ? { label: c.name, detail: catalog.describe(c), user: '' } : { label: '(deleted connection)', detail: '', user: '' };
  }
  const p = parseConnection(t.cs);
  return { label: p.label, detail: '', user: p.user };
}

/** Rebuilds the tree's roots from what the sheets are connected to. Roots nobody uses any more disappear. */
function rebuildTree() {
  const used = new Map<string, Target>(connecting);
  for (const s of sheets) if (s.target) used.set(targetKey(s.target), s.target);
  for (const key of [...entries.keys()]) if (!used.has(key)) entries.delete(key);

  const activeKey = active?.target ? targetKey(active.target) : null;
  const infos = [...used].map(([key, t]) => ({ key, entry: entries.get(key)!, ...describeTarget(t) })).filter((i) => i.entry);
  const roots: TreeRoot[] = infos.map((i) => ({
    id: i.entry.id, label: i.label, schema: i.entry.schema,
    // Two pasted strings that read the same (different users, say) are told apart by the user name.
    detail: i.detail || (infos.some((o) => o !== i && o.label === i.label) ? i.user : ''),
    current: activeKey === i.key,
    readOnly: sheets.some((s) => s.readOnly && s.target && targetKey(s.target) === i.key),
  }));
  tree.setRoots(roots, connectNote);
  const any = roots.some((r) => r.schema);
  treeFilter.disabled = !any;
  refreshBtn.disabled = !active?.target;
  tree.setFilter(treeFilter.value);
}

/** Opens a session for one sheet and shows (or reuses) its connection's schema in the tree. */
async function connectSheet(sheet: Sheet, target: Target) {
  const key = targetKey(target);
  let entry = entries.get(key);
  if (!entry) entries.set(key, entry = { id: `c${++connectionSeq}`, schema: null });
  connecting.set(key, target);
  rebuildTree();
  // Only the first session on a connection brings its schema over for the tree; later ones just fill their own cache.
  const r = await sheet.connect(target, !entry.schema);
  connecting.delete(key);
  if (r.ok) {
    sheet.draftChoice = choiceFor(target);
    if (target.kind === 'string') sheet.draftText = target.cs;
    else catalog.lastUsed = target.id;
    if (r.schema) entry.schema = r.schema;
    connectNote = catalog.error ?? 'Not connected.';
  } else if (!r.cancelled) {
    connectNote = `Connect failed: ${r.error ?? 'unknown error'}`;
  }
  rebuildTree();
  if (sheet === active) renderPicker();
  return r;
}

/** Connect acts on the visible sheet only: other sheets keep their sessions, on whatever they were connected to. */
async function connectActive() {
  const sheet = active;
  if (!sheet) return;
  const target = draftTarget(sheet);
  if (!target) {
    sheet.setMessage(sheet.draftChoice === NONE_CHOICE
      ? 'Pick a connection first. SiftDB does not guess which database a file is for.'
      : 'Type a connection string, or pick a saved connection, then press Connect.', 'warning');
    if (sheet.draftChoice === STRING_CHOICE) connEl.focus();
    return;
  }
  if (sheet.txPending &&
      !window.confirm(`"${sheet.title}" has changes that are not committed. Connecting again closes its session and rolls them back.

Continue?`)) return;
  connectBtn.disabled = true;
  await sheet.disconnect();
  rebuildTree();
  await connectSheet(sheet, target);
  connectBtn.disabled = false;
  sheet.focusEditor();
}

/** Reloads the visible sheet's connection: every session on it refreshes its cache, and the tree takes the new schema. */
async function refreshSchema() {
  const key = active?.target ? targetKey(active.target) : null;
  const entry = key ? entries.get(key) : undefined;
  if (!active || !key || !entry) return;
  refreshBtn.disabled = true;
  const same = sheets.filter((s) => s.target && targetKey(s.target) === key);
  const all = await Promise.all(same.map((s) => s.refreshSchema(s === active)));
  const mine = all[same.indexOf(active)];
  if (mine) entry.schema = mine;
  rebuildTree();
}

connectBtn.onclick = () => void connectActive();
refreshBtn.onclick = () => void refreshSchema();
treeFilter.addEventListener('input', () => tree.setFilter(treeFilter.value));
$('newSheet').onclick = () => addSheet();
$('newTab').onclick = () => addSheet();

// ---- files and autosave (design section 6) -----------------------------------------------------------------------
// Opening and saving go through the host (the native dialogs) and the engine (the file). Autosave keeps every sheet,
// saved or not, a moment after each change, so closing the window loses nothing.

const AUTOSAVE_DELAY_MS = 600;
/** Per sheet id, the editor version last written to the session: unchanged text is not sent again. */
const autosaved = new Map<string, number>();
let autosaveTimer: number | undefined;
let autosaveRunning: Promise<void> | null = null;
let autosavePending = false;
/** While sheets are being restored, their setup must not trigger saves of a half-built session. */
let restoring = true;

/** The saved connection a sheet will be saved with: the one it is connected to, else the one picked. */
function savedIdFor(sheet: Sheet): string | null {
  if (sheet.target) return sheet.target.kind === 'saved' ? sheet.target.id : null;
  const id = savedIdOf(sheet.draftChoice);
  return id && catalog.byId(id) ? id : null;
}

function scheduleAutosave() {
  if (restoring) return;
  window.clearTimeout(autosaveTimer);
  autosaveTimer = window.setTimeout(() => void autosaveNow(), AUTOSAVE_DELAY_MS);
}

/** Writes the session now. One write at a time; a request during a write runs straight after it. */
async function autosaveNow(): Promise<void> {
  window.clearTimeout(autosaveTimer);
  if (restoring) return;
  if (autosaveRunning) { autosavePending = true; return autosaveRunning; }
  autosaveRunning = (async () => {
    do {
      autosavePending = false;
      const versions = sheets.map((s) => [s.id, s.version] as const);
      const payload = sheets.map((s) => {
        const savedId = savedIdFor(s);
        return {
          id: s.id, title: s.untitled, path: s.file?.path ?? null, dirty: s.dirty, savedId,
          // A pasted connection string is never kept (it may hold a password); a name is, so a file's intent survives.
          connectionName: savedId ? catalog.byId(savedId)?.name ?? null : s.connectionName,
          mode: s.mode, lineEnding: s.file?.lineEnding ?? 'crlf', encoding: s.file?.encoding ?? 'utf-8',
          headerOther: s.file?.headerOther ?? [],
          ...(autosaved.get(s.id) !== s.version ? { text: s.text } : {}),
        };
      });
      const r = await bridge.request({ type: 'saveSession', active: active?.id ?? null, sheets: payload });
      if (r.type === 'error') { active?.setMessage(`Autosave failed: ${r.message}`, 'error'); break; }
      autosaved.clear();
      for (const [id, v] of versions) autosaved.set(id, v);
    } while (autosavePending);
  })();
  try { await autosaveRunning; } finally { autosaveRunning = null; }
}

/** Points a sheet at the connection a file (or a restored session) names: saved here, or unknown and said so. */
function applyConnection(sheet: Sheet, savedId: string | null, connectionName: string | null) {
  sheet.connectionName = savedId ? null : connectionName;
  if (savedId) sheet.draftChoice = `saved:${savedId}`;
  else if (connectionName) {
    sheet.draftChoice = NONE_CHOICE;
    sheet.setMessage(sheet.file
      ? `This file names the connection '${connectionName}', which is not saved here. Pick a connection.`
      : `This sheet was on the connection '${connectionName}', which is no longer saved. Pick a connection.`, 'warning');
  }
}

/** Ctrl+O: the native dialog, then the file in a sheet (an already-open file just comes to the front). */
async function openFile() {
  const d = await bridge.request({ type: 'openFileDialog', currentPath: active?.file?.path ?? null });
  if (!d.path) return;
  const already = sheets.find((s) => s.file?.path.toLowerCase() === String(d.path).toLowerCase());
  if (already) { activate(already); return; }
  const r = await bridge.request({ type: 'openFile', path: d.path });
  if (r.type === 'error') { active?.setMessage(`Could not open ${d.path}: ${r.message}`, 'error'); return; }

  // A blank untitled sheet is reused, unless the file names a connection and that sheet is connected elsewhere.
  const namesConnection = !!(r.savedId || r.connectionName);
  let sheet: Sheet;
  if (active?.blank && !(namesConnection && active.connected)) sheet = active;
  else if (namesConnection) {
    sheet = createSheet(`Sheet ${++sheetCounter}`); // its own connection: not inherited from the sheet you were on
    sheet.draftText = '';
  } else sheet = addSheet();
  const file: FileState = { path: r.path, fileName: r.fileName, lineEnding: r.lineEnding, encoding: r.encoding, headerOther: r.headerOther };
  sheet.load(r.text, file, false);
  sheet.setMode((r.mode as QueryMode | null) ?? settings.values.newSheetMode); // a file that does not say takes the preferred mode
  sheet.setMessage('');
  if (namesConnection) applyConnection(sheet, r.savedId ?? null, r.connectionName ?? null);
  activate(sheet);
}

/**
 * Ctrl+S (Save As when the sheet has no file yet) and Ctrl+Shift+S. An Assisted sheet saves the SQL that runs: its
 * greyed quotes become real text first. False when nothing was saved (cancelled, or it failed).
 */
async function saveSheet(sheet: Sheet, as = false): Promise<boolean> {
  let path = sheet.file?.path ?? null;
  if (!path || as) {
    const d = await bridge.request({
      type: 'saveFileDialog', currentPath: path, suggestedName: sheet.file?.fileName ?? `${sheet.untitled}.sql`,
    });
    if (!d.path) return false;
    path = d.path as string;
  }
  let note = '';
  if (sheet.mode === 'assisted' && !(await sheet.acceptCorrectionsForSave())) note = ', as typed (connect first to have its quoting checked)';
  const savedId = savedIdFor(sheet);
  const lineEnding = sheet.file?.lineEnding ?? 'crlf', encoding = sheet.file?.encoding ?? 'utf-8', headerOther = sheet.file?.headerOther ?? [];
  const r = await bridge.request({
    type: 'saveFile', path, text: sheet.text, savedId, connectionName: savedId ? null : sheet.connectionName,
    mode: sheet.mode, lineEnding, encoding, headerOther,
  });
  if (r.type === 'error') { sheet.setMessage(`Not saved: ${r.message}`, 'error'); return false; }
  sheet.markSaved({ path: r.path, fileName: r.fileName, lineEnding, encoding, headerOther });
  sheet.setMessage(`Saved ${r.fileName}${note}.`);
  renderTabs();
  return true;
}

$('openFile').onclick = () => void openFile();
$('saveFile').onclick = () => { if (active) void saveSheet(active); };
/** Settings saved: open results pick up new styling rules straight away. */
async function openSettings() {
  if (await editSettings(settings)) sheets.forEach((s) => s.restyle());
}
$('settings').onclick = () => void openSettings();
$('update').onclick = () => bridge.send({ type: 'applyUpdate' });

/** At startup: the sheets from last time, in order, without connecting (design section 6). Nothing saved: one new sheet. */
async function restoreSession() {
  const r = await bridge.request({ type: 'loadSession' });
  const saved = r.type === 'session' ? (r.sheets as any[]) : [];
  let toShow: Sheet | null = null;
  for (const s of saved) {
    const n = /^Sheet (\d+)$/.exec(s.title)?.[1];
    if (n) sheetCounter = Math.max(sheetCounter, Number(n));
    const sheet = createSheet(s.title ?? `Sheet ${++sheetCounter}`);
    const file: FileState | null = s.path
      ? { path: s.path, fileName: s.fileName, lineEnding: s.lineEnding, encoding: s.encoding, headerOther: s.headerOther ?? [] }
      : null;
    sheet.load(s.text ?? '', file, !!s.dirty && !(file === null && !s.text));
    sheet.setMode(s.mode === 'strict' ? 'strict' : 'assisted');
    sheet.draftChoice = defaultChoice();
    applyConnection(sheet, s.savedId ?? null, s.connectionName ?? null);
    if (s.missing) sheet.setMessage(`${s.path} is no longer there. This is the text autosaved before; save it to keep it.`, 'warning');
    if (s.id === r.active) toShow = sheet;
  }
  restoring = false;
  if (sheets.length === 0) addSheet(true);
  else activate(toShow ?? sheets[0]);
  await autosaveNow(); // the session now names these sheets
}

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

/** A connection form or password prompt is open: the sheets' shortcuts must not act behind it. */
const dialogOpen = () => document.querySelector('dialog[open]') !== null;

window.addEventListener('keydown', (e) => {
  if (dialogOpen()) return;
  const key = e.key.toLowerCase();
  const handled = (fn: () => void) => { e.preventDefault(); e.stopPropagation(); fn(); };

  if (e.ctrlKey && !e.shiftKey && !e.altKey) {
    if (key === 't') return handled(() => addSheet());
    if (key === 'w' && active) return handled(() => void closeSheet(active!));
    if (key === 's' && active) return handled(() => void saveSheet(active!));
    if (key === 'o') return handled(() => void openFile());
    if (key === ',') return handled(() => void openSettings());
  }
  if (e.ctrlKey && e.shiftKey && !e.altKey && key === 's' && active) return handled(() => void saveSheet(active!, true));
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
  e.preventDefault(); // never a page reload, dialog or not
  if (!dialogOpen()) void active?.runFromEditor();
});

// ---- go ----------------------------------------------------------------------------------------------------------

bridge = connectBridge(onEvent, onBatch);
catalog = new ConnectionCatalog(bridge);
settings = new Settings(bridge);
// The first sheet starts on the last connection used, selected but not connected (design section 8).
void Promise.all([catalog.load(), settings.load()]).then(async () => {
  if (catalog.error) connectNote = catalog.error; // unreadable file: say why, and pasted strings still work
  try {
    await restoreSession();
  } catch {
    restoring = false; // a session that cannot be restored must not stop the app starting
    if (sheets.length === 0) addSheet(true);
  }
});
setInterval(() => active?.tick(), 250);
