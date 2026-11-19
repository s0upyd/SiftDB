// Types for the few Monaco internals the command palette uses (design section 14, F12). Monaco ships no declarations
// for its internal modules; these describe only what palette.ts calls. The version is pinned in package.json.

declare module '@monaco/editor/standalone/browser/standaloneServices.js' {
  export const StandaloneServices: { get<T>(id: unknown): T };
}

declare module '@monaco/platform/instantiation/common/instantiation.js' {
  export const IInstantiationService: unknown;
}
declare module '@monaco/platform/contextkey/common/contextkey.js' {
  export const IContextKeyService: unknown;
}
declare module '@monaco/platform/theme/common/themeService.js' {
  export const IThemeService: unknown;
}
declare module '@monaco/platform/configuration/common/configuration.js' {
  export const IConfigurationService: unknown;
}

declare module '@monaco/platform/keybinding/common/keybinding.js' {
  export const IKeybindingService: unknown;
  export interface IKeybindingService {
    lookupKeybinding(commandId: string): { getLabel(): string | null } | undefined;
  }
}

declare module '@monaco/platform/quickinput/common/quickInput.js' {
  export enum QuickPickFocus { First = 1, Second = 2, Last = 3, Next = 4, Previous = 5, NextPage = 6, PreviousPage = 7 }
}

declare module '@monaco/base/common/event.js' {
  export class Emitter<T> {
    readonly event: (listener: (e: T) => void) => { dispose(): void };
    fire(e: T): void;
  }
  export const Event: { None: (listener: unknown) => { dispose(): void } };
}

declare module '@monaco/platform/quickinput/browser/quickInputService.js' {
  export interface QuickPickItem {
    label: string;
    description?: string;
    detail?: string;
    /** Shown on the right, like a shortcut. */
    keybinding?: unknown;
    type?: 'item';
  }
  export interface QuickPickSeparator { type: 'separator'; label?: string }
  export interface PickOptions { placeHolder?: string; matchOnDescription?: boolean; matchOnDetail?: boolean }
  export class QuickInputService {
    /** The picker on screen, if any. */
    readonly currentQuickInput: { focus(focus: number): void; accept(inBackground?: boolean): void; hide(): void } | undefined;
    constructor(instantiation: unknown, contextKeys: unknown, theme: unknown, layout: unknown, configuration: unknown);
    createController(host?: unknown, options?: unknown): unknown;
    pick<T extends QuickPickItem>(picks: (T | QuickPickSeparator)[], options?: PickOptions): Promise<T | undefined>;
    input(options?: { placeHolder?: string; prompt?: string; value?: string }): Promise<string | undefined>;
  }
}
