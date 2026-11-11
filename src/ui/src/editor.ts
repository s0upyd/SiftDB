import { monaco } from './monaco';

/** The engine's answer for a sheet (see EngineHost "analyzeSheet"). Offsets are UTF-16, same as Monaco's. */
export interface SheetAnalysis {
  statements: { start: number; length: number }[];
  pick: { start: number; length: number; reason: 'none' | 'selection' | 'cursorStatement' | 'statementAbove' | 'onlyStatement' };
}

export type Analyze = (text: string, selectionStart: number, selectionEnd: number) => Promise<SheetAnalysis | null>;

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
  | { kind: 'none' }          // connected, but nothing sensible under the cursor
  | { kind: 'disconnected' }; // no connection, so no SQL dialect to split with

export interface EditorCallbacks {
  /** F5 or Ctrl+Enter. */
  run(): void;
  /** Ctrl+. */
  cancel(): void;
  /** The ▶ in the margin. */
  runSql(sql: string): void;
  /** Schema-aware suggestions. Answered by the engine, which owns the dialect and the schema cache. */
  complete: Complete;
}

const ANALYZE_DELAY_MS = 120;

/**
 * Monaco configured for SQL. Statement boundaries come from the engine (never from looking for ';'): after each pause
 * in typing or cursor movement it is asked to split the sheet, and the result drives a ▶ in the margin for every
 * statement and a light highlight on the one F5 would run.
 */
export class SqlEditor {
  private editor: monaco.editor.IStandaloneCodeEditor;
  private model: monaco.editor.ITextModel;
  private decorations: monaco.editor.IEditorDecorationsCollection;
  private timer: number | undefined;
  private seq = 0;

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
    this.registerCompletions();
    this.model = this.editor.getModel()!;
    // One line-ending convention, so offsets we send to the engine always match offsets Monaco reports.
    this.model.setEOL(monaco.editor.EndOfLineSequence.LF);
    this.decorations = this.editor.createDecorationsCollection();

    const { KeyCode, KeyMod } = monaco;
    this.editor.addCommand(KeyCode.F5, () => cb.run());
    this.editor.addCommand(KeyMod.CtrlCmd | KeyCode.Enter, () => cb.run());
    this.editor.addCommand(KeyMod.CtrlCmd | KeyCode.Period, () => cb.cancel());

    this.editor.onDidChangeModelContent(() => this.schedule());
    this.editor.onDidChangeCursorSelection(() => this.schedule());
    this.editor.onMouseDown((e) => {
      if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position) {
        void this.runStatementOnLine(e.target.position.lineNumber);
      }
    });
  }

  private registerCompletions() {
    const Kind = monaco.languages.CompletionItemKind;
    const kinds = { schema: Kind.Module, table: Kind.Class, view: Kind.Interface, column: Kind.Field, alias: Kind.Variable };
    monaco.languages.registerCompletionItemProvider('pgsql', {
      // Space matters most (right after FROM or JOIN); the engine answers "nothing" wherever nothing fits.
      triggerCharacters: [' ', '.', '"', ','],
      provideCompletionItems: async (model, position) => {
        const r = await this.cb.complete(model.getValue(), model.getOffsetAt(position));
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
  }

  setTheme(dark: boolean) { monaco.editor.setTheme(dark ? 'vs-dark' : 'vs'); }
  setCursor(line: number, column = 1) { this.editor.setPosition({ lineNumber: line, column }); }
  focus() { this.editor.focus(); }

  /** Call after connecting or disconnecting: the margin markers depend on having a dialect. */
  refresh() { this.schedule(0); }

  /** What F5 would run right now. Asks the engine afresh, so it is never a stale answer from before the last keystroke. */
  async statementToRun(): Promise<RunTarget> {
    const { text, start, end } = this.snapshot();
    const a = await this.analyze(text, start, end);
    if (!a) return { kind: 'disconnected' };
    if (a.pick.reason === 'none') return { kind: 'none' };
    return { kind: 'run', sql: text.substr(a.pick.start, a.pick.length) };
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
    const a = await this.analyze(text, start, end);
    // Superseded by a newer request, or the text moved on while we waited (another update is already queued).
    if (mine !== this.seq || text !== this.model.getValue()) return;
    this.paint(a);
  }

  private paint(a: SheetAnalysis | null) {
    if (!a) { this.decorations.clear(); return; }
    const m = this.model;
    const items: monaco.editor.IModelDeltaDecoration[] = a.statements.map((s) => {
      const line = m.getPositionAt(s.start).lineNumber;
      return {
        range: new monaco.Range(line, 1, line, 1),
        options: { glyphMarginClassName: 'run-glyph', glyphMarginHoverMessage: { value: 'Run this statement' } },
      };
    });
    const r = a.pick.reason;
    if (r === 'cursorStatement' || r === 'statementAbove' || r === 'onlyStatement') {
      const from = m.getPositionAt(a.pick.start);
      const to = m.getPositionAt(a.pick.start + a.pick.length);
      items.push({
        range: new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column),
        options: { className: 'current-statement' },
      });
    }
    this.decorations.set(items);
  }

  private async runStatementOnLine(line: number) {
    const { text, start, end } = this.snapshot();
    const a = await this.analyze(text, start, end);
    if (!a) return;
    const s = a.statements.find((x) => this.model.getPositionAt(x.start).lineNumber === line);
    if (s) this.cb.runSql(text.substr(s.start, s.length));
  }
}
