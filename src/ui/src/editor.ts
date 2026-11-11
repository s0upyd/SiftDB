import { monaco } from './monaco';

export type QueryMode = 'assisted' | 'strict';

/** An edit that is safe to apply unasked: the text with quotes added, nothing else changed. */
export interface Correction { start: number; length: number; text: string; message: string }

/** A problem that needs the user: a suggested fix, or none when a choice is needed. */
export interface Issue {
  start: number;
  length: number;
  code: 'caseMismatch' | 'ambiguous' | 'stringQuote' | 'unknownColumn';
  severity: 'info' | 'warning' | 'error';
  message: string;
  fixText: string | null;
  /** Every candidate replacement (an unknown column can have several), best first. Empty when the user has to choose. */
  fixes: string[];
}

/** The engine's answer for a sheet (see EngineHost "analyzeSheet"). Offsets are UTF-16, same as Monaco's. */
export interface SheetAnalysis {
  statements: { start: number; length: number }[];
  pick: {
    start: number;
    length: number;
    reason: 'none' | 'selection' | 'cursorStatement' | 'statementAbove' | 'onlyStatement';
    /** What to send to the database: the statement with the mode's corrections applied. */
    sql: string | null;
    /** Why it must not run yet, or null. */
    blocked: string | null;
  };
  corrections: Correction[];
  issues: Issue[];
}

export type Analyze = (text: string, selectionStart: number, selectionEnd: number, mode: QueryMode) => Promise<SheetAnalysis | null>;

/** The engine's suggestions for a cursor offset (see EngineHost "complete"). `start`/`end` is the text they replace. */
export interface CompletionResponse {
  start: number;
  end: number;
  items: {
    label: string;
    insertText: string;
    filterText: string;
    kind: 'schema' | 'table' | 'view' | 'column' | 'alias';
    detail: string | null;
    sortText: string;
  }[];
}

export type Complete = (text: string, offset: number) => Promise<CompletionResponse | null>;

export type RunTarget =
  | { kind: 'run'; sql: string }
  | { kind: 'blocked'; reason: string } // the engine says it must not run yet (problems in Strict, an ambiguity in Assisted)
  | { kind: 'none' }                    // connected, but nothing sensible under the cursor
  | { kind: 'disconnected' };           // no connection, so no SQL dialect to split with

export interface EditorCallbacks {
  /** F5 or Ctrl+Enter. */
  run(): void;
  /** Ctrl+. */
  cancel(): void;
  /** The ▶ in the margin. */
  runSql(target: RunTarget): void;
  /** Schema-aware suggestions. Answered by the engine, which owns the dialect and the schema cache. */
  complete: Complete;
}

const ANALYZE_DELAY_MS = 120;
const MARKER_OWNER = 'siftdb';

interface Fix { start: number; end: number; text: string; title: string }

/** Monaco's completion and quick-fix providers are global to a language. With several editors on the page they must be
 * registered once and route by model, or every suggestion would appear once per open sheet. */
const editors = new Map<string, SqlEditor>();
let providersRegistered = false;
let editorCounter = 0;

