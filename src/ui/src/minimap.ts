// The query mini-map (design section 14, F5): a small diagram of the statement the cursor is in, at the top right of
// the editor. One box per table or alias, lines for the conditions that join them, a red box for a table nothing joins to
// the rest, and a dashed line for a foreign key the statement does not use (clicking it writes the condition). The
// engine works all of this out (JoinMapper); this only lays it out and draws it.

/** What the engine sends with the sheet analysis (EngineHost "analyzeSheet", JoinMapper). Offsets are the editor's. */
export interface JoinMap {
  scopes: { id: number; parent: number; kind: 'statement' | 'cte' | 'subquery'; name: string | null; start: number; length: number }[];
  nodes: MapNode[];
  edges: MapEdge[];
  parses: boolean;
}

export interface MapNode {
  id: number;
  scope: number;
  label: string;
  table: string | null;
  kind: 'table' | 'view' | 'materializedView' | 'foreignTable' | 'cte' | 'derived' | 'function' | 'unknown';
  target: boolean;
  start: number;
  length: number;
  warning: string | null;
}

export interface MapEdge {
  from: number;
  to: number;
  kind: 'join' | 'where' | 'cross' | 'suggested';
  conditions: { fromColumn: string; toColumn: string; start: number; length: number }[];
  fix: { start: number; length: number; text: string; title: string } | null;
}

/** What the map needs from its editor. `version` is the text version the map was made for. */
export interface MiniMapHost {
  select(start: number, end: number, version: number): void;
  highlight(ranges: { start: number; length: number }[]): void;
  insert(version: number, at: number, text: string): void;
}

// ---- shown or not: one choice for every sheet, kept between runs -------------------------------------------------

const KEY = 'siftdb.miniMap';
let on = (() => { try { return localStorage.getItem(KEY) !== 'off'; } catch { return true; } })(); // on by default (design decision)
const maps = new Set<MiniMap>();

export const miniMapOn = () => on;

/** Ctrl+Shift+M, the palette, or the panel's own buttons: for every sheet at once. */
export function toggleMiniMap() {
  on = !on;
  try { localStorage.setItem(KEY, on ? 'on' : 'off'); } catch { /* private window or blocked storage: this run only */ }
  maps.forEach((m) => m.redraw());
}

// ---- layout ----------------------------------------------------------------------------------------------------

const SVG = 'http://www.w3.org/2000/svg';
const NODE_H = 22, GAP_X = 44, GAP_Y = 10, PAD = 8, GROUP_HEAD = 16, CHAR_W = 6.4;

interface Box { node: MapNode; x: number; y: number; w: number; h: number }
interface Group { scope: JoinMap['scopes'][number]; x: number; y: number; w: number; h: number }

