import type { BridgeEvent, DatabaseSchema, RelationInfo, RelationKind } from './bridge';
import { toast, writeClipboard } from './copy';
import { lockIcon } from './icons';
import { MenuItem, showMenu } from './menu';

const KIND: Record<RelationKind, { letter: string; label: string; group: string }> = {
  table: { letter: 'T', label: 'Table', group: 'Tables' },
  partitionedTable: { letter: 'P', label: 'Partitioned table', group: 'Tables' },
  view: { letter: 'V', label: 'View', group: 'Views' },
  materializedView: { letter: 'M', label: 'Materialized view', group: 'Materialized views' },
  foreignTable: { letter: 'F', label: 'Foreign table', group: 'Foreign tables' },
};
const RELATION_GROUPS = ['Tables', 'Views', 'Materialized views', 'Foreign tables'];

/** Rows a group shows at most; past this the filter narrows it. Rendering thousands of rows per keystroke is no use. */
const MAX_IN_GROUP = 500;

// ---- what the engine sends (mirrors SiftDB.Abstractions SchemaDetail.cs) ------------------------------------------

interface ColumnDetail {
  name: string; comment: string | null; identity: string | null; generated: string | null;
  /** The most characters a check constraint allows (`lengthCheck` names it), shown beside the type. */
  maxLength?: number | null; lengthCheck?: string | null;
}
interface KeyConstraint { name: string; primary: boolean; columns: string[]; definition: string }
interface ForeignKeyDetail {
  name: string; columns: string[]; referencedSchema: string; referencedRelation: string; referencedColumns: string[];
  onDelete: string | null; onUpdate: string | null; deferrable: boolean; definition: string;
}
interface IndexDetail { name: string; method: string; unique: boolean; primary: boolean; keys: string; predicate: string | null; bytes: number | null; constraint: string | null; definition: string }
interface CheckConstraint { name: string; exclusion: boolean; definition: string }
interface TriggerInfo { name: string; timing: string; events: string[]; forEachRow: boolean; function: string; enabled: boolean; definition: string }
interface PartitionInfo { schema: string; name: string; bound: string }
interface RelationDetail {
  comment: string | null; estimatedRows: number | null; totalBytes: number | null; columns: ColumnDetail[]; keys: KeyConstraint[];
  foreignKeys: ForeignKeyDetail[]; indexes: IndexDetail[]; checks: CheckConstraint[]; triggers: TriggerInfo[]; partitions: PartitionInfo[];
  definition: string | null;
}
interface FunctionInfo { name: string; kind: string; arguments: string; returns: string | null; comment: string | null }
interface SequenceInfo { name: string; dataType: string; ownedBy: string | null }
interface TypeInfo { name: string; kind: string; values: string[]; detail: string | null; comment: string | null }
interface SchemaObjects { functions: FunctionInfo[]; sequences: SequenceInfo[]; types: TypeInfo[] }

/** One connection in the tree. Sheets on the same saved connection (or the same pasted string) share one root. */
export interface TreeRoot {
  id: string;
  label: string;
  /** Shown dimmed after the label, e.g. the user name when two roots would otherwise look identical. */
  detail: string;
  /** Null while the first schema load is still running. */
  schema: DatabaseSchema | null;
  /** True for the root the visible sheet is connected to. */
  current: boolean;
  /** Opened read-only: marked with a lock, so a refused write is never a mystery. */
  readOnly: boolean;
}

/** What the tree needs from the app: a session on a root's connection to ask, and a sheet to put text in. */
export interface TreeContext {
  /** Sends a command on one of the root's sessions (the visible sheet's if it is one). Null when none is connected. */
  request(rootId: string, cmd: Record<string, unknown>): Promise<BridgeEvent | null>;
  /** "Script as": into the visible sheet, at the cursor, never run. */
  insertScript(sql: string): void;
  /** Refresh: reload the root's schema (and so every detail). */
  refresh(rootId: string): void;
}

/** Where a click on a link row goes: a table, and the columns there to land on (a key's), if any. */
interface Jump { schema: string; table: string; columns?: string[] }

/** What a row stands for, for its menu, drag and click. */
type NodeInfo =
  | { kind: 'relation'; root: string; schema: string; name: string; rel: RelationInfo }
  | { kind: 'column'; root: string; schema: string; table: string; name: string }
  | { kind: 'item'; root: string; name: string; definition: string | null; jump?: Jump }
  | { kind: 'object'; root: string; schema: string; name: string; objectKind: 'function' | 'sequence' | 'type'; arguments?: string };

