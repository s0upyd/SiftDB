import type { DatabaseSchema, RelationInfo, RelationKind } from './bridge';

const KIND: Record<RelationKind, { letter: string; label: string }> = {
  table: { letter: 'T', label: 'Table' },
  partitionedTable: { letter: 'P', label: 'Partitioned table' },
  view: { letter: 'V', label: 'View' },
  materializedView: { letter: 'M', label: 'Materialized view' },
  foreignTable: { letter: 'F', label: 'Foreign table' },
};

/** Rendering thousands of rows per keystroke isn't useful; past this the user should refine the filter. */
const MAX_RELATIONS_SHOWN = 500;

/** One connection in the tree. Sheets that connected with the same connection string share one root. */
export interface TreeRoot {
  id: string;
  label: string;
  /** Shown dimmed after the label, e.g. the user name when two roots would otherwise look identical. */
  detail: string;
  /** Null while the first schema load is still running. */
  schema: DatabaseSchema | null;
  /** True for the root the visible sheet is connected to. */
  current: boolean;
}

/**
 * Schema browser: one root per distinct connection, then schemas, relations, columns. A flat list of rows (indentation
 * by depth), rebuilt on every change. Children are only built for expanded nodes, so a 2,000-table database stays cheap.
 */
export class SchemaTree {
  private roots: TreeRoot[] = [];
  private emptyMessage = 'Not connected.';
  private expanded = new Set<string>();
  private seen = new Set<string>();
  private filter = '';

  constructor(private root: HTMLElement, private summary: HTMLElement) {
    root.setAttribute('role', 'tree');
    root.addEventListener('click', (e) => {
      const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
      if (row) this.toggle(row.dataset.key!);
    });
    root.addEventListener('keydown', (e) => this.onKey(e));
  }

  /** Replaces the roots. Expansion survives; a root seen for the first time opens, with its main schema. */
  setRoots(roots: TreeRoot[], emptyMessage = 'Not connected.') {
    this.roots = roots;
    this.emptyMessage = emptyMessage;
    for (const r of roots) {
      if (!this.seen.has(r.id)) { this.seen.add(r.id); this.expanded.add(`n:${r.id}`); }
      if (r.schema && !this.seen.has(`schema:${r.id}`)) {
        this.seen.add(`schema:${r.id}`);
        const s = r.schema.schemas;
        const first = s.find((x) => x.name === 'public') ?? s.find((x) => x.relations.length);
        if (first) this.expanded.add(`s:${r.id}:${first.name}`);
      }
    }
    this.render();
  }

  setFilter(text: string) {
    this.filter = text.trim().toLowerCase();
    this.render();
  }

  private toggle(key: string, focus = true) {
    if (this.expanded.has(key)) this.expanded.delete(key); else this.expanded.add(key);
    this.render();
    if (focus) this.rowFor(key)?.focus();
  }

  private rowFor(key: string) {
    return [...this.root.children].find((r) => (r as HTMLElement).dataset.key === key) as HTMLElement | undefined;
  }

  private onKey(e: KeyboardEvent) {
    const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
    if (!row) return;
    const key = row.dataset.key!;
    const expandable = row.hasAttribute('aria-expanded');
    const open = row.getAttribute('aria-expanded') === 'true';
    switch (e.key) {
      case 'Enter': case ' ': if (expandable) this.toggle(key); break;
      case 'ArrowRight': if (expandable && !open) this.toggle(key); break;
      case 'ArrowLeft': if (expandable && open) this.toggle(key); break;
      case 'ArrowDown': (row.nextElementSibling as HTMLElement | null)?.focus(); break;
      case 'ArrowUp': (row.previousElementSibling as HTMLElement | null)?.focus(); break;
      default: return;
    }
    e.preventDefault();
  }

