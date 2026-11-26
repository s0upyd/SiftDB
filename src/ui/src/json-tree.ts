// The JSON viewer's tree (design section 14, F20): a value's objects and lists as rows that open and close, instead of a
// block of text. Rows are drawn for what is open only, so a large value opens at once; a long list shows 500 items at a
// time. Keyboard as the schema tree: arrows move, Right and Left open and close, * opens everything below.

import { announce, writeClipboard } from './copy';
import { showMenu } from './menu';

type Seg = string | number;
type Kind = 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null';

/** Items a list shows before "show more", and how many more each press adds. */
const PAGE = 500;
/** Rows drawn at most at once (Expand all on a huge value stops there, and says so). */
const MAX_ROWS = 20_000;
/** Search hits highlighted at most. */
const MAX_HITS = 5_000;

const kindOf = (v: unknown): Kind =>
  v === null ? 'null' : Array.isArray(v) ? 'array' : typeof v === 'object' ? 'object'
  : typeof v === 'string' ? 'string' : typeof v === 'number' ? 'number' : typeof v === 'boolean' ? 'boolean' : 'null';

const keyOf = (path: Seg[]) => JSON.stringify(path);

const PLAIN_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * A path as it goes into a statement: <c>lines[0].sku</c>, <c>address.city</c>, with a name that isn't a plain word in
 * double quotes (<c>meta."created at"</c>). That is how DynamoDB's PartiQL writes a path into an item, the column being
 * the attribute; <paramref name="root"/> is that column, or null when the value is a set of attributes itself (the
 * "…other" column), so its keys are attributes.
 */
export function pathText(root: string | null, path: Seg[]): string {
  const name = (s: string) => (PLAIN_NAME.test(s) ? s : `"${s.replace(/"/g, '""')}"`);
  let out = root === null ? '' : name(root);
  for (const seg of path) {
    if (typeof seg === 'number') out += `[${seg}]`;
    else out += out === '' ? name(seg) : `.${name(seg)}`;
  }
  return out;
}

/** What a value's children are: [key, value] pairs for an object, [index, item] for a list, none for the rest. */
function childrenOf(v: unknown): [Seg, unknown][] {
  if (Array.isArray(v)) return v.map((item, i) => [i, item]);
  if (v !== null && typeof v === 'object') return Object.entries(v as Record<string, unknown>);
  return [];
}

/** "{3 keys}", "[12 items]": what a closed object or list holds. */
function summary(v: unknown): string {
  if (Array.isArray(v)) return `[${v.length.toLocaleString()} item${v.length === 1 ? '' : 's'}]`;
  const n = Object.keys(v as object).length;
  return `{${n.toLocaleString()} key${n === 1 ? '' : 's'}}`;
}

/** A primitive as it reads: strings in quotes (so a string "null" never passes for null), the rest as JSON writes them. */
const literal = (v: unknown) => (typeof v === 'string' ? JSON.stringify(v) : String(v));

interface Row { path: Seg[]; key: string; value: unknown; depth: number; more?: { shown: number; total: number } }

export class JsonTree {
  readonly el = document.createElement('div');
  private readonly expanded = new Set<string>();
  /** Items shown so far of each long list, by its key. */
  private readonly shown = new Map<string, number>();
  private focused = keyOf([]);
  private needle = '';
  private hits: string[] = [];
  private hitAt = -1;
  private hitSet = new Set<string>();
  private truncated = false;
  /** What the last search was for, as typed (Enter then moves to the next match instead of searching again). */
  searchedFor = '';
  /** Called after a search, with the number of matches. */
  onSearched: (count: number) => void = () => {};

  /** <paramref name="root"/>: the column the value came from, for paths (null: its keys are attributes themselves). */
  constructor(private readonly value: unknown, private readonly root: string | null) {
    this.el.className = 'jsontree';
    this.el.setAttribute('role', 'tree');
    this.el.setAttribute('aria-label', root ? `The value of ${root}` : 'The value');
    // The first two levels open at the start: the value, and its children.
    this.expanded.add(keyOf([]));
    for (const [seg, child] of childrenOf(value)) if (childrenOf(child).length) this.expanded.add(keyOf([seg]));
    this.el.addEventListener('keydown', (e) => this.onKey(e));
    this.el.addEventListener('click', (e) => this.onClick(e));
    this.el.addEventListener('contextmenu', (e) => this.onMenu(e));
    this.render();
  }