/**
 * Schema browser (design section 14, F13): one root per distinct connection, then schemas, and in each schema its
 * relations grouped by kind (tables, views, materialized views, foreign tables) and its functions, sequences and types.
 * A table opens into its columns, keys, foreign keys, what references it, indexes, checks, triggers and partitions; a
 * group appears only when it has something in it, with its count. Details are loaded the first time a table is opened
 * (and a schema's other objects the first time the schema is), then kept until the schema is refreshed.
 *
 * A flat list of rows (indentation by depth), rebuilt on every change; children are built only for open nodes, so a
 * 2,000-table database stays cheap. Hovering shows a definition; a foreign key or "referenced by" entry jumps to its
 * table; a node can be dragged into the editor (its name, quoted as SQL needs it); the menu copies and scripts.
 */
export class SchemaTree {
  private roots: TreeRoot[] = [];
  private emptyMessage = 'Not connected.';
  private expanded = new Set<string>();
  private seen = new Set<string>();
  private filter = '';
  /** Also match column names, and show the matching columns under their tables. */
  private matchColumns = false;
  /** The rows a jump landed on (a table, or the columns of a key in it), highlighted until the next jump. */
  private highlight = new Set<string>();
  /** Per root: the schema it was loaded from (a new one empties the caches below), relations' details, schemas' objects. */
  private loadedFrom = new Map<string, DatabaseSchema | null>();
  private details = new Map<string, RelationDetail | 'loading' | 'missing'>();
  private objects = new Map<string, SchemaObjects | 'loading' | 'missing'>();
  private nodes = new Map<string, NodeInfo>();
  ctx: TreeContext | null = null;

  constructor(private root: HTMLElement, private summary: HTMLElement) {
    root.setAttribute('role', 'tree');
    root.addEventListener('click', (e) => {
      const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
      if (!row) return;
      const info = this.nodes.get(row.dataset.key!);
      if (info?.kind === 'item' && info.jump) { this.jumpTo(info.root, info.jump); return; }
      if (row.hasAttribute('aria-expanded')) this.toggle(row.dataset.key!);
    });
    root.addEventListener('keydown', (e) => this.onKey(e));
    root.addEventListener('contextmenu', (e) => {
      const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
      if (!row) return;
      e.preventDefault();
      row.focus();
      this.openMenu(row, e.clientX, e.clientY);
    });
    root.addEventListener('dragstart', (e) => {
      const row = (e.target as HTMLElement).closest<HTMLElement>('[data-key]');
      const text = row && this.dragText(row.dataset.key!);
      if (!text || !e.dataTransfer) { e.preventDefault(); return; }
      e.dataTransfer.setData('text/plain', text);
      e.dataTransfer.effectAllowed = 'copy';
    });
  }

  /** Replaces the roots. Expansion survives; a root seen for the first time opens, with its main schema. */
  setRoots(roots: TreeRoot[], emptyMessage = 'Not connected.') {
    this.roots = roots;
    this.emptyMessage = emptyMessage;
    for (const r of roots) {
      // A new schema load (a refresh) makes every detail loaded from the old one stale.
      if (this.loadedFrom.get(r.id) !== r.schema) {
        this.loadedFrom.set(r.id, r.schema);
        for (const m of [this.details, this.objects]) for (const k of [...m.keys()]) if (k.startsWith(`${r.id}:`)) m.delete(k);
      }
      if (!this.seen.has(r.id)) { this.seen.add(r.id); this.expanded.add(`n:${r.id}`); }
      if (r.schema && !this.seen.has(`schema:${r.id}`)) {
        this.seen.add(`schema:${r.id}`);
        const s = r.schema.schemas;
        const first = s.find((x) => x.name === 'public') ?? s.find((x) => x.relations.length);
        if (first) { this.expanded.add(`s:${r.id}:${first.name}`); this.expanded.add(`g:${r.id}:${first.name}:Tables`); }
      }
    }
    this.render();
  }

  setFilter(text: string) {
    this.filter = text.trim().toLowerCase();
    this.render();
  }

