import { waitIcon } from './icons';

// Who's blocking me (design section 14, F3): the amber strip a sheet shows while its statement waits for another
// session's lock, and the note on its transaction bar while its own open transaction makes others wait. The engine
// finds the sessions (EngineHost lock watcher), and says which of them are SiftDB's own sheets; this draws them and
// offers what can be done about them.

/** Another session, as the engine sends it ("blockers", "blocking"). Times in seconds. */
export interface OtherSession {
  /** Who it is across servers; matched by the engine to SiftDB's own sessions. */
  key: string | null;
  pid: number;
  user: string | null;
  application: string | null;
  client: string | null;
  state: string | null;
  /** Running a statement now: cancelling its query can free the lock. Otherwise idle in a transaction. */
  running: boolean;
  inTransactionSeconds: number | null;
  runningSeconds: number | null;
  waitingForLock: boolean;
  /** Its current or last statement; null when unknown (another SiftDB sheet's own bookkeeping, say). */
  query: string | null;
  /** This session's role may cancel it or end it. */
  canSignal: boolean;
}

/** One of SiftDB's own sheets, when the other session is its session: named, and one click away. */
export interface OwnSheet { title: string; show(): void }

/** Which sessions, by pid, are SiftDB's own sheets. */
export type OwnSheets = ReadonlyMap<number, OwnSheet>;

export interface BlockerActions {
  cancelMine(): void;
  /** Cancels their statement, or (`terminate`) ends their session. Resolves with the server's refusal, or null. */
  signal(pid: number, terminate: boolean): Promise<string | null>;
}

