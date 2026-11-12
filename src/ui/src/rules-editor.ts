import { el, showDialog } from './dialogs';
import { CONDITIONS, Condition, GENERIC_TYPES, GenericType, StyleRule, contrast, parseColour, toTime } from './styles';

// The Results page of the settings editor (design section 10): the styling rules as one ordered list. The first rule
// that matches styles the cell. Presets are rows like any other, but can only be switched off and moved.

/** Below this the two colours are hard to read together (WCAG AA for normal text). */
const MIN_CONTRAST = 4.5;

let seq = 0;

/** What a rule says, in a line: "status · equals shipped", "Boolean columns · is one of true, t, yes, …". */
export function describeRule(r: StyleRule): string {
  const where = [
    r.columns.trim() ? `columns ${r.columns.trim()}` : '',
    r.types.length ? `${r.types.map((t) => GENERIC_TYPES.find((g) => g.id === t)?.label ?? t).join(', ')} columns` : '',
  ].filter(Boolean).join(', ') || 'every column';
  const cond = CONDITIONS.find((c) => c.id === r.condition)?.label ?? r.condition;
  const list = (v: string[]) => (v.length > 4 ? [...v.slice(0, 4), '…'] : v).join(', ');
  const what = r.condition === 'range'
    ? r.values[0] && r.values[1] ? `${cond} ${r.values[0]} and ${r.values[1]}`
      : r.values[0] ? `is at least ${r.values[0]}` : `is at most ${r.values[1]}`
    : r.condition === 'oneOf' ? `${cond} ${list(r.values)}`
    : r.condition === 'equals' || r.condition === 'pattern' ? `${cond} ${r.values[0] ?? ''}`
    : cond;
  return `${where} · ${what}`;
}

/** A small cell drawn in the rule's own colours, as the grid would show it. */
function example(r: Pick<StyleRule, 'text' | 'background' | 'bold' | 'condition' | 'values'>): HTMLElement {
  const sample = r.condition === 'null' ? 'NULL' : r.condition === 'negative' ? '-42' : r.condition === 'range' ? (r.values[0] || r.values[1] || '10')
    : r.condition === 'past' ? '2020-01-01' : r.condition === 'future' ? '2099-01-01'
    : r.values[0] || 'value';
  const cell = el('span', { className: 'rulecell', textContent: sample });
  cell.style.color = r.text;
  cell.style.background = r.background;
  cell.style.fontWeight = r.bold ? '700' : '400';
  if (r.condition === 'null') cell.style.fontStyle = 'italic';
  cell.setAttribute('aria-hidden', 'true');
  return cell;
}