function ensureProviders() {
  if (providersRegistered) return;
  providersRegistered = true;

  const Kind = monaco.languages.CompletionItemKind;
  const kinds = { schema: Kind.Module, table: Kind.Class, view: Kind.Interface, column: Kind.Field, alias: Kind.Variable };
  monaco.languages.registerCompletionItemProvider('pgsql', {
    // Space matters most (right after FROM or JOIN); the engine answers "nothing" wherever nothing fits.
    triggerCharacters: [' ', '.', '"', ','],
    provideCompletionItems: async (model, position) => {
      const editor = editors.get(model.uri.toString());
      const r = editor ? await editor.completeAt(model.getValue(), model.getOffsetAt(position)) : null;
      if (!r || r.items.length === 0) return { suggestions: [] };
      const from = model.getPositionAt(r.start);
      const to = model.getPositionAt(r.end);
      const range = new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column);
      return {
        // The engine filters by what was typed, so ask again as typing continues.
        incomplete: true,
        suggestions: r.items.map((i) => ({
          label: i.label,
          kind: kinds[i.kind],
          insertText: i.insertText,
          filterText: i.filterText,
          sortText: i.sortText,
          detail: i.detail ?? undefined,
          range,
          // "shop." should open the list of shop's tables straight away.
          command: i.kind === 'schema' ? { id: 'editor.action.triggerSuggest', title: '' } : undefined,
        })),
      };
    },
  });

  // The lightbulb: offers whichever of that editor's current fixes overlap the cursor or selection.
  monaco.languages.registerCodeActionProvider('pgsql', {
    provideCodeActions: (model, range) => {
      const editor = editors.get(model.uri.toString());
      if (!editor) return { actions: [], dispose: () => {} };
      const from = model.getOffsetAt(range.getStartPosition());
      const to = model.getOffsetAt(range.getEndPosition());
      const actions = editor.fixesIn(from, to).map((f) => {
        const a = model.getPositionAt(f.start);
        const b = model.getPositionAt(f.end);
        return {
          title: f.title,
          kind: 'quickfix',
          isPreferred: true,
          edit: {
            edits: [{
              resource: model.uri,
              versionId: undefined,
              textEdit: { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text: f.text },
            }],
          },
        };
      });
      return { actions, dispose: () => {} };
    },
  });
}

/**
 * Monaco configured for SQL. Statement boundaries and identifier problems come from the engine (never from looking for
 * ';' or guessing at quoting): after each pause in typing or cursor movement it is asked to analyze the sheet, and the
 * result drives a ▶ in the margin per statement, a light highlight on the one F5 would run, and:
 *  - Assisted mode: the quotes the database needs, shown greyed in the text. They are not part of the document, the
 *    cursor skips them, and what runs is exactly what is displayed.
 *  - Strict mode: the same problems as squiggles with quick fixes; the text you write is the text that runs.
 */
export class SqlEditor {
  private editor: monaco.editor.IStandaloneCodeEditor;
  private model: monaco.editor.ITextModel;
  private decorations: monaco.editor.IEditorDecorationsCollection;
  private timer: number | undefined;
  private seq = 0;
  private mode: QueryMode = 'assisted';
  private corrections: Correction[] = [];
  private fixes: Fix[] = [];