  /** The filter's "columns too" option. */
  setMatchColumns(on: boolean) {
    this.matchColumns = on;
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
    const info = this.nodes.get(key);
    switch (e.key) {
      case 'Enter': case ' ':
        if (info?.kind === 'item' && info.jump) this.jumpTo(info.root, info.jump);
        else if (expandable) this.toggle(key);
        break;
      case 'ArrowRight': if (expandable && !open) this.toggle(key); break;
      case 'ArrowLeft': if (expandable && open) this.toggle(key); break;
      case 'ArrowDown': (row.nextElementSibling as HTMLElement | null)?.focus(); break;
      case 'ArrowUp': (row.previousElementSibling as HTMLElement | null)?.focus(); break;
      case '*':
        // A whole table: it and every group in it.
        if (info?.kind !== 'relation') return;
        this.expanded.add(key);
        for (const g of REL_GROUPS) this.expanded.add(`${key}:${g}`);
        this.render();
        this.rowFor(key)?.focus();
        break;
      case 'ContextMenu': case 'F10':
        if (e.key === 'F10' && !e.shiftKey) return;
        { const box = row.getBoundingClientRect(); this.openMenu(row, box.left + 24, box.bottom); }
        break;
      default: return;
    }
    e.preventDefault();
  }

  // ---- loading what is not in the fast schema load ---------------------------------------------------------------

  private detailFor(rootId: string, schema: string, name: string): RelationDetail | 'loading' | 'missing' {
    const k = `${rootId}:${schema}.${name}`;
    const have = this.details.get(k);
    if (have) return have;
    this.details.set(k, 'loading');
    void this.ctx?.request(rootId, { type: 'relationDetail', schema, name }).then((r) => {
      this.details.set(k, r && r.type === 'relationDetail' && r.found ? (r.detail as RelationDetail) : 'missing');
      if (r?.quoted) this.addQuoted(rootId, r.quoted as Record<string, string>);
      this.render();
    });
    return 'loading';
  }

  private objectsFor(rootId: string, schema: string): SchemaObjects | 'loading' | 'missing' {
    const k = `${rootId}:${schema}`;
    const have = this.objects.get(k);
    if (have) return have;
    this.objects.set(k, 'loading');
    void this.ctx?.request(rootId, { type: 'schemaObjects', schema }).then((r) => {
      this.objects.set(k, r && r.type === 'schemaObjects' && r.found ? (r.objects as SchemaObjects) : 'missing');
      if (r?.quoted) this.addQuoted(rootId, r.quoted as Record<string, string>);
      this.render();
    });
    return 'loading';
  }

  /** Names that must be quoted, as the engine says (only those that differ from how they are stored). */
  private extraQuoted = new Map<string, Record<string, string>>();
  private addQuoted(rootId: string, q: Record<string, string>) { this.extraQuoted.set(rootId, { ...this.extraQuoted.get(rootId), ...q }); }
  private quote(rootId: string, name: string): string {
    const r = this.roots.find((x) => x.id === rootId);
    return r?.schema?.quoted?.[name] ?? this.extraQuoted.get(rootId)?.[name] ?? name;
  }

  // ---- jumping, dragging, the menu -------------------------------------------------------------------------------

  /**
   * Opens the way to a table (its root, schema and group), highlights it and brings it into view. With columns (a
   * foreign key's, at either end), it goes on down to them: the table opens on its Columns, and those are highlighted.
   */
  private jumpTo(rootId: string, jump: Jump) {
    const { schema, table } = jump;
    const root = this.roots.find((r) => r.id === rootId);
    const rel = root?.schema?.schemas.find((s) => s.name === schema)?.relations.find((r) => r.name === table);
    if (!rel) { toast(this.root, `${schema}.${table} is not in the tree. Refresh the schema?`, 'warn'); return; }
    const key = `r:${rootId}:${schema}.${table}`;
    this.expanded.add(`n:${rootId}`);
    this.expanded.add(`s:${rootId}:${schema}`);
    this.expanded.add(`g:${rootId}:${schema}:${KIND[rel.kind].group}`);
    const columns = (jump.columns ?? []).filter((c) => rel.columns.some((x) => x.name === c));
    if (columns.length) {
      this.expanded.add(key);
      this.expanded.add(`${key}:Columns`);
    }
    const targets = columns.length ? columns.map((c) => `c:${rootId}:${schema}.${table}.${c}`) : [key];
    this.highlight = new Set(targets);
    if (this.filter && !`${schema}.${table}`.toLowerCase().includes(this.filter)) toast(this.root, 'The filter hides that table: clear it to see it.');
    this.render();
    // The table's row first, so it is in view too when there is room, then the column the jump is for.
    this.rowFor(key)?.scrollIntoView({ block: 'nearest' });
    const row = this.rowFor(targets[0]);
    row?.scrollIntoView({ block: 'nearest' });
    row?.focus();
  }

