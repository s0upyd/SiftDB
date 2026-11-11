import type { Bridge, DatabaseSchema } from './bridge';
import { CompletionResponse, QueryMode, RunTarget, SheetAnalysis, SqlEditor } from './editor';
import { ResultView, ResultsContext, plural } from './results';
import { revealSelectedTab, scrollTabsWithWheel } from './tabs';

export type TxState = 'none' | 'open' | 'changed' | 'failed';

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
}

export interface ConnectResult { ok: boolean; schema: DatabaseSchema | null; error?: string }

const TEMPLATE = `
  <div class="sheetbar">
    <button class="run" disabled title="Run the statement under the cursor (F5 or Ctrl+Enter)">Run</button>
    <button class="cancel" disabled title="Cancel the running statement (Ctrl+.)">Cancel</button>
    <button class="begin" disabled title="Begin a transaction. You can also just type BEGIN: the bar follows the server either way.">Begin</button>
    <button class="mode" aria-pressed="false" title="Assisted: quotes are added for you and shown greyed; what runs is what you see. Strict: the text you write is the text that runs; problems are squiggles with quick fixes (Alt+Enter) and block running.">Assisted</button>
    <label><input class="fetchAll" type="checkbox" checked> fetch all</label>
    <button class="bench" disabled>Scroll benchmark</button>
  </div>
  <div class="txbar" hidden role="status" aria-live="polite">
    <span class="txIcon" aria-hidden="true"></span>
    <strong class="txLabel"></strong>
    <span class="txDetail"></span>
    <span class="grow"></span>
    <button class="txEnd" title="End the transaction (commit). Nothing has changed in it.">End</button>
    <button class="txCommit" title="Commit the changes (Ctrl+Shift+Enter)">Commit</button>
    <button class="txRollback" title="Roll back (Ctrl+Shift+R)">Rollback</button>
  </div>
  <div class="editor"></div>
  <div class="msg" hidden role="status"></div>
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
  readonly tx = { state: 'none' as TxState, startedAt: null as string | null, statements: 0, rowsChanged: 0 };

  private readonly editor: SqlEditor;
  private results: ResultView[] = [];
  private activeResult: ResultView | null = null;
  private running: ResultView | null = null;

  private readonly el: {
    run: HTMLButtonElement; cancel: HTMLButtonElement; begin: HTMLButtonElement; mode: HTMLButtonElement;
    fetchAll: HTMLInputElement; bench: HTMLButtonElement;
    txBar: HTMLElement; txIcon: HTMLElement; txLabel: HTMLElement; txDetail: HTMLElement;
    txEnd: HTMLButtonElement; txCommit: HTMLButtonElement; txRollback: HTMLButtonElement;
    editor: HTMLElement; msg: HTMLElement; tabs: HTMLElement; results: HTMLElement; empty: HTMLElement;
  };
  private readonly resultsCtx: ResultsContext;

  /** `parent` is where the sheet lives: it is attached before the editor is built, because Monaco measures its container. */
  constructor(private ctx: SheetContext, parent: HTMLElement, public title: string, initialText: string, cursorLine = 1) {
    this.root.className = 'sheet';
    this.root.innerHTML = TEMPLATE;
    parent.append(this.root);
    const $ = <T extends HTMLElement = HTMLElement>(selector: string) => this.root.querySelector(selector) as T;
    this.el = {
      run: $('.run'), cancel: $('.cancel'), begin: $('.begin'), mode: $('.mode'), fetchAll: $('.fetchAll'), bench: $('.bench'),
      txBar: $('.txbar'), txIcon: $('.txIcon'), txLabel: $('.txLabel'), txDetail: $('.txDetail'),
      txEnd: $('.txEnd'), txCommit: $('.txCommit'), txRollback: $('.txRollback'),
      editor: $('.editor'), msg: $('.msg'), tabs: $('.resulttabs'), results: $('.results'), empty: $('.empty'),
    };
    scrollTabsWithWheel(this.el.tabs);
    this.resultsCtx = {
      bridge: ctx.bridge,
      fetchAll: () => this.el.fetchAll.checked,
      registerQuery: (id, v) => ctx.registerQuery(id, v),
      unregisterQuery: (id) => ctx.unregisterQuery(id),
      onFinished: (v) => {
        if (this.running === v) this.running = null;
        this.updateButtons();
      },
    };

    this.editor = new SqlEditor(this.el.editor, this.analyze, {
      run: () => void this.runFromEditor(),
      cancel: () => this.cancel(),
      runSql: (t) => this.handleTarget(t),
      complete: this.complete,
    }, initialText);
    this.editor.setCursor(cursorLine);

    this.el.run.onclick = () => void this.runFromEditor();
    this.el.cancel.onclick = () => this.cancel();
    this.el.begin.onclick = () => void this.txAction('begin');
    this.el.txEnd.onclick = () => void this.txAction('commit');
    this.el.txCommit.onclick = () => void this.txAction('commit');
    this.el.txRollback.onclick = () => void this.txAction('rollback');
    this.el.bench.onclick = () => this.activeResult?.runScrollBenchmark();
    // Strict: the text you write is the text that runs. Assisted: the editor shows (and runs) the quoting the database needs.
    this.el.mode.onclick = () => {
      const next: QueryMode = this.editor.getMode() === 'assisted' ? 'strict' : 'assisted';
      this.editor.setMode(next);
      this.el.mode.textContent = next === 'assisted' ? 'Assisted' : 'Strict';
      this.el.mode.setAttribute('aria-pressed', String(next === 'strict'));
    };

    this.setMessage('Not connected. Enter a connection string above and press Connect.');
    this.updateButtons();
  }

  // ---- the session ----------------------------------------------------------------------------------------------

  get connected(): boolean { return this.connectionId !== 0; }

  /** Opens this sheet's own session. `wantSchema` is true for the one sheet whose schema fills the sidebar. */
  async connect(connectionString: string, wantSchema: boolean): Promise<ConnectResult> {
    this.setMessage('Connecting…');
    const r = await this.ctx.bridge.request({ type: 'connect', provider: 'postgres', connectionString });
    if (r.type === 'error') {
      this.setMessage(`Connect failed: ${r.message}`);
      return { ok: false, schema: null, error: r.message };
    }
    this.connectionId = r.connectionId;
    this.ctx.registerConnection(this.connectionId, this);
    this.updateButtons();

    // The engine keeps a schema cache per session: completion and the identifier resolver read it.
    const s = await this.ctx.bridge.request({ type: 'introspect', connectionId: this.connectionId, send: wantSchema });
    this.setMessage('');
    this.editor.refresh();
    return { ok: true, schema: s.type === 'schema' ? (s.schema as DatabaseSchema) : null };
  }

  /** Reloads this session's schema cache; returns the schema when the sidebar wants a copy. */
  async refreshSchema(wantSchema: boolean): Promise<DatabaseSchema | null> {
    if (!this.connected) return null;
    const s = await this.ctx.bridge.request({ type: 'introspect', connectionId: this.connectionId, send: wantSchema });
    return s.type === 'schema' ? (s.schema as DatabaseSchema) : null;
  }

  /** Ends the session. The engine rolls back anything uncommitted; results already shown stay on screen. */
  async disconnect() {
    if (!this.connected) return;
    const id = this.connectionId;
    this.ctx.unregisterConnection(id); // stop routing events to a session that is going away
    this.connectionId = 0;
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
    if (!active) return;
    this.editor.layout();
    this.activeResult?.grid.invalidate();
  }

  focusEditor() { this.editor.focus(); }

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

  private handleTarget(target: RunTarget) {
    switch (target.kind) {
      case 'run': void this.runSql(target.sql); break;
      case 'blocked': this.setMessage(`not run: ${target.reason}`); break;
      case 'none': this.setMessage('nothing to run: put the cursor in a statement, or select some text'); break;
      case 'disconnected':
        this.setMessage('Not connected. Connect first: the connection decides which SQL dialect splits your statements.');
        break;
    }
  }

  private async runSql(sql: string) {
    this.setMessage('');
    const view = this.viewForRun();
    this.running = view;
    this.updateButtons();
    this.renderResultTabs();
    await view.run(this.connectionId, sql);
    this.renderResultTabs(); // its label now names this statement
  }

  cancel() {
    this.running?.cancel();
  }

  // ---- result tabs ----------------------------------------------------------------------------------------------

  /** The tab a run fills: the unpinned one, or a new one if every tab is pinned. */
  private viewForRun(): ResultView {
    let view = this.results.find((r) => !r.pinned);
    if (!view) {
      view = new ResultView(this.resultsCtx, this.ctx.isDark());
      this.results.push(view);
      this.el.results.append(view.panel);
    }
    this.showResult(view);
    return view;
  }

  private showResult(view: ResultView | null) {
    this.activeResult = view;
    this.results.forEach((r) => { r.panel.hidden = r !== view; });
    this.el.empty.hidden = this.results.length > 0;
    view?.grid.invalidate();
    this.renderResultTabs();
    this.updateButtons();
  }

  private closeResult(view: ResultView) {
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
      tab.title = view.panel.title;
      tab.onclick = () => this.showResult(view);

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

      tab.append(label, pin, close);
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
  onTransaction(e: { state: TxState; startedAt: string | null; statements: number; rowsChanged: number }) {
    this.setTx(e.state, e.startedAt ?? null, e.statements, e.rowsChanged);
  }

  private setTx(state: TxState, startedAt: string | null, statements: number, rowsChanged: number) {
    Object.assign(this.tx, { state, startedAt, statements, rowsChanged });
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
      el.txEnd.hidden = s !== 'open';      // nothing changed: ending it is just a commit
      el.txCommit.hidden = s !== 'changed';
      el.txRollback.hidden = false;        // the only way out of a failed transaction
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
    this.el.txDetail.textContent = parts.join(' · ');
  }

  async txAction(action: 'begin' | 'commit' | 'rollback') {
    if (!this.connected) return;
    const r = await this.ctx.bridge.request({ type: 'transaction', connectionId: this.connectionId, action });
    if (r.type === 'error') this.setMessage(`${action} failed: ${r.message}`);
  }

  // ---- small things ---------------------------------------------------------------------------------------------

  setMessage(text: string) {
    this.el.msg.textContent = text;
    this.el.msg.hidden = !text;
  }

  private updateButtons() {
    const connected = this.connected;
    this.el.run.disabled = !connected || this.running !== null;
    this.el.cancel.disabled = this.running === null;
    this.el.begin.disabled = !connected || this.tx.state !== 'none';
    this.el.bench.disabled = !connected || !this.activeResult;
  }
}
