// Seeing a whole value from the results grid: the cell shows the start of it ("…"), this shows all of it.

import { announce, writeClipboard } from './copy';
import { el, showDialog } from './dialogs';
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

/** A read-only view of one value, with line wrapping, JSON formatting when it is JSON, and Copy. Escape closes it. */
export async function showValue(v: ShownValue) {
  const title = `${v.column} · row ${v.row.toLocaleString()}`;
  const close = el('button', { type: 'submit', className: 'primary', textContent: 'Close' });
  const body: (Node | string)[] = [];
  const actions: (Node | string)[] = [];

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
    body.push(el('p', { className: 'dim', textContent: raw.length === 0
      ? 'An empty string.'
      : `${plural(raw.length, 'character')} · ${plural(lines, 'line')}` }), text);

    const wrap = el('input', { type: 'checkbox', checked: true });
    const applyWrap = () => { text.wrap = wrap.checked ? 'soft' : 'off'; };
    wrap.onchange = applyWrap;
    applyWrap();
    actions.push(el('label', { className: 'check' }, wrap, 'Wrap lines'));
    if (json !== null) {
      const fmt = el('input', { type: 'checkbox', checked: json !== raw });
      const applyFmt = () => { text.value = fmt.checked ? json : raw; };
      fmt.onchange = applyFmt;
      applyFmt();
      actions.push(el('label', { className: 'check' }, fmt, 'Format JSON'));
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
  (dialog.querySelector('textarea') ?? close).focus();
  await done;
}