  /** What dragging a row into the editor inserts: its name as SQL needs it (a table schema-qualified). */
  private dragText(key: string): string | null {
    const n = this.nodes.get(key);
    if (!n) return null;
    switch (n.kind) {
      case 'relation': case 'object': {
        // A database without schemas (DynamoDB) names a table alone: its one schema only groups the tree.
        const writes = this.roots.find((r) => r.id === n.root)?.schema?.writesSchemaNames !== false;
        return writes ? `${this.quote(n.root, n.schema)}.${this.quote(n.root, n.name)}` : this.quote(n.root, n.name);
      }
      case 'column': case 'item': return this.quote(n.root, n.name);
    }
  }

  private openMenu(row: HTMLElement, x: number, y: number) {
    const key = row.dataset.key!;
    const n = this.nodes.get(key);
    const rootId = n?.root ?? (key.startsWith('n:') ? key.slice(2) : null);
    const items: MenuItem[] = [];
    if (n) {
      const name = this.dragText(key)!;
      // A quick look at what a table holds, first in its menu: written into the sheet, never run.
      if (n.kind === 'relation')
        items.push({ label: 'Select top 100 rows', action: () => void this.script(n.root, { kind: 'relation', schema: n.schema, name: n.name, script: 'top' }) });
      items.push({ label: 'Copy name', separatorBefore: n.kind === 'relation', action: () => void this.copy(name, 'Copied the name') });
      if (n.kind === 'item' && n.definition) items.push({ label: 'Copy definition', action: () => void this.copy(n.definition!, 'Copied the definition') });
      const script = (what: 'create' | 'drop' | 'select' | 'top', label: string, separatorBefore = false) => {
        if (n.kind !== 'relation' && n.kind !== 'object') return;
        const kind = n.kind === 'relation' ? 'relation' : n.objectKind;
        items.push({
          label, separatorBefore,
          action: () => void this.script(n.root, { kind, schema: n.schema, name: n.name, arguments: n.kind === 'object' ? n.arguments : undefined, script: what }),
        });
      };
      script('create', 'Script as CREATE', true);
      if (n.kind === 'relation') script('select', 'Script as SELECT');
      script('drop', 'Script as DROP');
    }
    if (rootId) items.push({ label: 'Refresh schema', separatorBefore: items.length > 0, action: () => this.ctx?.refresh(rootId) });
    if (items.length) showMenu(x, y, items, 'Schema actions');
  }

  private async copy(text: string, said: string) {
    toast(this.root, (await writeClipboard(text)) ? said : 'Could not copy', 'info');
  }

  /** "Script as": the statement goes into the visible sheet, at the cursor; it is never run. */
  private async script(rootId: string, cmd: Record<string, unknown>) {
    const r = await this.ctx?.request(rootId, { type: 'scriptObject', ...cmd });
    if (!r) { toast(this.root, 'Connect a sheet to this connection first.', 'warn'); return; }
    if (r.type === 'error') { toast(this.root, r.message, 'warn'); return; }
    this.ctx?.insertScript(r.sql as string);
  }

  // ---- drawing ---------------------------------------------------------------------------------------------------

