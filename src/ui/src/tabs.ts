/** Small behaviours shared by the two tab strips (sheets, and results under an editor). */

/** The strips have no scrollbar, so the mouse wheel has to scroll them sideways. */
export function scrollTabsWithWheel(strip: HTMLElement) {
  strip.addEventListener('wheel', (e) => {
    if (strip.scrollWidth <= strip.clientWidth || e.deltaX !== 0) return; // nothing to scroll, or already a sideways gesture
    strip.scrollLeft += e.deltaY;
    e.preventDefault();
  }, { passive: false });
}

/** Keeps the selected tab visible, so a new or switched-to tab is never hiding off the edge of the strip. */
export function revealSelectedTab(strip: HTMLElement) {
  const selected = strip.querySelector<HTMLElement>('[aria-selected="true"]');
  if (!selected) return;
  const left = selected.offsetLeft - strip.offsetLeft;
  const right = left + selected.offsetWidth;
  if (left < strip.scrollLeft) strip.scrollLeft = left - 8;
  else if (right > strip.scrollLeft + strip.clientWidth) strip.scrollLeft = right - strip.clientWidth + 8;
}
