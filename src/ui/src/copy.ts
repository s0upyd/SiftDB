// Copying from the results grid: the text that goes on the clipboard, and the small notes that say what happened.

/**
 * One value as a tab-separated field, the way Excel and most editors read a paste: a value holding a tab, a line break or
 * a double quote is wrapped in quotes, with its quotes doubled. NULL is an empty field (a spreadsheet has no NULL, and the
 * text "NULL" would be read as a word).
 */
export function tsvField(value: string | null): string {
  if (value === null) return '';
  return /[\t\r\n"]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

/** Rows of values as tab-separated text, one line per row, with an optional header line first. Lines end in CRLF (Windows). */
export function toTsv(rows: (string | null)[][], header?: string[]): string {
  const lines = rows.map((r) => r.map(tsvField).join('\t'));
  if (header) lines.unshift(header.map(tsvField).join('\t'));
  return lines.join('\r\n');
}

/** Puts text on the clipboard. False when the page was not allowed to (it has no focus, for one). */
export async function writeClipboard(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

let live: HTMLElement | null = null;

/** Says something to screen readers without showing it. One polite live region for the whole page. */
export function announce(text: string) {
  if (!live) {
    live = document.createElement('div');
    live.className = 'srOnly';
    live.setAttribute('aria-live', 'polite');
    document.body.append(live);
  }
  // Clearing first makes a repeat of the same words (copying twice) be read again.
  live.textContent = '';
  requestAnimationFrame(() => { live!.textContent = text; });
}

/** A short note in the bottom right of `over` (the grid) for a moment: "Copied 3 rows". Also read out. */
export function toast(over: HTMLElement, text: string, tone: 'info' | 'warn' = 'info') {
  document.querySelectorAll('.gridtoast').forEach((t) => t.remove());
  const t = document.createElement('div');
  t.className = 'gridtoast';
  t.dataset.tone = tone;
  t.textContent = text;
  t.setAttribute('aria-hidden', 'true'); // announce() reads it; reading it twice would be noise
  const r = over.getBoundingClientRect();
  t.style.right = `${Math.max(8, window.innerWidth - r.right + 16)}px`;
  t.style.bottom = `${Math.max(8, window.innerHeight - r.bottom + 16)}px`;
  document.body.append(t);
  announce(text);
  setTimeout(() => t.remove(), tone === 'warn' ? 4000 : 1800);
}