  private render() {
    const f = this.filter;
    const frag = document.createDocumentFragment();
    this.nodes.clear();
    let matched = 0, total = 0;

    for (const root of this.roots) {
      const nKey = `n:${root.id}`;
      const schema = root.schema;
      let relCount = 0, relMatched = 0;
      const rows: HTMLElement[] = [];
      const nOpen = f ? true : this.expanded.has(nKey);

      for (const s of schema?.schemas ?? []) {
        relCount += s.relations.length;
        const hits = f ? s.relations.filter((r) => this.matches(s.name, r)) : s.relations;
        relMatched += hits.length;
        if (!nOpen || (f && !hits.length)) continue;

        const sKey = `s:${root.id}:${s.name}`;
        const sOpen = f ? true : this.expanded.has(sKey);
        rows.push(this.row(sKey, 1, sOpen, [this.text(s.name), this.dim(`${hits.length}`)], 'Schema'));
        if (!sOpen) continue;

        // Relations, by kind.
        for (const group of RELATION_GROUPS) {
          const rels = hits.filter((r) => KIND[r.kind].group === group);
          if (!rels.length) continue;
          const gKey = `g:${root.id}:${s.name}:${group}`;
          const gOpen = f ? true : this.expanded.has(gKey);
          rows.push(this.row(gKey, 2, gOpen, [this.text(group), this.dim(`${rels.length}`)], group));
          if (!gOpen) continue;
          for (const r of rels.slice(0, MAX_IN_GROUP)) this.relationRows(root.id, s.name, r, 3, rows);
          if (rels.length > MAX_IN_GROUP) rows.push(this.note(3, `… ${rels.length - MAX_IN_GROUP} more. Type in the filter to narrow the list.`));
        }
        // Functions, sequences and types: loaded the first time the schema is opened.
        if (!f) this.objectRows(root.id, s.name, rows);
      }
      total += relCount;
      matched += relMatched;
      if (f && schema && !relMatched) rows.push(this.note(1, 'No tables or views match the filter.'));

      // The tooltip has the whole description: in a narrow sidebar the end of the row (host / database) is cut off.
      const tip = `${root.label}${root.detail ? ` · ${root.detail}` : ''}${root.readOnly ? ' · read-only' : ''}`;
      const count = !schema ? 'loading…' : f ? `${relMatched} of ${relCount}` : `${relCount}`;
      const head = this.row(nKey, 0, nOpen, [this.text(root.label), this.dim(count)], tip);
      head.classList.add('root');
      // The lock goes right after the name: the sidebar is often narrow, and it must not be the part that scrolls away.
      if (root.readOnly) {
        const ro = this.badge('read-only', 'Read-only connection: the server refuses changes');
        ro.classList.add('ro');
        ro.prepend(lockIcon());
        head.insertBefore(ro, head.children[2] ?? null);
      }
      if (root.detail) head.append(this.dim(root.detail));
      head.classList.toggle('current', root.current);
      frag.append(head, ...rows);
    }

    this.root.replaceChildren(frag);
    const first = this.root.firstElementChild as HTMLElement | null;
    if (first) first.tabIndex = 0; // roving tabindex: one tab stop for the whole tree
    const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
    this.summary.textContent = !this.roots.length ? this.emptyMessage
      : f ? `${matched} of ${plural(total, 'relation')} match`
      : `${plural(this.roots.length, 'connection')} · ${plural(total, 'relation')}`;
  }

  private matches(schema: string, r: RelationInfo): boolean {
    const f = this.filter;
    if (r.name.toLowerCase().includes(f) || `${schema}.${r.name}`.toLowerCase().includes(f)) return true;
    return this.matchColumns && r.columns.some((c) => c.name.toLowerCase().includes(f));
  }

