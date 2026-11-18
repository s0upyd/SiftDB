# Changelog

What changed in each SiftDB release. Generated from the commit messages by git-cliff (see cliff.toml); do not edit it by hand.

## 0.2.0 - 2026-11-18

### Highlights

- Release notes generated from commits, and a What's New window after updates ([Try it](command:settings))

### Other changes

- Add the planned features (F1-F17) with a status table
- Stop the browser's right-click menu reloading the page and dropping every session
- Stop the results grid flickering when its rows or columns nearly fit
- Missing WHERE alarm: ask before an UPDATE or DELETE with no WHERE, or a TRUNCATE
- Dry run: run an UPDATE or DELETE, show the rows it would change, roll back
- Format SQL: lay out statements without ever changing what they do
- Expand *: a quick fix that replaces * with the columns it stands for
- Follow foreign keys: peek at, open and find what references a row
- Schema explorer: keys, constraints, indexes, triggers, partitions, functions and types

## 0.1.4 - 2026-11-16

### Other changes

- Grid selection, copy, and seeing a whole value
- Export results to CSV or JSON
- Icons for the result bar's Export and Details buttons
- Zoom setting: make the whole window larger

## 0.1.3 - 2026-11-13

### Other changes

- Local builds report 0.1.2, the current release
- Run the tests in CI against a real Postgres; releases wait for them
- App icon, and icons instead of words on the toolbar buttons

## 0.1.2 - 2026-11-13

### Other changes

- Scaffold solution, design doc and UI spike project
- Bridge, streaming engine, Postgres provider, WebView2 host, canvas grid
- Build the UI bundle as part of the Host build
- Record spike results in design doc
- Add engine timing breakdown and streaming benchmark; record Release results
- Handle every ColumnKind explicitly
- Choose pgsqlparser for libpg_query; add characterization tests
- Add schema introspection for Postgres
- Show the database schema in a sidebar tree
- Monaco editor with F5 statement picking and schema-aware autocomplete
- Document statement picking and autocomplete decisions
- Show NULL as NULL, not 0 or an empty string
- Add the identifier resolver: Assisted and Strict modes
- Add the transaction bar: state from the server, never from the app
- End a session cleanly, one session per sheet
- Sheets in tabs, with result tabs under each editor
- Cancel a statement before its first row; hint at columns that do not exist
- Clearer results: a status bar that shows failure in red, and amber when not connected
- Show Assisted and Strict as a two-option switch
- Give each sheet its own connection; one tree root per distinct connection
- Follow the Windows dark theme in the title bar and native scroll bars
- Add a splitter between the editor and the results
- Add a splitter between the schema tree and the sheets
- Design saved connections: one database each, read-only and auto-transaction settings
- Saved connections: connection manager, read-only connections, DPAPI passwords
- Keep a writable sheet always inside a transaction
- Small cleanups: spike controls, first sheet, line endings, test warnings
- Grid columns: fit to content, ellipsis, drag and double-click to resize
- Design saving and opening sheets, autosave and restore
- Save and open sheets, autosave and restore; fix typing going to the first sheet
- Sheet tab spinner and finished mark; one shared spinner; green tree read-only badge
- Propose result styling: column-name rules, required colours, presets, rule order
- Mark user settings as the next piece of work
- Add MIT licence
- Simplify bind variables: global by name, generic types chosen in the prompt
- Add user settings: engine-owned store and a settings editor, with the preferred mode for new sheets
- Add result styling: one ordered rule list with built-in presets, generic column types
- End statements at blank lines; explain a missing semicolon instead of false ambiguity
- Undo just the failed statement instead of aborting the transaction; provider-described settings
- Catch qualified SET columns and 0/1 for booleans; mark stale results; one round trip fewer per guarded statement
- Match the SQL editor's right-click menu to the native one
- Result styling: drag to reorder, date ranges, past/future date presets, softer preset colours
- Create README.md for SiftDB project
- Release build, Velopack installer and in-app updates


