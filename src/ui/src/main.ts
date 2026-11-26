import { BatchMeta, Bridge, BridgeEvent, DatabaseSchema, connectBridge } from './bridge';
import { ConnectionCatalog, EditResult, askPassword, editConnection } from './connections';
import { askSaveChanges } from './dialogs';
import { applyIcons, lockIcon, spinner, waitIcon } from './icons';
import type { OtherSession } from './blockers';

/** Which of the other sessions an event names are this window's own sheets (design section 14, F3). */
type Mine = { pid: number; connectionId: number }[];
import { ResultView } from './results';
import { Command, CommandGroup, CommandRegistry, GROUPS, commands, shortcutOf } from './commands';
import { PaletteItem, choose, paletteDone, paletteOpen, pickFrom, shortcutFor } from './palette';
import { toggleMiniMap } from './minimap';
import { Settings, UpdateCheckResult, UpdateHooks, describeUpdateCheck, editSettings } from './settings';
import { Release, showUpdateNotice, showWhatsNew } from './whatsnew';
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
  otherSheet: (connectionId) => {
    const s = connections.get(connectionId);
    return s ? { title: s.title, show: () => activate(s) } : null;
  },
};

// ---- events from the engine --------------------------------------------------------------------------------------

/** Schema reloads waiting to happen, by tree root (see "schemaChanged"). */
const schemaReloads = new Map<string, ReturnType<typeof setTimeout>>();

