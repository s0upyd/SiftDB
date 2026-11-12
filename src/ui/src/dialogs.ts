// Small DOM helpers and modal dialogs shared by the connection form, the password prompt and the save prompt.

// NoInfer: the tag alone decides the element type. Inferring it from the props as well is too much work for the compiler.
export function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[NoInfer<K>]> = {}, ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
  const e: HTMLElementTagNameMap[K] = Object.assign(document.createElement(tag), props);
  e.append(...children);
  return e;
}

/** Shows a modal dialog and resolves when it closes. Escape and the Cancel button both close it with no value. */
export function showDialog(dialog: HTMLDialogElement): Promise<void> {
  document.body.append(dialog);
  dialog.showModal();
  return new Promise((resolve) => dialog.addEventListener('close', () => { dialog.remove(); resolve(); }, { once: true }));
}

/**
 * Closing a sheet with unsaved edits (design section 6): its text would be gone, so ask. Save is the default button;
 * Escape cancels. The window closing never asks: autosave brings everything back.
 */
export async function askSaveChanges(title: string): Promise<'save' | 'discard' | 'cancel'> {
  let answer: 'save' | 'discard' | 'cancel' = 'cancel';
  const save = el('button', { type: 'submit', className: 'primary', textContent: 'Save' });
  const form = el('form', { method: 'dialog' },
    el('h2', { textContent: `Save changes to ${title}?` }),
    el('p', { className: 'dim', textContent: 'Closing the sheet throws away edits that are not saved.' }),
    el('div', { className: 'factions' },
      el('button', { type: 'button', className: 'danger', textContent: "Don't save", onclick: () => { answer = 'discard'; dialog.close(); } }),
      el('span', { className: 'grow' }),
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => dialog.close() }),
      save));
  form.onsubmit = () => { answer = 'save'; };
  const dialog = el('dialog', { className: 'modal small' }, form);
  dialog.setAttribute('aria-label', `Save changes to ${title}?`);
  const done = showDialog(dialog);
  save.focus();
  await done;
  return answer;
}
