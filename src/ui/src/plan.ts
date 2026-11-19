import type { Bridge } from './bridge';
import { writeClipboard } from './copy';
import type { RunTarget, TrackedRanges } from './editor';
import { spinner } from './icons';
import { ResultState, ResultTab, parseError, sqlLabel } from './results';

// Visual EXPLAIN (design section 14, F6): a statement's plan as an icicle graph. Each step is a box as wide as its total
// time (EXPLAIN ANALYZE) or cost (estimates), its children below it; its colour is the time or cost spent in the step
// itself, so the step that costs the most stands out. Clicking a step selects its table in the editor, and pointing at a
// table in the editor marks its steps. The engine works out sizes, advice and links; this only draws them.

interface Advice { kind: 'estimate' | 'wholeTable' | 'spill' | 'loops'; message: string }

/** A plan step, as the engine reports it (PlanReportNode). */
interface PlanNode {
  id: number;
  operation: string;
  label: string;
  /** Width: ms over every loop (analysed) or total cost (estimates), children included. */
  value: number;
  /** The part of `value` spent in this step itself. */
  self: number;
  ms: number | null;
  cost: number;
  estimatedRows: number;
  actualRows: number | null;
  loops: number | null;
  advice: Advice[];
  /** Where its table is written in the editor's text, when the engine could tell. */
  sql: { start: number; length: number }[];
  properties: { name: string; value: string }[];
  children: PlanNode[];
}

interface Plan {
  analyzed: boolean;
  measure: 'time' | 'cost';
  planningMs: number | null;
  executionMs: number | null;
  changedData: boolean;
  text: string;
  root: PlanNode;
}

/** What a plan tab needs from its sheet. */
export interface PlanContext {
  bridge: Bridge;
  onFinished(view: PlanView): void;
  /** Follows these ranges of the editor, or null when the statement there is no longer the one explained. */
  track(ranges: { start: number; length: number }[], source: string, sourceStart: number): TrackedRanges | null;
  select(start: number, end: number): void;
}

const ROW = 24;   // one level of the graph
const GAP = 2;    // the surface gap between boxes
const MIN_TEXT = 34;

/** One hue, light to dark (the default sequential ramp): the more time or cost a step spends itself, the darker. */
const RAMP = ['#cde2fb', '#b7d3f6', '#9ec5f4', '#86b6ef', '#6da7ec', '#5598e7', '#3987e5', '#2a78d6', '#256abf', '#1c5cab', '#184f95', '#104281', '#0d366b'];

const INK_DARK = '#1f2328';
const INK_LIGHT = '#ffffff';

/** sRGB relative luminance, for choosing the label ink that reads on a box. */
function luminance(hex: string) {
  const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}
const contrast = (a: string, b: string) => {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
};
const inkFor = (fill: string) => (contrast(fill, INK_DARK) >= contrast(fill, INK_LIGHT) ? INK_DARK : INK_LIGHT);

