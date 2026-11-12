import type { Bridge } from './bridge';
import { el, showDialog } from './dialogs';

// Saved connections (design section 8): the list from the engine, the edit form, and the password prompt. The engine owns
// the store; a password goes from these forms to the engine once and never comes back.

export type FieldKind = 'text' | 'number' | 'choice' | 'filePath' | 'keywords';

/** One field of a provider's connection form (mirrors SiftDB.Abstractions ConnectionField). */
export interface FieldInfo {
  key: string;
  label: string;
  kind: FieldKind;
  group: 'server' | 'security' | 'advanced' | string;
  default: string | null;
  required: boolean;
  placeholder: string | null;
  help: string | null;
  choices: { value: string; label: string }[] | null;
  enabledWhen: { key: string; values: string[] } | null;
}

export interface ProviderInfo { id: string; displayName: string; fields: FieldInfo[]; readOnlyEnforcement: 'server' | 'client' }

export interface SavedConnection {
  id: string;
  name: string;
  provider: string;
  fields: Record<string, string>;
  rememberPassword: boolean;
  readOnly: boolean;
  /** Begin a transaction before the first change (ignored on a read-only connection). */
  autoTransaction: boolean;
  /** A password is saved (never the password itself). */
  hasPassword: boolean;
}

/** What the engine has saved, refreshed after every change. */
export class ConnectionCatalog {
  providers: ProviderInfo[] = [];
  saved: SavedConnection[] = [];
  lastUsed: string | null = null;
  /** Why the saved connections could not be read (the file is left alone); pasted strings still work. */
  error: string | null = null;

  constructor(private bridge: Bridge) {}

  async load() {
    const r = await this.bridge.request({ type: 'listConnections' });
    if (r.type === 'error') { this.error = r.message; return; }
    this.error = null;
    this.providers = r.providers;
    this.saved = [...(r.connections as SavedConnection[])].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    this.lastUsed = r.lastUsed ?? null;
  }

  byId(id: string) { return this.saved.find((c) => c.id === id); }
  provider(id: string) { return this.providers.find((p) => p.id === id); }

  /** "host:port / database", for telling connections apart. */
  describe(c: SavedConnection) {
    const host = c.fields.host || 'localhost';
    return `${c.fields.port ? `${host}:${c.fields.port}` : host} / ${c.fields.database || '(default)'}`;
  }
}

// ---- small DOM helpers ------------------------------------------------------------------------------------------

let fieldSeq = 0;

/** A labelled row: the label is tied to its control, and help text is announced with it. */
function row(label: string, control: HTMLElement, help?: string | null) {
  const id = control.id || (control.id = `f${++fieldSeq}`);
  const wrap = el('div', { className: 'frow' }, el('label', { htmlFor: id, textContent: label }), control);
  if (help) {
    const h = el('div', { className: 'fhelp', id: `${id}-help`, textContent: help });
    control.setAttribute('aria-describedby', h.id);
    wrap.append(h);
  }
  return wrap;
}

function check(label: string, checked: boolean, help?: string) {
  const box = el('input', { type: 'checkbox', checked });
  const wrap = el('label', { className: 'fcheck' }, box, el('span', {}, label));
  if (help) wrap.append(el('span', { className: 'fhelp', textContent: help }));
  return { box, wrap };
}

// ---- the password prompt ----------------------------------------------------------------------------------------

/**
 * Asks for a connection's password. `retry` says the last one was turned down. Resolves to null if cancelled. Nothing
 * here keeps the value: it goes straight to the engine.
 */
export async function askPassword(name: string, retry: boolean, message?: string): Promise<string | null> {
  const input = el('input', { type: 'password', autocomplete: 'off', spellcheck: false });
  const note = retry
    ? el('p', { className: 'fstatus', textContent: `The server turned that password down.${message ? ` ${message}` : ''}` })
    : el('p', { className: 'dim', textContent: 'This connection does not remember its password. It is kept in memory until SiftDB closes.' });
  if (retry) { note.dataset.kind = 'error'; note.setAttribute('role', 'alert'); }
  let value: string | null = null;
  const form = el('form', { method: 'dialog' },
    el('h2', { textContent: `Password for ${name}` }),
    note,
    row('Password', input),
    el('div', { className: 'factions' },
      el('span', { className: 'grow' }),
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => dialog.close() }),
      el('button', { type: 'submit', className: 'primary', textContent: 'Connect' })));
  form.onsubmit = () => { value = input.value; };
  const dialog = el('dialog', { className: 'modal small' }, form);
  dialog.setAttribute('aria-label', `Password for ${name}`);
  const done = showDialog(dialog);
  input.focus();
  await done;
  return value;
}