/** The part of a table's name shown after its alias, when it says something more: "o · orders". */
function tableName(n: MapNode): string | null {
  const name = n.table?.split('.').pop() ?? null;
  return name && name.toLowerCase() !== n.label.toLowerCase().replace(/"/g, '') ? name : null;
}

/** A CTE's or subquery's box says what it is: "with big", "subquery t". */
const groupName = (s: JoinMap['scopes'][number]) => s.kind === 'cte' ? `with ${s.name ?? ''}` : s.name ? `subquery ${s.name}` : 'subquery';

/** Where a box's text starts: after its 12 px kind icon. */
const ICON_W = 22;

/**
 * Each kind's name (for its tooltip) and its 12 px line icon. Its colour is the CSS class's (`.mm-node.view` and so on, in
 * index.html): tables blue, views orange, CTEs and subqueries aqua, a function or an unknown name neutral.
 */
const KINDS: Record<MapNode['kind'], { name: string; icon: string[] }> = {
  // A grid: a header row and a column line.
  table: { name: 'Table', icon: ['M1 1.5h10v9H1z', 'M1 4.5h10', 'M4.5 4.5v6'] },
  // An eye: a view shows rows it does not keep.
  view: { name: 'View', icon: ['M.8 6C2.2 3.4 3.9 2.2 6 2.2s3.8 1.2 5.2 3.8C9.8 8.6 8.1 9.8 6 9.8S2.2 8.6.8 6z', 'M7.6 6a1.6 1.6 0 1 1-3.2 0a1.6 1.6 0 1 1 3.2 0z'] },
  // Stacked layers: a view whose rows are kept.
  materializedView: { name: 'Materialized view', icon: ['M6 1.2l5 2.6-5 2.6-5-2.6z', 'M1 6.2l5 2.6 5-2.6', 'M1 8.6l5 2.6 5-2.6'] },
  // A globe: rows that live on another server.
  foreignTable: { name: 'Foreign table', icon: ['M11 6a5 5 0 1 1-10 0a5 5 0 1 1 10 0z', 'M1 6h10', 'M6 1c1.6 1.4 2.2 3 2.2 5s-.6 3.6-2.2 5c-1.6-1.4-2.2-3-2.2-5s.6-3.6 2.2-5z'] },
  // Braces: a named query of this statement.
  cte: { name: 'CTE', icon: ['M4.4 1.2c-1.2 0-1.6.6-1.6 1.6v1.6c0 .8-.4 1.6-1.3 1.6.9 0 1.3.8 1.3 1.6v1.6c0 1 .4 1.6 1.6 1.6', 'M7.6 1.2c1.2 0 1.6.6 1.6 1.6v1.6c0 .8.4 1.6 1.3 1.6-.9 0-1.3.8-1.3 1.6v1.6c0 1-.4 1.6-1.6 1.6'] },
  // Parentheses: a subquery in FROM.
  derived: { name: 'Subquery', icon: ['M4 1.2C2.4 2.5 1.7 4.1 1.7 6s.7 3.5 2.3 4.8', 'M8 1.2c1.6 1.3 2.3 2.9 2.3 4.8s-.7 3.5-2.3 4.8'] },
  // An italic f: a function in FROM.
  function: { name: 'Function', icon: ['M9.2 1.6C7.6 1 6.4 1.6 6 3.4L4.8 8.8c-.4 1.6-1.4 2-2.8 1.6', 'M3.4 4.8h5'] },
  // A question mark: a name not in the schema.
  unknown: { name: 'Not in the schema', icon: ['M3.9 4.1a2.1 2.1 0 1 1 3 1.9c-.6.3-.9.8-.9 1.4v.5', 'M6 9.8v.3'] },
};

const nodeWidth = (n: MapNode) => {
  const sub = tableName(n);
  return Math.round(ICON_W + 8 + (n.warning || n.target ? 12 : 0) + CHAR_W * (n.label.length + (sub ? sub.length + 3 : 0)));
};

/**
 * A layered left-to-right layout, per scope: in each group of joined tables, a table's column is how many joins it is from
 * the group's first table (in the order written). Groups of a scope are stacked; the statement's own scope comes first,
 * then each CTE and subquery in a box of its own below it. Small graphs (up to about 15 tables) need nothing smarter.
 */
export function layout(map: JoinMap): { boxes: Map<number, Box>; groups: Group[]; width: number; height: number } {
  const boxes = new Map<number, Box>();
  const groups: Group[] = [];
  let y = PAD, width = 0;
  for (const scope of map.scopes) {
    const nodes = map.nodes.filter((n) => n.scope === scope.id);
    if (!nodes.length) continue;
    const inScope = new Set(nodes.map((n) => n.id));
    const next = new Map<number, number[]>(nodes.map((n) => [n.id, []]));
    for (const e of map.edges) {
      if (!inScope.has(e.from) || !inScope.has(e.to)) continue;
      next.get(e.from)!.push(e.to);
      next.get(e.to)!.push(e.from);
    }
    const boxed = scope.id !== 0;
    const top = y + (boxed ? GROUP_HEAD : 0);
    const left = PAD + (boxed ? PAD : 0);
    let rowY = top + (boxed ? 4 : 0);
    let scopeRight = left;
    const placed = new Set<number>();
    for (const first of nodes) {
      if (placed.has(first.id)) continue;
      // Breadth first from the first table of this group: its column is its distance.
      const column = new Map<number, number>([[first.id, 0]]);
      const queue = [first.id];
      placed.add(first.id);
      while (queue.length) {
        const id = queue.shift()!;
        for (const to of next.get(id)!) {
          if (placed.has(to)) continue;
          placed.add(to);
          column.set(to, column.get(id)! + 1);
          queue.push(to);
        }
      }
      const columns: MapNode[][] = [];
      for (const n of nodes) if (column.has(n.id)) (columns[column.get(n.id)!] ??= []).push(n);
      let x = left, tallest = 0;
      for (const col of columns) {
        const w = Math.max(...col.map(nodeWidth));
        col.forEach((n, i) => boxes.set(n.id, { node: n, x, y: rowY + i * (NODE_H + GAP_Y), w: nodeWidth(n), h: NODE_H }));
        tallest = Math.max(tallest, col.length);
        x += w + GAP_X;
      }
      scopeRight = Math.max(scopeRight, x - GAP_X);
      rowY += tallest * (NODE_H + GAP_Y);
    }
    const bottom = rowY - GAP_Y + (boxed ? PAD : 0);
    // A box's right edge is PAD past its tables, and never narrower than its own label.
    if (boxed) scopeRight = Math.max(scopeRight, 12 + CHAR_W * groupName(scope).length);
    if (boxed) groups.push({ scope, x: PAD, y, w: scopeRight, h: bottom - y });
    width = Math.max(width, scopeRight + (boxed ? PAD : 0));
    y = bottom + GAP_Y + 2;
  }
  return { boxes, groups, width: width + PAD, height: y - GAP_Y - 2 + PAD };
}

// ---- drawing ---------------------------------------------------------------------------------------------------

function el<K extends keyof SVGElementTagNameMap>(name: K, attrs: Record<string, string | number>, ...children: (SVGElement | string)[]) {
  const e = document.createElementNS(SVG, name);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, String(v));
  for (const c of children) e.append(c);
  return e;
}

