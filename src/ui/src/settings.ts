import type { Bridge } from './bridge';
import { el, showDialog } from './dialogs';
import type { QueryMode } from './editor';
import { buildRulesPage } from './rules-editor';
import { StyleRule, setRules } from './styles';

// User settings (design section 10). The engine owns the store; this keeps the current values for the UI and draws the
// settings editor. Every later feature adds its settings here, as a category of the editor (see CATEGORIES).

export interface UserSettings {
  /** The mode new sheets open in (design section 3). */
  newSheetMode: QueryMode;
  /** Result styling (design section 10): one ordered list, presets included. The first rule that matches styles the cell. */
  resultRules: StyleRule[];
}

/** Until the engine answers. Its defaults hold the presets; the page never invents them. */
const DEFAULTS: UserSettings = { newSheetMode: 'assisted', resultRules: [] };

/** The settings as the engine last reported them. Until loaded, or when they cannot be read, the defaults apply. */
export class Settings {
  values: UserSettings = { ...DEFAULTS };
  /** Why the settings file could not be read (it is left alone, and the defaults apply). */
  error: string | null = null;

  constructor(private bridge: Bridge) {}

  async load() {
    const r = await this.bridge.request({ type: 'getSettings' });
    if (r.type === 'error') { this.error = r.message; return; }
    this.take(r.settings);
    this.error = r.error ?? null;
  }

  /** Saves the whole set. Null when it was saved, else the engine's reason. */
  async save(values: UserSettings): Promise<string | null> {
    const r = await this.bridge.request({ type: 'saveSettings', settings: values });
    if (r.type === 'error') return r.message;
    this.take(r.settings);
    return null;
  }

  private take(s: Partial<UserSettings> | undefined) {
    this.values = {
      newSheetMode: s?.newSheetMode === 'strict' ? 'strict' : 'assisted',
      resultRules: Array.isArray(s?.resultRules) ? s.resultRules : [],
    };
    setRules(this.values.resultRules); // every grid re-matches its columns on its next draw
  }
}

// ---- the editor's categories ------------------------------------------------------------------------------------

/**
 * One entry in the editor's list on the left. `build` draws its page from the current values and returns `apply`, which
 * writes what the page holds into the set being saved. A page is built once per opening, so switching category keeps
 * unsaved edits.
 */
interface Category {
  id: string;
  label: string;
  build(values: UserSettings): { page: HTMLElement; apply(into: UserSettings): void; focus(): void };
}

let seq = 0;

/** One choice of a radio group, with its explanation underneath. */
function radio(name: string, value: string, label: string, help: string, checked: boolean) {
  const input = el('input', { type: 'radio', name, value, checked });
  const wrap = el('label', { className: 'fcheck' }, input, el('span', {}, label), el('span', { className: 'fhelp', textContent: help }));
  return { input, wrap };
}

const sqlCategory: Category = {
  id: 'sql',
  label: 'SQL',
  build(values) {
    const group = `mode${++seq}`;
    const assisted = radio(group, 'assisted', 'Assisted',
      'Write loosely. The quotes the database needs are added for you and shown greyed; what runs is exactly what you see.',
      values.newSheetMode === 'assisted');
    const strict = radio(group, 'strict', 'Strict',
      'The text you write is the text that runs. Quoting problems are squiggles with quick fixes, and block the run.',
      values.newSheetMode === 'strict');
    const page = el('div', { className: 'fgroup' },
      el('fieldset', {},
        el('legend', { textContent: 'New sheets open in' }),
        assisted.wrap, strict.wrap,
        el('p', { className: 'fhelp', textContent: 'Each sheet can still be switched on its toolbar. Open sheets, and files that say which mode they use, keep theirs.' })));
    return {
      page,
      apply: (into) => { into.newSheetMode = strict.input.checked ? 'strict' : 'assisted'; },
      focus: () => (strict.input.checked ? strict.input : assisted.input).focus(),
    };
  },
};

/** Result styling (design section 10): the rules list. */
const resultsCategory: Category = {
  id: 'results',
  label: 'Results',
  build(values) {
    const rules = buildRulesPage(values.resultRules);
    return { page: rules.page, apply: (into) => { into.resultRules = rules.rules(); }, focus: rules.focus };
  },
};

/** In the order they are listed. */
const CATEGORIES: Category[] = [sqlCategory, resultsCategory];

/** The category last looked at, for this run of the app: reopening the editor goes back to it. */
let lastCategory = CATEGORIES[0].id;

// ---- the editor -------------------------------------------------------------------------------------------------

/** The settings editor: categories on the left, the chosen one's page on the right. Resolves true when something was saved. */
export async function editSettings(settings: Settings): Promise<boolean> {
  let saved = false;
  const built = CATEGORIES.map((c) => ({ category: c, ...c.build(settings.values) }));

  const nav = el('div', { className: 'settingsnav' });
  nav.setAttribute('role', 'tablist');
  nav.setAttribute('aria-orientation', 'vertical');
  nav.setAttribute('aria-label', 'Settings categories');
  const pages = el('div', { className: 'settingspages' });

  const tabs = built.map((b) => {
    const tabId = `st${++seq}`, pageId = `sp${seq}`;
    const tab = el('button', { type: 'button', id: tabId, textContent: b.category.label });
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-controls', pageId);
    const panel = el('div', { className: 'settingspage', id: pageId }, el('h3', { textContent: b.category.label }), b.page);
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', tabId);
    pages.append(panel);
    return tab;
  });

  const show = (index: number, focusTab = false) => {
    lastCategory = built[index].category.id;
    tabs.forEach((t, i) => {
      const on = i === index;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1; // one tab stop for the list; arrows move within it
      (pages.children[i] as HTMLElement).hidden = !on;
    });
    if (focusTab) tabs[index].focus();
  };
  tabs.forEach((t, i) => {
    t.onclick = () => show(i);
    t.onkeydown = (e) => {
      const n = tabs.length;
      const to = e.key === 'ArrowDown' ? (i + 1) % n : e.key === 'ArrowUp' ? (i - 1 + n) % n
        : e.key === 'Home' ? 0 : e.key === 'End' ? n - 1 : -1;
      if (to < 0) return;
      e.preventDefault();
      show(to, true);
    };
  });
  nav.append(...tabs);

  const status = el('p', { className: 'fstatus' });
  const setError = (text: string) => {
    status.textContent = text;
    status.dataset.kind = 'error';
    status.setAttribute('role', 'alert');
  };
  if (settings.error) setError(settings.error);

  const save = el('button', { type: 'submit', className: 'primary', textContent: 'Save' });
  save.disabled = !!settings.error; // the file is left alone until it can be read
  const form = el('form', { method: 'dialog' },
    el('h2', { textContent: 'Settings' }),
    el('div', { className: 'settingsbody' }, nav, pages),
    status,
    el('div', { className: 'factions' },
      el('span', { className: 'grow' }),
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => dialog.close() }),
      save));

  form.onsubmit = async (e) => {
    e.preventDefault(); // stay open until the engine has said yes
    const values = { ...settings.values };
    for (const b of built) b.apply(values); // every category, not only the one on screen
    save.disabled = true;
    const error = await settings.save(values);
    save.disabled = false;
    if (error) { setError(`Not saved: ${error}`); return; }
    saved = true;
    dialog.close();
  };

  const dialog = el('dialog', { className: 'modal wide' }, form);
  dialog.setAttribute('aria-label', 'Settings');
  const start = Math.max(0, built.findIndex((b) => b.category.id === lastCategory));
  show(start);
  const done = showDialog(dialog);
  built[start].focus();
  await done;
  return saved;
}
