// Small flat icons, one colour each (they take the text colour, set in CSS), instead of emoji, which bring their own
// colours and shading.

const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * The one "running" mark, used on sheet tabs, result tabs and the result status bar: a ring in one colour, turning.
 * Still when the system asks for reduced motion (see .spinner in index.html).
 */
export function spinner(): HTMLSpanElement {
  const s = document.createElement('span');
  s.className = 'spinner';
  s.setAttribute('aria-hidden', 'true');
  return s;
}

/** A padlock: a shackle and a filled body. */
export function lockIcon(): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 12 12');
  svg.setAttribute('width', '11');
  svg.setAttribute('height', '11');
  svg.setAttribute('aria-hidden', 'true');
  svg.classList.add('lockicon');
  const shackle = document.createElementNS(SVG_NS, 'path');
  shackle.setAttribute('d', 'M3.5 5.5V3.75a2.5 2.5 0 0 1 5 0V5.5');
  shackle.setAttribute('fill', 'none');
  shackle.setAttribute('stroke', 'currentColor');
  shackle.setAttribute('stroke-width', '1.5');
  const body = document.createElementNS(SVG_NS, 'rect');
  body.setAttribute('x', '2');
  body.setAttribute('y', '5.5');
  body.setAttribute('width', '8');
  body.setAttribute('height', '5.5');
  body.setAttribute('rx', '1');
  body.setAttribute('fill', 'currentColor');
  svg.append(shackle, body);
  return svg;
}

/**
 * Toolbar button icons: 16 px, one colour (currentColor, so CSS gives each its meaning: green to run or begin, amber to
 * commit, red to stop or roll back). Lines unless a shape is marked filled.
 */
const F = 'fill="currentColor" stroke="none"';
const TOOLBAR_ICONS: Record<string, string> = {
  run: `<path ${F} d="M4.5 2.6v10.8a.6.6 0 0 0 .9.5l8.6-5.4a.6.6 0 0 0 0-1L5.4 2.1a.6.6 0 0 0-.9.5z"/>`,
  stop: `<rect ${F} x="3.5" y="3.5" width="9" height="9" rx="1.5"/>`,
  begin: '<circle cx="8" cy="8" r="6"/><path d="M8 5v6M5 8h6"/>',
  tick: '<path stroke-width="2.2" d="M2.8 8.6l3.4 3.4 7-7.4"/>',
  cross: '<path stroke-width="2.2" d="M4 4l8 8M12 4l-8 8"/>',
  connect: '<path d="M5.5 1.5v3M10.5 1.5v3M3.5 4.5h9v2a4.5 4.5 0 0 1-9 0zM8 11v3.5"/>',
  edit: '<path d="M10.8 2.2l3 3L6 13H3v-3z M9 4l3 3"/>',
  newConnection: '<ellipse cx="7" cy="3.8" rx="4.5" ry="1.8"/><path d="M2.5 3.8v7.4c0 1 2 1.8 4.5 1.8M11.5 3.8V8M2.5 7.5c0 1 2 1.8 4.5 1.8"/><path stroke-width="1.8" d="M12 10v5M9.5 12.5h5"/>',
  saveConnection: '<path d="M4 1.8h8a.5.5 0 0 1 .5.5v12l-4.5-3-4.5 3v-12a.5.5 0 0 1 .5-.5z"/>',
  open: '<path d="M1.8 4V12.7a.8.8 0 0 0 .8.8h10.8a.8.8 0 0 0 .8-.8V5.8a.8.8 0 0 0-.8-.8H8L6.4 3.2H2.6a.8.8 0 0 0-.8.8z"/>',
  save: '<path d="M2.5 2.5h8.6l2.4 2.4v8.6h-11z M5 2.5V6h5V2.5 M5 13.5v-4h6v4"/>',
  newSheet: '<path d="M9 1.8H4.3a.8.8 0 0 0-.8.8v10.8a.8.8 0 0 0 .8.8h7.4a.8.8 0 0 0 .8-.8V5.3z M9 1.8v3.5h3.5 M8 7.5v4.5M5.8 9.8h4.4"/>',
  // A gear: eight teeth and a hole.
  settings: '<path stroke-width="1.3" d="M12.75 6.14L14.77 6.65L14.77 9.35L12.75 9.86L12.67 10.04L13.74 11.83L11.83 13.74L10.04 12.67L9.86 12.75L9.35 14.77L6.65 14.77L6.14 12.75L5.96 12.67L4.17 13.74L2.26 11.83L3.33 10.04L3.25 9.86L1.23 9.35L1.23 6.65L3.25 6.14L3.33 5.96L2.26 4.17L4.17 2.26L5.96 3.33L6.14 3.25L6.65 1.23L9.35 1.23L9.86 3.25L10.04 3.33L11.83 2.26L13.74 4.17L12.67 5.96Z"/><circle cx="8" cy="8" r="2.2"/>',
  download: '<path d="M8 2v8.5M4.5 7l3.5 3.5L11.5 7M2.5 13.5h11"/>',
};

/** Puts each `[data-icon]` element's icon in front of its contents (an icon-only button has none, just its aria-label). */
export function applyIcons(root: ParentNode): void {
  root.querySelectorAll<HTMLElement>('[data-icon]').forEach((el) => {
    const body = TOOLBAR_ICONS[el.dataset.icon!];
    if (!body || el.querySelector(':scope > svg.ticon')) return;
    const holder = document.createElement('template');
    holder.innerHTML = `<svg class="ticon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" fill="none"
      stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
    el.prepend(holder.content.firstElementChild!);
  });
}