const title = (text: string) => el('title', {}, text);

/** What an edge says: its columns ("customer_id = id"), "×" for a product asked for, "+1" when there are more. */
function edgeLabel(e: MapEdge): string {
  if (e.kind === 'cross') return '×';
  const c = e.conditions[0];
  if (!c) return '';
  const one = c.fromColumn === c.toColumn ? c.fromColumn : `${c.fromColumn} = ${c.toColumn}`;
  const text = e.conditions.length > 1 ? `${one} +${e.conditions.length - 1}` : one;
  return text.length > 26 ? `${text.slice(0, 25)}…` : text;
}

function edgeTitle(e: MapEdge, map: JoinMap): string {
  const a = map.nodes[e.from].label, b = map.nodes[e.to].label;
  const conditions = e.conditions.map((c) => `${a}.${c.fromColumn} = ${b}.${c.toColumn}`).join('\nand ');
  switch (e.kind) {
    case 'cross': return `${a} × ${b}: every row with every row, asked for`;
    case 'suggested': return `Not joined. A foreign key links them:\n${conditions}${e.fix ? `\n\nClick to add it (${e.fix.title.toLowerCase()}).` : ''}`;
    case 'where': return `Joined in WHERE:\n${conditions}`;
    default: return `Joined:\n${conditions}`;
  }
}

