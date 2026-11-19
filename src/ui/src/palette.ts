// The command palette (design section 14, F12), on Monaco's own quick-input widget: the same widget, list and input box
// VS Code's palette uses. Standalone Monaco hosts that widget inside the focused editor, so it needs one and is drawn at
// the top of it; VS Code hosts it in the whole window. This makes a second instance hosted by the window instead, so it
// opens centred over the app from anywhere (the grid, the tree, the tab strip) with no editor focused. Internal Monaco
// modules: see monaco-internal.d.ts.

import { StandaloneServices } from '@monaco/editor/standalone/browser/standaloneServices.js';
import { IInstantiationService } from '@monaco/platform/instantiation/common/instantiation.js';
import { IContextKeyService } from '@monaco/platform/contextkey/common/contextkey.js';
import { IThemeService } from '@monaco/platform/theme/common/themeService.js';
import { IConfigurationService } from '@monaco/platform/configuration/common/configuration.js';
import { IKeybindingService } from '@monaco/platform/keybinding/common/keybinding.js';
import { Emitter, Event } from '@monaco/base/common/event.js';
import { QuickPickFocus } from '@monaco/platform/quickinput/common/quickInput.js';
import { QuickInputService, QuickPickItem, QuickPickSeparator } from '@monaco/platform/quickinput/browser/quickInputService.js';

/** One entry of the palette. `shortcut` is shown beside it. */
export interface PaletteCommand {
  id: string;
  label: string;
  shortcut?: string;
  /** Extra words it can be found by, and a short note shown with it. */
  description?: string;
  run(): void | Promise<void>;
}

/** The whole window as the widget's host: what Monaco's layout service says for VS Code's workbench. */
function windowHost(container: HTMLElement, returnFocus: () => void) {
  const resized = new Emitter<{ width: number; height: number }>();
  const size = () => ({ width: window.innerWidth, height: window.innerHeight });
  window.addEventListener('resize', () => resized.fire(size()));
  return {
    _serviceBrand: undefined,
    get mainContainer() { return container; },
    get activeContainer() { return container; },
    get containers() { return [container]; },
    getContainer: () => container,
    whenContainerStylesLoaded: () => undefined,
    get mainContainerDimension() { return size(); },
    get activeContainerDimension() { return size(); },
    onDidLayoutMainContainer: resized.event,
    onDidLayoutActiveContainer: resized.event,
    onDidLayoutContainer: Event.None,
    onDidChangeActiveContainer: Event.None,
    onDidAddContainer: Event.None,
    mainContainerOffset: { top: 0, quickPickTop: 0 },
    activeContainerOffset: { top: 0, quickPickTop: 0 },
    focus: returnFocus,
  };
}

class WindowQuickInputService extends QuickInputService {
  constructor(private readonly host: ReturnType<typeof windowHost>) {
    super(
      StandaloneServices.get(IInstantiationService),
      StandaloneServices.get(IContextKeyService),
      StandaloneServices.get(IThemeService),
      host,
      StandaloneServices.get(IConfigurationService),
    );
  }
  override createController() { return super.createController(this.host); }
}

type Picker = NonNullable<QuickInputService['currentQuickInput']>;

/**
 * What a key does to the open picker, as Monaco's quick-input keybindings have it: Down and Up (Ctrl optional), Page
 * Down and Up, Ctrl+Home and End move through the list, Enter picks, Esc closes. Null for any other key, which the
 * widget handles itself (typing in the box).
 */
function keyAction(e: KeyboardEvent): ((p: Picker) => void) | null {
  if (e.altKey || e.metaKey) return null;
  if (e.key === 'Enter') return (p) => p.accept();
  if (e.key === 'Escape') return (p) => p.hide();
  if (e.shiftKey) return null;
  const move = (f: QuickPickFocus) => (p: Picker) => p.focus(f);
  switch (e.key) {
    case 'ArrowDown': return move(QuickPickFocus.Next);
    case 'ArrowUp': return move(QuickPickFocus.Previous);
    case 'PageDown': return move(QuickPickFocus.NextPage);
    case 'PageUp': return move(QuickPickFocus.PreviousPage);
    case 'Home': return e.ctrlKey ? move(QuickPickFocus.First) : null; // plain Home moves the cursor in the box
    case 'End': return e.ctrlKey ? move(QuickPickFocus.Last) : null;
    default: return null;
  }
}

let service: WindowQuickInputService | null = null;
/** Where focus was when the palette opened: it goes back there when the palette closes without running anything. */
let returnTo: HTMLElement | null = null;

function quickInput(): WindowQuickInputService {
  if (!service) {
    // `monaco-component`: where Monaco's theme puts its colour variables for widgets outside an editor.
    const container = document.createElement('div');
    container.className = 'monaco-component siftdb-quickinput';
    // The list's keys (moving, Enter, Esc) are Monaco keybindings whose handlers ask Monaco's own quick-input service for
    // the open picker. That service only knows the pickers hosted by editors (and Monaco only listens for keybindings
    // inside editors), so they never reach this one: do what those handlers would, on this picker.
    container.addEventListener('keydown', (e) => {
      const act = keyAction(e);
      const open = service?.currentQuickInput;
      if (!act || !open) return;
      e.preventDefault();
      e.stopPropagation();
      act(open);
    });
    document.body.append(container);
    service = new WindowQuickInputService(windowHost(container, () => returnTo?.focus()));
  }
  return service;
}

/** A choice from a list, in the palette's widget: the second step of a command that needs one. Undefined if cancelled. */
export async function pickFrom<T>(choices: { label: string; description?: string; value: T }[], placeHolder: string): Promise<T | undefined> {
  returnTo ??= document.activeElement as HTMLElement | null;
  const picked = await quickInput().pick(choices.map((c) => ({ label: c.label, description: c.description, choice: c })),
    { placeHolder, matchOnDescription: true });
  return picked?.choice.value;
}

/** The shortcut Monaco has for one of its commands ("Ctrl+/"), or undefined. */
export function shortcutFor(commandId: string): string | undefined {
  try {
    return StandaloneServices.get<IKeybindingService>(IKeybindingService).lookupKeybinding(commandId)?.getLabel() ?? undefined;
  } catch {
    return undefined;
  }
}

/** Recently run commands, newest first, for the top of the list. For this run of the app only. */
const recent: string[] = [];

/** Opens the palette on these commands; the chosen one runs once the palette has closed. */
export async function openPalette(commands: PaletteCommand[]) {
  returnTo = document.activeElement as HTMLElement | null;
  const byId = new Map(commands.map((c) => [c.id, c]));
  const item = (c: PaletteCommand): QuickPickItem & { command: PaletteCommand } => ({
    label: c.label,
    description: [c.shortcut, c.description].filter(Boolean).join(' · ') || undefined,
    command: c,
  });
  const used = recent.map((id) => byId.get(id)).filter((c): c is PaletteCommand => !!c);
  const rest = commands.filter((c) => !used.includes(c)).sort((a, b) => a.label.localeCompare(b.label));
  const picks: ((QuickPickItem & { command: PaletteCommand }) | QuickPickSeparator)[] = used.length
    ? [{ type: 'separator', label: 'recently used' }, ...used.map(item), { type: 'separator', label: 'other commands' }, ...rest.map(item)]
    : rest.map(item);
  const chosen = await quickInput().pick(picks, { placeHolder: 'Type the name of a command', matchOnDescription: true });
  if (!chosen) { returnTo = null; return; }
  recent.splice(0, recent.length, chosen.command.id, ...recent.filter((id) => id !== chosen.command.id).slice(0, 9));
  await chosen.command.run();
  returnTo = null;
}