  private render() {
    const f = this.filter;
    const frag = document.createDocumentFragment();
    let shown = 0, matched = 0, total = 0;

    for (const root of this.roots) {
      const nKey = `n:${root.id}`;
      const schema = root.schema;
      let relCount = 0, relMatched = 0;
      const rows: HTMLElement[] = [];
      const nOpen = f ? true : this.expanded.has(nKey);

      for (const s of schema?.schemas ?? []) {
        relCount += s.relations.length;
        const rels = f
          ? s.relations.filter((r) => r.name.toLowerCase().includes(f) || `${s.name}.${r.name}`.toLowerCase().includes(f))
          : s.relations;
        relMatched += rels.length;
        if (!nOpen || (f && !rels.length)) continue;

        const sKey = `s:${root.id}:${s.name}`;
        const sOpen = f ? true : this.expanded.has(sKey);
        rows.push(this.row(sKey, 1, sOpen, [this.text(s.name), this.dim(`${rels.length}`)], 'Schema'));
        if (!sOpen) continue;

        for (const r of rels) {
          if (shown >= MAX_RELATIONS_SHOWN) break;
          shown++;
          const rKey = `r:${root.id}:${s.name}.${r.name}`;
          const rOpen = this.expanded.has(rKey);
          rows.push(this.row(rKey, 2, rOpen, [this.badge(KIND[r.kind].letter, KIND[r.kind].label), this.text(r.name)], KIND[r.kind].label));
          if (rOpen) for (const c of this.columnRows(root.id, s.name, r)) rows.push(c);
        }
      }
      total += relCount;
      matched += relMatched;
      if (f && schema && !relMatched) continue; // a filter hides connections with nothing that matches

      const head = this.row(nKey, 0, nOpen, [this.text(root.label), this.dim(schema ? `${relCount}` : 'loading…')], 'Connection');
      head.classList.add('root');
      if (root.detail) head.append(this.dim(root.detail));
      head.classList.toggle('current', root.current);
      frag.append(head, ...rows);
    }

    if (matched > shown && this.roots.some((r) => r.schema)) {
      const note = document.createElement('div');
      note.className = 'row dim';
      note.style.paddingLeft = '30px';
      note.textContent = `… ${matched - shown} more. Type in the filter to narrow the list.`;
      frag.append(note);
    }

    this.root.replaceChildren(frag);
    const first = this.root.firstElementChild as HTMLElement | null;
    if (first) first.tabIndex = 0; // roving tabindex: one tab stop for the whole tree
    const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
    this.summary.textContent = !this.roots.length ? this.emptyMessage
      : f ? `${matched} of ${plural(total, 'relation')} match`
      : `${plural(this.roots.length, 'connection')} · ${plural(total, 'relation')}`;
  }

  private *columnRows(rootId: string, schemaName: string, r: RelationInfo) {
    const pk = new Set(r.primaryKey);
    for (const c of r.columns) {
      const fk = r.foreignKeys.find((k) => k.columns.includes(c.name));
      const parts: Node[] = [this.text(c.name), this.dim(c.dataType + (c.isNullable ? '' : ' not null'))];
      if (pk.has(c.name)) parts.push(this.badge('PK', 'Primary key'));
      if (fk) {
        const target = `${fk.referencedSchema}.${fk.referencedRelation}(${fk.referencedColumns.join(', ')})`;
        parts.push(this.badge('FK', `Foreign key → ${target}`));
      }
      yield this.row(`c:${rootId}:${schemaName}.${r.name}.${c.name}`, 3, null, parts, c.default ? `Default: ${c.default}` : '');
    }
  }

  /** `open` is null for leaves. */
  private row(key: string, depth: number, open: boolean | null, content: Node[], title: string) {
    const el = document.createElement('div');
    el.className = 'row';
    el.dataset.key = key;
    el.tabIndex = -1;
    el.setAttribute('role', 'treeitem');
    el.setAttribute('aria-level', String(depth + 1));
    if (title) el.title = title;
    if (open !== null) el.setAttribute('aria-expanded', String(open));
    el.style.paddingLeft = `${8 + depth * 14}px`;
    const twisty = document.createElement('span');
    twisty.className = 'twisty';
    twisty.textContent = open === null ? '' : open ? '▾' : '▸';
    el.append(twisty, ...content);
    return el;
  }

  private text(t: string) { const s = document.createElement('span'); s.textContent = t; return s; }
  private dim(t: string) { const s = this.text(t); s.className = 'dim'; return s; }
  private badge(t: string, title: string) { const s = this.text(t); s.className = 'badge'; s.title = title; return s; }
}
