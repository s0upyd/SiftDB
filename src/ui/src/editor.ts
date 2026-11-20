import { commands, parseKey, shortcutOf } from './commands';
import { showMenu } from './menu';
import { monaco } from './monaco';

export type QueryMode = 'assisted' | 'strict';

/** An edit that is safe to apply unasked: the text with quotes added, nothing else changed. */
export interface Correction { start: number; length: number; text: string; message: string }

/** A problem that needs the user: a suggested fix, or none when a choice is needed. */
export interface Issue {
  start: number;
  length: number;
  code: 'caseMismatch' | 'ambiguous' | 'stringQuote' | 'unknownColumn' | 'missingSemicolon' | 'qualifiedSetColumn' | 'numberForBoolean';
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
  /** Quick fixes with no problem behind them, so no squiggle: expanding a * (design section 14, F10). */
  actions?: { start: number; length: number; text: string; title: string }[];
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

/**
 * `sql` is what runs; `source` is the same statement as written in the editor (before Assisted corrections), starting at
 * `sourceStart`, so a plan can point back at its table references (design section 14, F6).
 */
export type RunTarget =
  | { kind: 'run'; sql: string; source: string; sourceStart: number }
  | { kind: 'blocked'; reason: string } // the engine says it must not run yet (problems in Strict, an ambiguity in Assisted)
  | { kind: 'none' }                    // connected, but nothing sensible under the cursor
  | { kind: 'disconnected' };           // no connection, so no SQL dialect to split with

/** What to do with a statement: run it, dry-run it, or show its plan (estimated, or analysed by running it). */
export type RunHow = 'run' | 'dry' | 'explain' | 'analyze';

/** Ranges of the editor's text that follow edits (Monaco decorations), for linking a plan to the SQL it came from. */
export interface TrackedRanges {
  /** Where range `i` is now, or null if its text was deleted. */
  rangeOf(i: number): { start: number; end: number } | null;
  /** The ranges that contain `offset`. */
  at(offset: number): number[];
  /** Marks these ranges in the editor (empty clears the marks). */
  highlight(indices: number[]): void;
  dispose(): void;
}

export interface EditorCallbacks {
  /** F5 or Ctrl+Enter. */
  run(): void;
  /** Ctrl+. */
  cancel(): void;
  /** Ctrl+Shift+F5: dry-run the statement F5 would run (design section 14, F2). */
  dryRun(): void;
  /** Format SQL (design section 14, F7): the statement under the cursor or the selection's, or the whole sheet. */
  format(whole: boolean): void;
  /** Ctrl+E: the plan of the statement F5 would run; with `analyze` (Ctrl+Shift+E) it runs and is timed (design section 14, F6). */
  explain(analyze: boolean): void;
  /** The ▶ in the margin (a click runs; its right-click menu can dry-run or explain). */
  runSql(target: RunTarget, how?: RunHow): void;
  /** Schema-aware suggestions. Answered by the engine, which owns the dialect and the schema cache. */
  complete: Complete;
}

const ANALYZE_DELAY_MS = 120;
const MARKER_OWNER = 'siftdb';

/** `optional`: an offer, not the fix for a problem (expanding a *), so it is not the one Alt+Enter picks first. */
interface Fix { start: number; end: number; text: string; title: string; optional?: boolean }

/** Monaco's completion and quick-fix providers are global to a language. With several editors on the page they must be
 * registered once and route by model, or every suggestion would appear once per open sheet. */
const editors = new Map<string, SqlEditor>();
let providersRegistered = false;
let editorCounter = 0;

/** A shortcut from the command registry ("Ctrl+Shift+F5") as a Monaco keybinding, or null for a key it has no name for. */
function monacoKey(spec: string): number | null {
  const { KeyCode, KeyMod } = monaco;
  const k = parseKey(spec);
  const named: Record<string, number> = { Enter: KeyCode.Enter, Tab: KeyCode.Tab, Escape: KeyCode.Escape, '.': KeyCode.Period, ',': KeyCode.Comma };
  const code = named[k.key]
    ?? (/^[a-z0-9]$/i.test(k.key) ? (KeyCode as unknown as Record<string, number>)[/\d/.test(k.key) ? `Digit${k.key}` : `Key${k.key.toUpperCase()}`] : undefined)
    ?? (/^F\d{1,2}$/.test(k.key) ? (KeyCode as unknown as Record<string, number>)[k.key] : undefined);
  if (code === undefined) return null;
  return code | (k.ctrl ? KeyMod.CtrlCmd : 0) | (k.shift ? KeyMod.Shift : 0) | (k.alt ? KeyMod.Alt : 0);
}

/**
 * A registry command as one of this editor's Monaco actions (design section 14, F12): its label and shortcuts come from
 * the registry, so the right-click menu and Monaco's keybindings say the same as the palette.
 */
function registryAction(commandId: string, menu: { group: string; order?: number } | null, precondition: string, run: () => void) {
  const c = commands.get(commandId);
  const keybindings = [...(c?.localKeys ?? []), ...(c?.keys ?? [])].map(monacoKey).filter((k): k is number => k !== null);
  return {
    id: `siftdb.${commandId}`,
    label: c?.label ?? commandId,
    keybindings,
    ...(menu ? { contextMenuGroupId: menu.group, contextMenuOrder: menu.order ?? 0 } : {}),
    precondition,
    run,
  };
}

/**
 * The editor's right-click "Command Palette" (and Monaco's F1, should a key ever reach it) opens the app's palette rather
 * than Monaco's in-editor one: a later registration of a command id wins over Monaco's own.
 */
let paletteCommandTaken = false;
function takeOverPaletteCommand() {
  if (paletteCommandTaken) return;
  paletteCommandTaken = true;
  monaco.editor.registerCommand('editor.action.quickCommand', () => void commands.get('commandPalette')?.run());
}

function ensureProviders() {
  if (providersRegistered) return;
  providersRegistered = true;
  takeOverPaletteCommand();

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
          isPreferred: !f.optional,
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
      // Monaco's experimental EditContext input sends typing to the first editor created once there are several (one per
      // sheet): text typed in Sheet 2 landed in Sheet 1. The text-area input is the long-proven one and follows focus.
      editContext: false,
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
    // SiftDB's editor actions (design section 14, F12): label and shortcuts from the command registry, so this menu, Monaco's
    // keybindings and the palette agree. Accept all corrections is Monaco's to bind (Ctrl+Alt+Q, in the editor only); the run
    // keys are the window's, bound here too so the right-click menu shows them.
    this.editor.addAction(registryAction('acceptAll', { group: 'modification' }, mine, () => this.acceptAll()));

    this.editor.onDidChangeModelContent(() => this.schedule());
    this.editor.onDidChangeCursorSelection(() => this.schedule());
    // Dry run (design section 14, F2) and Visual EXPLAIN (F6): Ctrl+E estimates only (nothing runs), Ctrl+Shift+E runs it and
    // times it.
    this.editor.addAction(registryAction('dryRun', { group: 'navigation', order: 0 }, mine, () => cb.dryRun()));
    this.editor.addAction(registryAction('explain', { group: 'navigation', order: 1 }, mine, () => cb.explain(false)));
    this.editor.addAction(registryAction('explainAnalyze', { group: 'navigation', order: 2 }, mine, () => cb.explain(true)));

    // Format SQL (design section 14, F7). Shift+Alt+F is Monaco's own Format Document, which does nothing here (there is no
    // formatter registered with Monaco): it is taken off and given to SiftDB's, which the engine does.
    monaco.editor.addKeybindingRule({ keybinding: KeyMod.Shift | KeyMod.Alt | KeyCode.KeyF, command: '-editor.action.formatDocument' });
    this.editor.addAction(registryAction('format', { group: '1_modification', order: 0 }, mine, () => cb.format(false)));
    this.editor.addAction(registryAction('formatSheet', { group: '1_modification', order: 1 }, mine, () => cb.format(true)));

    this.editor.onMouseDown((e) => {
      if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position && e.event.leftButton) {
        void this.runStatementOnLine(e.target.position.lineNumber);
      }
    });
    // Right-clicking a ▶ offers Run and Dry run for that statement. Monaco has no menu of its own on the glyph margin.
    this.editor.onContextMenu((e) => {
      if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position) return;
      const line = e.target.position.lineNumber;
      if (!this.model.getLineDecorations(line).some((d) => d.options.glyphMarginClassName === 'run-glyph')) return; // no ▶ here
      const { posx, posy } = e.event;
      showMenu(posx, posy, [
        // The same ways to run as the registry's commands, for this statement rather than the cursor's.
        ...([['run', 'run'], ['dryRun', 'dry'], ['explain', 'explain'], ['explainAnalyze', 'analyze']] as const).map(([id, how]) => {
          const c = commands.get(id);
          return { label: c?.label ?? id, shortcut: c ? shortcutOf(c) : undefined, action: () => void this.runStatementOnLine(line, how) };
        }),
      ], 'Statement actions');
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
    return this.toTarget(await this.analyze(text, start, end, this.mode), text);
  }