  /** A relation, and when open, its groups: from its details once loaded, from the fast load until then. */
  private relationRows(rootId: string, schema: string, r: RelationInfo, depth: number, rows: HTMLElement[]) {
    const rKey = `r:${rootId}:${schema}.${r.name}`;
    // A filter that matched only columns opens the table on its matching columns.
    const columnHit = !!this.filter && this.matchColumns && !r.name.toLowerCase().includes(this.filter)
      && !`${schema}.${r.name}`.toLowerCase().includes(this.filter);
    const rOpen = columnHit || this.expanded.has(rKey);
    const loaded = this.details.get(`${rootId}:${schema}.${r.name}`);
    const d = typeof loaded === 'object' ? loaded : null;
    const facts = d ? [d.estimatedRows !== null ? `≈ ${compact(d.estimatedRows)} rows` : '', d.totalBytes !== null ? bytes(d.totalBytes) : ''].filter(Boolean).join(' · ') : '';
    const row = this.row(rKey, depth, rOpen, [this.badge(KIND[r.kind].letter, KIND[r.kind].label), this.text(r.name), ...(facts ? [this.dim(facts)] : [])],
      [`${KIND[r.kind].label} ${schema}.${r.name}`, d?.comment].filter(Boolean).join('\n'));
    if (this.highlight.has(rKey)) row.classList.add('jumped');
    row.draggable = true;
    this.nodes.set(rKey, { kind: 'relation', root: rootId, schema, name: r.name, rel: r });
    rows.push(row);
    if (!rOpen) return;

    const detail = columnHit ? d : this.detailFor(rootId, schema, r.name);
    const det = typeof detail === 'object' ? detail : null;
    const group = (name: string, count: number, build: (depth: number) => HTMLElement[]) => {
      if (count === 0) return;
      const gKey = `${rKey}:${name}`;
      const open = (columnHit && name === 'Columns') || this.expanded.has(gKey);
      rows.push(this.row(gKey, depth + 1, open, [this.text(name), this.dim(`${count}`)], name));
      if (open) rows.push(...build(depth + 2).slice(0, MAX_IN_GROUP));
    };

    // Columns: as the fast load has them, with identity, generated and comment once the details are in.
    const cols = columnHit ? r.columns.filter((c) => c.name.toLowerCase().includes(this.filter)) : r.columns;
    group('Columns', cols.length, (dd) => cols.map((c) => this.columnRow(rootId, schema, r, c, det?.columns.find((x) => x.name === c.name) ?? null, dd)));
    if (columnHit) return;

    // Keys: the primary key and unique constraints.
    const keys = det?.keys ?? (r.primaryKey.length ? [{ name: 'primary key', primary: true, columns: r.primaryKey, definition: `PRIMARY KEY (${r.primaryKey.join(', ')})` }] : []);
    group('Keys', keys.length, (dd) => keys.map((k) =>
      this.item(rootId, `${rKey}:key:${k.name}`, dd, [this.badge(k.primary ? 'PK' : 'UQ', k.primary ? 'Primary key' : 'Unique'), this.text(k.name), this.dim(`(${k.columns.join(', ')})`)], k.name, k.definition)));

    // Foreign keys out: click one to go to the table it points at.
    const fks = det?.foreignKeys ?? r.foreignKeys.map((k) => ({ ...k, onDelete: null, onUpdate: null, deferrable: false, definition: '' }));
    group('Foreign keys', fks.length, (dd) => fks.map((k) => {
      const actions = [k.onDelete ? `on delete ${k.onDelete}` : '', k.onUpdate ? `on update ${k.onUpdate}` : '', k.deferrable ? 'deferrable' : ''].filter(Boolean).join(' · ');
      const target = `${k.referencedSchema === schema ? '' : k.referencedSchema + '.'}${k.referencedRelation}(${k.referencedColumns.join(', ')})`;
      return this.item(rootId, `${rKey}:fk:${k.name}`, dd, [this.text(k.columns.join(', ')), this.dim('→'), this.link(target), ...(actions ? [this.dim(actions)] : [])],
        k.name, k.definition || null, { schema: k.referencedSchema, table: k.referencedRelation, columns: k.referencedColumns },
        `${k.name}\n${k.definition}\nClick to go to ${target}`);
    }));

    // Referenced by: worked out from every table's foreign keys, already loaded.
    const refs = this.referencedBy(rootId, schema, r.name);
    group('Referenced by', refs.length, (dd) => refs.map((x) =>
      this.item(rootId, `${rKey}:ref:${x.schema}.${x.table}.${x.fk}`, dd,
        [this.link(`${x.schema === schema ? '' : x.schema + '.'}${x.table}`), this.dim(`(${x.columns.join(', ')})`)], x.fk, null,
        { schema: x.schema, table: x.table, columns: x.columns }, `${x.fk}\nClick to go to ${x.schema}.${x.table}(${x.columns.join(', ')})`)));

    // Indexes, checks, triggers, partitions, a view's definition: from the details.
    if (!det) {
      if (detail === 'loading') rows.push(this.note(depth + 1, 'loading details…'));
      return;
    }
    group('Indexes', det.indexes.length, (dd) => det.indexes.map((i) => this.item(rootId, `${rKey}:ix:${i.name}`, dd, [
      ...(i.primary ? [this.badge('PK', 'Backs the primary key')] : i.unique ? [this.badge('UQ', 'Unique')] : []),
      this.text(i.name), this.dim(`${i.method} (${i.keys})${i.predicate ? ` where ${i.predicate}` : ''}${i.bytes !== null ? ` · ${bytes(i.bytes)}` : ''}`),
    ], i.name, i.definition)));
    group('Checks', det.checks.length, (dd) => det.checks.map((c) => this.item(rootId, `${rKey}:ck:${c.name}`, dd, [
      ...(c.exclusion ? [this.badge('EX', 'Exclusion constraint')] : []), this.text(c.name), this.dim(c.definition.replace(/^CHECK /, '')),
    ], c.name, c.definition)));
    group('Triggers', det.triggers.length, (dd) => det.triggers.map((t) => this.item(rootId, `${rKey}:tg:${t.name}`, dd, [
      this.text(t.name), this.dim(`${t.timing} ${t.events.join(', ')} · ${t.function}()${t.enabled ? '' : ' · disabled'}`),
    ], t.name, t.definition)));
    group('Partitions', det.partitions.length, (dd) => det.partitions.map((p) => this.item(rootId, `${rKey}:pt:${p.schema}.${p.name}`, dd, [
      this.link(p.schema === schema ? p.name : `${p.schema}.${p.name}`), this.dim(p.bound.replace(/^FOR VALUES /, '')),
    ], p.name, p.bound, { schema: p.schema, table: p.name }, `${p.bound}\nClick to go to ${p.schema}.${p.name}`)));
    if (det.definition) {
      const def = det.definition;
      const key = `${rKey}:definition`;
      const row = this.row(key, depth + 1, null, [this.text('Definition'), this.dim(def.replace(/\s+/g, ' '))], def);
      this.nodes.set(key, { kind: 'item', root: rootId, name: r.name, definition: def });
      rows.push(row);
    }
  }

