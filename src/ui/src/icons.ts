// Small flat icons, one colour each (they take the text colour, set in CSS), instead of emoji, which bring their own
// colours and shading.

const SVG_NS = 'http://www.w3.org/2000/svg';

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