/** From one box to another: side to side when they are in different columns, a loop on the right when in the same one. */
function edgePath(a: Box, b: Box): { d: string; mx: number; my: number } {
  const ay = a.y + a.h / 2, by = b.y + b.h / 2;
  if (Math.abs(a.x - b.x) < 1) {
    const x = Math.max(a.x + a.w, b.x + b.w), bulge = 18;
    return { d: `M${a.x + a.w},${ay} C${x + bulge},${ay} ${x + bulge},${by} ${b.x + b.w},${by}`, mx: x + bulge * 0.75, my: (ay + by) / 2 };
  }
  const [l, r, ly, ry] = a.x < b.x ? [a, b, ay, by] : [b, a, by, ay];
  const x1 = l.x + l.w, x2 = r.x, dx = (x2 - x1) / 2;
  return { d: `M${x1},${ly} C${x1 + dx},${ly} ${x2 - dx},${ry} ${x2},${ry}`, mx: (x1 + x2) / 2, my: (ly + ry) / 2 };
}

/**
 * One sheet's map, in a corner of its editor. It follows every analysis; while the statement does not parse it keeps the
 * last good map of that statement, greyed and not clickable (its offsets are from before the edit).
 */
export class MiniMap {
  private readonly panel = document.createElement('div');
  private shown: { map: JoinMap; version: number; stale: boolean } | null = null;
  private lastGood: { map: JoinMap; version: number; statement: number } | null = null;

  constructor(parent: HTMLElement, private host: MiniMapHost) {
    this.panel.className = 'minimap';
    this.panel.hidden = true;
    parent.append(this.panel);
    maps.add(this);
  }

  dispose() {
    maps.delete(this);
    this.panel.remove();
  }

  /** A new analysis. `statement`: where the statement it is for starts, to tell a different statement from an edited one. */
  update(map: JoinMap | null, statement: number, version: number) {
    if (!map) {
      this.shown = null;
      this.lastGood = null;
    } else if (map.parses) {
      this.shown = { map, version, stale: false };
      this.lastGood = { map, version, statement };
    } else if (this.lastGood?.statement === statement) {
      this.shown = { map: this.lastGood.map, version: this.lastGood.version, stale: true };
    } else {
      // Half-typed from the start: what can be read so far, greyed, but its offsets are current.
      this.shown = { map, version, stale: false };
    }
    this.redraw();
  }

  redraw() {
    this.host.highlight([]);
    const s = this.shown;
    this.panel.hidden = !s;
    if (!s) return;
    const { map } = s;
    const warned = map.nodes.filter((n) => n.warning).length;
    const tables = `${map.nodes.length} tables`;
    this.panel.classList.toggle('greyed', !map.parses || s.stale);
    this.panel.classList.toggle('collapsed', !on);

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'minimap-toggle';
    toggle.onclick = () => toggleMiniMap();
    if (!on) {
      // Hidden: a small chip, which still says when a table is not joined.
      toggle.textContent = warned ? `⚠ ${tables}` : tables;
      toggle.title = `Show the query map (Ctrl+Shift+M)${warned ? `\n${warned === 1 ? 'A table is' : `${warned} tables are`} not joined to the rest.` : ''}`;
      if (warned) toggle.classList.add('warn');
      this.panel.replaceChildren(toggle);
      return;
    }
    toggle.textContent = '–';
    toggle.title = 'Hide the query map (Ctrl+Shift+M)';
    toggle.setAttribute('aria-label', 'Hide the query map');
    const head = document.createElement('div');
    head.className = 'minimap-head';
    const caption = document.createElement('span');
    caption.textContent = !map.parses || s.stale ? `${tables} · as last read` : tables;
    if (warned) {
      const w = document.createElement('span');
      w.className = 'warn';
      w.textContent = ` · ⚠ ${warned} not joined`;
      caption.append(w);
    }
    head.append(caption, toggle);
    this.panel.replaceChildren(head, this.draw(s.map, s.version, !s.stale));
  }