// ---- the edit form ----------------------------------------------------------------------------------------------

export interface EditStart {
  /** Prefilled fields, e.g. from a pasted connection string. */
  fields?: Record<string, string>;
  /** The pasted string this came from: its password is used unless another is typed. */
  fromConnectionString?: string;
  /** The pasted string has a password. */
  hasPassword?: boolean;
}

export type EditResult = { kind: 'saved'; id: string } | { kind: 'deleted'; id: string } | { kind: 'cancelled' };

/**
 * The connection form: new (existing null) or edit. Saving, testing and deleting all go through the engine, which checks
 * names are unique and values make sense; its message is shown in the form.
 */
export async function editConnection(bridge: Bridge, catalog: ConnectionCatalog, existing: SavedConnection | null, start: EditStart = {}): Promise<EditResult> {
  let provider = (existing && catalog.provider(existing.provider)) ?? catalog.providers[0];
  if (!provider) return { kind: 'cancelled' };
  let result: EditResult = { kind: 'cancelled' };

  let values: Record<string, string> = { ...(existing?.fields ?? start.fields ?? {}) };
  const name = el('input', { type: 'text', value: existing?.name ?? '', spellcheck: false, required: true, placeholder: 'e.g. dev, shop (read-only)' });

  // The database type comes first: everything below it is drawn from that provider's description. A saved connection
  // keeps its type (its fields mean nothing to another one), and so does one taken from a pasted string (already parsed).
  const typeLocked = !!existing || !!start.fields;
  const type = el('select', {}, ...catalog.providers.map((p) => el('option', { value: p.id, textContent: p.displayName })));
  type.value = provider.id;
  type.disabled = typeLocked;
  const typeHelp = typeLocked
    ? 'A saved connection keeps its database type. For another type, save a new connection.'
    : catalog.providers.length === 1
      ? `${provider.displayName} only, for now. Support for more database types is planned.`
      : 'The fields below follow the type you pick.';

  // Containers the provider's fields go into, refilled when the type changes.
  const serverFields = el('div', { className: 'fgroup' });
  const securitySet = el('fieldset', {});
  const advanced = el('details', {});
  const legend = el('legend', {});
  const controls = new Map<string, HTMLInputElement | HTMLSelectElement>();
  const rows = new Map<string, HTMLElement>();
  const fresh = !existing && !start.fields;

  // Fields that only matter for some values of another (the CA file only when the certificate is verified).
  const applyConditions = () => {
    for (const f of provider.fields) {
      if (!f.enabledWhen) continue;
      const on = f.enabledWhen.values.includes(controls.get(f.enabledWhen.key)?.value ?? '');
      controls.get(f.key)!.disabled = !on;
      rows.get(f.key)!.classList.toggle('off', !on);
    }
  };

  /** Draws the provider's fields, grouped as it says. */
  const buildFields = () => {
    controls.clear();
    rows.clear();
    for (const f of provider.fields) {
      let control: HTMLInputElement | HTMLSelectElement;
      if (f.kind === 'choice') {
        control = el('select', {}, ...(f.choices ?? []).map((c) => el('option', { value: c.value, textContent: c.label })));
        control.value = values[f.key] ?? f.default ?? f.choices?.[0]?.value ?? '';
      } else {
        // A new connection starts from the defaults as real values: a required field (Host) is then never refused while
        // showing "localhost". An existing one shows what was saved; empty means "the default", shown as a placeholder.
        control = el('input', { type: 'text', spellcheck: false, value: values[f.key] ?? (fresh ? f.default ?? '' : ''), placeholder: f.placeholder ?? f.default ?? '' });
        if (f.kind === 'number') control.inputMode = 'numeric';
        if (f.kind === 'keywords' || f.kind === 'filePath') control.classList.add('mono');
      }
      if (f.required) control.required = true;
      control.dataset.key = f.key;
      control.addEventListener('change', applyConditions);
      controls.set(f.key, control);
      rows.set(f.key, row(f.label, control, f.help));
    }
    const group = (g: string) => provider.fields.filter((f) => f.group === g).map((f) => rows.get(f.key)!);
    legend.textContent = provider.displayName;
    serverFields.replaceChildren(...group('server'));
    securitySet.replaceChildren(el('legend', { textContent: 'Security' }), ...group('security'));
    securitySet.hidden = securitySet.children.length === 1;
    advanced.replaceChildren(el('summary', { textContent: 'Advanced' }), ...group('advanced'));
    advanced.hidden = advanced.children.length === 1;
    applyConditions();
  };
  buildFields();

  // The password: never shown. When one is saved, it stays unless "Change" is pressed.
  const password = el('input', { type: 'password', autocomplete: 'new-password', spellcheck: false });
  let passwordChanged = !existing?.hasPassword && !start.hasPassword;
  const change = el('button', { type: 'button', textContent: 'Change', className: 'inline' });
  const passwordBox = el('div', { className: 'finline' }, password);
  if (!passwordChanged) {
    password.disabled = true;
    password.placeholder = start.hasPassword ? 'from the connection string' : 'saved password';
    passwordBox.append(change);
    change.onclick = () => {
      passwordChanged = true;
      password.disabled = false;
      password.placeholder = '';
      change.remove();
      password.focus();
    };
  } else if (existing && existing.rememberPassword) {
    password.placeholder = 'none saved';
  }
  const remember = check('Remember password', existing?.rememberPassword ?? true,
    'Saved encrypted for your Windows account. Otherwise SiftDB asks when connecting.');
  const readOnlyHelp = () => provider.readOnlyEnforcement === 'server'
    ? 'The server refuses any change on this connection. To change data, save a second, writable connection.'
    : 'SiftDB refuses statements that change data. To change data, save a second, writable connection.';
  const readOnly = check('Read-only', existing?.readOnly ?? false, readOnlyHelp());
  const auto = check('Auto-transaction', existing?.autoTransaction ?? false,
    'Sheets on this connection are always inside a transaction: one starts on connecting and after every Commit or Rollback. Changes wait for Commit, so a mistake (a missing WHERE) can still be rolled back.');
  // On a read-only connection nothing can change, so there is nothing to roll back: auto-transaction is off there, and
  // the box says so (unticked, not just greyed, which read as "on"). Unticking Read-only brings back what was chosen.
  const autoHelp = auto.wrap.querySelector('.fhelp')!;
  const autoHelpText = autoHelp.textContent!;
  let autoWanted = auto.box.checked;
  auto.box.addEventListener('change', () => { autoWanted = auto.box.checked; });
  const syncAuto = () => {
    const ro = readOnly.box.checked;
    auto.box.disabled = ro;
    auto.box.checked = ro ? false : autoWanted;
    auto.wrap.classList.toggle('off', ro);
    autoHelp.textContent = ro
      ? 'Not on a read-only connection: the server refuses every change, so there is never anything to roll back.'
      : autoHelpText;
  };
  readOnly.box.addEventListener('change', syncAuto);
  syncAuto();

  // A new connection can switch type. Values the user typed carry over where the new type has the same field; anything
  // still at the old type's default takes the new type's default instead (a port, say).
  type.addEventListener('change', () => {
    const next = catalog.provider(type.value);
    if (!next || next === provider) return;
    for (const [key, c] of controls) values[key] = c.value.trim();
    const oldDefaults = new Map(provider.fields.map((f) => [f.key, f.default ?? '']));
    const carried: Record<string, string> = {};
    for (const f of next.fields) {
      const v = values[f.key];
      if (v && v !== oldDefaults.get(f.key)) carried[f.key] = v;
    }
    values = carried;
    provider = next;
    buildFields();
    readOnly.wrap.querySelector('.fhelp')!.textContent = readOnlyHelp();
  });

  const status = el('p', { className: 'fstatus' });
  status.setAttribute('role', 'status');
  const setStatus = (text: string, kind: 'info' | 'ok' | 'error' = 'info') => {
    status.textContent = text;
    status.dataset.kind = kind;
    status.setAttribute('role', kind === 'error' ? 'alert' : 'status');
  };

  const readForm = () => {
    for (const [key, c] of controls) values[key] = c.disabled ? '' : c.value.trim();
    return {
      id: existing?.id, name: name.value.trim(), provider: provider.id, fields: { ...values },
      rememberPassword: remember.box.checked, readOnly: readOnly.box.checked, autoTransaction: auto.box.checked,
    };
  };
  /** The password parts of a save or test: a typed one, else the pasted string's, else (absent) the saved one. */
  const passwordPart = () => ({
    ...(passwordChanged ? { password: password.value } : {}),
    ...(start.fromConnectionString && !passwordChanged ? { fromConnectionString: start.fromConnectionString } : {}),
  });
  const missing = () => {
    if (!name.value.trim()) return name;
    return [...controls.values()].find((c) => c.required && !c.disabled && !c.value.trim()) ?? null;
  };

  const test = el('button', { type: 'button', textContent: 'Test' });
  test.onclick = async () => {
    const m = missing();
    if (m && m !== name) { setStatus('Fill in the required fields first.', 'error'); m.focus(); return; }
    test.disabled = true;
    setStatus('Testing…');
    const r = await bridge.request({ type: 'testConnection', connection: readForm(), ...passwordPart() });
    test.disabled = false;
    if (r.type === 'error') setStatus(`Could not connect: ${r.message}`, 'error');
    else setStatus(`Connected in ${r.ms} ms${readOnly.box.checked ? ', read-only' : ''}.`, 'ok');
  };

  const remove = el('button', { type: 'button', textContent: 'Delete…', className: 'danger' });
  remove.hidden = !existing;
  remove.onclick = async () => {
    if (!existing || !window.confirm(`Delete the saved connection "${existing.name}"? Its saved password is removed too.`)) return;
    const r = await bridge.request({ type: 'deleteConnection', savedId: existing.id });
    if (r.type === 'error') { setStatus(`Not deleted: ${r.message}`, 'error'); return; }
    result = { kind: 'deleted', id: existing.id };
    dialog.close();
  };

  const save = el('button', { type: 'submit', textContent: 'Save', className: 'primary' });
  const form = el('form', { method: 'dialog', noValidate: true },
    el('h2', { textContent: existing ? `Edit ${existing.name}` : 'New connection' }),
    existing ? el('p', { className: 'dim', textContent: 'Changes apply the next time a sheet connects.' }) : '',
    row('Database type', type, typeHelp),
    row('Name', name, 'Sheet files refer to the connection by this name, so it must be unique.'),
    el('fieldset', {}, legend, serverFields, row('Password', passwordBox), remember.wrap),
    securitySet,
    el('fieldset', {}, el('legend', { textContent: 'Safety' }), readOnly.wrap, auto.wrap),
    advanced,
    status,
    el('div', { className: 'factions' },
      remove, el('span', { className: 'grow' }), test,
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => dialog.close() }),
      save));
  // A password label is tied to the field itself, not the box around it.
  form.querySelector<HTMLLabelElement>(`label[for="${passwordBox.id}"]`)!.htmlFor = password.id = `f${++fieldSeq}`;

  form.onsubmit = async (e) => {
    e.preventDefault(); // stay open until the engine has said yes
    const m = missing();
    if (m) { setStatus(m === name ? 'Give the connection a name.' : 'Fill in the required fields.', 'error'); m.focus(); return; }
    save.disabled = true;
    const r = await bridge.request({ type: 'saveConnection', connection: readForm(), ...passwordPart() });
    save.disabled = false;
    if (r.type === 'error') { setStatus(r.message, 'error'); return; }
    result = { kind: 'saved', id: r.savedId };
    dialog.close();
  };

  const dialog = el('dialog', { className: 'modal' }, form);
  dialog.setAttribute('aria-label', existing ? `Edit connection ${existing.name}` : 'New connection');
  const done = showDialog(dialog);
  (existing ? controls.get(provider.fields[0]?.key ?? '') ?? name : typeLocked || catalog.providers.length === 1 ? name : type).focus();
  await done;
  return result;
}
