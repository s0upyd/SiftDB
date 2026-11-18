import { el, showDialog } from './dialogs';

// What's New (design section 14, F17): the release notes the app ships with, generated from the commit messages when it
// was released. The engine decides when it shows (after a major or minor update; a notice after a patch); this draws it.
// The text is set as text, never as HTML: entries can hold `code`, which is drawn as code, and nothing else is read.

export interface ReleaseEntry { text: string; tryIt: string | null }
export interface ReleaseSection { title: string; entries: ReleaseEntry[] }
export interface Release { version: string; date: string | null; sections: ReleaseSection[] }

/** Sections longer than this start folded, below the highlights: a long list of fixes should not push them out of view. */
const FOLD_AFTER = 6;

/** An entry's text, with `code` spans drawn as code. */
function entryText(text: string): Node[] {
  return text.split(/(`[^`]+`)/).filter(Boolean).map((part) =>
    part.startsWith('`') && part.endsWith('`') ? el('code', { textContent: part.slice(1, -1) }) : document.createTextNode(part));
}

/**
 * The window: every release given, newest first; in each, breaking changes and highlights first and open, improvements and
 * fixes below (folded when long). An entry with a "Try it" runs that command, if this build has it, and closes the window.
 */
export async function showWhatsNew(releases: Release[], runCommand: (id: string) => boolean, canRun: (id: string) => boolean) {
  const body = el('div', { className: 'whatsnewbody' });
  if (releases.length === 0) {
    body.append(el('p', { className: 'fhelp', textContent: 'This build has no release notes. Notes come with releases built by the release workflow.' }));
  }
  let dialog!: HTMLDialogElement;
  for (const r of releases) {
    const release = el('section', { className: 'release' },
      el('h3', {}, `SiftDB ${r.version}`, ...(r.date ? [el('span', { className: 'dim', textContent: ` · ${r.date}` })] : [])));
    for (const s of r.sections) {
      const important = /^(breaking|highlights)/i.test(s.title);
      const list = el('ul');
      for (const e of s.entries) {
        const li = el('li', {}, ...entryText(e.text));
        if (e.tryIt && canRun(e.tryIt)) {
          const id = e.tryIt;
          li.append(' ', el('button', { type: 'button', className: 'tryit', textContent: 'Try it', onclick: () => { dialog.close(); runCommand(id); } }));
        }
        list.append(li);
      }
      const details = el('details', { className: `notes${/^breaking/i.test(s.title) ? ' breaking' : ''}`, open: important || s.entries.length <= FOLD_AFTER },
        el('summary', { textContent: `${s.title} (${s.entries.length})` }), list);
      release.append(details);
    }
    body.append(release);
  }
  const close = el('button', { type: 'button', className: 'primary', textContent: 'Close', onclick: () => dialog.close() });
  dialog = el('dialog', { className: 'modal wide whatsnew' },
    el('h2', { textContent: "What's new" }), body, el('div', { className: 'factions' }, el('span', { className: 'grow' }), close));
  dialog.setAttribute('aria-label', "What's new in SiftDB");
  const done = showDialog(dialog);
  close.focus();
  await done;
}

/** After an update with only fixes: a small notice, bottom left, that opens What's New, or is dismissed. */
export function showUpdateNotice(version: string, summary: string | null, open: () => void) {
  document.querySelector('.updatenotice')?.remove();
  const notice = el('div', { className: 'updatenotice' });
  notice.setAttribute('role', 'status');
  const dismiss = () => notice.remove();
  notice.append(
    el('span', { textContent: `Updated to ${version}${summary ? `, ${summary}` : ''}` }),
    el('button', { type: 'button', className: 'linkbtn', textContent: "What's new", onclick: () => { dismiss(); open(); } }),
    el('button', { type: 'button', className: 'linkbtn', textContent: '×', title: 'Dismiss', onclick: dismiss }));
  document.body.append(notice);
  setTimeout(dismiss, 30_000); // it is only news: it goes by itself
}