  private draw(map: JoinMap, version: number, live: boolean): SVGSVGElement {
    const { boxes, groups, width, height } = layout(map);
    const svg = el('svg', { viewBox: `0 0 ${width} ${height}`, width, height, role: 'group', 'aria-label': 'Tables of this statement and how they are joined' });

    for (const g of groups) {
      svg.append(el('rect', { class: 'mm-group', x: g.x, y: g.y, width: g.w, height: g.h, rx: 4 }),
        el('text', { class: 'mm-group-label', x: g.x + 6, y: g.y + 11 }, groupName(g.scope)));
    }

    // Lines first, so the boxes sit on top of them.
    for (const e of map.edges) {
      const a = boxes.get(e.from), b = boxes.get(e.to);
      if (!a || !b) continue;
      const { d, mx, my } = edgePath(a, b);
      const g = el('g', { class: `mm-edge ${e.kind}` }, title(edgeTitle(e, map)));
      const label = edgeLabel(e);
      g.append(el('path', { class: 'mm-line', d }), el('path', { class: 'mm-hit', d }));
      if (label) g.append(el('text', { class: 'mm-edge-label', x: mx, y: my - 3, 'text-anchor': 'middle' }, label));
      if (live) {
        const ranges = e.conditions.filter((c) => c.length > 0);
        g.addEventListener('pointerenter', () => this.host.highlight(ranges));
        g.addEventListener('pointerleave', () => this.host.highlight([]));
        if (e.kind === 'suggested' && e.fix) {
          const fix = e.fix;
          const apply = () => this.host.insert(version, fix.start, fix.text);
          g.classList.add('clickable');
          g.setAttribute('tabindex', '0');
          g.setAttribute('role', 'button');
          g.setAttribute('aria-label', fix.title);
          g.addEventListener('click', apply);
          g.addEventListener('keydown', (k) => { if (k.key === 'Enter' || k.key === ' ') { k.preventDefault(); apply(); } });
        }
      }
      svg.append(g);
    }

    for (const box of boxes.values()) {
      const n = box.node;
      const sub = tableName(n);
      const classes = ['mm-node', n.kind, n.warning ? 'warn' : '', n.target ? 'target' : ''].filter(Boolean).join(' ');
      const kind = KINDS[n.kind];
      const describe = [
        `${n.label}: ${n.table ? `${kind.name.toLowerCase()} ${n.table}` : kind.name.toLowerCase()}`,
        n.target ? 'The table this statement changes.' : '',
        n.warning ?? '',
        live ? 'Click to select it in the editor.' : '',
      ].filter(Boolean).join('\n');
      // The kind's icon first, in the kind's colour: identity is never the colour alone.
      const icon = el('g', { class: 'mm-icon', transform: `translate(${box.x + 6} ${box.y + 5})` });
      icon.append(...kind.icon.map((d) => el('path', { d })));
      const text = el('text', { x: box.x + ICON_W, y: box.y + 15 });
      if (n.warning || n.target) text.append(el('tspan', { class: 'mm-mark' }, n.warning ? '⚠ ' : '✎ '));
      text.append(el('tspan', {}, n.label));
      if (sub) text.append(el('tspan', { class: 'mm-sub' }, ` · ${sub}`));
      const g = el('g', { class: classes }, title(describe),
        el('rect', { x: box.x, y: box.y, width: box.w, height: box.h, rx: 3 }), icon, text);
      if (live) {
        g.setAttribute('tabindex', '0');
        g.setAttribute('role', 'button');
        g.setAttribute('aria-label', describe.replace(/\n/g, '. '));
        const select = () => this.host.select(n.start, n.start + n.length, version);
        g.addEventListener('click', select);
        g.addEventListener('keydown', (k) => { if (k.key === 'Enter' || k.key === ' ') { k.preventDefault(); select(); } });
        g.addEventListener('pointerenter', () => this.host.highlight([{ start: n.start, length: n.length }]));
        g.addEventListener('pointerleave', () => this.host.highlight([]));
        g.addEventListener('focus', () => this.host.highlight([{ start: n.start, length: n.length }]));
        g.addEventListener('blur', () => this.host.highlight([]));
      }
      svg.append(g);
    }
    return svg;
  }
}