  /** `text`: what was analysed, so the statement as written can go with it. */
  private toTarget(a: SheetAnalysis | null, text: string): RunTarget {
    if (!a) return { kind: 'disconnected' };
    if (a.pick.reason === 'none' || a.pick.sql === null) return { kind: 'none' };
    if (a.pick.blocked) return { kind: 'blocked', reason: a.pick.blocked };
    return { kind: 'run', sql: a.pick.sql, source: text.slice(a.pick.start, a.pick.start + a.pick.length), sourceStart: a.pick.start };
  }

  // ---- the text, for files and autosave ----------------------------------------------------------------------------

  getText(): string { return this.model.getValue(); }

  /** Replaces the whole text (opening a file, restoring a sheet). Undo history starts again from here. */
  setText(text: string) {
    this.model.setValue(text);
    this.model.setEOL(monaco.editor.EndOfLineSequence.LF);
  }

  /**
   * A number that changes with every edit and comes back when edits are undone: equal to the number at the last save
   * means "nothing unsaved", even after typing and undoing.
   */
  versionId(): number { return this.model.getAlternativeVersionId(); }

  /** Called after every change to the text. */
  onChange(fn: () => void) { this.model.onDidChangeContent(fn); }

  /**
   * Saving an Assisted sheet writes the SQL that runs (design section 6): the greyed quotes become real text first.
   * Asks the engine afresh, so corrections from before the last keystroke are never applied. False when there is no
   * analysis to go on (not connected), so the caller can say the text was saved as typed.
   */
  async acceptCorrectionsForSave(): Promise<boolean> {
    const { text, start, end } = this.snapshot();
    const a = await this.analyze(text, start, end, this.mode);
    if (!a || text !== this.model.getValue()) return false;
    this.corrections = a.corrections;
    this.acceptAll();
    return true;
  }