/** Builds the page. `rules()` gives the list as it stands, for Save. */
export function buildRulesPage(initial: StyleRule[]) {
  let rules = initial.map((r) => ({ ...r, types: [...r.types], values: [...r.values] }));
  const list = el('ol', { className: 'rulelist' });
  list.setAttribute('aria-label', 'Styling rules, first match wins');
  const add = el('button', { type: 'button', textContent: 'Add rule…' });

  /** The index of the rule being dragged, or -1. */
  let dragFrom = -1;
  const clearDropMarks = () => list.querySelectorAll('.dropbefore, .dropafter').forEach((r) => r.classList.remove('dropbefore', 'dropafter'));

  const render = (focus?: { index: number; part: string }) => {
    list.replaceChildren(...rules.map((r, i) => {
      const on = el('input', { type: 'checkbox', checked: r.enabled, title: r.enabled ? 'On: switch this rule off' : 'Off: switch this rule on' });
      on.setAttribute('aria-label', `${r.name || 'Rule'} is on`);
      on.onchange = () => { r.enabled = on.checked; render({ index: i, part: 'on' }); };
      const name = el('span', { className: 'rulename', textContent: r.name || '(no name)' });
      const tags = r.preset ? el('span', { className: 'badge', textContent: 'built in' }) : '';
      const text = el('div', { className: 'ruletext' },
        el('div', {}, name, ' ', tags), el('div', { className: 'fhelp', textContent: describeRule(r) }));
      const btn = (label: string, part: string, title: string, fn: () => void, disabled = false) => {
        const b = el('button', { type: 'button', className: 'rulebtn', textContent: label, title, disabled, onclick: fn });
        b.dataset.part = part;
        b.setAttribute('aria-label', `${title}: ${r.name || 'rule'}`);
        return b;
      };
      const move = (to: number) => () => {
        const [moved] = rules.splice(i, 1);
        rules.splice(to, 0, moved);
        render({ index: to, part: to < i ? 'up' : 'down' });
      };
      const actions = el('div', { className: 'ruleactions' },
        btn('↑', 'up', 'Move up', move(i - 1), i === 0),
        btn('↓', 'down', 'Move down', move(i + 1), i === rules.length - 1));
      if (!r.preset) {
        actions.append(
          btn('Edit…', 'edit', 'Edit', async () => {
            const edited = await editRule(r);
            if (edited) { rules[i] = edited; render({ index: i, part: 'edit' }); }
          }),
          btn('✕', 'delete', 'Delete', () => {
            if (!window.confirm(`Delete the rule "${r.name || describeRule(r)}"?`)) return;
            rules.splice(i, 1);
            render();
            add.focus();
          }));
      }
      // Drag by the grip to reorder (the arrows do the same from the keyboard). Only the grip starts a drag, so clicking the
      // checkbox or a button never does.
      const grip = el('span', { className: 'rulegrip', textContent: '⠿', title: 'Drag to reorder' });
      grip.setAttribute('aria-hidden', 'true');
      const row = el('li', { className: `rulerow${r.enabled ? '' : ' off'}` }, grip, on, example(r), text, actions);
      grip.onpointerdown = () => { row.draggable = true; };
      grip.onpointerup = () => { if (dragFrom < 0) row.draggable = false; }; // a click, not a drag
      row.ondragstart = (e) => { dragFrom = i; row.classList.add('dragging'); e.dataTransfer?.setData('text/plain', String(i)); };
      row.ondragend = () => { row.draggable = false; row.classList.remove('dragging'); clearDropMarks(); dragFrom = -1; };
      row.ondragover = (e) => {
        if (dragFrom < 0) return;
        e.preventDefault(); // allows the drop
        const box = row.getBoundingClientRect();
        const after = e.clientY > box.top + box.height / 2;
        clearDropMarks();
        row.classList.add(after ? 'dropafter' : 'dropbefore');
      };
      row.ondrop = (e) => {
        e.preventDefault();
        if (dragFrom < 0) return;
        const after = row.classList.contains('dropafter');
        let to = i + (after ? 1 : 0);
        const [moved] = rules.splice(dragFrom, 1);
        if (dragFrom < to) to--; // the list closed up behind the rule that left
        rules.splice(to, 0, moved);
        dragFrom = -1;
        render({ index: to, part: 'up' });
      };
      return row;
    }));
    if (focus) {
      const row = list.children[focus.index] as HTMLElement | undefined;
      const target = focus.part === 'on' ? row?.querySelector('input') : row?.querySelector<HTMLButtonElement>(`[data-part="${focus.part}"]:not(:disabled)`);
      (target ?? row?.querySelector('input'))?.focus();
    }
  };

  add.onclick = async () => {
    const created = await editRule(null);
    if (!created) return;
    // New rules go first: above the presets, so they win, and where they can be seen.
    rules.unshift(created);
    render({ index: 0, part: 'edit' });
  };

  render();
  const page = el('div', { className: 'fgroup' },
    el('p', { className: 'fhelp', textContent: 'Rules are checked from the top; the first that matches a cell styles it. Built-in rules can be switched off or moved below or above yours.' }),
    list,
    el('div', {}, add));
  return { page, rules: () => rules, focus: () => (list.querySelector('input') ?? add).focus() };
}

