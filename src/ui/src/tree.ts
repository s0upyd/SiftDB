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

/**
 * Schema browser: schemas, then relations, then columns. A flat list of rows (indentation by depth), rebuilt on every
 * change. Children are only built for expanded nodes, so a 2,000-table database stays cheap.
 */
export class SchemaTree {
  private schema: DatabaseSchema | null = null;
  private expanded = new Set<string>();
  private filter = '';

  constructor(private root: HTMLElement, private summary: HTMLElement) {
    root.setAttribute('role', 'tree');
    root.addEventListener('click', (e) => {
      const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
      if (row) this.toggle(row.dataset.key!);
    });
    root.addEventListener('keydown', (e) => this.onKey(e));
  }

  setSchema(schema: DatabaseSchema) {
    this.schema = schema;
    this.expanded.clear();
    const first = schema.schemas.find((s) => s.name === 'public') ?? schema.schemas.find((s) => s.relations.length);
    if (first) this.expanded.add(`s:${first.name}`);
    this.render();
  }

  clear(message: string) {
    this.schema = null;
    this.root.replaceChildren();
    this.summary.textContent = message;
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
    const schema = this.schema;
    if (!schema) return;
    const f = this.filter;
    const frag = document.createDocumentFragment();
    let shown = 0, matched = 0, total = 0;

    for (const s of schema.schemas) {
      total += s.relations.length;
      const rels = f
        ? s.relations.filter((r) => r.name.toLowerCase().includes(f) || `${s.name}.${r.name}`.toLowerCase().includes(f))
        : s.relations;
      matched += rels.length;
      if (f && !rels.length) continue;

      const sKey = `s:${s.name}`;
      const sOpen = f ? true : this.expanded.has(sKey);
      frag.append(this.row(sKey, 0, sOpen, [this.text(s.name), this.dim(`${rels.length}`)], 'Schema'));
      if (!sOpen) continue;

      for (const r of rels) {
        if (shown >= MAX_RELATIONS_SHOWN) break;
        shown++;
        const rKey = `r:${s.name}.${r.name}`;
        const rOpen = this.expanded.has(rKey);
        frag.append(this.row(rKey, 1, rOpen, [this.badge(KIND[r.kind].letter, KIND[r.kind].label), this.text(r.name)], KIND[r.kind].label));
        if (rOpen) for (const c of this.columnRows(s.name, r)) frag.append(c);
      }
    }

    if (matched > shown) {
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
    this.summary.textContent = f
      ? `${matched} of ${plural(total, 'relation')} match`
      : `${plural(schema.schemas.length, 'schema')} · ${plural(total, 'relation')}`;
  }

  private *columnRows(schemaName: string, r: RelationInfo) {
    const pk = new Set(r.primaryKey);
    for (const c of r.columns) {
      const fk = r.foreignKeys.find((k) => k.columns.includes(c.name));
      const parts: Node[] = [this.text(c.name), this.dim(c.dataType + (c.isNullable ? '' : ' not null'))];
      if (pk.has(c.name)) parts.push(this.badge('PK', 'Primary key'));
      if (fk) {
        const target = `${fk.referencedSchema}.${fk.referencedRelation}(${fk.referencedColumns.join(', ')})`;
        parts.push(this.badge('FK', `Foreign key → ${target}`));
      }
      yield this.row(`c:${schemaName}.${r.name}.${c.name}`, 2, null, parts, c.default ? `Default: ${c.default}` : '');
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
