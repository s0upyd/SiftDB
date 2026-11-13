// A small context menu: a list of actions at a point. Arrow keys move, Enter runs, Escape or a click elsewhere closes,
// and focus goes back where it was.

export interface MenuItem {
  label: string;
  /** Shown on the right, for reading only: the shortcut is handled by whoever owns it. */
  shortcut?: string;
  disabled?: boolean;
  action: () => void;
}

let closeOpen: (() => void) | null = null;

export function showMenu(clientX: number, clientY: number, items: MenuItem[], label: string) {
  closeOpen?.();
  const returnTo = document.activeElement as HTMLElement | null;
  const menu = document.createElement('div');
  menu.className = 'menu';
  menu.setAttribute('role', 'menu');
  menu.setAttribute('aria-label', label);

  const buttons = items.map((item) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.setAttribute('role', 'menuitem');
    b.tabIndex = -1;
    b.disabled = !!item.disabled;
    const name = document.createElement('span');
    name.textContent = item.label;
    b.append(name);
    if (item.shortcut) {
      const k = document.createElement('span');
      k.className = 'shortcut';
      k.textContent = item.shortcut;
      b.append(k);
    }
    b.onclick = () => { close(); item.action(); };
    menu.append(b);
    return b;
  });
  const enabled = () => buttons.filter((b) => !b.disabled);

  const close = (restore = true) => {
    if (closeOpen !== close) return;
    closeOpen = null;
    menu.remove();
    document.removeEventListener('pointerdown', outside, true);
    window.removeEventListener('blur', blur);
    if (restore) returnTo?.focus({ preventScroll: true });
  };
  const outside = (e: PointerEvent) => { if (!menu.contains(e.target as Node)) close(false); };
  const blur = () => close(false);

  menu.addEventListener('keydown', (e) => {
    const list = enabled();
    const at = list.indexOf(document.activeElement as HTMLButtonElement);
    const move = (to: number) => list[(to + list.length) % list.length]?.focus();
    if (e.key === 'ArrowDown') move(at + 1);
    else if (e.key === 'ArrowUp') move(at - 1);
    else if (e.key === 'Home') move(0);
    else if (e.key === 'End') move(list.length - 1);
    else if (e.key === 'Escape' || e.key === 'Tab') close();
    else return;
    e.preventDefault();
    e.stopPropagation();
  });

  document.body.append(menu);
  // Keep it on screen: open up or left of the point when there is no room below or to the right.
  const r = menu.getBoundingClientRect();
  const x = clientX + r.width > window.innerWidth - 4 ? Math.max(4, clientX - r.width) : clientX;
  const y = clientY + r.height > window.innerHeight - 4 ? Math.max(4, clientY - r.height) : clientY;
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
  closeOpen = close;
  document.addEventListener('pointerdown', outside, true);
  window.addEventListener('blur', blur);
  enabled()[0]?.focus();
}