// ---- one rule -------------------------------------------------------------------------------------------------

const DEFAULT_RULE: StyleRule = {
  id: '', preset: false, name: '', enabled: true, columns: '', types: [], condition: 'equals', values: [''],
  text: '#1f2328', background: '#ddf4ff', bold: false,
};

/** The form for a new rule (null) or one of the user's. Resolves to the rule, or null if cancelled. */
export async function editRule(existing: StyleRule | null): Promise<StyleRule | null> {
  const start = existing ?? DEFAULT_RULE;
  let result: StyleRule | null = null;
  const id = () => `r${++seq}`;
  const row = (label: string, control: HTMLElement, help?: string) => {
    control.id ||= id();
    const wrap = el('div', { className: 'frow' }, el('label', { htmlFor: control.id, textContent: label }), control);
    if (help) {
      const h = el('div', { className: 'fhelp', id: `${control.id}-help`, textContent: help });
      control.setAttribute('aria-describedby', h.id);
      wrap.append(h);
    }
    return wrap;
  };

  const name = el('input', { type: 'text', value: start.name, placeholder: 'e.g. Shipped orders', spellcheck: false });
  const columns = el('input', { type: 'text', value: start.columns, placeholder: 'every column', spellcheck: false, className: 'mono' });
  const typeBoxes = GENERIC_TYPES.map((t) => {
    const box = el('input', { type: 'checkbox', checked: start.types.includes(t.id), value: t.id });
    return { id: t.id, box, wrap: el('label', { className: 'typecheck' }, box, ` ${t.label}`) };
  });
  const types = el('div', { className: 'typechecks' }, ...typeBoxes.map((t) => t.wrap));
  types.setAttribute('role', 'group');
  types.setAttribute('aria-label', 'Column types');

  const condition = el('select', {}, ...CONDITIONS.map((c) => el('option', { value: c.id, textContent: c.label })));
  condition.value = start.condition;
  const value = el('input', { type: 'text', spellcheck: false, className: 'mono' });
  const low = el('input', { type: 'text', placeholder: 'lowest', spellcheck: false, className: 'mono' });
  const high = el('input', { type: 'text', placeholder: 'highest', spellcheck: false, className: 'mono' });
  low.setAttribute('aria-label', 'Lowest');
  high.setAttribute('aria-label', 'Highest');
  if (start.condition === 'range') { low.value = start.values[0] ?? ''; high.value = start.values[1] ?? ''; }
  else if (start.condition === 'oneOf') value.value = start.values.join(', ');
  else value.value = start.values[0] ?? '';
  const valueRow = row('Value', value);
  const valueHelp = el('div', { className: 'fhelp' });
  valueRow.append(valueHelp);
  const rangeRow = el('div', { className: 'frow' }, el('label', { textContent: 'Between' }),
    el('div', { className: 'finline' }, low, el('span', { className: 'dim', textContent: 'and' }), high),
    el('div', { className: 'fhelp', textContent: 'Numbers, or dates written 2026-01-31 (a time can follow: 2026-01-31 14:00). Either end can be left empty. Both ends are included; a date includes its whole day.' }));

  const textColour = el('input', { type: 'color', value: start.text });
  const bgColour = el('input', { type: 'color', value: start.background });
  const bold = el('input', { type: 'checkbox', checked: start.bold });
  const colours = el('div', { className: 'finline colours' },
    el('label', {}, 'Text ', textColour), el('label', {}, 'Background ', bgColour), el('label', {}, bold, ' Bold'));

  const preview = el('div', { className: 'rulepreview' });
  const warning = el('p', { className: 'fstatus' });
  const status = el('p', { className: 'fstatus' });

  const read = (): StyleRule => {
    const cond = condition.value as Condition;
    const values = cond === 'range' ? [low.value.trim(), high.value.trim()]
      : cond === 'oneOf' ? value.value.split(',').map((v) => v.trim()).filter(Boolean)
      : cond === 'equals' || cond === 'pattern' ? [value.value.trim()]
      : [];
    return {
      id: existing?.id ?? '', preset: false, name: name.value.trim(), enabled: existing?.enabled ?? true, columns: columns.value.trim(),
      types: typeBoxes.filter((t) => t.box.checked).map((t) => t.id as GenericType), condition: cond, values,
      text: textColour.value, background: bgColour.value, bold: bold.checked,
    };
  };

  const update = () => {
    const r = read();
    const cond = r.condition;
    valueRow.hidden = !(cond === 'equals' || cond === 'oneOf' || cond === 'pattern');
    rangeRow.hidden = cond !== 'range';
    valueHelp.textContent = cond === 'oneOf' ? 'Separate the values with commas. Case is ignored.'
      : cond === 'pattern' ? '* matches anything, ? one character. Case is ignored. The whole value must match.'
      : 'Case is ignored. In number columns, 1.0 equals 1.';
    preview.replaceChildren(el('span', { className: 'dim', textContent: 'Example ' }), example(r), el('span', { className: 'dim', textContent: ` ${describeRule(r)}` }));
    const ratio = parseColour(r.text) && parseColour(r.background) ? contrast(r.text, r.background) : 21;
    warning.textContent = ratio < MIN_CONTRAST
      ? `These colours are hard to read together (contrast ${ratio.toFixed(1)} : 1; aim for ${MIN_CONTRAST} or more).` : '';
    warning.dataset.kind = ratio < MIN_CONTRAST ? 'error' : '';
  };

  const problem = (r: StyleRule): [string, HTMLElement] | null => {
    if ((r.condition === 'equals' || r.condition === 'pattern') && !r.values[0]) return ['Type a value to compare with.', value];
    if (r.condition === 'oneOf' && r.values.length === 0) return ['Type at least one value.', value];
    if (r.condition === 'range') {
      if (!r.values[0] && !r.values[1]) return ['Give a lowest or highest number or date, or both.', low];
      const bad = [low, high].find((i) => i.value.trim() && Number.isNaN(Number(i.value.trim())) && Number.isNaN(toTime(i.value.trim())));
      if (bad) return ['That is not a number, or a date written 2026-01-31.', bad];
    }
    return null;
  };

  const save = el('button', { type: 'submit', className: 'primary', textContent: existing ? 'Done' : 'Add' });
  const form = el('form', { method: 'dialog', noValidate: true },
    el('h2', { textContent: existing ? 'Edit rule' : 'New rule' }),
    row('Name', name),
    el('fieldset', {}, el('legend', { textContent: 'Applies to' }),
      row('Column names', columns, 'A name, or a pattern such as is_* or *_status. Case is ignored. Empty: every column.'),
      el('div', { className: 'frow' }, el('span', { textContent: 'Column types' }), types,
        el('div', { className: 'fhelp', textContent: 'None ticked: any type.' }))),
    el('fieldset', {}, el('legend', { textContent: 'When the value' }), row('Condition', condition), valueRow, rangeRow),
    el('fieldset', {}, el('legend', { textContent: 'Style' }), colours, preview, warning),
    status,
    el('div', { className: 'factions' },
      el('span', { className: 'grow' }),
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => dialog.close() }),
      save));
  form.addEventListener('input', update);
  form.addEventListener('change', update);
  form.onsubmit = (e) => {
    e.preventDefault();
    const r = read();
    const p = problem(r);
    if (p) {
      status.textContent = p[0];
      status.dataset.kind = 'error';
      status.setAttribute('role', 'alert');
      p[1].focus();
      return;
    }
    result = r;
    dialog.close();
  };

  const dialog = el('dialog', { className: 'modal' }, form);
  dialog.setAttribute('aria-label', existing ? 'Edit rule' : 'New rule');
  update();
  const done = showDialog(dialog);
  name.focus();
  await done;
  return result;
}