export function formatMs(ms: number): string {
  if (ms < 1) return `${ms.toFixed(3)} ms`;
  if (ms < 1000) return `${ms.toFixed(ms < 10 ? 2 : 1)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}
const formatCost = (c: number) => c.toLocaleString(undefined, { maximumFractionDigits: 1 });
const formatRows = (n: number) => Math.round(n).toLocaleString();

interface Box { node: PlanNode; x0: number; x1: number; depth: number; parent: Box | null; kids: Box[] }

export class PlanView implements ResultTab {
  readonly panel = document.createElement('div');
  label = '';
  pinned = false;
  state: ResultState = 'idle';
  stale = false;
  errorHeadline = '';
  readonly tabClass = 'plan';

  private plan: Plan | null = null;
  private source: RunTarget & { kind: 'run' } | null = null;
  private analyze = false;
  private connectionId = 0;
  private errorExtra = '';
  private started = 0;
  private dark = false;

  private readonly statusEl = document.createElement('div');
  private readonly iconEl = document.createElement('span');
  private readonly staleEl = document.createElement('span');
  private readonly headlineEl = document.createElement('span');
  private readonly extraEl = document.createElement('span');
  private readonly ctlEl = document.createElement('span');
  private readonly graphBtn = document.createElement('button');
  private readonly stepsBtn = document.createElement('button');
  private readonly copyBtn = document.createElement('button');
  private readonly bodyEl = document.createElement('div');
  private readonly graphEl = document.createElement('div');
  private readonly canvas = document.createElement('canvas');
  private readonly tipEl = document.createElement('div');
  private readonly stepsEl = document.createElement('div');
  private readonly detailEl = document.createElement('aside');

  private boxes: Box[] = [];
  private byId = new Map<number, Box>();
  private selected: Box | null = null;
  private hovered: Box | null = null;
  /** Steps marked because their table is under the mouse in the editor. */
  private hot = new Set<number>();
  private links: TrackedRanges | null = null;
  /** Each step's ranges in `links`, by step id. */
  private linkIndex = new Map<number, number[]>();
  private maxSelf = 0;
  private showing: 'graph' | 'steps' = 'graph';
  private readonly resize = new ResizeObserver(() => this.draw());

  constructor(private ctx: PlanContext, dark: boolean) {
    this.dark = dark;
    this.panel.className = 'resultpanel planpanel';

    this.statusEl.className = 'status plan';
    const main = document.createElement('div');
    main.className = 'statusMain';
    this.iconEl.className = 'statusIcon';
    this.iconEl.setAttribute('aria-hidden', 'true');
    const text = document.createElement('div');
    text.className = 'statusText';
    this.headlineEl.className = 'headline';
    this.extraEl.className = 'extra';
    this.staleEl.className = 'stalechip';
    this.staleEl.textContent = 'Earlier run';
    this.staleEl.title = 'This plan is from an earlier run. The last attempt on this sheet did not run (see the message above).';
    this.staleEl.hidden = true;
    text.append(this.staleEl, this.headlineEl, this.extraEl);

    this.ctlEl.className = 'planctl';
    const modes = document.createElement('span');
    modes.className = 'modes';
    modes.setAttribute('role', 'group');
    modes.setAttribute('aria-label', 'Show the plan as');
    this.graphBtn.textContent = 'Graph';
    this.graphBtn.title = 'The plan as a graph: width is time or cost, colour is what the step spends itself';
    this.stepsBtn.textContent = 'Steps';
    this.stepsBtn.title = 'The plan as a table of steps, with every number';
    this.graphBtn.onclick = () => this.show('graph');
    this.stepsBtn.onclick = () => this.show('steps');
    modes.append(this.graphBtn, this.stepsBtn);
    this.copyBtn.textContent = 'Copy as text';
    this.copyBtn.title = 'Copy the plan in the usual EXPLAIN text form, for pasting elsewhere';
    this.copyBtn.onclick = async () => {
      if (!this.plan) return;
      const ok = await writeClipboard(this.plan.text);
      this.copyBtn.textContent = ok ? 'Copied' : 'Could not copy';
      setTimeout(() => { this.copyBtn.textContent = 'Copy as text'; }, 1500);
    };
    this.ctlEl.append(modes, this.copyBtn);
    main.append(this.iconEl, text, this.ctlEl);
    this.statusEl.append(main);

    this.bodyEl.className = 'planbody';
    this.graphEl.className = 'plangraph';
    this.canvas.tabIndex = 0;
    this.canvas.setAttribute('role', 'img');
    this.tipEl.className = 'plantip';
    this.tipEl.hidden = true;
    this.graphEl.append(this.canvas, this.tipEl);
    this.stepsEl.className = 'plansteps';
    this.stepsEl.hidden = true;
    this.detailEl.className = 'plandetail';
    this.detailEl.setAttribute('aria-live', 'polite');
    const mainArea = document.createElement('div');
    mainArea.className = 'planmain';
    mainArea.append(this.graphEl, this.stepsEl);
    this.bodyEl.append(mainArea, this.detailEl);
    this.panel.append(this.statusEl, this.bodyEl);

    this.canvas.onpointermove = (e) => this.hover(this.boxAt(e.offsetX, e.offsetY), e);
    this.canvas.onpointerleave = () => this.hover(null);
    this.canvas.onclick = (e) => {
      const b = this.boxAt(e.offsetX, e.offsetY);
      if (!b) return;
      this.select(b);
      this.revealSql(b);
    };
    this.canvas.onkeydown = (e) => this.key(e);
    this.resize.observe(this.graphEl);
    this.refreshStats();
  }

  // ---- ResultTab ---------------------------------------------------------------------------------------------------

  shown() { this.draw(); }
  restyle() { /* plans have no result styling */ }
  setTheme(dark: boolean) { this.dark = dark; this.draw(); }
  markStale() { if (this.state !== 'idle') { this.stale = true; this.refreshStats(); } }

  cancel() {
    if (this.state === 'running' && this.connectionId) this.ctx.bridge.send({ type: 'cancel', connectionId: this.connectionId });
  }

  dispose() {
    this.cancel();
    this.resize.disconnect();
    this.links?.dispose();
    this.panel.remove();
  }

  // ---- running -----------------------------------------------------------------------------------------------------

  async run(connectionId: number, target: RunTarget & { kind: 'run' }, analyze: boolean) {
    this.links?.dispose();
    this.links = null;
    this.linkIndex.clear();
    this.plan = null;
    this.selected = this.hovered = null;
    this.hot.clear();
    this.source = target;
    this.analyze = analyze;
    this.connectionId = connectionId;
    this.label = `${analyze ? 'Analyze' : 'Plan'}: ${sqlLabel(target.sql)}`;
    this.panel.title = target.sql;
    this.stale = false;
    this.state = 'running';
    this.errorHeadline = this.errorExtra = '';
    this.started = performance.now();
    this.render();

    const r = await this.ctx.bridge.request({
      type: 'explain', connectionId, sql: target.sql, analyze, source: target.source, sourceStart: target.sourceStart,
    });
    if (r.type === 'cancelled') this.state = 'cancelled';
    else if (r.type === 'error') {
      const e = parseError(r.message);
      this.errorHeadline = e.headline;
      this.errorExtra = e.extra;
      this.state = 'error';
    } else {
      this.plan = r.plan as Plan;
      this.state = 'complete';
      this.layout();
      this.link();
      // Open on the step that spends the most itself: the one worth looking at first.
      this.select(this.boxes.reduce<Box | null>((best, b) => (!best || b.node.self > best.node.self ? b : best), null));
    }
    this.render();
    this.ctx.onFinished(this);
  }

  private link() {
    const s = this.source;
    if (!this.plan || !s) return;
    const ranges: { start: number; length: number }[] = [];
    for (const b of this.boxes) {
      const idx = b.node.sql.map((r) => ranges.push(r) - 1);
      if (idx.length) this.linkIndex.set(b.node.id, idx);
    }
    if (ranges.length) this.links = this.ctx.track(ranges, s.source, s.sourceStart);
  }

  /** The editor's mouse moved: mark the steps whose table is under it. */
  hoverSql(offset: number | null) {
    if (!this.links || this.panel.hidden) return;
    const at = new Set(offset === null ? [] : this.links.at(offset));
    const ids = new Set([...this.linkIndex].filter(([, idx]) => idx.some((i) => at.has(i))).map(([id]) => id));
    if (ids.size === this.hot.size && [...ids].every((id) => this.hot.has(id))) return;
    this.hot = ids;
    this.links.highlight([...at]);
    this.draw();
  }

  private revealSql(b: Box) {
    const idx = this.linkIndex.get(b.node.id);
    const r = idx && this.links?.rangeOf(idx[0]);
    if (r) this.ctx.select(r.start, r.end);
  }

  // ---- the status bar ----------------------------------------------------------------------------------------------

  private describe(): { headline: string; extra: string } {
    const p = this.plan;
    switch (this.state) {
      case 'running':
        return {
          headline: this.analyze ? 'Running it and timing each step…' : 'Asking for the plan…',
          extra: formatMs(performance.now() - this.started),
        };
      case 'cancelled': return { headline: 'Cancelled', extra: '' };
      case 'error': return { headline: this.errorHeadline, extra: this.errorExtra };
      case 'complete': {
        if (!p) return { headline: '', extra: '' };
        const notes: string[] = [];
        const advice = this.boxes.reduce((n, b) => n + b.node.advice.length, 0);
        if (advice) notes.push(`⚠ ${advice === 1 ? '1 thing' : `${advice} things`} to look at`);
        if (p.analyzed) {
          if (p.changedData) notes.push('it changed data: rolled back, nothing kept');
          if (p.planningMs !== null) notes.push(`planning ${formatMs(p.planningMs)}`);
          return { headline: `Ran in ${formatMs(p.executionMs ?? p.root.value)}`, extra: notes.join(' · ') };
        }
        notes.unshift('estimates only, nothing ran');
        return { headline: `Estimated cost ${formatCost(p.root.cost)}`, extra: notes.join(' · ') };
      }
      default: return { headline: '', extra: '' };
    }
  }

  refreshStats() {
    const { headline, extra } = this.describe();
    this.statusEl.dataset.state = this.state;
    this.statusEl.hidden = this.state === 'idle';
    this.statusEl.classList.toggle('stale', this.stale);
    this.staleEl.hidden = !this.stale;
    this.statusEl.setAttribute('role', this.state === 'error' && !this.stale ? 'alert' : 'status');
    if (this.state === 'running') { if (!this.iconEl.querySelector('.spinner')) this.iconEl.replaceChildren(spinner()); }
    else this.iconEl.textContent = this.state === 'complete' ? '✓' : this.state === 'cancelled' ? '⊘' : this.state === 'error' ? '✖' : '';
    if (this.headlineEl.textContent !== headline) this.headlineEl.textContent = headline;
    if (this.extraEl.textContent !== extra) this.extraEl.textContent = extra;
    this.ctlEl.hidden = this.state !== 'complete';
    this.graphBtn.setAttribute('aria-pressed', String(this.showing === 'graph'));
    this.stepsBtn.setAttribute('aria-pressed', String(this.showing === 'steps'));
  }

  private render() {
    this.refreshStats();
    this.bodyEl.hidden = !this.plan;
    this.renderSteps();
    this.renderDetail();
    this.draw();
  }

  private show(which: 'graph' | 'steps') {
    this.showing = which;
    this.graphEl.hidden = which !== 'graph';
    this.stepsEl.hidden = which !== 'steps';
    this.refreshStats();
    if (which === 'graph') this.draw();
  }

  // ---- layout and drawing ------------------------------------------------------------------------------------------

  /** Boxes in units of the root's width (0..1); `draw` scales them to pixels. */
  private layout() {
    this.boxes = [];
    this.byId.clear();
    this.maxSelf = 0;
    const place = (node: PlanNode, x0: number, x1: number, depth: number, parent: Box | null): Box => {
      const box: Box = { node, x0, x1, depth, parent, kids: [] };
      this.boxes.push(box);
      this.byId.set(node.id, box);
      this.maxSelf = Math.max(this.maxSelf, node.self);
      // Children share their parent's width by value. If they add up to more (parallel workers each report their own
      // time), they are scaled to fit, so a child is never wider than its parent.
      const total = node.children.reduce((s, c) => s + c.value, 0);
      const scale = (x1 - x0) / Math.max(node.value, total, Number.MIN_VALUE);
      let x = x0;
      for (const c of node.children) {
        const w = c.value * scale;
        box.kids.push(place(c, x, x + w, depth + 1, box));
        x += w;
      }
      return box;
    };
    if (this.plan) place(this.plan.root, 0, 1, 0, null);
    const depth = Math.max(0, ...this.boxes.map((b) => b.depth)) + 1;
    this.canvas.setAttribute('aria-label',
      `Plan graph, ${this.boxes.length} steps, ${depth} levels deep. Arrow keys move between steps, Enter selects the step's table in the editor. The Steps view lists every step as a table.`);
  }

  private fill(node: PlanNode) {
    const t = this.maxSelf > 0 ? node.self / this.maxSelf : 0;
    const i = Math.round(t * (RAMP.length - 1));
    // Dark mode: the anchor flips, so the step that costs the most is the brightest against the dark surface.
    return RAMP[this.dark ? RAMP.length - 1 - i : i];
  }

  private width() { return this.graphEl.clientWidth; }

  private rect(b: Box) {
    const w = this.width();
    return { x: b.x0 * w + GAP / 2, y: b.depth * ROW, w: (b.x1 - b.x0) * w - GAP, h: ROW - GAP };
  }

  private draw() {
    if (!this.plan || this.panel.hidden || this.graphEl.hidden) return;
    const w = this.width();
    if (w <= 0) return;
    const depth = Math.max(...this.boxes.map((b) => b.depth)) + 1;
    const h = depth * ROW;
    const dpr = window.devicePixelRatio || 1;
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(h * dpr);
    this.canvas.style.width = `${w}px`;
    this.canvas.style.height = `${h}px`;
    const g = this.canvas.getContext('2d')!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const css = getComputedStyle(this.panel);
    const fg = css.getPropertyValue('--fg').trim() || INK_DARK;
    g.font = '12px "Segoe UI", sans-serif';
    g.textBaseline = 'middle';

    for (const b of this.boxes) {
      const r = this.rect(b);
      if (r.w < 1) continue;
      const fill = this.fill(b.node);
      g.fillStyle = fill;
      g.beginPath();
      g.roundRect(r.x, r.y, r.w, r.h, Math.min(4, r.w / 2));
      g.fill();
      // Selected: a solid ring. Pointed at (here, or its table in the editor): dashed. Never a status colour: amber means a warning.
      const selected = b === this.selected;
      if (selected || b === this.hovered || this.hot.has(b.node.id)) {
        g.lineWidth = 2;
        g.strokeStyle = fg;
        g.setLineDash(selected ? [] : [4, 3]);
        g.beginPath();
        g.roundRect(r.x + 1, r.y + 1, Math.max(0, r.w - 2), r.h - 2, Math.min(3, r.w / 2));
        g.stroke();
        g.setLineDash([]);
      }
      const warned = b.node.advice.length > 0;
      if (r.w < (warned ? 16 : MIN_TEXT)) continue;
      g.save();
      g.beginPath();
      g.rect(r.x + 4, r.y, r.w - 8, r.h);
      g.clip();
      g.fillStyle = inkFor(fill);
      const text = (warned ? '⚠ ' : '') + (r.w >= MIN_TEXT ? b.node.label : '');
      g.fillText(this.fit(g, text, r.w - 8), r.x + 5, r.y + r.h / 2);
      g.restore();
    }
  }

  /** The text cut to fit `room` pixels, ending in "…". */
  private fit(g: CanvasRenderingContext2D, text: string, room: number) {
    if (g.measureText(text).width <= room) return text;
    let lo = 0, hi = text.length;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (g.measureText(text.slice(0, mid) + '…').width <= room) lo = mid; else hi = mid - 1;
    }
    return lo > 0 ? text.slice(0, lo) + '…' : '';
  }

  /** The box under a point; the gap around a box counts as the box, so thin ones can still be hit. */
  private boxAt(x: number, y: number): Box | null {
    const depth = Math.floor(y / ROW);
    const w = this.width();
    return this.boxes.find((b) => b.depth === depth && x >= b.x0 * w - 1 && x < b.x1 * w + 1 && (b.x1 - b.x0) * w > 0) ?? null;
  }

  // ---- hover, selection, keys --------------------------------------------------------------------------------------

  private hover(b: Box | null, e?: PointerEvent) {
    if (b !== this.hovered) {
      this.hovered = b;
      // Pointing at a step marks its table in the editor.
      if (this.links) this.links.highlight(b ? this.linkIndex.get(b.node.id) ?? [] : []);
      this.canvas.style.cursor = b ? 'pointer' : '';
      this.draw();
    }
    if (!b || !e) { this.tipEl.hidden = true; return; }
    this.tipEl.replaceChildren(...this.summary(b.node, true));
    this.tipEl.hidden = false;
    const room = this.graphEl.clientWidth;
    const left = Math.min(e.offsetX + 14, room - this.tipEl.offsetWidth - 4);
    this.tipEl.style.left = `${Math.max(4, left)}px`;
    this.tipEl.style.top = `${e.offsetY + 18}px`;
  }

  private select(b: Box | null) {
    this.selected = b;
    this.renderDetail();
    this.stepsEl.querySelectorAll('tr[data-id]').forEach((tr) => tr.setAttribute('aria-selected', String((tr as HTMLElement).dataset.id === String(b?.node.id))));
    this.draw();
  }

  private key(e: KeyboardEvent) {
    const b = this.selected ?? this.boxes[0];
    if (!b) return;
    const siblings = b.parent?.kids ?? [b];
    const at = siblings.indexOf(b);
    let next: Box | null | undefined = null;
    if (e.key === 'ArrowUp') next = b.parent;
    else if (e.key === 'ArrowDown') next = b.kids[0];
    else if (e.key === 'ArrowLeft') next = siblings[at - 1];
    else if (e.key === 'ArrowRight') next = siblings[at + 1];
    else if (e.key === 'Enter') { e.preventDefault(); this.revealSql(b); return; }
    else return;
    e.preventDefault();
    if (next) this.select(next);
  }

  // ---- details and the steps table ---------------------------------------------------------------------------------

  /** A step's numbers: the value first, then what it is and the rest. `brief` for the tooltip. */
  private summary(n: PlanNode, brief: boolean): HTMLElement[] {
    const p = this.plan!;
    const share = p.root.value > 0 ? ` · ${Math.round((n.value / p.root.value) * 100)}% of the total` : '';
    const out: HTMLElement[] = [];
    const strong = document.createElement('strong');
    strong.textContent = p.measure === 'time' ? `${formatMs(n.value)}${share}` : `cost ${formatCost(n.value)}${share}`;
    out.push(strong);
    const line = (text: string, cls = '') => { const d = document.createElement('div'); d.textContent = text; if (cls) d.className = cls; out.push(d); };
    line(n.label, 'planlabel');
    line(p.measure === 'time' ? `In this step itself: ${formatMs(n.self)}` : `In this step itself: cost ${formatCost(n.self)}`);
    const loops = n.loops !== null && n.loops > 1 ? ` per loop, ${formatRows(n.loops)} loops` : '';
    line(n.actualRows !== null
      ? `Rows: ${formatRows(n.actualRows)} (expected ${formatRows(n.estimatedRows)})${loops}`
      : `Rows: about ${formatRows(n.estimatedRows)}`);
    if (!brief && p.measure === 'time') line(`Cost estimate: ${formatCost(n.cost)}`);
    for (const a of n.advice) line(`⚠ ${a.message}`, 'planwarn');
    return out;
  }

  private renderDetail() {
    const b = this.selected;
    if (!b || !this.plan) {
      this.detailEl.replaceChildren(Object.assign(document.createElement('p'), { className: 'dim', textContent: 'Pick a step to see its details.' }));
      return;
    }
    const head = document.createElement('h3');
    head.textContent = b.node.operation;
    const parts: HTMLElement[] = [head, ...this.summary(b.node, false)];
    if (this.linkIndex.has(b.node.id) && this.links) {
      const go = document.createElement('button');
      go.textContent = 'Show in SQL';
      go.title = 'Select this step\'s table in the editor';
      go.onclick = () => this.revealSql(b);
      parts.push(go);
    }
    if (b.node.properties.length) {
      const dl = document.createElement('dl');
      for (const prop of b.node.properties) {
        const dt = document.createElement('dt');
        dt.textContent = prop.name;
        const dd = document.createElement('dd');
        dd.textContent = prop.value;
        dl.append(dt, dd);
      }
      const more = document.createElement('details');
      const sum = document.createElement('summary');
      sum.textContent = 'Everything the database said';
      more.append(sum, dl);
      parts.push(more);
    }
    this.detailEl.replaceChildren(...parts);
  }

  private renderSteps() {
    if (!this.plan) { this.stepsEl.replaceChildren(); return; }
    const time = this.plan.measure === 'time';
    const table = document.createElement('table');
    const head = table.createTHead().insertRow();
    for (const h of ['Step', time ? 'Time' : 'Cost', 'Self', 'Rows', time ? 'Expected' : '', time ? 'Loops' : '']) {
      if (!h && !time) continue;
      const th = document.createElement('th');
      th.textContent = h;
      head.append(th);
    }
    const body = table.createTBody();
    for (const b of this.boxes) {
      const n = b.node;
      const tr = body.insertRow();
      tr.dataset.id = String(n.id);
      tr.tabIndex = 0;
      tr.setAttribute('aria-selected', String(b === this.selected));
      const cell = (text: string, cls = '') => { const td = tr.insertCell(); td.textContent = text; if (cls) td.className = cls; return td; };
      const step = cell('', 'step');
      step.style.paddingLeft = `${6 + b.depth * 14}px`;
      step.textContent = (n.advice.length ? '⚠ ' : '') + n.label;
      if (n.advice.length) step.title = n.advice.map((a) => a.message).join('\n');
      cell(time ? formatMs(n.value) : formatCost(n.value), 'num');
      cell(time ? formatMs(n.self) : formatCost(n.self), 'num');
      if (time) {
        cell(n.actualRows === null ? '' : formatRows(n.actualRows), 'num');
        cell(formatRows(n.estimatedRows), 'num');
        cell(n.loops === null ? '' : formatRows(n.loops), 'num');
      } else {
        cell(formatRows(n.estimatedRows), 'num');
      }
      tr.onclick = () => { this.select(b); this.revealSql(b); };
      tr.onfocus = () => this.select(b);
      tr.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); this.revealSql(b); } };
      tr.onmouseenter = () => this.links?.highlight(this.linkIndex.get(n.id) ?? []);
      tr.onmouseleave = () => this.links?.highlight([]);
    }
    this.stepsEl.replaceChildren(table);
  }
}
