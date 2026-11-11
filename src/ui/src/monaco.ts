// A lean Monaco: the core editor plus only the features a SQL editor uses. The package's own `editor.main` would also
// drag in the CSS, HTML, JSON and TypeScript language services (and their workers), which we never use.
// `@monaco` is an alias for node_modules/monaco-editor/esm/vs (see vite.config.ts and tsconfig.json): the package's
// "exports" map hides its CSS and internals. These are internal paths, so the version is pinned in package.json.
// To add a feature, copy its import from node_modules/monaco-editor/esm/vs/editor/editor.main.js, swapping the
// leading `./` or `../` for `@monaco/editor/` or `@monaco/`.
import * as monaco from '@monaco/editor/editor.api.js';
import EditorWorker from '@monaco/editor/editor.worker.js?worker';

import '@monaco/editor/browser/coreCommands.js';
import '@monaco/editor/browser/widget/codeEditor/codeEditorWidget.js';
import '@monaco/base/browser/ui/codicons/codicon/codicon.css';
import '@monaco/base/browser/ui/codicons/codicon/codicon-modifiers.css';

// Editing
import '@monaco/editor/contrib/bracketMatching/browser/bracketMatching.js';
import '@monaco/editor/contrib/clipboard/browser/clipboard.js';
import '@monaco/editor/contrib/comment/browser/comment.js';
import '@monaco/editor/contrib/cursorUndo/browser/cursorUndo.js';
import '@monaco/editor/contrib/indentation/browser/indentation.js';
import '@monaco/editor/contrib/lineSelection/browser/lineSelection.js';
import '@monaco/editor/contrib/linesOperations/browser/linesOperations.js';
import '@monaco/editor/contrib/multicursor/browser/multicursor.js';
import '@monaco/editor/contrib/smartSelect/browser/smartSelect.js';
import '@monaco/editor/contrib/snippet/browser/snippetController2.js';
import '@monaco/editor/contrib/wordHighlighter/browser/wordHighlighter.js';
import '@monaco/editor/contrib/wordOperations/browser/wordOperations.js';
import '@monaco/editor/contrib/folding/browser/folding.js';
import '@monaco/editor/contrib/tokenization/browser/tokenization.js';

// Find, hover, context menu, command palette (F1)
import '@monaco/features/find/register.js';
import '@monaco/editor/contrib/find/browser/findController.js';
import '@monaco/editor/contrib/hover/browser/hoverContribution.js';
import '@monaco/editor/contrib/contextmenu/browser/contextmenu.js';
import '@monaco/editor/standalone/browser/quickAccess/standaloneCommandsQuickAccess.js';
import '@monaco/editor/standalone/browser/quickAccess/standaloneGotoLineQuickAccess.js';

// Autocomplete and ghost text (design section 5)
import '@monaco/editor/contrib/suggest/browser/suggestController.js';
import '@monaco/editor/contrib/suggest/browser/suggestInlineCompletions.js';
import '@monaco/editor/contrib/inlineCompletions/browser/inlineCompletions.contribution.js';

import '@monaco/editor/common/standaloneStrings.js';

// The only language we register: PostgreSQL highlighting. Loaded lazily by Monaco.
import '@monaco/languages/definitions/pgsql/register.js';

(self as unknown as { MonacoEnvironment: unknown }).MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

export { monaco };
