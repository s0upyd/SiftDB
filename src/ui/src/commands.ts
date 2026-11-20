// The command registry (design section 14, F12): every SiftDB action, defined once. The palette lists them, the window's
// shortcuts run them, the editor's right-click menu and Monaco's keybindings take their labels and keys from here, and a
// release note's "Try it" runs one by id. A new feature adds its command here and it appears everywhere, with the right
// shortcut text.

export type CommandGroup = 'Sheets' | 'Run' | 'Transactions' | 'Connections' | 'Editor' | 'Results' | 'Schema' | 'App';

/** The order groups are listed in the palette. */
export const GROUPS: CommandGroup[] = ['Sheets', 'Run', 'Transactions', 'Connections', 'Editor', 'Results', 'Schema', 'App'];

export interface Command {
  /** Stable: release notes name it ("Try-it: format", CLAUDE.md) and the recently used list stores it. */
  id: string;
  label: string;
  group: CommandGroup;
  /** A short note shown beside it; the palette finds commands by it too. */
  description?: string;
  /** Shortcuts the window handles, wherever the focus is ("Ctrl+Shift+F5"). The first is the one shown. */
  keys?: string[];
  /**
   * Shortcuts that work only where something else has the focus and binds them itself: the editor (Monaco binds the ones
   * of SiftDB's editor actions from here) or the grid. Shown when there is no window shortcut; the window never handles them.
   */
  localKeys?: string[];
  /**
   * Can it run now? true: yes. false: hidden from the palette, and its shortcut is left to whatever else wants the key.
   * A string: listed with that reason (Monaco's list cannot grey an item out), and running it says the reason instead.
   * Left out: always.
   */
  when?(): boolean | string;
  /** Not listed in the palette (the palette itself). */
  unlisted?: boolean;
  run(): void | Promise<void>;
}

export class CommandRegistry {
  private readonly byId = new Map<string, Command>();

  add(...commands: Command[]) {
    for (const c of commands) {
      if (this.byId.has(c.id)) throw new Error(`Command '${c.id}' is defined twice`);
      this.byId.set(c.id, c);
    }
  }

  get(id: string): Command | undefined { return this.byId.get(id); }
  has(id: string): boolean { return this.byId.has(id); }
  all(): Command[] { return [...this.byId.values()]; }

  /** True to run it, false to leave it out, or the reason it cannot run now. */
  static state(c: Command): boolean | string { return c.when ? c.when() : true; }

  /** The command whose window shortcut this key press is, whether or not it can run now. */
  forKey(e: KeyboardEvent): Command | undefined {
    return this.all().find((c) => c.keys?.some((k) => keyMatches(k, e)));
  }
}

/** The app's one registry. main.ts fills it at startup, before any sheet (and so any editor) is made. */
export const commands = new CommandRegistry();

/** The shortcut shown for a command: its window shortcut, else its local one. */
export const shortcutOf = (c: Pick<Command, 'keys' | 'localKeys'>): string | undefined => c.keys?.[0] ?? c.localKeys?.[0];

/** A shortcut taken apart: "Ctrl+Shift+F5" → the modifiers and the key ("F5", "E", ".", "Enter"). */
export function parseKey(spec: string): { ctrl: boolean; shift: boolean; alt: boolean; key: string } {
  const parts = spec.split('+');
  // "Ctrl++" would be a plus key; none of SiftDB's shortcuts uses it.
  const key = parts.pop()!;
  const mods = new Set(parts.map((p) => p.toLowerCase()));
  return { ctrl: mods.has('ctrl'), shift: mods.has('shift'), alt: mods.has('alt'), key };
}

/** Whether a key press is this shortcut: the same modifiers exactly, and the same key (letters in either case). */
export function keyMatches(spec: string, e: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'>): boolean {
  const k = parseKey(spec);
  if (e.metaKey || e.ctrlKey !== k.ctrl || e.shiftKey !== k.shift || e.altKey !== k.alt) return false;
  return e.key.toLowerCase() === k.key.toLowerCase();
}