  focus() { (this.el.querySelector<HTMLElement>('[tabindex="0"]') ?? this.el).focus(); }

  expandAll() {
    const walk = (v: unknown, path: Seg[]) => {
      if (this.expanded.size > MAX_ROWS) return;
      if (!childrenOf(v).length) return;
      this.expanded.add(keyOf(path));
      for (const [seg, child] of childrenOf(v)) walk(child, [...path, seg]);
    };
    walk(this.value, []);
    this.render();
  }

  collapseAll() {
    this.expanded.clear();
    this.expanded.add(keyOf([]));
    this.focused = keyOf([]);
    this.render();
  }

  /** Highlights keys and values holding <paramref name="text"/> (case ignored), opening the way to each. */
  search(text: string) {
    this.searchedFor = text;
    this.needle = text.trim().toLowerCase();
    this.hits = [];
    if (this.needle) {
      const walk = (v: unknown, path: Seg[], key: string) => {
        if (this.hits.length >= MAX_HITS) return;
        const kids = childrenOf(v);
        const own = key.toLowerCase().includes(this.needle) || (!kids.length && literal(v).toLowerCase().includes(this.needle));
        if (own && path.length) {
          this.hits.push(keyOf(path));
          for (let i = 0; i < path.length; i++) this.expanded.add(keyOf(path.slice(0, i)));
        }
        for (const [seg, child] of kids) walk(child, [...path, seg], typeof seg === 'number' ? '' : seg);
      };
      walk(this.value, [], '');
    }
    this.hitSet = new Set(this.hits);
    this.hitAt = this.hits.length ? 0 : -1;
    if (this.hitAt === 0) this.focused = this.hits[0];
    this.render();
    if (this.hitAt === 0) this.scrollToFocused();
    this.onSearched(this.hits.length);
  }

  /** The next (or, with <paramref name="back"/>, previous) match: focused and scrolled to. */
  nextHit(back = false) {
    if (!this.hits.length) return;
    this.hitAt = (this.hitAt + (back ? -1 : 1) + this.hits.length) % this.hits.length;
    this.focused = this.hits[this.hitAt];
    this.render();
    this.scrollToFocused();
  }

  /** The value at the focused row (the whole value when none). */
  private nodeAt(path: Seg[]): unknown {
    let v: unknown = this.value;
    for (const seg of path) v = (v as Record<string | number, unknown>)?.[seg as never];
    return v;
  }

  // ---- drawing ---------------------------------------------------------------------------------------------------

  private visibleRows(): Row[] {
    const rows: Row[] = [];
    this.truncated = false;
    const walk = (v: unknown, path: Seg[], key: string, depth: number) => {
      if (rows.length >= MAX_ROWS) { this.truncated = true; return; }
      rows.push({ path, key, value: v, depth });
      const k = keyOf(path);
      if (!this.expanded.has(k)) return;
      const kids = childrenOf(v);
      const limit = Array.isArray(v) ? Math.min(kids.length, this.shown.get(k) ?? PAGE) : kids.length;
      for (let i = 0; i < limit; i++) {
        const [seg, child] = kids[i];
        walk(child, [...path, seg], typeof seg === 'number' ? `[${seg}]` : seg, depth + 1);
      }
      if (limit < kids.length) rows.push({ path, key: '', value: v, depth: depth + 1, more: { shown: limit, total: kids.length } });
    };
    walk(this.value, [], '', 0);
    return rows;
  }

  private render() {
    const rows = this.visibleRows();
    const keys = new Set(rows.filter((r) => !r.more).map((r) => keyOf(r.path)));
    if (!keys.has(this.focused)) this.focused = keyOf([]);
    const frag = document.createDocumentFragment();
    for (const r of rows) frag.append(r.more ? this.moreRow(r) : this.row(r));
    if (this.truncated) {
      const note = document.createElement('div');
      note.className = 'jt-note';
      note.textContent = `Only the first ${MAX_ROWS.toLocaleString()} rows are drawn. Close some, or use Text.`;
      frag.append(note);
    }
    this.el.replaceChildren(frag);
  }