function onEvent(e: BridgeEvent) {
  switch (e.type) {
    case 'init':
      dark = !!e.dark;
      document.documentElement.dataset.theme = dark ? 'dark' : 'light';
      sheets.forEach((s) => s.setTheme(dark));
      if (e.version) $('settings').title = `Settings (Ctrl+,) · SiftDB ${e.version}`;
      gotVersion(typeof e.version === 'string' ? e.version : null);
      return;
    case 'updateProgress':
      // A download a check by hand started (Settings › About): its percent.
      updateProgress.forEach((fn) => fn(String(e.version), Number(e.percent)));
      return;
    case 'updateReady': {
      // Downloaded in the background by the host. Restarting goes through the usual close (warning, last autosave).
      updateReadyVersion = String(e.version);
      const button = $('update');
      button.querySelector('.label')!.textContent = `Restart to update to ${e.version}`;
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
    // Who's blocking me (design section 14, F3): whom a sheet's statement waits for, and who waits for its transaction.
    case 'blockers':
      connections.get(e.connectionId)?.onBlockers((e.blockers as OtherSession[] | undefined) ?? [], (e.mine as Mine | undefined) ?? []);
      return;
    case 'blocking':
      connections.get(e.connectionId)?.onBlocking((e.sessions as OtherSession[] | undefined) ?? [], (e.mine as Mine | undefined) ?? []);
      return;
    case 'transactionUnknown':
      connections.get(e.connectionId)?.setMessage(`transaction state unavailable: ${e.message}`, 'warning');
      return;
    case 'schemaChanged': {
      // Columns learnt from rows (DynamoDB, contract v17): load that connection's schema again, once things settle,
      // so completion, the resolver and the tree know them. Several tables learnt at once make one reload.
      const target = connections.get(e.connectionId)?.target;
      const id = target ? entries.get(targetKey(target))?.id : undefined;
      if (!id) return;
      clearTimeout(schemaReloads.get(id));
      schemaReloads.set(id, setTimeout(() => { schemaReloads.delete(id); void refreshSchema(id); }, 400));
      return;
    }
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
    // Changes waiting for Commit, or a failed transaction waiting for Rollback, colour the whole tab, so they are seen from
    // any other sheet. An open transaction with nothing changed only has its dot: auto-transaction sheets are always in one.
    if (sheet.tx.state === 'changed' || sheet.tx.state === 'failed') tab.classList.add(`tx-${sheet.tx.state}`);
    tab.onclick = () => activate(sheet);

    // A dot when this sheet's session is inside a transaction, so it can't be forgotten behind another tab.
    const dot = document.createElement('span');
    dot.className = `dot tx-${sheet.tx.state}`;
    dot.hidden = sheet.tx.state === 'none';
    dot.title = TX_LABEL[sheet.tx.state];

    const title = document.createElement('span');
    title.className = 'title';
    title.textContent = sheet.title;
    tab.title = [sheet.file?.path, sheet.tx.state === 'changed' ? 'Changes pending: not committed yet'
      : sheet.tx.state === 'failed' ? 'Transaction aborted: roll back to continue' : ''].filter(Boolean).join('\n');

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
    if (sheet.waitingForLock) {
      // Waiting for another session's lock (design section 14, F3): an hourglass, not the spinner, so it is told apart.
      run.className = 'running waiting';
      run.append(waitIcon());
      run.title = 'Waiting for another session\'s lock: open the sheet to see who';
      run.setAttribute('aria-label', 'waiting for a lock');
    } else if (sheet.isRunning) {
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
  if (last && !last.unavailable) return `saved:${last.id}`;
  const first = catalog?.saved.find((c) => !c.unavailable);
  return first ? `saved:${first.id}` : STRING_CHOICE;
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
    ...catalog.saved.map((c) => {
      // Its provider didn't load (F18): listed, greyed, and not pickable; one a sheet already shows stays shown.
      const o = option(`saved:${c.id}`, `${c.name}${c.unavailable ? ' (unavailable)' : c.readOnly ? ' (read-only)' : ''} — ${catalog.describe(c)}`);
      if (c.unavailable) { o.disabled = true; o.title = c.unavailable; }
      return o;
    }),
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
  const picked = id ? catalog.byId(id) : null;
  editBtn.disabled = isString || choice === NONE_CHOICE || !!catalog.error || !!picked?.unavailable;
  newConnBtn.disabled = !!catalog.error || catalog.providers.length === 0;
  pickEl.title = picked?.unavailable
    ? `${picked.name}: ${picked.unavailable}`
    : picked
    ? `${picked.name}:${catalog.provider(picked.provider)?.displayName ?? picked.provider}, ${catalog.describe(picked)}${picked.readOnly ? '. Read-only: the server refuses changes.' : picked.autoTransaction ? '. Auto-transaction: always inside a transaction; changes wait for Commit.' : ''}`
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

/** The saved connection the picker shows for the visible sheet, in the connection form. */
async function editPickedConnection() {
  const id = active ? savedIdOf(active.draftChoice) : null;
  const c = id ? catalog.byId(id) : undefined;
  if (c) await afterEdit(await editConnection(bridge, catalog, c));
}
const newConnection = async () => afterEdit(await editConnection(bridge, catalog, null));
editBtn.onclick = () => void editPickedConnection();
newConnBtn.onclick = () => void newConnection();
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
  $<HTMLButtonElement>('treeCols').disabled = !any;
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

/**
 * Reloads a connection (the visible sheet's, or the tree root `rootId`): every session on it refreshes its cache, and the
 * tree takes the new schema (and drops the details it had loaded from the old one).
 */
async function refreshSchema(rootId?: string) {
  const key = rootId ? [...entries].find(([, e]) => e.id === rootId)?.[0] : active?.target ? targetKey(active.target) : null;
  const entry = key ? entries.get(key) : undefined;
  if (!key || !entry) return;
  const same = sheets.filter((s) => s.target && targetKey(s.target) === key);
  if (!same.length) return;
  refreshBtn.disabled = true;
  const asker = same.includes(active!) ? active! : same[0];
  const all = await Promise.all(same.map((s) => s.refreshSchema(s === asker)));
  const mine = all[same.indexOf(asker)];
  if (mine) entry.schema = mine;
  rebuildTree();
}

// The schema explorer (design section 14, F13) asks for details on one of a root's sessions, the visible sheet's if it
// is on that root, and puts scripts into the visible sheet.
tree.ctx = {
  request: async (rootId, cmd) => {
    const key = [...entries].find(([, e]) => e.id === rootId)?.[0];
    const onIt = sheets.filter((s) => s.connected && s.target && targetKey(s.target) === key);
    const sheet = onIt.includes(active!) ? active! : onIt[0];
    return sheet ? bridge.request({ ...cmd, connectionId: sheet.connectionId }) : null;
  },
  insertScript: (sql) => { active?.insertScript(sql); },
  refresh: (rootId) => void refreshSchema(rootId),
};

connectBtn.onclick = () => void connectActive();
refreshBtn.onclick = () => void refreshSchema();
treeFilter.addEventListener('input', () => tree.setFilter(treeFilter.value));
// The filter's "columns too": tables with a matching column show, opened on those columns.
const treeCols = $<HTMLButtonElement>('treeCols');
treeCols.onclick = () => {
  const on = treeCols.getAttribute('aria-pressed') !== 'true';
  treeCols.setAttribute('aria-pressed', String(on));
  tree.setMatchColumns(on);
};
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
  if (d.path) await openPath(String(d.path));
}

/** A file in a sheet, from the dialog or the recent files (the engine adds it to them). */
async function openPath(path: string) {
  const already = sheets.find((s) => s.file?.path.toLowerCase() === path.toLowerCase());
  if (already) { activate(already); return; }
  const r = await bridge.request({ type: 'openFile', path });
  if (r.type === 'error') { active?.setMessage(`Could not open ${path}: ${r.message}`, 'error'); return; }

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
// ---- updates by hand (design section 9) ----------------------------------------------------------------------------
// The host checks by itself a little after start and every few hours; these are for checking now.

/** The version the host has downloaded and is waiting to restart into, once it says so. */
let updateReadyVersion: string | null = null;
const updateProgress = new Set<(version: string, percent: number) => void>();

const updateHooks: UpdateHooks = {
  check: async () => {
    const r = await bridge.request({ type: 'checkForUpdates' });
    if (r.type === 'error') return { status: 'failed', version: null, error: r.message };
    return { status: r.status as UpdateCheckResult['status'], version: (r.version as string | null) ?? null, error: (r.error as string | null) ?? null };
  },
  ready: () => updateReadyVersion,
  onProgress: (fn) => { updateProgress.add(fn); return () => updateProgress.delete(fn); },
  restart: () => bridge.send({ type: 'applyUpdate' }),
};

/** The palette's "Check for updates": the outcome in the sheet's message bar; a downloaded one also shows Restart at the top. */
async function checkForUpdates() {
  active?.setMessage('Checking for updates…');
  const r = await updateHooks.check();
  active?.setMessage(describeUpdateCheck(r, await appVersion), r.status === 'failed' ? 'warning' : 'info');
}

async function openSettings(category?: string) {
  const hooks = { version: await appVersion, whatsNew: () => void openWhatsNew(), updates: updateHooks };
  if (await editSettings(settings, hooks, category)) sheets.forEach((s) => s.restyle());
}

// ---- What's New (design section 14, F17) ---------------------------------------------------------------------------

/** The running version, from the host's "init" (null when it does not say). */
let gotVersion: (v: string | null) => void = () => {};
const appVersion = new Promise<string | null>((resolve) => {
  gotVersion = resolve;
  setTimeout(() => resolve(null), 5000); // a host that never says: carry on without
});

/** Every release this build has notes for, by hand (Settings > About). */
async function openWhatsNew() {
  const r = await bridge.request({ type: 'whatsNew', version: (await appVersion) ?? '0.0.0', all: true });
  await showWhatsNew((r.releases as Release[] | undefined) ?? [], runCommand, (id) => commands.has(id));
}

/**
 * A release note's "Try it" (design section 14, F17): any registry command, by id. One that cannot run now says why in
 * the sheet's message bar. False for an id this build does not have.
 */
function runCommand(id: string): boolean {
  const c = commands.get(id);
  if (!c) return false;
  void runIfAllowed(c);
  return true;
}

/** At startup: the engine says whether this is an update worth showing, and records the version as seen. */
async function whatsNewAtStartup() {
  const version = await appVersion;
  if (!version) return;
  const r = await bridge.request({ type: 'whatsNew', version });
  const releases = (r.releases as Release[] | undefined) ?? [];
  if (r.show === 'window') await showWhatsNew(releases, runCommand, (id) => commands.has(id));
  else if (r.show === 'notice') showUpdateNotice(version, (r.summary as string | null) ?? null, () => void showWhatsNew(releases, runCommand, (id) => commands.has(id)));
}

// ---- the command registry and palette (design section 14, F12) ------------------------------------------------------
// Every SiftDB action, defined once (commands.ts). The palette lists them, the window's shortcuts run them (see keyboard
// below), the editor takes its menu labels and keybindings from them, and What's New's "Try it" runs them by id. The ids
// are stable: release notes and the recently used list name them.

/** A command that cannot run now says why where the user is looking: the visible sheet's message bar. */
const notify = (reason: string) => active?.setMessage(reason, 'warning');

async function runIfAllowed(c: Command) {
  const state = CommandRegistry.state(c);
  if (state === false) return;
  if (typeof state === 'string') { notify(state); return; }
  await c.run();
}

/** The grid on screen, or false (the result commands are then not listed). */
const grid = () => active?.currentGrid ?? null;
const needsSelection = (): boolean | string => !grid() ? false : !!grid()!.selection || 'Select some cells in the result first.';
const copy = (id: string, label: string, as: 'tsv' | 'tsvHeaders' | 'csv' | 'json', keys?: string[]): Command => ({
  id, label, group: 'Results', when: needsSelection, run: () => grid()?.copy(as), ...(keys ? { localKeys: keys } : {}),
});

commands.add(
  // Sheets
  { id: 'newSheet', label: 'New sheet', group: 'Sheets', keys: ['Ctrl+T'], description: 'on the connection of the sheet you are on', run: () => { addSheet(); } },
  { id: 'openFile', label: 'Open file…', group: 'Sheets', keys: ['Ctrl+O'], run: () => openFile() },
  { id: 'openRecent', label: 'Open recent file…', group: 'Sheets', description: 'files you opened or saved', run: () => openRecentFile() },
  { id: 'save', label: 'Save', group: 'Sheets', keys: ['Ctrl+S'], when: () => !!active, run: async () => { await saveSheet(active!); } },
  { id: 'saveAs', label: 'Save as…', group: 'Sheets', keys: ['Ctrl+Shift+S'], when: () => !!active, run: async () => { await saveSheet(active!, true); } },
  { id: 'closeSheet', label: 'Close sheet', group: 'Sheets', keys: ['Ctrl+W'], when: () => !!active, run: () => closeSheet(active!) },
  { id: 'nextSheet', label: 'Next sheet', group: 'Sheets', keys: ['Ctrl+Tab'], when: () => sheets.length > 1, run: () => cycle(1) },
  { id: 'previousSheet', label: 'Previous sheet', group: 'Sheets', keys: ['Ctrl+Shift+Tab'], when: () => sheets.length > 1, run: () => cycle(-1) },
  { id: 'goToSheet', label: 'Go to sheet…', group: 'Sheets', description: 'pick one of the open sheets', when: () => sheets.length > 1, run: goToSheet },
  {
    id: 'modeAssisted', label: 'Switch to Assisted mode', group: 'Sheets', description: 'the quotes the database needs are added for you',
    when: () => !!active && active.mode !== 'assisted', run: () => active?.setMode('assisted'),
  },
  {
    id: 'modeStrict', label: 'Switch to Strict mode', group: 'Sheets', description: 'the text you write is the text that runs',
    when: () => !!active && active.mode !== 'strict', run: () => active?.setMode('strict'),
  },

  // Run. The sheet itself says why a statement cannot run (not connected, nothing under the cursor), as it does for F5.
  { id: 'run', label: 'Run statement', group: 'Run', keys: ['F5'], localKeys: ['Ctrl+Enter'], description: 'or the selection', when: () => !!active, run: () => active?.runFromEditor() },
  {
    id: 'dryRun', label: 'Dry run statement', group: 'Run', keys: ['Ctrl+Shift+F5'], description: 'run it, show what it changes, roll back',
    when: () => !!active, run: () => active?.dryRunFromEditor(),
  },
  {
    id: 'explain', label: 'Explain statement', group: 'Run', keys: ['Ctrl+E'], description: 'show its plan; nothing runs',
    when: () => !!active, run: () => active?.explainFromEditor(false),
  },
  {
    id: 'explainAnalyze', label: 'Explain analyze statement', group: 'Run', keys: ['Ctrl+Shift+E'], description: 'run it, time each step, roll back',
    when: () => !!active, run: () => active?.explainFromEditor(true),
  },
  { id: 'cancel', label: 'Cancel running statement', group: 'Run', localKeys: ['Ctrl+.'], when: () => !!active?.isRunning, run: () => active?.cancel() },

  // Transactions: only the ones that mean something now are listed, and their keys are left alone otherwise (Ctrl+Shift+Enter
  // is Monaco's "insert line above").
  {
    id: 'begin', label: 'Begin transaction', group: 'Transactions',
    when: () => !!active?.connected && !active.readOnly && active.hasTransactions && active.tx.state === 'none', run: () => active?.txAction('begin'),
  },
  {
    id: 'commit', label: 'Commit transaction', group: 'Transactions', keys: ['Ctrl+Shift+Enter'],
    when: () => active?.tx.state === 'open' || active?.tx.state === 'changed', run: () => active?.txAction('commit'),
  },
  {
    id: 'rollback', label: 'Roll back transaction', group: 'Transactions', keys: ['Ctrl+Shift+R'],
    when: () => !!active && active.tx.state !== 'none', run: () => active?.txAction('rollback'),
  },

  // Connections
  { id: 'connect', label: 'Connect', group: 'Connections', description: 'the connection picked above, in a session of its own', when: () => !!active, run: connectActive },
  { id: 'connectTo', label: 'Connect sheet to…', group: 'Connections', description: 'pick a saved connection', when: () => !!active, run: connectTo },
  { id: 'disconnect', label: 'Disconnect', group: 'Connections', description: "close this sheet's session", when: () => !!active?.connected, run: disconnectActive },
  {
    id: 'editConnection', label: 'Edit connection…', group: 'Connections',
    when: () => !!active && !catalog.error && !!savedIdOf(active.draftChoice) && !!catalog.byId(savedIdOf(active.draftChoice)!), run: editPickedConnection,
  },
  { id: 'newConnection', label: 'New connection…', group: 'Connections', when: () => !catalog.error && catalog.providers.length > 0, run: newConnection },

  // Editor. Monaco's own editor actions are added to the list when a sheet is open (see openPalette).
  { id: 'format', label: 'Format SQL', group: 'Editor', localKeys: ['Shift+Alt+F'], description: 'the statement, or the selection', when: () => !!active, run: () => active?.format(false) },
  { id: 'formatSheet', label: 'Format sheet', group: 'Editor', description: 'every statement', when: () => !!active, run: () => active?.format(true) },
  {
    id: 'acceptAll', label: 'Accept all identifier corrections', group: 'Editor', localKeys: ['Ctrl+Alt+Q'], description: 'the greyed quotes become text',
    when: () => !!active, run: () => active?.acceptAllCorrections(),
  },
  {
    // The query mini-map (design section 14, F5): for every sheet, remembered between runs.
    id: 'toggleMiniMap', label: 'Show or hide query map', group: 'Editor', keys: ['Ctrl+Shift+M'],
    description: 'the tables of the statement and how they are joined', run: () => toggleMiniMap(),
  },

  // Results: the result on screen.
  copy('copy', 'Copy selection', 'tsv', ['Ctrl+C']),
  copy('copyWithNames', 'Copy selection with column names', 'tsvHeaders', ['Ctrl+Shift+C']),
  copy('copyCsv', 'Copy selection as CSV', 'csv'),
  copy('copyJson', 'Copy selection as JSON', 'json'),
  {
    // The record view (design section 14, F8): the selected rows, or a result of 1 to 5 rows, down the page.
    id: 'recordView', label: 'Show or hide record view', group: 'Results', localKeys: ['Space'],
    description: 'Grid | Record: 1 to 5 rows down the page, one column per row',
    when: () => !active?.currentGrid ? false : active.canToggleRecordView || 'Select 1 to 5 rows in the result first.',
    run: () => active?.toggleRecordView(),
  },
  {
    id: 'exportResult', label: 'Export result…', group: 'Results',
    when: () => !grid() ? false : grid()!.canExport() || 'Nothing to export yet.', run: () => grid()?.onExport(null),
  },
  { id: 'pinResult', label: 'Pin result', group: 'Results', description: 'the next run opens a new tab', when: () => active?.currentResult?.pinned === false, run: () => active?.togglePin() },
  { id: 'unpinResult', label: 'Unpin result', group: 'Results', description: 'the next run replaces it', when: () => !!active?.currentResult?.pinned, run: () => active?.togglePin() },
  { id: 'closeResult', label: 'Close result', group: 'Results', when: () => !!active?.currentResult, run: () => active?.closeCurrentResult() },
  { id: 'closeResults', label: 'Close unpinned results', group: 'Results', when: () => (active?.resultCount ?? 0) > 0, run: () => active?.closeResults(false) },
  { id: 'closeAllResults', label: 'Close all results', group: 'Results', description: 'pinned ones too', when: () => (active?.resultCount ?? 0) > 0, run: () => active?.closeResults(true) },

  // Schema
  { id: 'refreshSchema', label: 'Refresh schema', group: 'Schema', description: "reload the connection's tables and columns", when: () => !active ? false : !!active.target || 'Not connected: connect the sheet first.', run: () => refreshSchema() },
  { id: 'filterSchema', label: 'Filter schema tree', group: 'Schema', when: () => !treeFilter.disabled || 'Connect first: the tree is empty.', run: () => treeFilter.focus() },

  // App
  { id: 'settings', label: 'Settings', group: 'App', keys: ['Ctrl+,'], run: () => openSettings() },
  { id: 'whatsNew', label: "What's New", group: 'App', description: 'every release this build has notes for', run: () => openWhatsNew() },
  { id: 'about', label: 'About SiftDB', group: 'App', description: 'the version, and when What\'s New opens', run: () => openSettings('about') },
  { id: 'checkForUpdates', label: 'Check for updates', group: 'App', description: 'and download one, ready for a restart', run: () => checkForUpdates() },
  { id: 'applyUpdate', label: 'Restart to update', group: 'App', when: () => !$('update').hidden, run: () => bridge.send({ type: 'applyUpdate' }) },
  { id: 'commandPalette', label: 'Show all commands', group: 'App', keys: ['F1', 'Ctrl+Shift+P'], unlisted: true, run: () => openPalette() },
);

/** Recently used commands, newest first (the engine keeps them in settings.json, so they survive a restart). */
let recentCommands: string[] = [];

function rememberCommand(id: string) {
  recentCommands = [id, ...recentCommands.filter((r) => r !== id)];
  void bridge.request({ type: 'rememberCommand', commandId: id }).then((r) => {
    if (r.type === 'recent') recentCommands = r.commands as string[];
  });
}

/**
 * F1 or Ctrl+Shift+P, from anywhere: the registry's commands that can be listed now, by group, then Monaco's own editor
 * actions for the sheet on screen. The chosen one runs once the palette has closed.
 */
async function openPalette() {
  if (paletteOpen()) return;
  const order = (g: string) => GROUPS.indexOf(g as CommandGroup);
  const items: (PaletteItem & { run(): void | Promise<void> })[] = [];
  for (const c of commands.all()) {
    if (c.unlisted) continue;
    const state = CommandRegistry.state(c);
    if (state === false) continue;
    items.push({
      id: c.id, label: c.label, group: c.group, shortcut: shortcutOf(c), description: c.description,
      reason: typeof state === 'string' ? state : undefined, run: c.run,
    });
  }
  // Monaco's (comment line, find, go to line…). SiftDB's own are in the registry already, and its palette is this one.
  const editorActions = (active?.editorActions() ?? [])
    .filter((a) => !a.id.startsWith('siftdb.') && a.id !== 'editor.action.quickCommand')
    .sort((a, b) => a.label.localeCompare(b.label))
    .map((a) => ({ id: a.id, label: a.label, group: 'Editor', shortcut: shortcutFor(a.id), run: a.run }));
  items.push(...editorActions);
  items.sort((a, b) => order(a.group) - order(b.group)); // stable: each group keeps its own order

  const chosen = await choose(items, recentCommands);
  if (!chosen) return;
  try {
    if (chosen.reason) { notify(chosen.reason); return; }
    rememberCommand(chosen.id);
    await items.find((i) => i.id === chosen.id)?.run();
  } finally {
    paletteDone();
  }
}

/** The palette's second step: one of the open sheets. */
async function goToSheet() {
  const sheet = await pickFrom(sheets.map((s) => ({ label: s.title, description: s === active ? 'current' : s.file?.path, value: s })), 'Pick a sheet');
  if (sheet) activate(sheet);
}

/** The palette's second step: a saved connection (or a pasted string) for the sheet on screen, then Connect. */
async function connectTo() {
  const sheet = active;
  if (!sheet) return;
  const current = sheet.target ? choiceFor(sheet.target) : null;
  const choices = [
    ...catalog.saved.filter((c) => !c.unavailable).map((c) => ({
      label: `${c.name}${c.readOnly ? ' (read-only)' : ''}`,
      description: [`saved:${c.id}` === current ? 'connected' : '', catalog.describe(c)].filter(Boolean).join(' · '),
      value: `saved:${c.id}`,
    })),
    { label: 'Connection string…', description: 'type or paste one above', value: STRING_CHOICE },
  ];
  const choice = await pickFrom(choices, `Connect "${sheet.title}" to`);
  if (!choice || sheet !== active) return;
  sheet.draftChoice = choice;
  renderPicker();
  scheduleAutosave();
  if (choice === STRING_CHOICE) { connEl.focus(); return; }
  await connectActive();
}

/** Ends the visible sheet's session. Uncommitted changes are rolled back, so they are asked about first. */
async function disconnectActive() {
  const sheet = active;
  if (!sheet?.connected) return;
  if (sheet.txPending &&
      !window.confirm(`"${sheet.title}" has changes that are not committed. Disconnecting rolls them back.\n\nDisconnect anyway?`)) return;
  await sheet.disconnect();
  sheet.setMessage('Disconnected. Press Connect to open a new session.');
  rebuildTree();
  renderPicker();
  updateHostState();
}

/** The palette's second step: a file opened or saved recently (the engine keeps the list, and leaves out ones gone since). */
async function openRecentFile() {
  const r = await bridge.request({ type: 'recent' });
  const files = r.type === 'recent' ? (r.files as string[]) : [];
  if (!files.length) { active?.setMessage('No recent files yet: files you open or save are listed here.'); return; }
  const split = (p: string) => { const i = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/')); return { name: p.slice(i + 1), dir: p.slice(0, i) }; };
  const open = new Set(sheets.map((s) => s.file?.path.toLowerCase()));
  const path = await pickFrom(files.map((f) => {
    const { name, dir } = split(f);
    return { label: name, description: open.has(f.toLowerCase()) ? `open · ${dir}` : dir, value: f };
  }), 'Open a recent file');
  if (path) await openPath(path);
}
applyIcons(document);
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
// The window's shortcuts are the registry's `keys` (design section 14, F12), from anywhere on the page: F5 and Ctrl+E in
// the grid or the tree as in the editor. Capture phase and stopPropagation: Monaco binds some of these itself
// (Ctrl+Shift+Enter is "insert line above"), so it must not also see them. A command that cannot be listed now (Commit
// with no transaction) leaves its key to whoever else wants it.

/** A connection form or password prompt is open: the sheets' shortcuts must not act behind it. */
const dialogOpen = () => document.querySelector('dialog[open]') !== null;

window.addEventListener('keydown', (e) => {
  // Never a page reload (it would drop every session), whatever has the keys.
  if (e.key === 'F5') e.preventDefault();
  // A dialog, or the palette (its list and any second step), has the keys while it is open.
  if (dialogOpen() || paletteOpen()) return;
  const c = commands.forKey(e);
  if (!c) return;
  const state = CommandRegistry.state(c);
  if (state === false) return;
  e.preventDefault();
  e.stopPropagation();
  void runIfAllowed(c);
}, true);

// The browser's own right-click menu has Refresh (and Back), which reload the page and drop every session: it showed
// wherever SiftDB has no menu of its own, e.g. the schema tree. Text boxes keep it, since there it is the
// cut / copy / paste menu, with no Refresh. The editor and the grid draw their own menus and already stop this one.
window.addEventListener('contextmenu', (e) => {
  const t = e.target as HTMLElement | null;
  const editable = t?.closest('input, textarea, [contenteditable="true"]') && !t.closest('.monaco-editor');
  if (!editable) e.preventDefault();
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
  void whatsNewAtStartup(); // over the restored sheets
});
void bridge.request({ type: 'recent' }).then((r) => { if (r.type === 'recent') recentCommands = r.commands as string[]; });
setInterval(() => active?.tick(), 250);