  constructor(container: HTMLElement, private analyze: Analyze, private cb: EditorCallbacks, initial: string) {
    this.editor = monaco.editor.create(container, {
      value: initial,
      language: 'pgsql',
      automaticLayout: true,
      glyphMargin: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontFamily: 'Consolas, "Cascadia Mono", monospace',
      fontSize: 13,
      lineNumbersMinChars: 3,
      tabSize: 2,
      renderLineHighlight: 'none',
      ariaLabel: 'SQL editor',
      // Popups (suggestions, hovers) are drawn over the whole window instead of being clipped by this small editor.
      fixedOverflowWidgets: true,
      // Suggestions come from the schema, not from words that happen to be in the document.
      wordBasedSuggestions: 'off',
      suggest: { showWords: false },
      quickSuggestions: { other: true, comments: false, strings: false },
      suggestOnTriggerCharacters: true,
      tabCompletion: 'on',
    });
    this.model = this.editor.getModel()!;
    editors.set(this.model.uri.toString(), this);
    ensureProviders();
    // One line-ending convention, so offsets we send to the engine always match offsets Monaco reports.
    this.model.setEOL(monaco.editor.EndOfLineSequence.LF);
    this.decorations = this.editor.createDecorationsCollection();

    // Monaco keybindings are global to the page, not per editor: with several sheets open, the last editor created would own
    // F5 for all of them. Each editor tags its own context with a unique id, and each binding only applies under that tag.
    const uid = `siftdb-editor-${++editorCounter}`;
    this.editor.createContextKey<string>('siftdbEditor', uid);
    const mine = `siftdbEditor == '${uid}'`;

    const { KeyCode, KeyMod } = monaco;
    this.editor.addCommand(KeyCode.F5, () => cb.run(), mine);
    this.editor.addCommand(KeyMod.CtrlCmd | KeyCode.Enter, () => cb.run(), mine);
    this.editor.addCommand(KeyMod.CtrlCmd | KeyCode.Period, () => cb.cancel(), mine);
    // Ctrl+. is Cancel (design section 6), so Monaco's own quick-fix command moves to Alt+Enter. Binding the real command (and
    // removing its default) rather than wrapping it means the hover's "Quick Fix... (Alt+Enter)" label tells the truth.
    monaco.editor.addKeybindingRule({ keybinding: KeyMod.CtrlCmd | KeyCode.Period, command: '-editor.action.quickFix' });
    monaco.editor.addKeybindingRule({ keybinding: KeyMod.Alt | KeyCode.Enter, command: 'editor.action.quickFix', when: `editorTextFocus && ${mine}` });
    this.editor.addAction({
      id: 'siftdb.acceptAll',
      label: 'Accept all identifier corrections',
      keybindings: [KeyMod.CtrlCmd | KeyMod.Alt | KeyCode.KeyQ],
      contextMenuGroupId: 'modification',
      precondition: mine,
      run: () => this.acceptAll(),
    });

    this.editor.onDidChangeModelContent(() => this.schedule());
    this.editor.onDidChangeCursorSelection(() => this.schedule());
    this.editor.onMouseDown((e) => {
      if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position) {
        void this.runStatementOnLine(e.target.position.lineNumber);
      }
    });
  }

  getMode(): QueryMode { return this.mode; }

  setMode(mode: QueryMode) {
    this.mode = mode;
    this.schedule(0);
  }

  setTheme(dark: boolean) { monaco.editor.setTheme(dark ? 'vs-dark' : 'vs'); }

  /** For the global completion provider: suggestions for this editor's text. */
  completeAt(text: string, offset: number) { return this.cb.complete(text, offset); }

  /** For the global quick-fix provider: this editor's current fixes that touch [from, to]. */
  fixesIn(from: number, to: number): Fix[] { return this.fixes.filter((f) => f.start <= to && f.end >= from); }

  /** Re-measure after the editor's container became visible (switching sheets). */
  layout() { this.editor.layout(); }

  /** Frees the editor and its document when its sheet is closed. */
  dispose() {
    window.clearTimeout(this.timer);
    editors.delete(this.model.uri.toString());
    this.editor.dispose();
    this.model.dispose();
  }
  setCursor(line: number, column = 1) { this.editor.setPosition({ lineNumber: line, column }); }
  focus() { this.editor.focus(); }

  /** Call after connecting or disconnecting: the margin markers depend on having a dialect. */
  refresh() { this.schedule(0); }

  /** What F5 would run right now. Asks the engine afresh, so it is never a stale answer from before the last keystroke. */
  async statementToRun(): Promise<RunTarget> {
    const { text, start, end } = this.snapshot();
    return this.toTarget(await this.analyze(text, start, end, this.mode));
  }

  private toTarget(a: SheetAnalysis | null): RunTarget {
    if (!a) return { kind: 'disconnected' };
    if (a.pick.reason === 'none' || a.pick.sql === null) return { kind: 'none' };
    if (a.pick.blocked) return { kind: 'blocked', reason: a.pick.blocked };
    return { kind: 'run', sql: a.pick.sql };
  }

  /** Applies every automatic correction as real text ("Accept all", design section 3). */
  private acceptAll() {
    const edits = [...this.corrections].sort((x, y) => y.start - x.start).map((c) => {
      const a = this.model.getPositionAt(c.start);
      const b = this.model.getPositionAt(c.start + c.length);
      return { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text: c.text };
    });
    if (edits.length) this.editor.executeEdits('siftdb.acceptAll', edits);
  }

  private snapshot() {
    const sel = this.editor.getSelection()!;
    return {
      text: this.model.getValue(),
      start: this.model.getOffsetAt(sel.getStartPosition()),
      end: this.model.getOffsetAt(sel.getEndPosition()),
    };
  }

  private schedule(delay = ANALYZE_DELAY_MS) {
    window.clearTimeout(this.timer);
    this.timer = window.setTimeout(() => void this.update(), delay);
  }

  private async update() {
    const mine = ++this.seq;
    const { text, start, end } = this.snapshot();
    const a = await this.analyze(text, start, end, this.mode);
    // Superseded by a newer request, or the text moved on while we waited (another update is already queued).
    if (mine !== this.seq || text !== this.model.getValue()) return;
    this.paint(a);
  }

  private paint(a: SheetAnalysis | null) {
    const m = this.model;
    if (!a) {
      this.decorations.clear();
      monaco.editor.setModelMarkers(m, MARKER_OWNER, []);
      this.corrections = [];
      this.fixes = [];
      return;
    }
    const text = m.getValue();
    const at = (offset: number) => m.getPositionAt(offset);
    const rangeOf = (start: number, length: number) => {
      const s = at(start), e = at(start + length);
      return new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column);
    };

    // ▶ per statement, and the highlight on the one that would run
    const items: monaco.editor.IModelDeltaDecoration[] = a.statements.map((s) => {
      const line = at(s.start).lineNumber;
      return {
        range: new monaco.Range(line, 1, line, 1),
        options: { glyphMarginClassName: 'run-glyph', glyphMarginHoverMessage: { value: 'Run this statement' } },
      };
    });
    const r = a.pick.reason;
    if (r === 'cursorStatement' || r === 'statementAbove' || r === 'onlyStatement') {
      items.push({ range: rangeOf(a.pick.start, a.pick.length), options: { className: 'current-statement' } });
    }

    // Assisted: the quotes the database needs, drawn greyed around the word. They are display only.
    this.corrections = a.corrections;
    this.fixes = [];
    const markers: monaco.editor.IMarkerData[] = [];
    for (const c of a.corrections) {
      const raw = text.substr(c.start, c.length);
      if (this.mode === 'assisted' && c.text === `"${raw}"`) {
        // One decoration over the word, with a quote injected at each end. (Monaco drops injected text on an empty
        // range, so the opening and closing quote hang off the word's own range rather than two empty ones.)
        const quote = { content: '"', inlineClassName: 'sift-ghost', cursorStops: monaco.editor.InjectedTextCursorStops.None };
        items.push({ range: rangeOf(c.start, c.length), options: { before: quote, after: quote } });
      } else {
        // Strict (or a correction that is not a pure insertion): a problem with a one-click fix.
        const range = rangeOf(c.start, c.length);
        markers.push({ severity: monaco.MarkerSeverity.Warning, message: c.message, code: 'needsQuotes',
          startLineNumber: range.startLineNumber, startColumn: range.startColumn,
          endLineNumber: range.endLineNumber, endColumn: range.endColumn });
        this.fixes.push({ start: c.start, end: c.start + c.length, text: c.text, title: `Quote as ${c.text}` });
      }
    }

    // Problems that need the user, in both modes.
    const severity = { info: monaco.MarkerSeverity.Info, warning: monaco.MarkerSeverity.Warning, error: monaco.MarkerSeverity.Error };
    for (const i of a.issues) {
      const range = rangeOf(i.start, i.length);
      markers.push({ severity: severity[i.severity], message: i.message, code: i.code,
        startLineNumber: range.startLineNumber, startColumn: range.startColumn,
        endLineNumber: range.endLineNumber, endColumn: range.endColumn });
      for (const fix of i.fixes ?? []) this.fixes.push({ start: i.start, end: i.start + i.length, text: fix, title: `Use ${fix}` });
    }

    this.decorations.set(items);
    monaco.editor.setModelMarkers(m, MARKER_OWNER, markers);
  }

  private async runStatementOnLine(line: number) {
    const { text, start, end } = this.snapshot();
    const first = await this.analyze(text, start, end, this.mode);
    if (!first) return;
    const s = first.statements.find((x) => this.model.getPositionAt(x.start).lineNumber === line);
    if (!s) return;
    // Ask again with that statement selected, so the engine prepares exactly it (corrections, blocking).
    const second = await this.analyze(text, s.start, s.start + s.length, this.mode);
    this.cb.runSql(this.toTarget(second));
  }
}