  /** Applies every automatic correction as real text ("Accept all", design section 3). */
  /** The palette's "Accept all identifier corrections" (design section 14, F12). */
  acceptAllCorrections() { this.acceptAll(); }

  private acceptAll() {
    const edits = [...this.corrections].sort((x, y) => y.start - x.start).map((c) => {
      const a = this.model.getPositionAt(c.start);
      const b = this.model.getPositionAt(c.start + c.length);
      return { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text: c.text };
    });
    if (edits.length) this.editor.executeEdits('siftdb.acceptAll', edits);
  }

  /**
   * Inserts a block of SQL at the cursor, on lines of its own (a blank line before and after it when it lands among
   * other text, so it is a statement of its own, §6), as one undoable edit, and selects it.
   */
  insertBlock(sql: string) {
    const pos = this.editor.getPosition() ?? { lineNumber: 1, column: 1 };
    const line = this.model.getLineContent(pos.lineNumber);
    const blankHere = line.trim() === '';
    const at = blankHere ? { lineNumber: pos.lineNumber, column: 1 } : { lineNumber: pos.lineNumber, column: line.length + 1 };
    const before = blankHere ? '' : '\n\n';
    const after = this.model.getLineCount() > at.lineNumber || !blankHere ? '\n' : '';
    const body = sql.replace(/\s+$/, '');
    const endAt = blankHere ? { lineNumber: pos.lineNumber, column: line.length + 1 } : at;
    this.editor.pushUndoStop();
    this.editor.executeEdits('siftdb.script', [{ range: new monaco.Range(at.lineNumber, at.column, endAt.lineNumber, endAt.column), text: before + body + after }]);
    this.editor.pushUndoStop();
    const start = this.model.getPositionAt(this.model.getOffsetAt(at) + before.length);
    const end = this.model.getPositionAt(this.model.getOffsetAt(start) + body.length);
    this.editor.setSelection(new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column));
    this.editor.revealRangeInCenterIfOutsideViewport(new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column));
    this.editor.focus();
  }

  /**
   * Adds a statement at the end of the sheet, after a blank line (which ends the one before it, §6), as its own undoable
   * edit, and puts the cursor on it.
   */
  appendStatement(sql: string) {
    const text = this.model.getValue();
    const trimmed = text.replace(/\s+$/, '');
    const end = this.model.getPositionAt(text.length);
    const from = this.model.getPositionAt(trimmed.length);
    const insert = (trimmed ? '\n\n' : '') + sql + ';\n';
    this.editor.pushUndoStop();
    this.editor.executeEdits('siftdb.follow', [{ range: new monaco.Range(from.lineNumber, from.column, end.lineNumber, end.column), text: insert }]);
    this.editor.pushUndoStop();
    const at = this.model.getPositionAt(trimmed.length + (trimmed ? 2 : 0));
    this.editor.setPosition(at);
    this.editor.revealLineInCenterIfOutsideViewport(at.lineNumber);
  }

  /** The text and selection, with the version they belong to: an edit worked out for them applies only if nothing changed since. */
  formatSnapshot() { return { ...this.snapshot(), version: this.model.getAlternativeVersionId() }; }

  /**
   * Replaces [start, start + length) with `text` as one edit, so one Undo takes it back. False when the text has changed
   * since `version` (the edit was worked out for text that is no longer there). The cursor goes to the start of the edit.
   */
  applyFormatted(version: number, start: number, length: number, text: string): boolean {
    if (this.model.getAlternativeVersionId() !== version) return false;
    const a = this.model.getPositionAt(start);
    const b = this.model.getPositionAt(start + length);
    this.editor.pushUndoStop();
    this.editor.executeEdits('siftdb.format', [{ range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text }]);
    this.editor.pushUndoStop();
    this.editor.setPosition(a);
    this.editor.revealPositionInCenterIfOutsideViewport(a);
    return true;
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
        options: { glyphMarginClassName: 'run-glyph', glyphMarginHoverMessage: { value: 'Run this statement. Right-click for a dry run.' } },
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
    // Offers the lightbulb shows where the cursor is, with nothing marked in the text.
    for (const x of a.actions ?? []) this.fixes.push({ start: x.start, end: x.start + x.length, text: x.text, title: x.title, optional: true });

    this.decorations.set(items);
    monaco.editor.setModelMarkers(m, MARKER_OWNER, markers);
  }

  private async runStatementOnLine(line: number, how: RunHow = 'run') {
    const { text, start, end } = this.snapshot();
    const first = await this.analyze(text, start, end, this.mode);
    if (!first) return;
    const s = first.statements.find((x) => this.model.getPositionAt(x.start).lineNumber === line);
    if (!s) return;
    // Ask again with that statement selected, so the engine prepares exactly it (corrections, blocking).
    const second = await this.analyze(text, s.start, s.start + s.length, this.mode);
    this.cb.runSql(this.toTarget(second, text), how);
  }

  /**
   * Monaco's own editor actions (and SiftDB's registered with addAction), for the app's command palette (design section
   * 14, F12). Running one gives this editor focus first: they act on it.
   */
  actions(): { id: string; label: string; run(): Promise<void> }[] {
    return this.editor.getSupportedActions().map((a) => ({
      id: a.id,
      label: a.label,
      run: async () => { this.editor.focus(); await a.run(); },
    }));
  }

  // ---- linking a plan to the SQL (design section 14, F6) ---------------------------------------------------------

  /** The text of [start, start + length), or null when that is past the end. */
  textAt(start: number, length: number): string | null {
    const text = this.model.getValue();
    return start + length <= text.length ? text.slice(start, start + length) : null;
  }

  /** Selects [start, end), scrolls it into view and gives the editor focus. */
  select(start: number, end: number) {
    const a = this.model.getPositionAt(start);
    const b = this.model.getPositionAt(end);
    const range = new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column);
    this.editor.setSelection(range);
    this.editor.revealRangeInCenterIfOutsideViewport(range);
    this.editor.focus();
  }

  /** Follows these ranges through later edits. Nothing shows until `highlight` is asked for. */
  track(ranges: { start: number; length: number }[]): TrackedRanges {
    const toRange = (s: number, e: number) => {
      const a = this.model.getPositionAt(s);
      const b = this.model.getPositionAt(e);
      return new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column);
    };
    const plain = { stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges };
    const marked = { ...plain, className: 'plan-link', description: 'plan step' };
    const collection = this.editor.createDecorationsCollection(ranges.map((r) => ({ range: toRange(r.start, r.start + r.length), options: plain })));
    const rangeOf = (i: number) => {
      const r = collection.getRange(i);
      if (!r || r.isEmpty()) return null;
      return { start: this.model.getOffsetAt(r.getStartPosition()), end: this.model.getOffsetAt(r.getEndPosition()) };
    };
    return {
      rangeOf,
      at: (offset) => ranges.map((_, i) => i).filter((i) => { const r = rangeOf(i); return !!r && offset >= r.start && offset < r.end; }),
      highlight: (indices) => {
        const now = ranges.map((_, i) => collection.getRange(i));
        collection.set(now.map((r, i) => ({ range: r ?? new monaco.Range(1, 1, 1, 1), options: indices.includes(i) ? marked : plain })));
      },
      dispose: () => collection.clear(),
    };
  }

  /** Calls `cb` with the text offset under the mouse, or null when it leaves the text. */
  onMouseOffset(cb: (offset: number | null) => void) {
    this.editor.onMouseMove((e) => {
      const p = e.target.type === monaco.editor.MouseTargetType.CONTENT_TEXT ? e.target.position : null;
      cb(p ? this.model.getOffsetAt(p) : null);
    });
    this.editor.onMouseLeave(() => cb(null));
  }
}