  private columnRow(rootId: string, schema: string, r: RelationInfo, c: RelationInfo['columns'][number], d: ColumnDetail | null, depth: number) {
    const pk = r.primaryKey.includes(c.name);
    const fk = r.foreignKeys.find((k) => k.columns.includes(c.name));
    const max = d?.maxLength ?? null;
    const parts: Node[] = [this.text(c.name), this.dim(c.dataType + (max !== null ? ` (max ${max})` : '') + (c.isNullable ? '' : ' not null'))];
    if (pk) parts.push(this.badge('PK', 'Primary key'));
    if (fk) parts.push(this.badge('FK', `Foreign key → ${fk.referencedSchema}.${fk.referencedRelation}(${fk.referencedColumns.join(', ')})`));
    if (d?.identity) parts.push(this.badge('ID', `Identity: generated ${d.identity}`));
    if (d?.generated) parts.push(this.badge('GEN', `Generated: ${d.generated}`));
    else if (c.default && !d?.identity) parts.push(this.dim(`= ${c.default.length > 40 ? c.default.slice(0, 40) + '…' : c.default}`));
    const key = `c:${rootId}:${schema}.${r.name}.${c.name}`;
    const tip = [max !== null ? `At most ${max} characters (check ${d?.lengthCheck})` : '', c.default ? `Default: ${c.default}` : '', d?.generated ? `Generated: ${d.generated}` : '', d?.comment ?? ''].filter(Boolean).join('\n');
    const row = this.row(key, depth, null, parts, tip);
    row.draggable = true;
    if (this.highlight.has(key)) row.classList.add('jumped');
    this.nodes.set(key, { kind: 'column', root: rootId, schema, table: r.name, name: c.name });
    return row;
  }