/** "12 min 05 s", "3 s", "2 h 07 min". */
export function duration(seconds: number | null): string {
  if (seconds === null) return '';
  const s = Math.max(0, Math.floor(seconds));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, '0')} s`;
  return `${Math.floor(s / 3600)} h ${String(Math.floor((s % 3600) / 60)).padStart(2, '0')} min`;
}

/** "your sheet “Sheet 2” (pid 4711)" for one of SiftDB's own; else "dave@10.0.0.4 (pid 4711)", "dave (local) (pid 4711)". */
export function who(o: OtherSession, sheet?: OwnSheet | null): string {
  if (sheet) return `your sheet “${sheet.title}” (pid ${o.pid})`;
  return `${o.user ?? 'someone'}${o.client ? `@${o.client}` : ' (local)'} (pid ${o.pid})`;
}

/** What a session is doing, for a line about it: "in a transaction for 12 min, running…", "idle, in a transaction…". */
function doing(o: OtherSession): string {
  const tx = o.inTransactionSeconds !== null ? `in a transaction for ${duration(o.inTransactionSeconds)}` : 'not in a transaction';
  if (o.running) return `${tx}, running a statement for ${duration(o.runningSeconds)}${o.waitingForLock ? ', itself waiting for a lock' : ''}`;
  return o.state === 'idle in transaction (aborted)' ? `idle in a failed transaction (for ${duration(o.inTransactionSeconds)})` : `idle, ${tx}`;
}

const shortQuery = (q: string) => { const one = q.replace(/\s+/g, ' ').trim(); return one.length > 80 ? `${one.slice(0, 79)}…` : one; };

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

/**
 * The strip: one line saying who holds the lock, with Cancel my statement always, and the blocker's own actions when there
 * is one; Details opens every blocker with its full statement. When the blocker is one of SiftDB's own sheets it is named
 * by its tab, and *Go to* that sheet comes first: that is nearly always where it is put right (commit or roll back
 * there). Cancel their query is offered only for a session running a statement (an idle one holds its locks until its
 * transaction ends), and both it and End their session only when the role may.
 */
export class BlockerBar {
  private blockers: OtherSession[] = [];
  private sheets: OwnSheets = new Map();
  private open = false;
  private message = '';

  constructor(private readonly bar: HTMLElement, private readonly actions: BlockerActions) {}

  /** The sheet's statement waits for these sessions; [] when it no longer waits. */
  get waiting(): boolean { return this.blockers.length > 0; }

  update(blockers: OtherSession[], sheets: OwnSheets = new Map()) {
    if (!blockers.length) this.message = '';
    this.blockers = blockers;
    this.sheets = sheets;
    this.render();
  }

  private render() {
    const list = this.blockers;
    this.bar.hidden = list.length === 0;
    if (!list.length) { this.bar.replaceChildren(); return; }

    const first = list[0];
    const firstSheet = this.sheets.get(first.pid);
    const head = el('div', 'lockhead');
    const icon = el('span', 'lockIcon');
    icon.setAttribute('aria-hidden', 'true');
    icon.append(waitIcon());
    const text = el('span', 'lockText');
    if (list.length === 1) {
      text.append('Waiting for a lock held by ', el('strong', '', who(first, firstSheet)), `, ${doing(first)}`);
      if (first.query) text.append(': ', el('code', '', shortQuery(first.query)));
      if (firstSheet && !first.running) text.append('. Commit or roll back there to let this go on.');
    } else {
      const own = list.filter((o) => this.sheets.has(o.pid)).length;
      text.append(`Waiting for locks held by ${list.length} sessions: `, el('strong', '', who(first, firstSheet)),
        ` and ${list.length - 1} more${own ? ` (${own === 1 ? 'one is your sheet' : `${own} are your sheets`})` : ''}`);
    }
    const mine = el('button', '', 'Cancel my statement');
    mine.title = 'Stop waiting: cancel the statement this sheet is running (Ctrl+.)';
    mine.onclick = () => this.actions.cancelMine();
    const more = el('button', 'linkbtn', this.open ? 'Hide details' : 'Details');
    more.setAttribute('aria-expanded', String(this.open));
    more.onclick = () => { this.open = !this.open; this.render(); };
    // One blocker: its actions in the line. One of SiftDB's own: going to it first, the rest in the details.
    const firstActions = list.length !== 1 ? [] : firstSheet ? [this.goTo(firstSheet)] : this.sessionActions(first);
    head.append(icon, text, el('span', 'grow'), ...firstActions, mine, more);

    const parts: HTMLElement[] = [head];
    if (this.message) parts.push(el('div', 'lockmsg', this.message));
    if (this.open) {
      const details = el('div', 'lockdetails');
      for (const o of list) {
        const sheet = this.sheets.get(o.pid);
        const row = el('div', 'lockrow');
        const line = el('div', 'lockline');
        line.append(el('strong', '', who(o, sheet)), ` · ${o.application ? `${o.application} · ` : ''}${doing(o)}`);
        row.append(line);
        row.append(o.query ? el('pre', 'lockquery', o.query) : el('div', 'lockquery dim', 'Its last statement is not known.'));
        const notes: string[] = [];
        if (sheet && !o.running) notes.push(`It is your sheet “${sheet.title}”, idle in its transaction: commit or roll back there.`);
        else if (!o.running) notes.push('It is idle in a transaction: only ending its session releases the lock (its uncommitted changes are rolled back).');
        if (!o.canSignal && !sheet) notes.push('Your role may not cancel this session or end it. Ask its owner, or an administrator.');
        if (notes.length) row.append(el('div', 'locknote', notes.join(' ')));
        const acts = [...(sheet ? [this.goTo(sheet)] : []), ...(list.length > 1 || sheet ? this.sessionActions(o, sheet) : [])];
        if (acts.length) { const box = el('div', 'lockacts'); box.append(...acts); row.append(box); }
        details.append(row);
      }
      parts.push(details);
    }
    this.bar.replaceChildren(...parts);
  }

  private goTo(sheet: OwnSheet): HTMLButtonElement {
    const b = el('button', 'primary', `Go to “${sheet.title}”`);
    b.title = 'Open the sheet holding the lock: commit or roll back there to let this statement go on';
    b.onclick = () => sheet.show();
    return b;
  }

  /** Cancel their query (when it is running one) and End their session, each asking first; nothing when the role may not. */
  private sessionActions(o: OtherSession, sheet?: OwnSheet): HTMLButtonElement[] {
    if (!o.canSignal) return [];
    const name = who(o, sheet);
    const buttons: HTMLButtonElement[] = [];
    if (o.running) {
      const cancel = el('button', '', 'Cancel their query');
      cancel.title = `Cancel the statement ${name} is running. Its session stays connected.`;
      cancel.onclick = () => void this.signal(o, false, name,
        `Cancel the statement ${name} is running?\n\nIts session stays connected. If it is inside a transaction, that transaction fails and has to be rolled back.`);
      buttons.push(cancel);
    }
    const end = el('button', 'danger', sheet ? 'End its session' : 'End their session');
    end.title = `Disconnect ${name}. Its open transaction is rolled back.`;
    end.onclick = () => void this.signal(o, true, name,
      `End the session of ${name}?\n\nIt is disconnected, and its open transaction is rolled back: any changes it has not committed are lost.`
      + (sheet ? `\n\nThe sheet “${sheet.title}” keeps its text, and has to connect again.` : ''));
    buttons.push(end);
    return buttons;
  }

  private async signal(o: OtherSession, terminate: boolean, name: string, question: string) {
    if (!window.confirm(question)) return;
    const refused = await this.actions.signal(o.pid, terminate);
    this.message = refused
      ? `${terminate ? 'Could not end that session' : 'Could not cancel that statement'}: ${refused}`
      : `${terminate ? 'Ended the session of' : 'Cancelled the statement of'} ${name}. Checking again…`;
    this.render();
  }
}

/** The transaction bar's note while this sheet's open transaction makes others wait: "blocking 2 sessions", who in its tooltip. */
export function blockingNote(sessions: OtherSession[], sheets: OwnSheets = new Map()): { text: string; title: string } | null {
  if (!sessions.length) return null;
  return {
    text: `⚠ blocking ${sessions.length === 1 ? '1 session' : `${sessions.length} sessions`}`,
    title: [
      `Your open transaction holds locks that ${sessions.length === 1 ? 'another session is' : `${sessions.length} sessions are`} waiting for. Commit or roll back to let ${sessions.length === 1 ? 'it' : 'them'} go on.`,
      ...sessions.map((o) => `${who(o, sheets.get(o.pid))}, waiting ${duration(o.runningSeconds)}${o.query ? `: ${shortQuery(o.query)}` : ''}`),
    ].join('\n'),
  };
}
