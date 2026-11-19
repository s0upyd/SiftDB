import type { Bridge, DatabaseSchema } from './bridge';
import { CompletionResponse, QueryMode, RunHow, RunTarget, SheetAnalysis, SqlEditor } from './editor';
import { ResultTab, ResultView, ResultsContext, plural } from './results';
import { PlanContext, PlanView } from './plan';
import { applyIcons, spinner } from './icons';
import { showMenu } from './menu';
import { revealSelectedTab, scrollTabsWithWheel } from './tabs';

export type TxState = 'none' | 'open' | 'changed' | 'failed';

/** A statement that finished while its sheet was not on screen: shown on the tab until the sheet is looked at. */
export interface FinishedRun { outcome: 'complete' | 'error' | 'cancelled'; ms: number }

/** Shorter runs never mark the tab: you would hardly have switched away, and a marker for every query is noise. */
const FINISHED_MARK_MIN_MS = 10_000;

/** What a sheet connects to: a saved connection, or a pasted connection string (which never reaches the DOM). */
export type Target = { kind: 'saved'; id: string } | { kind: 'string'; cs: string };

/** Sheets with the same key share a root in the schema tree. */
export const targetKey = (t: Target) => (t.kind === 'saved' ? `saved:${t.id}` : `cs:${t.cs}`);

/** What a sheet needs from the app around it. */
export interface SheetContext {
  bridge: Bridge;
  isDark(): boolean;
  registerQuery(queryId: number, view: ResultView): void;
  unregisterQuery(queryId: number): void;
  registerConnection(connectionId: number, sheet: Sheet): void;
  unregisterConnection(connectionId: number): void;
  /** The tab title or the transaction state changed: the tab strip and the window-close warning need refreshing. */
  changed(sheet: Sheet): void;
  /** Asks for a saved connection's password; null if the user cancelled. */
  askPassword(name: string, retry: boolean, message?: string): Promise<string | null>;
  /** The text, the mode or the file changed: the session needs autosaving. */
  persist(sheet: Sheet): void;
}

/** A sheet's file, as the engine reported it (design section 6). */
export interface FileState {
  path: string;
  fileName: string;
  lineEnding: string;
  encoding: string;
  headerOther: [string, string][];
}

/** `cancelled`: the user dismissed the password prompt, which is not a failure worth showing in red. */
export interface ConnectResult { ok: boolean; schema: DatabaseSchema | null; error?: string; cancelled?: boolean }

const TEMPLATE = `
  <div class="sheetbar">
    <span class="split" role="group" aria-label="Run">
      <button class="run ib ic-go" data-icon="run" disabled aria-label="Run" title="Run the statement under the cursor (F5 or Ctrl+Enter)"></button>
      <button class="runMore ib ic-go" data-icon="chevronDown" disabled aria-label="More ways to run" aria-haspopup="menu" title="More ways to run: Dry run (Ctrl+Shift+F5)"></button>
    </span>
    <button class="cancel ib ic-stop" data-icon="stop" disabled aria-label="Cancel" title="Cancel the running statement (Ctrl+.)"></button>
    <button class="begin ib ic-go" data-icon="begin" disabled aria-label="Begin transaction" title="Begin a transaction. You can also just type BEGIN: the bar follows the server either way."></button>
    <div class="modes" role="group" aria-label="How identifiers are quoted">
      <button class="modeAssisted" aria-pressed="true" title="Assisted: write loosely. The quotes the database needs are added for you and shown greyed, and what runs is exactly what you see.">Assisted</button>
      <button class="modeStrict" aria-pressed="false" title="Strict: the text you write is the text that runs. Quoting problems are squiggles with quick fixes (Alt+Enter), and a statement with any is not run.">Strict</button>
    </div>
    <span class="autochip" hidden title="Auto-transaction: this sheet is always inside a transaction. Changes wait for Commit, and Rollback undoes them; a new transaction starts after either.">Auto-transaction</span>
    <span class="rochip" hidden title="This sheet's connection is read-only: the server refuses changes. To change data, connect the sheet to a writable connection.">Read-only</span>
  </div>
  <div class="txbar" hidden role="status" aria-live="polite">
    <span class="txIcon" aria-hidden="true"></span>
    <strong class="txLabel"></strong>
    <span class="txDetail"></span>
    <span class="grow"></span>
    <button class="txEnd ib ic-go" data-icon="tick" aria-label="End transaction" title="End the transaction (commit). Nothing has changed in it."></button>
    <button class="txCommit ib ic-commit" data-icon="tick" aria-label="Commit" title="Commit the changes (Ctrl+Shift+Enter)"></button>
    <button class="txRollback ib ic-stop" data-icon="cross" aria-label="Roll back" title="Roll back (Ctrl+Shift+R)"></button>
  </div>
  <div class="confirmbar" hidden>
    <span class="confirmIcon" aria-hidden="true">⚠</span>
    <span class="confirmText" role="alert"></span>
    <span class="grow"></span>
    <button class="confirmDry" title="Run it, show the rows it would change, then roll it back (Ctrl+Shift+F5)">Dry run</button>
    <button class="confirmRun danger" title="Run it as it is">Run anyway</button>
    <button class="confirmCancel" title="Don't run it (Esc)">Cancel</button>
  </div>
  <div class="editor"></div>
  <div class="splitter" role="separator" aria-orientation="horizontal" aria-label="Resize editor and results" tabindex="0" title="Drag to resize (or use the up and down arrow keys)"></div>
  <div class="msg" hidden role="status"><span class="msgIcon" aria-hidden="true"></span><span class="msgText"></span></div>
  <div class="resulttabs" role="tablist" aria-label="Results"></div>
  <div class="results"><div class="empty">Run a statement (F5) to see its results here.</div></div>
`;

function elapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, '0')} s`;
  return `${Math.floor(s / 3600)} h ${String(Math.floor((s % 3600) / 60)).padStart(2, '0')} min`;
}

/**
 * One tab: a sheet of SQL with its own database session, so BEGIN/COMMIT, temp tables, SET and the transaction bar are
 * all per sheet (design section 6). It owns an editor, the Strict/Assisted mode, and a strip of result tabs: a run fills
 * the unpinned result tab, or opens a new one when every existing tab is pinned.
 */
export class Sheet {
  readonly root = document.createElement('section');
  connectionId = 0;
  /** What this sheet's session was opened on; null while not connected. Sheets with the same target share a tree root. */
  target: Target | null = null;
  /** The session was opened read-only (from the saved connection's setting at the time). */
  readOnly = false;
  /** What the connection picker showed for this sheet the last time it was visible: 'saved:<id>' or 'string'. */
  draftChoice = 'string';
  /** The connection string typed for this sheet, for when the picker is on "Connection string…". */
  draftText = '';
  readonly tx = { state: 'none' as TxState, startedAt: null as string | null, statements: 0, rowsChanged: 0, automatic: false };

  /** Set when a long statement finished while this sheet was hidden; cleared when the sheet is shown. */
  finished: FinishedRun | null = null;
  private runStartedAt = 0;
  private visible = false;

  private readonly editor: SqlEditor;
  private results: ResultTab[] = [];
  private activeResult: ResultTab | null = null;
  private running: ResultTab | null = null;

  private readonly el: {
    run: HTMLButtonElement; runMore: HTMLButtonElement; cancel: HTMLButtonElement; begin: HTMLButtonElement;
    modeAssisted: HTMLButtonElement; modeStrict: HTMLButtonElement;
    roChip: HTMLElement; autoChip: HTMLElement;
    txBar: HTMLElement; txIcon: HTMLElement; txLabel: HTMLElement; txDetail: HTMLElement;
    txEnd: HTMLButtonElement; txCommit: HTMLButtonElement; txRollback: HTMLButtonElement;
    confirmBar: HTMLElement; confirmText: HTMLElement; confirmDry: HTMLButtonElement; confirmRun: HTMLButtonElement; confirmCancel: HTMLButtonElement;
    editor: HTMLElement; splitter: HTMLElement; msg: HTMLElement; msgIcon: HTMLElement; msgText: HTMLElement; tabs: HTMLElement; results: HTMLElement; empty: HTMLElement;
  };
  private readonly resultsCtx: ResultsContext;
  private readonly planCtx: PlanContext;

  /** `parent` is where the sheet lives: it is attached before the editor is built, because Monaco measures its container. */
  /** Stable for the sheet's life: names its autosaved text. */
  readonly id = crypto.randomUUID();
  /** The file it was opened from or saved to, or null for an untitled sheet. */
  file: FileState | null = null;
  /** A connection the sheet's file named that is not saved here: kept, so saving does not lose it, and shown until one is chosen. */
  connectionName: string | null = null;
  /** The editor's version at the last open or save; -1 when unknown (restored with unsaved edits). */
  private savedVersion: number;
  private wasDirty = false;

  /** `untitled` is the name shown until the sheet has a file ("Sheet 3"). */
  constructor(private ctx: SheetContext, parent: HTMLElement, readonly untitled: string, initialText: string, cursorLine = 1) {
    this.root.className = 'sheet';
    this.root.innerHTML = TEMPLATE;
    applyIcons(this.root);
    parent.append(this.root);
    const $ = <T extends HTMLElement = HTMLElement>(selector: string) => this.root.querySelector(selector) as T;
    this.el = {
      run: $('.run'), runMore: $('.runMore'), cancel: $('.cancel'), begin: $('.begin'), modeAssisted: $('.modeAssisted'), modeStrict: $('.modeStrict'),
      roChip: $('.rochip'), autoChip: $('.autochip'),
      txBar: $('.txbar'), txIcon: $('.txIcon'), txLabel: $('.txLabel'), txDetail: $('.txDetail'),
      txEnd: $('.txEnd'), txCommit: $('.txCommit'), txRollback: $('.txRollback'),
      confirmBar: $('.confirmbar'), confirmText: $('.confirmText'), confirmDry: $('.confirmDry'), confirmRun: $('.confirmRun'), confirmCancel: $('.confirmCancel'),
      editor: $('.editor'), splitter: $('.splitter'), msg: $('.msg'), msgIcon: $('.msgIcon'), msgText: $('.msgText'), tabs: $('.resulttabs'), results: $('.results'), empty: $('.empty'),
    };
    scrollTabsWithWheel(this.el.tabs);
    this.resultsCtx = {
      bridge: ctx.bridge,
      registerQuery: (id, v) => ctx.registerQuery(id, v),
      unregisterQuery: (id) => ctx.unregisterQuery(id),
      // The user has seen what it does: run it straight away, without asking about a missing WHERE again.
      runForReal: (sql) => { if (this.connected) void this.startRun(sql); },
      // Following a foreign key (design section 14, F4): nothing is hidden. The statement goes at the end of the sheet,
      // where it can be read, changed and run again, and runs in a result tab of its own so the one it came from stays.
      openQuery: (sql) => {
        if (!this.connected) return;
        this.editor.appendStatement(sql);
        void this.startRun(sql, false, true);
      },
      onFinished: (v) => this.runFinished(v),
    };
    // Plans (design section 14, F6) link their steps to the table references in the editor.
    this.planCtx = {
      bridge: ctx.bridge,
      onFinished: (v) => this.runFinished(v),
      track: (ranges, source, sourceStart) =>
        this.editor.textAt(sourceStart, source.length) === source ? this.editor.track(ranges) : null,
      select: (start, end) => this.editor.select(start, end),
    };

    this.editor = new SqlEditor(this.el.editor, this.analyze, {
      run: () => void this.runFromEditor(),
      dryRun: () => void this.dryRunFromEditor(),
      format: (whole) => void this.format(whole),
      cancel: () => this.cancel(),
      explain: (analyze) => void this.explainFromEditor(analyze),
      runSql: (t, how) => this.handleTarget(t, how),
      complete: this.complete,
    }, initialText);
    this.editor.setCursor(cursorLine);
    // A new sheet with only the starter text has nothing worth asking about; with text from elsewhere it does (see restore).
    this.savedVersion = this.editor.versionId();
    this.editor.onChange(() => this.textChanged());
    // Pointing at a table in the editor marks its steps in the plan on screen.
    this.editor.onMouseOffset((offset) => { if (this.activeResult instanceof PlanView) this.activeResult.hoverSql(offset); });

    this.el.run.onclick = () => void this.runFromEditor();
    // The ▾ beside Run: the other ways to run the same statement (design section 14, F2), so a dry run is in plain sight.
    this.el.runMore.onclick = () => {
      const r = this.el.runMore.getBoundingClientRect();
      showMenu(r.left, r.bottom + 2, [
        { label: 'Run', shortcut: 'F5', action: () => void this.runFromEditor() },
        { label: 'Dry run: run, show what changes, roll back', shortcut: 'Ctrl+Shift+F5', action: () => void this.dryRunFromEditor() },
        { label: 'Explain: show the plan, nothing runs', shortcut: 'Ctrl+E', action: () => void this.explainFromEditor(false) },
        { label: 'Explain analyze: run, time each step, roll back', shortcut: 'Ctrl+Shift+E', action: () => void this.explainFromEditor(true) },
      ], 'Ways to run');
    };
    this.el.cancel.onclick = () => this.cancel();
    this.el.begin.onclick = () => void this.txAction('begin');
    this.el.txEnd.onclick = () => void this.txAction('commit');
    this.el.txCommit.onclick = () => void this.txAction('commit');
    this.el.txRollback.onclick = () => void this.txAction('rollback');
    // Strict: the text you write is the text that runs. Assisted: the editor shows (and runs) the quoting the database needs.
    // Both choices are always visible and the current one is highlighted: a single button that changes its own label
    // reads as "click for Strict" when it actually means "you are in Strict".
    this.initSplitter();
    this.el.modeAssisted.onclick = () => this.setMode('assisted');
    this.el.modeStrict.onclick = () => this.setMode('strict');
    this.el.confirmRun.onclick = () => this.answerConfirm('run');
    this.el.confirmDry.onclick = () => this.answerConfirm('dry');
    this.el.confirmCancel.onclick = () => this.answerConfirm('cancel');
    this.el.confirmBar.onkeydown = (e) => { if (e.key === 'Escape') { e.preventDefault(); this.answerConfirm('cancel'); } };

    this.setMessage('Not connected. Pick a connection above and press Connect.', 'warning');
    this.updateButtons();
  }

  /** The editor keeps the height it is given; the results take the rest. Dragging, or arrow keys on the focused bar. */
  private initSplitter() {
    const { splitter, editor: box } = this.el;
    const MIN_EDITOR = 80, MIN_RESULTS = 140;
    const setHeight = (px: number) => {
      const room = this.root.clientHeight - box.offsetTop - MIN_RESULTS;
      box.style.height = `${Math.round(Math.max(MIN_EDITOR, Math.min(px, room)))}px`;
      this.editor.layout();
    };
    let startY = 0, startH = 0;
    splitter.onpointerdown = (e) => {
      splitter.setPointerCapture(e.pointerId);
      splitter.classList.add('dragging');
      startY = e.clientY; startH = box.offsetHeight;
      e.preventDefault();
    };
    splitter.onpointermove = (e) => { if (splitter.hasPointerCapture(e.pointerId)) setHeight(startH + e.clientY - startY); };
    const end = (e: PointerEvent) => { splitter.classList.remove('dragging'); splitter.releasePointerCapture?.(e.pointerId); };
    splitter.onpointerup = end;
    splitter.onpointercancel = end;
    splitter.ondblclick = () => setHeight(190);
    splitter.onkeydown = (e) => {
      const step = e.shiftKey ? 80 : 20;
      if (e.key === 'ArrowUp') setHeight(box.offsetHeight - step);
      else if (e.key === 'ArrowDown') setHeight(box.offsetHeight + step);
      else return;
      e.preventDefault();
    };
  }

  // ---- the session ----------------------------------------------------------------------------------------------

  get connected(): boolean { return this.connectionId !== 0; }

  /** A statement is running on this sheet (its tab shows a spinner). */
  get isRunning(): boolean { return this.running !== null; }

  /** Words for a finished mark's tooltip and screen readers, e.g. "Finished after 2 min 05 s". */
  static describeFinished(f: FinishedRun): string {
    const took = elapsed(f.ms);
    return f.outcome === 'complete' ? `Finished after ${took}` : f.outcome === 'error' ? `Failed after ${took}` : `Cancelled after ${took}`;
  }

  /**
   * Opens this sheet's own session. `wantSchema` is true for the one sheet whose schema fills the sidebar. A saved
   * connection may need its password: the engine says so, the user is asked, and the connect is tried again with it.
   */
  async connect(target: Target, wantSchema: boolean): Promise<ConnectResult> {
    this.setMessage('Connecting…');
    let password: string | undefined;
    let r;
    for (;;) {
      r = await this.ctx.bridge.request(target.kind === 'saved'
        ? { type: 'connect', savedId: target.id, ...(password !== undefined ? { password } : {}) }
        : { type: 'connect', provider: 'postgres', connectionString: target.cs });
      if (r.type !== 'passwordNeeded') break;
      const typed = await this.ctx.askPassword(r.name, r.retry, r.message);
      if (typed === null) {
        this.setMessage('Not connected: no password was given.', 'warning');
        return { ok: false, schema: null, cancelled: true };
      }
      password = typed;
    }
    if (r.type === 'error') {
      this.setMessage(`Connect failed: ${r.message}`, 'error');
      return { ok: false, schema: null, error: r.message };
    }
    this.connectionId = r.connectionId;
    this.target = target;
    this.readOnly = !!r.readOnly;
    this.el.roChip.hidden = !this.readOnly;
    this.el.autoChip.hidden = !r.autoTransaction;
    this.ctx.registerConnection(this.connectionId, this);
    this.ctx.changed(this); // the tab shows the read-only lock
    this.updateButtons();

    // The engine keeps a schema cache per session: completion and the identifier resolver read it.
    const s = await this.ctx.bridge.request({ type: 'introspect', connectionId: this.connectionId, send: wantSchema });
    this.setMessage('');
    this.editor.refresh();
    return { ok: true, schema: schemaOf(s) };
  }

  /** Reloads this session's schema cache; returns the schema when the sidebar wants a copy. */
  async refreshSchema(wantSchema: boolean): Promise<DatabaseSchema | null> {
    if (!this.connected) return null;
    const s = await this.ctx.bridge.request({ type: 'introspect', connectionId: this.connectionId, send: wantSchema });
    return schemaOf(s);
  }

  /** Ends the session. The engine rolls back anything uncommitted; results already shown stay on screen. */
  async disconnect() {
    if (!this.connected) return;
    const id = this.connectionId;
    this.hideConfirm();
    this.ctx.unregisterConnection(id); // stop routing events to a session that is going away
    this.connectionId = 0;
    this.target = null;
    this.readOnly = false;
    this.el.roChip.hidden = true;
    this.el.autoChip.hidden = true;
    this.setTx('none', null, 0, 0);
    await this.ctx.bridge.request({ type: 'disconnect', connectionId: id });
    this.updateButtons();
  }

  /** Closes the sheet for good: its session, its results and its editor. */
  async close() {
    await this.disconnect();
    this.results.forEach((r) => r.dispose());
    this.results = [];
    this.editor.dispose();
    this.root.remove();
  }

  // ---- visibility -----------------------------------------------------------------------------------------------

  setActive(active: boolean) {
    this.root.hidden = !active;
    this.visible = active;
    if (!active) return;
    this.finished = null; // seen now (the caller redraws the tabs)
    this.editor.layout();
    this.activeResult?.shown();
  }

  focusEditor() { this.editor.focus(); }

  /** This sheet's editor actions, for the command palette (design section 14, F12). */
  editorActions() { return this.editor.actions(); }

  /** "Script as" from the schema tree (design section 14, F13): into the sheet at the cursor, never run. */
  insertScript(sql: string) {
    this.editor.insertBlock(sql);
    this.setMessage('Scripted into the sheet. Nothing was run.');
  }

  /** Switches how this sheet treats identifier problems, and shows which is active. A blocked-run message is cleared: it described the old mode. */
  setMode(mode: QueryMode) {
    this.editor.setMode(mode);
    this.el.modeAssisted.setAttribute('aria-pressed', String(mode === 'assisted'));
    this.el.modeStrict.setAttribute('aria-pressed', String(mode === 'strict'));
    if (this.el.msg.dataset.kind === 'warning' && this.el.msgText.textContent?.startsWith('not run:')) this.setMessage('');
    this.ctx.persist(this);
  }

  get mode(): QueryMode { return this.editor.getMode(); }

  // ---- the file (design section 6) ------------------------------------------------------------------------------

  /** The tab's name: the file's, or "Sheet 3" until it has one. */
  get title(): string { return this.file?.fileName ?? this.untitled; }

  /** Edits not in the sheet's file (or, untitled, any text at all beyond the starter). */
  get dirty(): boolean { return this.editor.versionId() !== this.savedVersion; }

  get text(): string { return this.editor.getText(); }

  /** A version number that changes with every edit, for autosave to know what it has already written. */
  get version(): number { return this.editor.versionId(); }

  /** True for an untitled sheet nobody has typed in: opening a file can use it instead of adding a tab. */
  get blank(): boolean { return !this.file && this.text.trim() === ''; }

  /**
   * Fills the sheet from a file or a restored session. `dirty` says whether this text has edits its file does not (a
   * restored sheet with unsaved edits): then it counts as unsaved until the next save.
   */
  load(text: string, file: FileState | null, dirty: boolean) {
    this.editor.setText(text);
    this.file = file;
    this.savedVersion = dirty ? -1 : this.editor.versionId();
    this.textChanged();
  }

  /** The sheet was written to `file`: nothing is unsaved now. */
  markSaved(file: FileState) {
    this.file = file;
    this.savedVersion = this.editor.versionId();
    this.textChanged();
  }

  /** Before saving an Assisted sheet: the greyed quotes become real text. False when there was no analysis (not connected). */
  acceptCorrectionsForSave(): Promise<boolean> { return this.editor.acceptCorrectionsForSave(); }

  private textChanged() {
    if (this.pendingConfirm !== null) this.hideConfirm(); // the question was about text that has changed
    const dirty = this.dirty;
    if (dirty !== this.wasDirty) {
      this.wasDirty = dirty;
      this.ctx.changed(this); // the tab's unsaved marker
    }
    this.ctx.persist(this);
  }

  /** The styling rules changed: redraw the results (each grid re-matches its columns as it draws). */
  restyle() { this.results.forEach((r) => r.restyle()); }

  setTheme(dark: boolean) {
    this.editor.setTheme(dark);
    this.results.forEach((r) => r.setTheme(dark));
  }

  /** Called a few times a second for the visible sheet: the numbers under the result and the transaction timer. */
  tick() {
    this.activeResult?.refreshStats();
    this.updateTxDetail();
  }

  // ---- running --------------------------------------------------------------------------------------------------

  private analyze = async (text: string, selectionStart: number, selectionEnd: number, mode: QueryMode): Promise<SheetAnalysis | null> => {
    if (!this.connected) return null;
    const r = await this.ctx.bridge.request({ type: 'analyzeSheet', connectionId: this.connectionId, text, selectionStart, selectionEnd, mode });
    return r.type === 'sheetAnalysis' ? (r as unknown as SheetAnalysis) : null;
  };

  private complete = async (text: string, offset: number): Promise<CompletionResponse | null> => {
    if (!this.connected) return null;
    const r = await this.ctx.bridge.request({ type: 'complete', connectionId: this.connectionId, text, offset });
    return r.type === 'completions' ? (r as unknown as CompletionResponse) : null;
  };

  async runFromEditor() {
    this.handleTarget(await this.editor.statementToRun());
  }

  /**
   * Format SQL (design section 14, F7): the engine lays out the statement under the cursor (or every statement the selection
   * touches, or with `whole` the sheet) and answers with one edit, applied only if the text has not changed meanwhile. A
   * statement it would not touch (one that does not parse, or one it could not lay out safely) is said in the message bar.
   */
  async format(whole: boolean) {
    if (!this.connected) {
      this.setMessage('Not connected. Connect first: the connection decides which SQL dialect the formatter reads.', 'warning');
      return;
    }
    const snap = this.editor.formatSnapshot();
    const r = await this.ctx.bridge.request({
      type: 'formatSql', connectionId: this.connectionId, text: snap.text, selectionStart: snap.start, selectionEnd: snap.end, whole,
    });
    if (r.type === 'error') { this.setMessage(`Format failed: ${r.message}`, 'error'); return; }
    if (typeof r.start === 'number' && !this.editor.applyFormatted(snap.version, r.start, r.length, r.text)) {
      this.setMessage('The text changed while it was being formatted, so nothing was changed. Try again.', 'warning');
      return;
    }
    this.setMessage(r.message ?? '', r.kind === 'warning' ? 'warning' : 'info');
  }

  /** Ctrl+Shift+F5: dry-run what F5 would run (design section 14, F2). */
  async dryRunFromEditor() {
    this.handleTarget(await this.editor.statementToRun(), 'dry');
  }

  /**
   * Ctrl+E: the plan of what F5 would run, estimates only. Ctrl+Shift+E (`analyze`): it runs, timed, and is rolled back
   * (design section 14, F6). Either opens in a result tab of the Plan kind.
   */
  async explainFromEditor(analyze: boolean) {
    this.handleTarget(await this.editor.statementToRun(), analyze ? 'analyze' : 'explain');
  }

  private handleTarget(target: RunTarget, how: RunHow = 'run') {
    switch (target.kind) {
      case 'run':
        if (how === 'explain' || how === 'analyze') void this.startPlan(target, how === 'analyze');
        else void (how === 'dry' ? this.startDryRun(target.sql) : this.runSql(target.sql));
        break;
      // Nothing ran, so the result on screen is from before: mark it, or its old red error reads as this attempt's outcome.
      case 'blocked': this.setMessage(`not run: ${target.reason}`, 'warning'); this.activeResult?.markStale(); break;
      case 'none': this.setMessage('nothing to run: put the cursor in a statement, or select some text'); break;
      case 'disconnected':
        this.setMessage('Not connected. Connect first: the connection decides which SQL dialect splits your statements.', 'warning');
        this.activeResult?.markStale();
        break;
    }
  }

  /**
   * Every run asks the engine first whether it needs confirming (an UPDATE or DELETE with no WHERE, or a TRUNCATE:
   * design section 14, F1). If so, the red bar above the editor asks, and nothing runs until "Run anyway".
   */
  private async runSql(sql: string) {
    this.hideConfirm();
    const check = await this.ctx.bridge.request({ type: 'checkRun', connectionId: this.connectionId, sql });
    if (check.type === 'error') { this.setMessage(`not run: ${check.message}`, 'error'); return; }
    if (check.allRows) { this.askConfirm(check.allRows as string, sql, !!check.dryRun); return; }
    await this.startRun(sql);
  }

  /** The SQL waiting for an answer on the red bar, or null while the bar is hidden. */
  private pendingConfirm: string | null = null;

  /** `canDryRun`: the bar offers a dry run too (not for a TRUNCATE, say). */
  private askConfirm(warning: string, sql: string, canDryRun: boolean) {
    this.el.confirmDry.hidden = !canDryRun;
    this.pendingConfirm = sql;
    this.setMessage('');
    this.activeResult?.markStale(); // nothing ran: the result on screen is from before
    this.el.confirmText.textContent = warning;
    this.el.confirmBar.hidden = false;
    this.el.confirmCancel.focus(); // the safe answer is the one Enter gives
  }

  private hideConfirm() {
    this.pendingConfirm = null;
    this.el.confirmBar.hidden = true;
  }

  private answerConfirm(choice: 'run' | 'dry' | 'cancel') {
    const sql = this.pendingConfirm;
    this.hideConfirm();
    this.editor.focus();
    if (sql === null) return;
    if (choice === 'cancel' || !this.connected) this.setMessage('Not run.');
    else if (choice === 'dry') void this.startDryRun(sql);
    else void this.startRun(sql);
  }

  /**
   * A dry run (design section 14, F2): the engine runs the statement, returns the rows it touches and rolls it back.
   * Refusals (not an UPDATE or DELETE, a read-only connection…) come back as the result's error.
   */
  private startDryRun(sql: string) {
    this.hideConfirm();
    return this.startRun(sql, true);
  }

  /** `newTab`: in a result tab of its own, whether or not the current one is pinned. */
  private async startRun(sql: string, dry = false, newTab = false) {
    const view = this.viewFor(ResultView, newTab);
    this.beginRun(view);
    await view.run(this.connectionId, sql, dry);
    this.renderResultTabs(); // its label now names this statement
  }

  /** A plan (design section 14, F6), in the unpinned tab like a run, as a tab of the Plan kind. */
  private async startPlan(target: RunTarget & { kind: 'run' }, analyze: boolean) {
    this.hideConfirm();
    const view = this.viewFor(PlanView);
    this.beginRun(view);
    await view.run(this.connectionId, target, analyze);
    this.renderResultTabs();
  }

  private beginRun(view: ResultTab) {
    this.setMessage('');
    this.running = view;
    this.runStartedAt = performance.now();
    this.finished = null;
    this.updateButtons();
    this.renderResultTabs();
    this.ctx.changed(this); // the sheet's tab shows a spinner while it runs
  }

  /** A run, a dry run or a plan finished, failed or was cancelled. */
  private runFinished(v: ResultTab) {
    if (this.running !== v) { this.renderResultTabs(); return; }
    this.running = null;
    const ms = performance.now() - this.runStartedAt;
    // Mark the tab only if nobody was watching: on screen, the result itself says it finished.
    if (!this.visible && ms >= FINISHED_MARK_MIN_MS && (v.state === 'complete' || v.state === 'error' || v.state === 'cancelled')) {
      this.finished = { outcome: v.state, ms };
    }
    this.updateButtons();
    this.renderResultTabs(); // a failed statement is marked on its tab
    this.ctx.changed(this); // the sheet's tab: the spinner goes, a finished mark may come
  }

  cancel() {
    this.running?.cancel();
  }

  // ---- result tabs ----------------------------------------------------------------------------------------------

  /**
   * The tab a run fills: the unpinned one, or a new one if every tab is pinned. An unpinned tab of the other kind (rows
   * for a plan, a plan for rows) is replaced where it stands.
   */
  private viewFor<T extends ResultView | PlanView>(kind: new (ctx: never, dark: boolean) => T, newTab = false): T {
    const make = (): T => kind === (PlanView as unknown)
      ? new PlanView(this.planCtx, this.ctx.isDark()) as T
      : new ResultView(this.resultsCtx, this.ctx.isDark()) as T;
    const unpinned = newTab ? undefined : this.results.find((r) => !r.pinned);
    let view: T;
    if (unpinned instanceof kind) view = unpinned;
    else {
      view = make();
      if (unpinned) {
        const at = this.results.indexOf(unpinned);
        if (this.running === unpinned) this.running = null;
        unpinned.dispose();
        this.results.splice(at, 1, view);
      } else this.results.push(view);
      this.el.results.append(view.panel);
    }
    this.showResult(view);
    return view;
  }

  private showResult(view: ResultTab | null) {
    this.activeResult = view;
    this.results.forEach((r) => { r.panel.hidden = r !== view; });
    this.el.empty.hidden = this.results.length > 0;
    view?.shown();
    this.renderResultTabs();
    this.updateButtons();
  }

  private closeResult(view: ResultTab) {
    const at = this.results.indexOf(view);
    view.dispose();
    this.results.splice(at, 1);
    if (this.running === view) this.running = null;
    if (this.activeResult === view) this.showResult(this.results[Math.min(at, this.results.length - 1)] ?? null);
    else this.showResult(this.activeResult);
  }

  private renderResultTabs() {
    this.el.tabs.replaceChildren(...this.results.map((view) => {
      const tab = document.createElement('div');
      tab.className = 'rtab';
      tab.setAttribute('role', 'tab');
      tab.setAttribute('aria-selected', String(view === this.activeResult));
      tab.dataset.state = view.state;
      if (view.tabClass) tab.classList.add(view.tabClass); // a dry run's is striped, like its bar: never taken for a real run's result
      // A failure is visible without opening the tab: a red marker here, and the reason on hover.
      tab.title = view.state === 'error' ? `${view.errorHeadline}\n\n${view.panel.title}` : view.panel.title;
      if (view.state === 'error') tab.setAttribute('aria-label', `Failed: ${view.label}`);
      tab.onclick = () => this.showResult(view);

      const mark = document.createElement('span');
      mark.className = 'mark';
      mark.setAttribute('aria-hidden', 'true');
      if (view.state === 'running') mark.append(spinner());
      else mark.textContent = view.state === 'error' ? '✖' : '';
      mark.hidden = view.state !== 'running' && view.state !== 'error';

      const label = document.createElement('span');
      label.className = 'label';
      label.textContent = view.label || '…';

      const pin = document.createElement('button');
      pin.className = 'pin';
      pin.textContent = '📌';
      pin.setAttribute('aria-pressed', String(view.pinned));
      pin.title = view.pinned ? 'Unpin: the next run replaces this result' : 'Pin: keep this result; the next run opens a new tab';
      pin.onclick = (e) => { e.stopPropagation(); view.pinned = !view.pinned; this.renderResultTabs(); };

      const close = document.createElement('button');
      close.className = 'close';
      close.textContent = '×';
      close.title = 'Close this result';
      close.setAttribute('aria-label', 'Close this result');
      close.onclick = (e) => { e.stopPropagation(); this.closeResult(view); };

      tab.append(mark, label, pin, close);
      return tab;
    }));
    revealSelectedTab(this.el.tabs);
  }

  // ---- the transaction bar (design section 7) -------------------------------------------------------------------
  // The state is whatever the server last said. The Begin button and a typed BEGIN end up in the same place because
  // both are observed afterwards, never assumed.

  /** Changes pending that closing this sheet would throw away. */
  get txPending(): boolean { return this.tx.state === 'changed' || this.tx.state === 'failed'; }

  /** An engine "transaction" event for this sheet's session. */
  onTransaction(e: {
    state: TxState; startedAt: string | null; statements: number; rowsChanged: number; automatic?: boolean; autoReset?: boolean;
    statementUndone?: boolean;
  }) {
    this.tx.automatic = !!e.automatic;
    this.setTx(e.state, e.startedAt ?? null, e.statements, e.rowsChanged);
    // A statement failed in the automatic transaction while nothing had changed: it was rolled back and started again.
    if (e.autoReset) this.setMessage('The statement failed. Nothing had changed, so the transaction was rolled back and a new one started.');
    // The failed statement was undone on its own (a savepoint): the transaction carries on, so say so beside the red result.
    else if (e.statementUndone) {
      this.setMessage(e.state === 'changed'
        ? 'The statement failed and was undone. The transaction is still open, with the changes made before it still pending.'
        : 'The statement failed and was undone. The transaction is still open.');
    }
  }

  private setTx(state: TxState, startedAt: string | null, statements: number, rowsChanged: number) {
    Object.assign(this.tx, { state, startedAt, statements, rowsChanged });
    if (state === 'none') this.tx.automatic = false;
    this.renderTx();
    this.ctx.changed(this);
  }

  private renderTx() {
    const s = this.tx.state;
    const el = this.el;
    el.txBar.hidden = s === 'none';
    el.txBar.className = s === 'none' ? 'txbar' : `txbar tx-${s}`;
    if (s !== 'none') {
      el.txIcon.textContent = s === 'open' ? 'ⓘ' : s === 'changed' ? '✎' : '✖';
      el.txLabel.textContent = s === 'open' ? 'Transaction open' : s === 'changed' ? 'Changes pending' : 'Transaction aborted';
      // An automatic transaction with nothing in it: ending or rolling it back would only start another, so no buttons.
      const idleAuto = s === 'open' && this.tx.automatic;
      el.txEnd.hidden = s !== 'open' || idleAuto; // nothing changed: ending it is just a commit
      el.txCommit.hidden = s !== 'changed';
      el.txRollback.hidden = idleAuto;     // the only way out of a failed transaction
      this.updateTxDetail();
    }
    this.updateButtons();
  }

  private updateTxDetail() {
    const t = this.tx;
    if (t.state === 'none') return;
    const parts: string[] = [];
    // The count runs from the first statement in the transaction, whether or not anything has changed yet.
    if (t.state === 'open') parts.push(`${plural(t.statements, 'statement')} · nothing changed yet`);
    else if (t.state === 'changed') parts.push(`${plural(t.statements, 'statement')} · ${plural(t.rowsChanged, 'row')} changed`);
    else parts.push('an error aborted it: roll back to continue');
    if (t.startedAt) parts.push(`open ${elapsed(Date.now() - Date.parse(t.startedAt))}`);
    // Say who started it: with auto-transaction, a plain UPDATE leaves a transaction open that the user never typed.
    if (t.automatic) parts.push('started automatically');
    this.el.txDetail.textContent = parts.join(' · ');
  }

  async txAction(action: 'begin' | 'commit' | 'rollback') {
    if (!this.connected) return;
    const r = await this.ctx.bridge.request({ type: 'transaction', connectionId: this.connectionId, action });
    if (r.type === 'error') this.setMessage(`${action} failed: ${r.message}`, 'error');
  }

  // ---- small things ---------------------------------------------------------------------------------------------

  /**
   * The message bar between the editor and the results. Its tone says how much it matters: amber for "this cannot run yet"
   * (not connected, blocked), red when something failed (a connection), neutral for the rest. The icon repeats the tone so
   * it never depends on colour alone.
   */
  setMessage(text: string, kind: 'info' | 'warning' | 'error' = 'info') {
    this.el.msgText.textContent = text;
    this.el.msgIcon.textContent = kind === 'warning' ? '⚠' : kind === 'error' ? '✖' : 'ⓘ';
    this.el.msg.dataset.kind = kind;
    this.el.msg.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    this.el.msg.hidden = !text;
  }

  private updateButtons() {
    const connected = this.connected;
    this.el.run.disabled = !connected || this.running !== null;
    this.el.runMore.disabled = this.el.run.disabled;
    this.el.cancel.disabled = this.running === null;
    // Begin only means something on a writable session outside a transaction. When it is greyed, the tooltip says why.
    const begin = this.el.begin;
    begin.disabled = !connected || this.readOnly || this.tx.state !== 'none';
    begin.title = !connected ? 'Connect first.'
      : this.readOnly ? 'Not on a read-only connection: nothing can change, so there is nothing to commit or roll back.'
      : this.tx.automatic ? 'Auto-transaction: this sheet is always inside a transaction.'
      : this.tx.state !== 'none' ? 'A transaction is already open.'
      : 'Begin a transaction. You can also just type BEGIN: the bar follows the server either way.';
  }
}

/** The schema from an engine "schema" event, with the names that must be quoted to write them (F13). */
function schemaOf(e: { type: string; schema?: unknown; quoted?: unknown }): DatabaseSchema | null {
  if (e.type !== 'schema') return null;
  const schema = e.schema as DatabaseSchema;
  schema.quoted = (e.quoted as Record<string, string> | undefined) ?? {};
  return schema;
}
