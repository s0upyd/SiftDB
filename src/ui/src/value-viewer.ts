// Seeing a whole value from the results grid: the cell shows the start of it ("…"), this shows all of it.

import { announce, writeClipboard } from './copy';
import { el, showDialog } from './dialogs';
import { JsonTree } from './json-tree';
import { plural } from './results';

export interface ShownValue {
  column: string;
  /** 1-based, as the grid's row numbers show it. */
  row: number;
  value: string | null;
}

/** JSON text laid out with indentation, or null when the value is not a JSON object or array. */
export function formatJson(value: string): string | null {
  const t = value.trimStart();
  if (!t.startsWith('{') && !t.startsWith('[')) return null;
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return null;
  }
}

/** Above this, a JSON value opens as text only: a tree of it would take too long to build. */
const TREE_LIMIT = 5_000_000;

/** The value parsed, when it is a JSON object or array (what the tree shows); else undefined. */
export function jsonTreeValue(value: string | null): unknown {
  if (value === null || value.length > TREE_LIMIT) return undefined;
  const t = value.trimStart();
  if (!t.startsWith('{') && !t.startsWith('[')) return undefined;
  try { return JSON.parse(value); } catch { return undefined; }
}

/**
 * A read-only view of one value, with line wrapping, JSON formatting when it is JSON, and Copy. A JSON object or array
 * opens on a tree (design section 14, F20), with Text one click away. Escape closes it.
 * <paramref name="pathRoot"/>: the column the value came from, for the paths the tree copies (null when its keys are
 * the attributes themselves: DynamoDB's "…other" column).
 */
export async function showValue(v: ShownValue, pathRoot: string | null = v.column) {
  const title = `${v.column} · row ${v.row.toLocaleString()}`;
  const close = el('button', { type: 'submit', className: 'primary', textContent: 'Close' });
  const body: (Node | string)[] = [];
  const actions: (Node | string)[] = [];
  let focusFirst: (() => void) | null = null;

  if (v.value === null) {
    body.push(el('p', { className: 'nullvalue', textContent: 'NULL' }),
      el('p', { className: 'dim', textContent: 'No value: not an empty string.' }));
    actions.push(el('span', { className: 'grow' }));
  } else {
    const raw = v.value;
    const json = formatJson(raw);
    const text = el('textarea', { className: 'valuetext', readOnly: true, spellcheck: false, value: raw });
    text.setAttribute('aria-label', `Value of ${v.column}, row ${v.row}`);
    const lines = raw.length === 0 ? 0 : raw.split('\n').length;
    const facts = el('p', { className: 'dim', textContent: raw.length === 0
      ? 'An empty string.'
      : `${plural(raw.length, 'character')} · ${plural(lines, 'line')}` });

    const wrap = el('input', { type: 'checkbox', checked: true });
    const applyWrap = () => { text.wrap = wrap.checked ? 'soft' : 'off'; };
    wrap.onchange = applyWrap;
    applyWrap();
    const wrapLabel = el('label', { className: 'check' }, wrap, 'Wrap lines');
    actions.push(wrapLabel);
    let fmtLabel: HTMLElement | null = null;
    if (json !== null) {
      const fmt = el('input', { type: 'checkbox', checked: json !== raw });
      const applyFmt = () => { text.value = fmt.checked ? json : raw; };
      fmt.onchange = applyFmt;
      applyFmt();
      fmtLabel = el('label', { className: 'check' }, fmt, 'Format JSON');
      actions.push(fmtLabel);
    }

    // The JSON viewer: a Tree | Text switch over the same value, on the tree for an object or array.
    const parsed = jsonTreeValue(raw);
    if (parsed !== undefined) {
      const tree = new JsonTree(parsed, pathRoot);
      const search = el('input', { type: 'search', placeholder: 'Find in the value', spellcheck: false, className: 'jt-search' });
      search.setAttribute('aria-label', 'Find in the value');
      const found = el('span', { className: 'dim jt-found' });
      let timer: ReturnType<typeof setTimeout> | undefined;
      search.oninput = () => { clearTimeout(timer); timer = setTimeout(() => tree.search(search.value), 200); };
      search.onkeydown = (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault(); // not the dialog's submit
        clearTimeout(timer);
        if (tree.searchedFor !== search.value) tree.search(search.value); else tree.nextHit(e.shiftKey);
      };
      // The dialog is above any toast: a copy from the tree says so where the match count goes, for a moment.
      tree.onCopied = (said) => {
        const was = found.textContent;
        found.textContent = said;
        setTimeout(() => { if (found.textContent === said) found.textContent = was; }, 1500);
      };
      tree.onSearched = (n) => { found.textContent = search.value.trim() ? (n ? plural(n, 'match') + (n >= 5000 ? '+' : '') : 'No match') : ''; };
      const expand = el('button', { type: 'button', textContent: 'Expand all', onclick: () => tree.expandAll() });
      const collapse = el('button', { type: 'button', textContent: 'Collapse all', onclick: () => tree.collapseAll() });
      const treeBar = el('div', { className: 'jt-bar' }, search, found, el('span', { className: 'grow' }), expand, collapse);
      const treeView = el('div', { className: 'jt-view' }, treeBar, tree.el);

      const asTree = el('button', { type: 'button', textContent: 'Tree' });
      const asText = el('button', { type: 'button', textContent: 'Text' });
      const show = (inTree: boolean) => {
        treeView.hidden = !inTree;
        text.hidden = inTree;
        wrapLabel.hidden = inTree;
        if (fmtLabel) fmtLabel.hidden = inTree;
        asTree.setAttribute('aria-pressed', String(inTree));
        asText.setAttribute('aria-pressed', String(!inTree));
      };
      asTree.onclick = () => { show(true); tree.focus(); };
      asText.onclick = () => { show(false); text.focus(); };
      const views = el('div', { className: 'modes jt-views' }, asTree, asText);
      views.setAttribute('role', 'group');
      views.setAttribute('aria-label', 'Show the value as');
      body.push(el('div', { className: 'jt-head' }, facts, views), treeView, text);
      show(true);
      focusFirst = () => tree.focus();
    } else {
      body.push(facts, text);
    }
    // The dialog is in the top layer, above any toast, so the button itself says what happened.
    const copy: HTMLButtonElement = el('button', {
      type: 'button', textContent: 'Copy', title: 'Copy the value as shown',
      onclick: async () => {
        const ok = await writeClipboard(text.value);
        copy.textContent = ok ? 'Copied' : 'Could not copy';
        announce(ok ? 'Copied the value' : 'Could not copy: the clipboard is not available');
        setTimeout(() => { copy.textContent = 'Copy'; }, 1500);
      },
    });
    actions.push(el('span', { className: 'grow' }), copy);
  }
  actions.push(close);

  const form = el('form', { method: 'dialog' }, el('h2', { textContent: title }), ...body, el('div', { className: 'factions' }, ...actions));
  const dialog = el('dialog', { className: 'modal wide' }, form);
  dialog.setAttribute('aria-label', title);
  const done = showDialog(dialog);
  if (focusFirst) focusFirst(); else (dialog.querySelector('textarea') ?? close).focus();
  await done;
}