  private row(r: Row): HTMLElement {
    const k = keyOf(r.path);
    const kind = kindOf(r.value);
    const branch = kind === 'object' || kind === 'array';
    const open = branch && this.expanded.has(k);
    const row = document.createElement('div');
    row.className = 'jt-row';
    row.dataset.key = k;
    row.setAttribute('role', 'treeitem');
    row.setAttribute('aria-level', String(r.depth + 1));
    if (branch) row.setAttribute('aria-expanded', String(open));
    row.tabIndex = k === this.focused ? 0 : -1;
    row.style.paddingLeft = `${4 + r.depth * 16}px`;
    if (this.hitSet.has(k)) row.classList.add('hit');
    if (this.hits[this.hitAt] === k) row.classList.add('current');

    const toggle = document.createElement('span');
    toggle.className = 'jt-toggle';
    toggle.textContent = branch ? (open ? '▾' : '▸') : '';
    toggle.setAttribute('aria-hidden', 'true');
    row.append(toggle);
    if (r.path.length) {
      const key = document.createElement('span');
      key.className = typeof r.path[r.path.length - 1] === 'number' ? 'jt-index' : 'jt-key';
      this.mark(key, r.key);
      row.append(key, Object.assign(document.createElement('span'), { className: 'jt-colon', textContent: ': ' }));
    }
    const value = document.createElement('span');
    if (branch) {
      value.className = 'jt-summary';
      value.textContent = summary(r.value);
    } else {
      value.className = `jt-${kind}`;
      this.mark(value, literal(r.value));
    }
    row.append(value);
    row.title = pathText(this.root, r.path) || (this.root ?? '');
    return row;
  }

  private moreRow(r: Row): HTMLElement {
    const row = document.createElement('div');
    row.className = 'jt-more';
    row.style.paddingLeft = `${4 + r.depth * 16 + 16}px`;
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'linkbtn';
    const next = Math.min(PAGE, r.more!.total - r.more!.shown);
    b.textContent = `Show ${next.toLocaleString()} more (${r.more!.shown.toLocaleString()} of ${r.more!.total.toLocaleString()} shown)`;
    b.onclick = () => { this.shown.set(keyOf(r.path), r.more!.shown + PAGE); this.render(); };
    row.append(b);
    return row;
  }

  /** The text, with what the search matched in a mark. */
  private mark(into: HTMLElement, text: string) {
    const at = this.needle ? text.toLowerCase().indexOf(this.needle) : -1;
    if (at < 0) { into.textContent = text; return; }
    const m = document.createElement('mark');
    m.textContent = text.slice(at, at + this.needle.length);
    into.append(text.slice(0, at), m, text.slice(at + this.needle.length));
  }

  private scrollToFocused() {
    const row = this.el.querySelector<HTMLElement>(`[data-key="${CSS.escape(this.focused)}"]`);
    row?.scrollIntoView({ block: 'nearest' });
    row?.focus({ preventScroll: true });
  }

  // ---- interaction ------------------------------------------------------------------------------------------------

  private rowFrom(e: Event) { return (e.target as HTMLElement).closest<HTMLElement>('.jt-row'); }

  private pathOf(row: HTMLElement): Seg[] { return JSON.parse(row.dataset.key!) as Seg[]; }

  private toggle(path: Seg[], open?: boolean) {
    const k = keyOf(path);
    if (!childrenOf(this.nodeAt(path)).length) return;
    const now = open ?? !this.expanded.has(k);
    if (now) this.expanded.add(k); else this.expanded.delete(k);
    this.focused = k;
    this.render();
    this.scrollToFocused();
  }

  private onClick(e: MouseEvent) {
    const row = this.rowFrom(e);
    if (!row) return;
    this.focused = row.dataset.key!;
    if ((e.target as HTMLElement).classList.contains('jt-toggle') || e.detail === 2) this.toggle(this.pathOf(row));
    else { this.render(); this.scrollToFocused(); }
  }