  /** A schema's functions, sequences and types, loaded the first time the schema is opened. */
  private objectRows(rootId: string, schema: string, rows: HTMLElement[]) {
    const o = this.objectsFor(rootId, schema);
    if (o === 'loading') { rows.push(this.note(2, 'loading functions, sequences and types…')); return; }
    if (o === 'missing') return;
    const group = (name: string, count: number, build: () => HTMLElement[]) => {
      if (count === 0) return;
      const gKey = `g:${rootId}:${schema}:${name}`;
      const open = this.expanded.has(gKey);
      rows.push(this.row(gKey, 2, open, [this.text(name), this.dim(`${count}`)], name));
      if (open) {
        const all = build();
        rows.push(...all.slice(0, MAX_IN_GROUP));
        if (all.length > MAX_IN_GROUP) rows.push(this.note(3, `… ${all.length - MAX_IN_GROUP} more.`));
      }
    };
    group('Functions', o.functions.length, () => o.functions.map((fn) => {
      const key = `f:${rootId}:${schema}.${fn.name}(${fn.arguments})`;
      const sig = `${fn.name}(${fn.arguments})`;
      const row = this.row(key, 3, null, [this.badge(fn.kind === 'procedure' ? 'PR' : fn.kind === 'aggregate' ? 'AG' : 'FN', fn.kind),
        this.text(fn.name), this.dim(`(${fn.arguments})${fn.returns ? ` → ${fn.returns}` : ''}`)], [sig, fn.returns ? `returns ${fn.returns}` : '', fn.comment].filter(Boolean).join('\n'));
      row.draggable = true;
      this.nodes.set(key, { kind: 'object', root: rootId, schema, name: fn.name, objectKind: 'function', arguments: fn.arguments });
      return row;
    }));
    group('Sequences', o.sequences.length, () => o.sequences.map((sq) => {
      const key = `q:${rootId}:${schema}.${sq.name}`;
      const row = this.row(key, 3, null, [this.badge('S', 'Sequence'), this.text(sq.name), this.dim(sq.dataType + (sq.ownedBy ? ` · for ${sq.ownedBy}` : ''))], `Sequence ${schema}.${sq.name}`);
      row.draggable = true;
      this.nodes.set(key, { kind: 'object', root: rootId, schema, name: sq.name, objectKind: 'sequence' });
      return row;
    }));
    group('Types', o.types.length, () => o.types.map((t) => {
      const key = `y:${rootId}:${schema}.${t.name}`;
      const shown = t.kind === 'enum' ? t.values.join(', ') : t.detail ?? '';
      const row = this.row(key, 3, null, [this.badge(t.kind === 'enum' ? 'E' : t.kind === 'domain' ? 'D' : t.kind === 'range' ? 'R' : 'C', t.kind), this.text(t.name), this.dim(shown)],
        [`${t.kind} ${schema}.${t.name}`, t.kind === 'enum' ? `values: ${t.values.join(', ')}` : t.detail, t.comment].filter(Boolean).join('\n'));
      row.draggable = true;
      this.nodes.set(key, { kind: 'object', root: rootId, schema, name: t.name, objectKind: 'type' });
      return row;
    }));
  }

  /** Every foreign key (in the root's whole schema) that points at a table. */
  private referencedBy(rootId: string, schema: string, table: string) {
    const out: { schema: string; table: string; columns: string[]; fk: string }[] = [];
    for (const s of this.roots.find((r) => r.id === rootId)?.schema?.schemas ?? [])
      for (const r of s.relations)
        for (const k of r.foreignKeys)
          if (k.referencedSchema === schema && k.referencedRelation === table) out.push({ schema: s.name, table: r.name, columns: k.columns, fk: k.name });
    return out;
  }

  /** A leaf with a name (for its menu and drag), its definition (tooltip, Copy definition), and maybe a table to jump to. */
  private item(rootId: string, key: string, depth: number, parts: Node[], name: string, definition: string | null,
    jump?: Jump, tip?: string) {
    const row = this.row(key, depth, null, parts, tip ?? definition ?? name);
    if (jump) row.classList.add('jump');
    row.draggable = true;
    this.nodes.set(key, { kind: 'item', root: rootId, name, definition, jump });
    return row;
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

  private note(depth: number, text: string) {
    const n = document.createElement('div');
    n.className = 'row dim';
    n.style.paddingLeft = `${8 + depth * 14 + 16}px`;
    n.textContent = text;
    return n;
  }

  private text(t: string) { const s = document.createElement('span'); s.textContent = t; return s; }
  private dim(t: string) { const s = this.text(t); s.className = 'dim'; return s; }
  private link(t: string) { const s = this.text(t); s.className = 'tlink'; return s; }
  private badge(t: string, title: string) { const s = this.text(t); s.className = 'badge'; s.title = title; return s; }
}

/** A table's groups, for "*" (open the whole table). */
const REL_GROUPS = ['Columns', 'Keys', 'Foreign keys', 'Referenced by', 'Indexes', 'Checks', 'Triggers', 'Partitions'];

/** 950, 12k, 1.2M: an estimate needs no more. */
function compact(n: number): string {
  if (n < 1000) return String(n);
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0).replace(/\.0$/, '')}k`;
  if (n < 1_000_000_000) return `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`;
  return `${(n / 1_000_000_000).toFixed(1).replace(/\.0$/, '')}B`;
}

function bytes(n: number): string {
  const units = ['bytes', 'kB', 'MB', 'GB', 'TB'];
  let v = n, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return u === 0 ? `${v} bytes` : `${v < 10 ? v.toFixed(1) : Math.round(v)} ${units[u]}`;
}