  private onKey(e: KeyboardEvent) {
    const rows = [...this.el.querySelectorAll<HTMLElement>('.jt-row')];
    const at = rows.findIndex((r) => r.dataset.key === this.focused);
    const path = at >= 0 ? this.pathOf(rows[at]) : [];
    const go = (i: number) => {
      const r = rows[Math.max(0, Math.min(rows.length - 1, i))];
      if (!r) return;
      this.focused = r.dataset.key!;
      rows.forEach((x) => { x.tabIndex = x === r ? 0 : -1; });
      r.focus();
      r.scrollIntoView({ block: 'nearest' });
    };
    const branch = childrenOf(this.nodeAt(path)).length > 0;
    switch (e.key) {
      case 'ArrowDown': go(at + 1); break;
      case 'ArrowUp': go(at - 1); break;
      case 'Home': go(0); break;
      case 'End': go(rows.length - 1); break;
      case 'ArrowRight':
        if (branch && !this.expanded.has(this.focused)) this.toggle(path, true);
        else if (branch) go(at + 1);
        break;
      case 'ArrowLeft':
        if (branch && this.expanded.has(this.focused)) this.toggle(path, false);
        else if (path.length) { this.focused = keyOf(path.slice(0, -1)); this.render(); this.scrollToFocused(); }
        break;
      case 'Enter': case ' ': this.toggle(path); break;
      case '*': {
        const walk = (v: unknown, p: Seg[]) => {
          if (!childrenOf(v).length || this.expanded.size > MAX_ROWS) return;
          this.expanded.add(keyOf(p));
          for (const [seg, child] of childrenOf(v)) walk(child, [...p, seg]);
        };
        walk(this.nodeAt(path), path);
        this.render();
        this.scrollToFocused();
        break;
      }
      case 'c': case 'C':
        if (!(e.ctrlKey || e.metaKey)) return;
        void this.copy(path, e.shiftKey ? 'path' : 'value');
        break;
      case 'F10':
        if (!e.shiftKey) return;
        this.menuAt(rows[at] ?? this.el, path);
        break;
      case 'ContextMenu': this.menuAt(rows[at] ?? this.el, path); break;
      default: return;
    }
    e.preventDefault();
    e.stopPropagation();
  }

  private onMenu(e: MouseEvent) {
    const row = this.rowFrom(e);
    if (!row) return;
    e.preventDefault();
    this.focused = row.dataset.key!;
    this.render();
    this.openMenu(e.clientX, e.clientY, this.pathOf(row));
  }

  private menuAt(row: HTMLElement, path: Seg[]) {
    const r = row.getBoundingClientRect();
    this.openMenu(r.left + 24, r.bottom, path);
  }

  private openMenu(x: number, y: number, path: Seg[]) {
    const last = path[path.length - 1];
    const branch = childrenOf(this.nodeAt(path)).length > 0;
    showMenu(x, y, [
      { label: 'Copy value', shortcut: 'Ctrl+C', action: () => void this.copy(path, 'value') },
      { label: 'Copy key', disabled: last === undefined, action: () => void this.copy(path, 'key') },
      { label: 'Copy path', shortcut: 'Ctrl+Shift+C', disabled: !path.length && !this.root, action: () => void this.copy(path, 'path') },
      { label: 'Expand all below', separatorBefore: true, disabled: !branch, action: () => { this.focused = keyOf(path); this.onKey(new KeyboardEvent('keydown', { key: '*' })); } },
    ], 'JSON value actions');
  }

  /** A string as its text; anything else as JSON (indented for an object or list); a key; or the path to it. */
  private async copy(path: Seg[], what: 'value' | 'key' | 'path') {
    const v = this.nodeAt(path);
    const last = path[path.length - 1];
    const text = what === 'path' ? pathText(this.root, path)
      : what === 'key' ? (last === undefined ? '' : String(last))
      : typeof v === 'string' ? v : JSON.stringify(v, null, 2);
    const ok = await writeClipboard(text);
    announce(ok ? `Copied the ${what}` : 'Could not copy: the clipboard is not available');
    this.onCopied(ok ? `Copied the ${what}` : 'Could not copy');
  }

  /** Told after a copy, so the dialog can say so (it is above any toast). */
  onCopied: (said: string) => void = () => {};
}
