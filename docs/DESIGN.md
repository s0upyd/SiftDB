# SiftDB — Design

A Windows desktop database tool with pluggable database support. Postgres is the first provider.

The aim is to take the pain out of everyday SQL work: identifier casing and quoting, reusable test data, and always knowing what state your transaction is in.

---

## 1. Architecture

| Layer | Technology | Responsibility |
|---|---|---|
| **Host** | C# / WPF | A bare window with one full-size WebView2. Native dialogs, OS theme detection, credential storage (DPAPI), plugin loading. **No UI logic.** |
| **Engine** | C# | Providers, connections, schema introspection and caching, query execution and streaming, SQL parsing and rewriting, variables, transactions, local state. Headless: it only talks through the bridge. |
| **UI** | TypeScript | Everything the user sees: connection tree, Monaco SQL editor, canvas-based results grid, tabs, settings. Never touches a database directly. |
| **Bridge** | Typed messages | Commands from UI to engine and events from engine to UI. Result rows go across in binary batches. |

**Principles**
- All UI is TypeScript. There's no mixing of WPF and web views, which avoids theming drift, airspace problems and focus problems.
- One theme system: CSS variables, with light/dark set from the OS flag the host passes in.
- Results always stream from `DbDataReader` into buffered batches. Never materialise a `DataTable`.
- Because the engine is headless, a TUI (Terminal.Gui v2) or CLI front end could be added later on the same engine.
- Windows-only is fine. The host is thin enough to swap for Photino if that ever changes.

---

## 2. Providers (plugins)

Each provider is a separate assembly loaded with `AssemblyLoadContext`, built against a versioned contract (`IDatabaseProvider` v1, v2…).

Each provider supplies:
- **Connection:** building a connection string, testing it, opening it
- **Schema introspection:** returns a provider-neutral model (databases, schemas, tables, columns, indexes, keys, functions)
- **Query execution:** streamed results, cancellation, multiple result sets
- **Capabilities:** flags such as `CanDetectWrites` or `ReadOnlyEnforcement`, so the UI can hide what doesn't apply
- **Dialect:** `IIdentifierPolicy` (case folding, quote characters, reserved words), paging syntax, keywords
- **Parser:** statement splitting, token positions, alias resolution
- **Transactions:** `ITransactionMonitor` (see §7)

The first provider is **Postgres**: Npgsql, plus libpg_query (Postgres's own parser) through a .NET binding or a small P/Invoke wrapper. A later SQL Server provider would use ScriptDom.

The app checks the contract version when loading a provider and refuses incompatible ones instead of crashing.

---

## 3. Identifier casing and quoting

Postgres lowercases identifiers that aren't quoted, so mixed-case names (e.g. EF Core's `"OrderItems"`) need double quotes everywhere. `"..."` means an identifier and `'...'` means a string.

The engine's resolver matches identifiers against the schema, ignoring case, and works out the correct form. It quotes only where needed: mixed case, special characters or reserved words.

### Two query modes

**Strict: the text you write is the text that runs**
- Problems get squiggles, and lightbulb quick fixes change the actual text.
- Running is blocked or warned about while fixable issues remain.
- Best for scripts that will be saved, shared or committed.

**Assisted: you write loosely, the editor shows what will run**
- You type `OrderItems` and the editor shows `"OrderItems"`, with the quotes greyed out (Monaco injected-text decorations that can't be edited and that the cursor skips).
- What's executed is exactly what's displayed.

**The mode switch shows both options.** Each sheet toolbar has an `Assisted | Strict` segmented control with the active option filled. An earlier single button changed its own label to the current mode, which read as "click for Strict". That made a correctly blocked Strict run look like Assisted failing. Mode is per sheet, and switching clears a stale "not run" message. **Mode belongs to the sheet, not the connection:** Assisted shows exactly what runs and blocks anything ambiguous, so Strict is a style choice (the file text is the real SQL), not a safety measure, and there is no per-connection default. New sheets open in the **preferred mode** from user settings (§10), Assisted unless changed; so does an opened file whose header names no mode. Restored sheets and files that name a mode keep theirs.

**Rules for both modes**
- Copy and save output the corrected SQL, or ask first.
- An "Accept all" command turns greyed corrections into real text.
- If an identifier could mean two things, nothing is filled in for it. It gets a squiggle and needs a choice before the query runs.
- New names in DDL (`CREATE TABLE MyTable`) prompt for intent: `"MyTable"` or `mytable`. Nudge towards the lowercase convention.
- Identifiers are resolved against fresh metadata when the query runs, not only the cache.
- History records both what was typed and what ran.
- Nothing inside strings or comments is ever rewritten.
- The mode shows on each tab and is easy to toggle.

Extra: catch quote mix-ups, e.g. `where name = "bob"` with no column called `bob` suggests `'bob'`.

---

## 4. Bind variables

- Type `@name` to get autosuggestions of stored variables, with their values.
- **The editor shows each value greyed inline**: `@surname 'Smith'`. Long values are shortened, with the full value on hover. Unset variables show a marker (`@dob ?`).
- Only `@` followed directly by a name counts as a variable. Postgres's own `@>`, `<@`, `@@` and `@` operators are excluded, as are strings, comments and `$$` bodies.
- **A variable is just a name**, usable from any sheet on any connection. There are no scopes or named sets; a per-connection level can be added later if it turns out to be needed.
- **Missing values** are collected in one inline prompt when you run, not one dialog per variable. Each row has the value and a **type** chooser. The type is preselected from the parameter type Postgres reports when the query is prepared; when it cannot be inferred, a setting decides: **default to Text** (the default) or **ask** (nothing preselected, you must pick). The input follows the type (date picker, number box, etc.), and the chosen type is stored with the variable.
- **Types are generic** (Text, Integer, Decimal, Boolean, Date, Date & time, and a list of any of these for `= ANY(@ids)`), and each provider maps them to its own, e.g. Postgres Text → `text`, Integer → `bigint`, Decimal → `numeric`, Date & time → `timestamp`.
- **Execution** uses real parameters (`@surname` becomes `$1`), never pasted text.
- **All stored values are encrypted** with DPAPI (CurrentUser). Decrypted values are kept in memory only and never logged.
- History stores the parameter values used, encrypted.
- A hide-values toggle masks values on screen, for screen sharing.
- Exporting or sharing variables produces a passphrase-encrypted file.
- Copy options: keep `@vars`, write values in as correctly quoted literals, or use `$1` placeholders.

---

## 5. Autocomplete

**Dropdown suggestions, aware of where you are**
- Tables after `FROM`/`JOIN`, columns after `alias.`, columns from the query's tables after `WHERE`
- Quoting is applied automatically to match the mode
- `@` suggests variables

**Ghost text, accepted with Tab** (schema-driven first)
- `JOIN order_items oi` suggests `ON oi.order_id = o.id`, from the foreign keys
- `INSERT INTO t` suggests the full column list
- Completions from query history
- AI completions (a local ONNX model or an LLM) come later

**Performance:** a compact copy of the schema is pushed to the front end so suggestions are instant. Only heavy work (alias resolution, history search) goes to the engine, with debouncing.

**Telling grey text apart:** there are three kinds, each with its own style:
- corrected quotes: faded
- variable values: pill style
- completion suggestions: italic

---

## 6. Sheets and execution

A query tab is a **sheet** that holds several queries. It can be saved to a file and reopened.

**What `F5` / `Ctrl+Enter` runs, in priority order**
1. Selected text
2. The statement the cursor is in (if the cursor is on a blank line, the statement above; if nothing sensible is close by, nothing)
3. The only statement in the sheet

**`Shift+F5`** runs the **whole sheet as a script**, and a **selection of several statements** runs the same way: split by the same rules as the editor (so a missing `;` between blank-line-separated statements is harmless), then run **one statement after another** on the sheet's session, each with its own result tab and its `NOTICE`/`RAISE` messages. An error stops the script and jumps to the failing line. An optional wrapping transaction (off by default) makes it all roll back together if a statement fails; without it each statement commits on its own, unlike a block sent in one go, which Postgres treats as one implicit transaction. (This replaces "sent in one go": running one by one is what gives each statement its own result and error position. There's no per-sheet "script mode" setting. That was considered and rejected.) Not built yet.

**`Ctrl+.` / `Esc`** cancels.

**Details**
- Statements are split by the parser (libpg_query reports where each starts and ends), never by looking for `;`.
- **A blank line also ends a statement**, because people often leave out the `;` between statements they keep apart with blank lines. It does so only when the text above parses on its own **and** the next line starts a new statement (`select`, `with`, `insert`, `update`, `create`, `begin`…), so a statement with blank lines inside it stays whole: a CTE, `select a` ⏎⏎ `from t`, `insert into t (a)` ⏎⏎ `select …`, a `union`. Strings, comments and function bodies are never split. The ▶, the highlight, F5 and the identifier checks all follow this split.
- **Two statements on adjacent lines** with neither a `;` nor a blank line between them read as one, which cannot run. SiftDB says so ("This looks like two statements. Is a ; missing at the end of line N?", with a squiggle on the second statement's first word) instead of whatever else the joined text looks like (the same table twice made every column "ambiguous").
- The statement that will run is lightly highlighted as the cursor moves.
- A ▶ in the margin next to each statement lets you run it with the mouse.
- Each statement that runs gets its own result tab, labelled with the start of its SQL. Results can be pinned.
- **Each sheet has its own database session**, so `BEGIN`/`COMMIT` across statements, temp tables and `SET` all carry over.
- **Each sheet has its own connection string too.** The connection box describes the visible sheet and Connect acts on that sheet only; a new sheet starts on the connection of the sheet you were on. The schema tree has one root per distinct connection string: sheets on the same string share a root (and a schema), a different string adds a root, and the last sheet leaving a connection removes it. Roots are labelled host / database (the user name is added when two would read the same); the password never reaches the DOM.

**Saving**
- Plain `.sql` files, so they open in other tools.
- Sheet settings go in a header comment, e.g. `-- db: connection=dev-pg; mode=assisted`.
- Variable values are never written to the file.
- Unsaved sheets are autosaved and restored on restart.
- **Assisted sheets save the corrected SQL**, so the file runs in psql or any other tool. On save, the greyed quotes become real text in the editor, as *Accept all* (Ctrl+Alt+Q) does.
- **The header line is SiftDB's, not the user's:** it is read and taken off when a file opens, and written back on save, so it never shows in the editor. It names the connection only for a *saved* connection (a pasted string is never written to a file). Other `--` comments at the top are left alone.
- **A file naming a connection that is not saved here** (`connection=prod-ro`) opens with **no connection chosen** and an amber message ("This file names 'prod-ro', which is not saved here. Pick a connection."). It never guesses, so a script cannot silently land on the wrong database.
- **Encoding and line endings are kept:** a file is read as UTF-8 (a byte-order mark is kept if it had one) and written back with the line endings it had; new files use CRLF, the Windows convention.
- **Restore on restart brings sheets back without connecting:** text, mode, file and connection are selected, waiting for Connect, as at startup. No surprise sessions, and no password prompts at launch.
- **Autosave covers every sheet, saved or not**, a moment after each change. Closing the window therefore never asks about unsaved edits (they come back on restart); closing a *tab* with unsaved edits asks, because that text would be gone. A sheet's title is its file name, with a dot while it has unsaved edits.
- Shortcuts: Ctrl+S saves (Save As for a new sheet), Ctrl+Shift+S saves as, Ctrl+O opens. Open and Save As use the native Windows dialogs, asked of the host.

---

## 7. Transaction state

A transaction starts from a toolbar button or a typed statement (`BEGIN`). A bar above the editor shows its state:

| Bar | Meaning | Buttons |
|---|---|---|
| 🟢 Green | Transaction open, nothing changed yet | Rollback / End |
| 🟡 Yellow | Changes pending | Commit, Rollback |
| 🔴 Red | Transaction aborted by an error (Postgres) | Rollback only |

- The yellow bar shows a summary (e.g. "3 statements · 42 rows changed") and how long the transaction has been open.
- Shortcuts for commit and rollback. Closing a tab with changes pending shows a warning.
- Each state also has an icon and a text label, not just a colour.
- **A failed statement does not cost you the transaction** (Postgres, on by default). In Postgres any error aborts the whole transaction until it is rolled back, so one typo throws away everything pending. Instead, a statement run inside a transaction is guarded by a savepoint (as psql's `ON_ERROR_ROLLBACK` does): if it fails, for any reason (a typo, a bad column, a constraint part way through an update, an error at row 90,000, a cancel), SiftDB rolls back to the savepoint, so only that statement is undone. The result bar is still red with the server's error; the transaction bar stays green or yellow, and the sheet says "The statement failed and was undone. The transaction is still open, with the changes made before it still pending." An undone statement does not count in the bar's summary. Not guarded: statements outside a transaction (nothing to protect), a transaction that has already failed, and statements that control the transaction themselves (`BEGIN`, `COMMIT`, `ROLLBACK`, the user's own `SAVEPOINT` / `ROLLBACK TO`), which act on the user's transaction. A statement that does not even parse is guarded: that is the commonest case. Cost: a savepoint before and a release after each statement inside a transaction, two short round trips. The **PostgreSQL** settings category (§10) offers Postgres's own behaviour instead ("Abort the transaction"), which makes the red bar below the usual outcome again. Script runs (§6) stop at the first error either way.
- Optionally, a per-connection setting (**auto-transaction**, off by default) keeps the sheet **always inside a transaction**, so every change waits for Commit and a missing `WHERE` can still be rolled back. It applies to writable connections only (§8). A transaction begins as soon as the sheet connects and again whenever one ends (Commit or Rollback, from the bar or typed). The bar says "started automatically" and, while nothing has changed, shows no End or Rollback (another would start at once). A failed statement is undone on its own, as above; with "Abort the transaction" chosen, a statement that fails while nothing has changed is rolled back and a fresh transaction started, with a message, and a failure after changes leaves the bar red as usual. A statement the server refuses inside a transaction (VACUUM, CREATE DATABASE, CONCURRENTLY…) ends an unchanged automatic transaction first, and is refused ("commit or roll back first") while there are changes, so nothing is ever committed behind the user's back. Trade-off, chosen deliberately: the session sits idle in a transaction while the user only reads (it holds a snapshot, and a server `idle_in_transaction_session_timeout` would end it).

**Split between core and providers**
- **Core** owns the bar, the buttons and the states (None → Open → Changed → Failed).
- **Provider** implements `ITransactionMonitor`: `Begin`, `Commit`, `Rollback`, `GetStateAsync()`, and capability flags.
- The state always comes from the server, never from the app's own tracking, so the button and typed SQL can't disagree.

| | Postgres | SQL Server |
|---|---|---|
| Start | `BEGIN` | `BEGIN TRAN` |
| Open? | protocol status | `@@TRANCOUNT > 0` |
| Changed? | `pg_current_xact_id_if_assigned()` is not null | `sys.dm_tran_database_transactions` (log records > 0), which needs `VIEW SERVER STATE` |
| Failed? | any error aborts the transaction | `XACT_STATE() = -1` |

**Fallback when writes can't be detected:** if anything other than a plain `SELECT` ran, or rows were affected, the bar goes yellow. Showing yellow wrongly is better than showing green with changes pending.

---

## 8. Connections

A **saved connection** is **one database** (not a whole server; a second database on the same server is a second connection): a named set of structured fields (host, port, database, user, SSL mode), plus an *advanced* box for extra driver keywords. The provider describes its own fields, so the form and the store are provider-neutral (a provider contract change).

**Storage**
- The **engine** owns the store, so a future TUI gets the same connections. A JSON file in `%APPDATA%\SiftDB` holds the connections; **secrets never go in that file**. Passwords are kept separately, encrypted with DPAPI (CurrentUser), so the connection file could later be shared or committed without leaking anything.
- A password travels **from the UI to the engine once** (typed into the edit form or the connect prompt) and is **never sent back**. Editing a connection shows *saved password* with a Change button, never the value.
- **Remember password** is a per-connection checkbox. Without a saved password, connecting asks for it.

**Identity**
- Connections have a stable internal **id**; sheet files refer to them by **name** (`connection=dev-pg`), matched when a file is opened, so files move between machines and renames don't break the store. Names are unique.
- Editing a connection takes effect the next time a sheet connects; deleting one is refused while a sheet uses it.
- At startup the picker selects the **last-used** saved connection but does not connect until asked.
- An ad-hoc pasted connection string still works as an **unsaved connection**, with *Save as…*.

**Per-connection settings** (deliberately only two)
- **Read-only.** The session opens with `default_transaction_read_only = on`, passed in the startup options (`-c ...`) rather than a `SET`, so `RESET`, `RESET ALL` and `DISCARD ALL` fall back to read-only. The server then refuses writes, including inside a typed `BEGIN`. The app blocks statements that would switch it off, recognised by the parser from the statement's structure rather than its text: `SET`/`SET SESSION CHARACTERISTICS ... READ WRITE`, `BEGIN`/`START TRANSACTION`/`SET TRANSACTION ... READ WRITE`, and `set_config('default_transaction_read_only' | 'transaction_read_only', ...)`. Anything inside a `DO` block or a function body gets through. Note the server also refuses every `CREATE`/`ALTER`/`DROP` (including `CREATE TEMP TABLE`) and `nextval` on ordinary sequences. There is **no per-tab lock or unlock**: to write, open the sheet on a writable connection. Users who want protection save a read-only and a writable connection to the same database and choose deliberately. This is a guard against mistakes, not security; real protection is a database role without write privileges. A read-only sheet can't go yellow. It shows on the tab (a flat amber padlock), as green *Read-only* chips on the sheet and the tree root, and as "(read-only)" in the connection picker, so a refused write is never a mystery. Coloured markers stay simple: one flat colour each, drawn as small inline icons rather than emoji, which bring their own colours and shading. SQL Server has no server-side equivalent and would rely on the app check (capability flag `ReadOnlyEnforcement = Server | Client`).
- **Auto-transaction** (§7), for writable connections.

There is no "production" flag or environment concept, and no per-connection default mode (§3).

**First version**
- Auth: password, and SSL modes including `verify-full` with a CA file. The stored fields leave room for Entra ID (Azure Postgres), client certificates and SSH tunnels, which come later.
- The store file carries a format version and is written atomically (temp file, then replace).
- UI: the schema tree stays the list of *open* connections. The toolbar's connection box became a picker: a dropdown of saved connections (plus *Connection string…* for a pasted one, with *Save as…*), and *Edit…* / *New…*; the edit form is a modal with a **Test** button. Grouping or folders come later.
- **The database can be picked from the server.** In the form, Database comes after the password and is an editable dropdown: type a name, or open the list (its button, Down or Alt+Down) to pick from the databases this login can open, so a new connection does not need the name known in advance. The list is asked of the server when it opens, with the form's details and password, as Test does (`listDatabases`); a missing Host, a refused login or a server that is down shows as a line in the list instead. The provider logs in wherever it can to ask: the named database if it exists, else `postgres`, else `template1` (Postgres lists `pg_database` rows that allow connections, are not templates and the login has `CONNECT` on). The list is kept while the server details and password stay the same. A provider marks the field with the `Database` field kind and offers `ListDatabasesAsync` (contract v10); without it the field only takes a typed name.

---

## 9. Delivery

- The host is published self-contained, so no .NET runtime install is needed.
- The UI is bundled and served locally, never from a remote URL. Engine and UI are released together.
- If WebView2 is missing, the installer runs Microsoft's bootstrapper.
- **Velopack** handles the installer, background delta updates and stable/beta channels. Releases are hosted on the public repository's GitHub Releases, published by a GitHub Actions workflow started by hand (*Actions › Release › Run workflow*), which works out the version and writes the release notes from the commit messages (F17).
- **Code signing** with Azure Trusted Signing, to avoid SmartScreen warnings. Deferred until the signing account is approved; until then the installer is unsigned and SmartScreen warns.
- Third-party providers go in a plugins folder (possibly NuGet-format packages) and are checked against the contract version.

---

## 10. Back burner

User settings are built (see §11, *User settings*). Every later feature adds its settings there; result styling below is the first expected user.

**Settings stay database-neutral.** A setting that only makes sense for one kind of database is **described by its provider** (key, label, help, and choices each with its own explanation), the way a provider describes its connection fields. The engine stores the values per provider and hands them back to the provider, never interpreting them; the settings editor draws **one category per provider that has settings**, after the app's own (SQL, Results), so the UI knows nothing about any database. The first is **PostgreSQL**: "When a statement fails inside a transaction" (§7).

The read-only lock that used to be here became the read-only connection setting (§8).

The features agreed for building next, with their status, are in §14 (*Planned features*).

### Result styling
Make results easier to read by colouring values, the way users are used to seeing their data.
- **One kind of rule.** A rule is *where it applies* + a *condition* + a *style*. Built-in presets are ordinary rules that ship with the app; nothing about them is special except that they cannot be deleted.
- **Where a rule applies:** columns whose **name matches** a simple wildcard pattern (`status`, `is_*`, `*_flag`; case-insensitive; empty means every column), and optionally only columns of chosen **generic types**. Later, by table and column (`orders.status`), once the grid knows which table each result column came from (the server reports it).
- **Generic types** are the same set as bind variables (§4): Text, Integer, Decimal, Boolean, Date, Date & time. The provider maps its own type names to them (Postgres `int4` → Integer, `numeric` → Decimal, `bool` and `bit(1)` → Boolean, `timestamptz` → Date & time; anything else → Text), and the engine sends each result column's generic type with the column list. Rules never name a database's own types, so they work on any provider.
- **Conditions:** NULL, equals, one of a list, a range (of numbers, or of dates written `2026-01-31`, optionally with a time; a date as the upper end includes its whole day), negative, a date in the past, a date in the future (a Date column by day, so today is neither; a Date & time column against now, asked per cell so an open result stays right), a wildcard pattern. Text comparisons ignore case; equals and one-of compare numbers as numbers in Integer and Decimal columns. Only the NULL condition matches a NULL.
- **Styles:** every rule **sets both** a text and a background colour (`#rgb` / `#rrggbb`), and optionally bold, so the cell carries its own background and reads the same in the light and dark themes. The settings editor shows a **live example** of each rule, and warns when the two colours are hard to read together.
- **Built-in presets**, written as rules with fixed colours taken from the app's flat tones: *NULL values* (grey tint, any type); *True* and *False* (green / amber, matched by value — true/t/yes/y/1, false/f/no/n/0 — in **Boolean columns only**, so a quantity of 1 stays plain); *Negative numbers* (red, Integer and Decimal); *Dates in the past* (amber) and *Dates in the future* (green), for Date and Date & time columns, **off by default** because most tables have a `created_at` and colouring every date in it would be loud. The tints are muted rather than bright (contrast 4.9 to 5.2 : 1).
- **Which rule wins:** all rules, presets included, are **one ordered list**, checked from the top; the **first rule that matches styles the cell**. Since every rule sets both colours, the winner decides the whole look, nothing is mixed. Presets start at the **bottom**, so the user's rules win; each can be switched off or moved, but not deleted or edited. A preset missing from the stored list (a new one in a later version) is added at the bottom. The editor reorders rules by dragging a row by its grip, or with move up / move down from the keyboard.
- Rules are matched to columns once per result, so drawing a cell only checks the few rules that apply to its column and scrolling is unaffected. The value always stays readable as text (a NULL still says NULL), so colour is never the only signal.
- **Stored** in user settings (§11), shown under the **Results** category of the settings editor. The engine owns the rules and validates them; preset entries store only their id, whether they are on, and their place, so a preset's definition can improve in a later version.

### Later
- **Watch open files** (low priority): notice when another program (git pull, another editor) changes an open `.sql` file; reload it when the sheet has no unsaved edits, ask when it has. Until then, saving overwrites the file as it is.
- A TUI front end on the same engine
- AI completions
- A SQL Server provider

---

## 11. Open questions

**Decided**
- Parser: **`pgsqlparser` 2.1.0 (MIT, libpg_query 18.x) inside `SiftDB.Providers.Postgres` only**, behind a provider-neutral `ISqlParser` in Abstractions, so its types never leak and another engine (ScriptDom for SQL Server) can implement the same interface. Pin the version. Fallback if the wrapper is abandoned: our own P/Invoke over libpg_query (MSVC builds are supported upstream) or a fork. We do **not** write a full Postgres parser. Observed behaviour (see `PgSqlParserCharacterization` tests):
  - Offsets are already UTF-16, which is what Monaco wants. Parse errors give a code-point position, with `GetCursorCharOffset` to convert.
  - `SplitWithScanner` copes with half-typed statements (`... where o.`) and empty statements but cannot handle SQL-standard `BEGIN ATOMIC ... END` function bodies (it splits inside them). `SplitWithParser` handles those but, like `Parse`, fails on any syntax error. `PostgresSqlParser.Split` therefore tries the parser split first and falls back to the scanner split. Known gap: a script that has both a syntax error and a `BEGIN ATOMIC` body can be mis-split. Split ranges include leading whitespace and comments, so they are trimmed using tokens.
  - `Scan` fails for the whole input on an unterminated string, quoted identifier or `/*` comment, losing even the tokens before it. The editor needs a tolerant layer: re-scan the text before the error position and add a synthetic "unterminated" token.
  - Quoted identifiers come back as `Ident` tokens whose text starts with `"`; keyword kinds (reserved, unreserved) are included. `"bob"` parses fine, so the resolver (§3) has to catch it, not the parser.
  - Speed: split 1.5 ms and scan 9-20 ms for a 62 KB, 500-statement script; parsing a single statement is about 0.07 ms. Fast enough to re-scan the whole sheet on every keystroke.
  - A small hand-written tolerant layer for autocomplete context (current clause, tables and aliases in scope) is still needed, because the real parser has no error recovery.
- Front-end framework: **Solid** (fine-grained reactivity, no virtual DOM). The spike uses plain TypeScript, since it needs no framework.
- Grid: **custom canvas grid**, read-only, with virtualised rows, column widths, selection and copy. Library grids (Glide is React-only, AG Grid has licence problems) were rejected to avoid framework mismatch and so the spike measures the real rendering ceiling.
- Bridge transport: **WebView2 shared buffers** (`CreateSharedBuffer` + `PostSharedBufferToScript`) for row batches. `PostWebMessageAsArrayBuffer` does not exist in WebView2, so the earlier plan was wrong. Commands and events are JSON for now (MessagePack deferred; they're low volume). Row batches use a custom columnar binary layout (see `RowBatchWriter`): a header with column types and row count, then typed-array columns, 8-byte aligned so the UI can wrap them directly. The UI copies each batch out and releases the buffer straight away. Arrow can be revisited later for interop.
- Large results: **stream with fetch-on-scroll**. The engine reads ahead from the `DbDataReader` into a capped buffer and pauses when the UI isn't consuming (backpressure). Fetched rows are kept in memory up to a cap, then spilled to a temp file, so scrolling back is instant. The UI shows a "fetched N rows, still loading" state, and cancel is instant. There is no hard row limit by default.
- NULLs: every batch column that contains a NULL carries a validity bitmap (one bit per row, set = NULL; no bitmap, no NULLs), with a well-formed 0 / empty string underneath. The grid draws NULL dim and italic so it can never be mistaken for `0`, `''` or the text "NULL". Verified with real Postgres NULLs through Npgsql, and by replaying the engine's real bytes into the real UI.
- What F5 / Ctrl+Enter runs (§6) is decided **in the engine** (`StatementPicker`), not the UI, so a future TUI behaves identically. Rules in priority order: selection (trimmed); the statement the cursor is in or touching; blank space or a single `;` directly below a statement picks that statement; a sheet with one statement runs it from anywhere; otherwise nothing, with a message. The UI asks the engine afresh on every F5 (never a stale answer), and re-analyzes after a 120 ms pause for the margin ▶ and the statement highlight.
- Autocomplete (§5) is computed by the engine from its cached schema (`CompletionEngine`), tolerant of half-typed SQL: tables after FROM/JOIN/INTO/UPDATE, columns after `alias.` and expression keywords, only from tables in the current statement, ambiguous names qualified, quoting from the dialect's `IIdentifierPolicy` (driven by libpg_query's keyword classes, not a list we keep). It offers nothing rather than something wrong (strings, comments, alias positions, unresolved names). Not yet: keywords, FK-driven `JOIN ... ON` ghost text.
- Strict vs Assisted (§3): **Assisted is the default**, switched per sheet from the toolbar; there is no per-connection default (§3), and new sheets open in the preferred mode from user settings. The resolver (`IdentifierResolver`) follows the database's own rules so it never "fixes" valid SQL (`ORDERS` already means `orders`). Findings: *needs quotes* (`Person` for `"Person"`, or a bare reserved word) is auto-applied in Assisted as greyed, non-editable quotes drawn around the word, and what runs is exactly what is shown; in Strict it is a squiggle with a quick fix (Alt+Enter) and running is blocked. *Case mismatch* (`person` for `"Person"`), *quote mix-up* (`name = "bob"` where `bob` is no column; offers `'bob'`) and *ambiguous column* (two tables in scope have it) are squiggles in both modes and never applied silently. Ambiguity blocks running even in Assisted ("nothing is filled in; needs a choice"). Ctrl+Alt+Q accepts all corrections as real text. Resolver limits, deliberately conservative: names it cannot resolve get no comment; derived tables/CTEs suppress guesses; output aliases and function names are skipped; a *bare* reserved-word column (`select user from t`) is not rewritten, because telling it from the clause keyword is unsafe. An *unknown column* (a name that is no column of any table in the statement, e.g. `id` when the key is `"PersonId"`) gets a hint with up to three ranked candidates (ends with what was typed, starts with it, a near miss, contains it; a name two tables share is offered qualified) and a quick fix per candidate, but is **never applied automatically** (a fuzzy guess silently changing a query could run the wrong one) and **never blocks, even in Strict**: only the server knows for sure, and a wrong squiggle must not stop valid SQL. It is silent unless every table in the statement is known (a derived table, CTE or function could own the column) and skips function names, casts, whole-row references (`select p from "Person" p`), aliases given without AS, named arguments, `extract(epoch from x)`, and bare names in ORDER BY / GROUP BY (which may be output columns). Not yet: unknown *tables* (CTE names make that unsafe without a CTE scanner), Copy/save output of corrected SQL, history recording both forms.
- Transaction bar (§7): state always comes from the **server's protocol status** (ReadyForQuery), observed after every statement and after Begin/Commit/Rollback, so the button and a typed `BEGIN` cannot disagree; "changed" is a read-only `pg_current_xact_id_if_assigned()` probe that runs only inside an open block. Green = open, yellow = changes pending (`3 statements · 42 rows changed`, open for N), red = aborted (Rollback only); each state has colour, icon and words. Npgsql exposes the status only on an internal class, so it is read by reflection in **one place** (`PostgresTransactionMonitor`) with a test that fails loudly, with an explanation, if an Npgsql upgrade moves it. (Timestamp tricks were tried and do not work: Npgsql's extended protocol refreshes `statement_timestamp()` on every message.) Everything on a session connection is serialised by a lock held for a statement's whole streaming life, so a probe, a Commit and a stream cannot collide; a new statement cancels the running one. Shortcuts: Commit Ctrl+Shift+Enter, Rollback Ctrl+Shift+R. The host warns before the window closes while changes are pending. Not yet: auto-transaction on first change per connection, the SQL Server write-detection fallback (the interface flag exists).
- Sheets and results (§6): the whole right-hand area is a **tab**, and each tab is a sheet with **its own database session**, so BEGIN/COMMIT, temp tables, SET, the transaction bar and the Strict/Assisted mode are all per sheet. New sheets (+ New sheet, the + on the strip, Ctrl+T) connect themselves on the shared connection; Ctrl+W closes, Ctrl+Tab and Ctrl+Shift+Tab cycle; closing the last sheet leaves a fresh one. A tab shows a coloured dot while its session is in a transaction, so one can't be forgotten behind another tab. Closing a sheet (or reconnecting) with uncommitted changes asks first, because closing the session rolls them back; the host warns the same way before the window closes. Under each editor is a second strip of **result tabs**, each labelled with the start of the SQL that ran (after any corrections): a run fills the unpinned tab, or opens a new one when every tab is pinned, so pinning keeps a result across runs. The engine gained `disconnect` and a quiet `introspect` (refresh a session's schema cache without re-sending the schema), and now releases finished queries. Engine events are routed by connection id (transactions) or query id (results and row batches). Two Monaco facts worth remembering: its keybindings and its completion/quick-fix providers are **global to the page**, not per editor, so each editor tags its own context key and scopes its F5/Ctrl+Enter/Ctrl+. bindings to it (otherwise the last editor created owns F5 for every sheet), and the providers register once and route by model. **The result status bar** is one line above each grid saying what happened, in four states: running (`Running… 120,000 rows so far · 1.4 s`, with a spinner), complete (`1,000,000 rows · 1.07 s`, or `3 rows affected · 4 ms` for INSERT/UPDATE/DELETE), cancelled (`Cancelled after 1.2 s`) and error. A **failure is red** (background, edge, ✖ and words, so it never depends on colour alone) with the server's message as the headline and its code and extra lines (`42703 · POSITION: 30`, `HINT: ...`) beside it, announced to screen readers as an alert; the failed result's **tab also carries a red ✖**, so it is visible with several tabs open. The detailed timings (first batch, first paint, engine split, drawing) are behind a **Details** toggle on the same bar, which stays open across results. The sheet's **message bar** (between the editor and the results) uses the same tones: **amber** with a ⚠ for "cannot run yet" (not connected, including trying to run while disconnected, or a run blocked by identifier problems), **red** with a ✖ when something failed (a connection), neutral for information (`Connecting…`, `nothing to run`). **Cancel works before the first row.** A statement is registered under a query id only once the database starts answering, so a slow aggregate or `pg_sleep` has no id, yet that is when cancelling matters most (and it holds the session lock, so nothing else on that sheet works, and closing the tab would wait it out). The engine therefore keeps a handle on a statement that is still starting: \`cancel\` can name a connection instead of a query, and Cancel, Ctrl+., closing the sheet, a new statement and Begin/Commit/Rollback all cancel it. A statement cancelled before any result reports \`cancelled\`, not an error; inside a transaction the server aborts the transaction, and the bar shows that. Not yet: a script run (Shift+F5) with several result sets per statement, renaming or reordering tabs, and sharing one schema cache between sessions on the same server (each session introspects for itself, about 0.2 s for 2,000 tables).
- Saved connections (§8): provider contract **v2** adds `ConnectionFields` (the UI draws the form from them), `BuildConnectionString` / `ParseConnectionString`, `OpenAsync(..., ConnectionOptions)`, `ReadOnlyEnforcement` and `CheckReadOnly`; a provider throws `ConnectionAuthenticationException` when the password is wrong or missing, and the engine answers `passwordNeeded` so the UI asks (a password typed at that prompt is kept in engine memory for the run, so a second sheet does not ask again; a wrong one is forgotten). The engine owns `ConnectionStore` (`%APPDATA%\SiftDB\connections.json`, format 1, unknown settings kept on rewrite, an unreadable or newer file is reported and never overwritten) and `DpapiSecretStore` (`secrets.json`, one DPAPI CurrentUser blob per connection, with app-specific entropy). The Postgres read-only guard walks libpg_query's parse tree (its protobuf messages' JSON form), so comments, casing and multi-statement runs make no difference; it is checked in `analyzeSheet` (amber "not run") and again in `execute`. Npgsql's `ContainsKey` is true for every keyword it knows, given or not, so parsing a pasted string keeps port and SSL mode only when they differ from the defaults. The app no longer reads `SIFTDB_SPIKE_CONN` (tests still do). The form starts with **Database type**, listing the loaded providers. With one provider it still shows, saying more types are planned, so users can see the app is not Postgres-only by design. A new connection can switch type (the fields are redrawn; typed values carry over where the new type has the same field, and old defaults take the new type's defaults). A saved connection, or one started from a pasted string, keeps its type. Not yet: a file picker for the CA certificate, choosing the type for a pasted connection string (it uses the first provider).
- Auto-transaction (§7): first built as "BEGIN before the first statement that may write" (with a read/write classifier); changed to **always open** because users expect the bar the moment they connect. The engine begins a transaction whenever it observes the session outside one (after connecting, after Commit/Rollback, after a typed COMMIT), and the tracker marks it automatic. The provider only answers `CannotRunInTransaction` (Postgres, from the parse tree: VACUUM, CREATE/DROP DATABASE, ALTER SYSTEM, CREATE/DROP TABLESPACE, CREATE/DROP SUBSCRIPTION, CREATE/DROP INDEX CONCURRENTLY, REINDEX CONCURRENTLY/DATABASE/SYSTEM, CLUSTER of every table, DISCARD ALL). CALL runs inside the transaction: a procedure that COMMITs fails with the server's own message. A typed BEGIN inside the automatic transaction only draws the server's "already in progress" warning.
- Spike leftovers removed: the "fetch all" checkbox and the scroll benchmark button. A result is always fetched to the end, because a statement holds its sheet's session until it is drained; fetch-on-scroll comes back with spill-to-disk (see Large results). The first sheet runs `select version();`, which works on any Postgres database. The repository is LF throughout (`.gitattributes`: `* text=auto eol=lf`).
- Grid columns: each starts as wide as its header and, once the first rows arrive, is fitted to the header and up to 1,000 rows (60 to 480 px), so one huge value cannot take over. Text that does not fit ends in "…" (the cell font is monospaced, so only strings short enough to possibly fit are ever measured, which keeps megabyte values cheap). Dragging a column's edge in the header resizes it, and double-clicking the edge fits it to up to 5,000 loaded rows (up to 1,200 px); a column the user has sized is never refitted. Widths last for one result. Not yet: resizing from the keyboard.
- Grid selection, copy and whole values: the grid takes focus (it is a tab stop) and selects **one rectangle**: click a cell, drag or Shift+click to extend (a drag past the edge scrolls), click the row numbers for whole rows, a header for whole columns, the corner for everything. Whole rows and columns are kept as such, so a column selected while rows still arrive covers them too. Keys: arrows (Ctrl to the edge), Home/End, PageUp/PageDown, Shift to extend, Ctrl+A, Escape back to one cell. The selection is a see-through accent tint with an outline and the focused cell outlined (grey while the grid is not focused, so it is clear where Ctrl+C goes); styled cells keep their colours under it, and the selected rows' numbers and columns' headers are tinted too. **Copy** (Ctrl+C, or Ctrl+Shift+C with the column names first) writes tab-separated text with CRLF lines, which Excel pastes into cells: a value holding a tab, line break or quote is quoted with its quotes doubled, and **NULL is an empty field** (a spreadsheet has no NULL, and the word would be read as text). Only rows that have arrived are copied (the note says so), and more than 2,000,000 values are refused with a note rather than freezing the page. A short note in the grid's corner says what was copied, and screen readers hear it and each cell moved to. **Whole values:** hovering a cut-off value (or one with line breaks) shows it in a tooltip after 450 ms (up to 1,500 characters); a double-click, Enter, or the menu opens the **value viewer**, a modal with the whole value read-only, its length and lines, Wrap lines, **Format JSON** when it is a JSON object or array, and Copy. NULL shows as NULL, "not an empty string". Right-click (or Shift+F10) opens the grid's own menu (Copy, Copy with column names, Select all, Show whole value) in the Windows 11 look; right-clicking inside the selection keeps it. A new run clears the selection. The menu also copies the selection **as CSV or as JSON**, the same text an export writes. Not yet: several separate ranges (Ctrl+click), copying as SQL (INSERT statements, an IN list).
- Sheet files and autosave (§6): the engine reads and writes files (`SheetFile`: the header line taken off and put back, other `--` comments kept, header values quoted when they hold `;`, `"` or `=`, unknown header keys kept in order; UTF-8 with or without a byte-order mark, and UTF-16, kept as they were; line endings normalised to LF for the editor and written back as the file had them, majority wins when mixed; a file that is not valid in its encoding is refused, not read with replacement characters that saving would write back as damage). `SessionStore` keeps `%APPDATA%\SiftDB\session`: `session.json` (the open sheets in order, the active one, each sheet's file, unsaved flag, saved connection id and name, mode, line ending, encoding) and one text file per sheet. The UI sends a sheet's text only when its editor version changed since the last autosave (600 ms after the last change). A sheet with no unsaved edits is restored from its file, which may have changed while SiftDB was closed; one whose file has gone comes back from its autosave, marked unsaved, with a message. Pasted connection strings are never stored. Closing the window: the host asks the page to flush its autosave and closes when it answers (or after 2 s). The native Open and Save As dialogs are the host's (`openFileDialog` / `saveFileDialog`, answered with a path or null). Monaco's experimental EditContext input sent typing to the first editor created once several existed (one per sheet), so the editors use the text-area input (`editContext: false`).
- Running in the background (§6): a sheet's tab shows a small spinner while a statement runs on it (one flat colour; still when the system asks for reduced motion), so you can work on another sheet and see it going. A statement that **finished while its sheet was not on screen and ran 10 s or more** leaves its outcome on the tab: green ✓, red ✖ or amber ■ (cancelled), with "Finished after 2 min 05 s" on hover. The mark stays **until the sheet is looked at**, not for a fixed time, so a long absence cannot hide it. Shorter runs, and runs you watched finish, leave no mark. Not yet: flashing the taskbar button when SiftDB is not the active window.
- Blank lines as statement ends (§6): an engine-side `StatementSplitter` wraps the provider's `ISqlParser` and refines its split, using only `Tokenize` and `Validate`, so it is provider-neutral and a future TUI gets it too; sheet analysis, the identifier resolver and completion all use it. It walks the tokens once (stepping over comments, so an empty line inside a block comment is not blank) and validates only at a blank line followed by a statement word. Cost on a 72 KB, 500-statement sheet (Debug build): 68–84 ms against the provider split's 14–57 ms; an early version re-scanned all tokens per gap and took 1.8 s, so keep it single-pass. `MissingSemicolon` finds a statement that fails as a whole but has a line starting a new statement after complete text; the engine then blocks the run with that message in both modes and drops the resolver's ambiguity complaints for that statement.
- Undoing a failed statement (§7) and provider settings (§10): provider contract **v4** adds `IDatabaseProvider.Settings` (`ProviderSetting` / `SettingChoice` descriptors) and `ApplySettings(values)`, and `ITransactionMonitor.BeforeStatementAsync(sql)` / `AfterStatementAsync(failed)`; all have default implementations (no settings, nothing guarded), since a database that does not abort transactions on errors needs none of it. The engine calls the guard around a statement's **whole life**, up to its last row (an error can come mid-stream), on every failure path (an error before the first row, a cancel before it, a failure while streaming), and reports `statementUndone` in the transaction event. The Postgres monitor decides from the protocol status: it sets `savepoint siftdb_statement` only when `InTransactionBlock` and the text holds no `TransactionStmt` (from the parse tree; unparsable text is guarded), and afterwards rolls back to it only if the status is `InFailedTransactionBlock` (so a cancelled SELECT that streamed fine has nothing "undone"), else releases it. Settings live in `settings.json` as `providerSettings: { postgres: { onError: "undoStatement" } }`; the engine fills in defaults and drops values that are no longer a choice before applying them (at startup and after each save, so open sessions follow from their next statement), refuses a non-choice on save, and keeps the values of providers that are not loaded. Not yet: piggybacking the release on the "changed?" probe to save a round trip.
- User settings (§10): the engine owns `SettingsStore` (`%APPDATA%\SiftDB\settings.json`, format 1), on the same rules as the connection store: written atomically, settings a newer version added are kept on rewrite, and an unreadable or newer file is reported and never overwritten (the defaults apply meanwhile, and the editor says why and cannot save). Values are always valid: anything missing or unrecognised reads as the default. Commands: `getSettings` and `saveSettings` (the whole set), both answered with `settings`. The UI's **Settings…** button (Ctrl+,) opens a modal editor with **categories on the left** (a vertical tab list; arrow keys move within it) and the chosen category's page on the right. Each category is self-contained in `settings.ts` (it draws its page from the current values and writes its part of the set on Save), so a feature adds its settings by adding a category; switching category keeps unsaved edits, Save saves every category, and the editor reopens on the category last viewed in this run. The first category is **SQL**; **Results** (styling) is expected next. The first setting is **New sheets open in** Assisted or Strict (radio buttons, both always shown, each with its explanation); it applies to sheets opened afterwards, and to opened files whose header names no mode, never to sheets already open. Not yet: the bind-variable "type when it cannot be inferred" setting (§4).
- Text size: the **Appearance** category (listed first) has **Zoom**, 100% (the default) to 200% (`zoom` in `settings.json`, in percent; the engine sends the choices as `zoomChoices`, and anything else reads as 100). It is **one zoom for the whole window**, WebView2's `ZoomFactor`, not a font-size variable: text, icons, spacing, the canvas grid (it draws at `devicePixelRatio`, which the zoom raises) and Monaco all grow together, and nothing sized in px gets cut off. The host reads the stored zoom before the page loads, so the first frame is already the right size; after a save the page sends `setZoom` (a host message, clamped to 50 to 300%) and it applies at once. Ctrl+mouse wheel still zooms for the moment and is not saved. Not yet: Ctrl+= / Ctrl+- / Ctrl+0 shortcuts that change the setting.
- Result styling (§10): provider contract **v3** adds `GenericTypeOf(dataTypeName)` and the `GenericType` enum in Abstractions; the engine sends each result column's generic type (`type`) in the `columns` event, from `DbDataReader.GetDataTypeName` (checked against a real server: `int8` → integer, `numeric(10,2)` → decimal, `bit(1)` → boolean, `timestamp` and `timestamptz` → dateTime, arrays and `uuid` → text). The engine owns the rule model (`StyleRules`): it validates user rules on save (a bad rule is refused with the reason, nothing written) and drops bad ones when reading a file; preset entries store only id and on/off. Settings carry the list as `resultRules`. The UI matches rules to columns once per result (`styles.ts`: a per-column styler, null when no rule applies, so an unstyled column costs nothing to draw) and again when the rules change, so a save restyles open results at once. The **Results** settings page lists every rule with an example cell in its colours; new rules go to the top. The rule form shows a live example and warns below 4.5:1 contrast (WCAG AA); the four presets all pass it. Colours are picked with the native colour input (so `#rrggbb`; `#rgb` is accepted from the file). Dates and times are shown in ISO form (`2026-11-12`, `2026-11-12 14:05:09.5`, a trailing `Z` for a UTC timestamp) rather than the invariant culture's US style, chosen by the column's type (`date` shows no time) so a column never mixes formats; date ranges compare these. Not yet: by-table rules (`orders.status`), which need each result column's source table: Npgsql offers it only through `GetColumnSchema()`, which costs a catalog query per result, so it needs a look at reading it from the row description instead. Now possible: F4 reads each column's origin from the row description at no cost (§11, *Follow foreign keys*), and the `columns` event carries it as `origin`.
- Delivery (§9): **`build/release.ps1`** makes a release: `dotnet publish` of the host, self-contained for win-x64 (not single-file, since Velopack's deltas work per file), then `vpk pack` (Velopack 1.2.161; the CLI is pinned in `dotnet-tools.json`) into `artifacts/releases`: `SiftDB-win-Setup.exe`, full and delta `.nupkg`s, `releases.<channel>.json` and a portable zip. The pack id is `SiftDB`, so it installs per user into `%LOCALAPPDATA%\SiftDB` with no admin prompt. The exe is `SiftDB.exe` (the host's assembly name). `--framework webview2` makes Setup install the WebView2 Runtime when it is missing; the host also checks at startup (a copy run without installing) and offers Microsoft's download page instead of crashing. **Releasing** (`.github/workflows/release.yml`): *Actions › Release › Run workflow* on `main`, choosing the **channel** and the **bump** (auto, the default, works it out from the commit messages; see F17 for the rest). **Stable** (`win`, the default channel) gives `vX.Y.Z`, the repository's *Latest* release, and covers everything since the last stable release, the betas' changes included. **Beta** gives `vX.Y.Z-beta.N`, a GitHub pre-release, and covers what changed since the last release of either kind. The usual way is a beta first (to check the installer, the notes and What's New on a beta copy), then a stable release of the same version: the stable run is not stopped by the beta tag in between. Each copy updates only along the channel it was installed from, so a stable copy never sees a beta, and a beta copy gets the stable release too once it is newer. A tag pushed by hand (`v0.2.0`, `v0.2.0-beta.1`) still releases exactly that tag. Either way the workflow downloads the previous release first so deltas are built, then `vpk upload github` publishes, and the generated notes become the release's description. Releases so far: 0.1.0 to 0.1.4 (tags pushed by hand), 0.2.0-beta.1 and 0.2.0 (the first from the workflow, 2026-11-18). A local build reports the csproj's `<Version>`. Velopack runs first in `Program.Main` (it handles the installer's hooks and applies an update that was downloaded but not restarted into). **Updates** (`Updater`): only an installed copy checks, 10 s after the page loads and then every 4 hours, against GitHub Releases without a token (prereleases included; the channel the copy was installed from decides which feed it reads, so stable never sees beta). A newer release downloads quietly, then an accent-coloured **Restart to update to X** button appears in the toolbar. Clicking it goes through the usual close (the uncommitted-changes warning, the last autosave), and the updater applies the update and restarts SiftDB; declining the warning cancels the update too. Ignoring the button is fine: the update is applied the next time SiftDB starts. Failures (offline, rate-limited) are silent and retried at the next check. WebView2's data folder (the page's localStorage) is `%LOCALAPPDATA%\SiftDB\WebView2`, not the default next to the exe, which every update replaces. **CI** (`.github/workflows/ci.yml`, on every push to main and pull request, and called by the release workflow so a release waits for it): the engine and provider tests on Linux against a `postgres:18` service container (`SIFTDB_SPIKE_CONN` points at it, so no database test skips), and a Windows build of the host and UI. Test waits that only matter when something hangs allow 15 s, so a slow runner does not fail them. **The app icon** is a database whose base narrows into a funnel, sifting out a drop, white on a blue tile: drawn in `src/SiftDB.Host/Assets/SiftDB.svg` and rendered to `SiftDB.ico` (16 to 256 px) by `build/icon` (run by hand after changing the SVG); it is the exe's, the window's and Setup's icon. Not yet: code signing, a setting to turn update checks off. Release notes are built (F17, §11).
- Toolbar icons: the toolbar buttons are icons, not words, each 16 px in **one colour that says what it does**: green to go (Run ▶, Begin ⊕, End ✓), amber to Commit ✓ (the colour of changes pending), red to stop (Cancel ■, Rollback ✕), the accent for Connect, and the text colour for the rest (edit, new and save connection, open, save, new sheet, settings). They use the transaction bar's colours, so they follow light and dark. Each has a tooltip with its shortcut and an `aria-label`. The icons live in `icons.ts` (`applyIcons` fills each `[data-icon]` element). Assisted | Strict stays as words: it is a mode, and the words are the meaning. The result status bar's buttons are icons too: **Export** a green table with an arrow leaving it (while an export runs it turns into a red stop with the percentage beside it, and cancels), and **Details** a chevron pointing down when the timings are hidden and up when they show (`aria-expanded` says which).
- Grid editing: none for now. The grid is read-only. Revisit later (it would interact with transactions and the lock).
- Export formats: CSV and JSON are enough for now. Import is not decided.
- Export (built): **Export…** on the result status bar (once the statement has finished, a cancelled one too) and **Export result… / Export selection…** in the grid menu. The host's Save dialog lists *CSV* and *JSON* as its file types, and that is the format choice (a typed `.json` or `.csv` wins over the list); the file name is suggested from the first table the statement reads (`select * from sales.orders` → `orders.csv`), and the format and folder are remembered for the run. The rows live in the page (the engine lets a statement go once streamed), so **the page formats and the engine writes**: `exportBegin {path, byteOrderMark}` → `exportStarted {exportId}`, then `exportWrite {exportId, text}` a piece (about 1 M characters) at a time, each awaited so pieces arrive in order and never pile up, then `exportEnd {exportId, cancel}` → `exportFinished`. The engine writes to `<file>.siftdb-export.tmp` beside the target and moves it into place only at the end, so a failed or cancelled export leaves no half-written file and the file it would replace is untouched (a target open in Excel fails at that point, with the old file kept). While it runs the button reads *Cancel export · 45%*; a new run on the tab, or closing it, cancels; unfinished exports are thrown away when the engine stops. **CSV:** RFC 4180, comma, CRLF, a header line, UTF-8 **with** a byte-order mark (Excel needs it); NULL is an empty field and an empty string is `""`, so they stay apart. **JSON:** UTF-8 without a BOM, an array with one object per row, one per line; numbers stay numbers with the database's own digits (a bigint or numeric keeps every digit), NaN and Infinity become strings, booleans become true/false, everything else (dates in ISO form, json columns too) is a string; a repeated column name becomes `id_2`, `id_3`, since JSON readers keep one of two equal keys. Not yet: json/jsonb columns written as JSON values (needs the database type, not just the generic one), other delimiters, exporting straight from the database without running first (re-running a statement could repeat its side effects).
- Query history retention: deferred until later.
- Missing WHERE alarm (F1, §14): provider contract **v5** adds `IDatabaseProvider.ChangesEveryRow(sql)` (the tables a run changes every row of: UPDATE or DELETE with no WHERE, `UPDATE ... FROM` and `DELETE ... USING` included, and TRUNCATE) and `IProviderConnection.DescribeTableAsync(schema, table)` (temporary? estimated rows?), both with "not supported" defaults. It is on the provider, not on `ISqlParser` as first planned, because the engine's `StatementSplitter` wraps the parser and a default method there would be silently lost; it sits beside `CheckReadOnly` and `CannotRunInTransaction`, which work the same way. Postgres reads the parse tree (`PostgresWholeTableRules`), so comments and casing make no difference, every statement in a run counts, and so does a data-changing CTE; a WHERE written on purpose (`where true`) is the user's choice, and function bodies and DO blocks are not looked into. **The table is described on the sheet's own session**, not from the schema cache, so its temp tables and tables its open transaction created are seen and a bare name resolves as the statement's will (`pg_table_is_visible`, pg_temp first); the estimate is `pg_class.reltuples`, with no scan. It is asked only while the session is idle (a running statement is never held up to ask), and an estimate of 0 or -1 (never analysed) reads as "every row" rather than "about 0". **Flow:** before every run the sheet sends `checkRun` and gets `runCheck {allRows}` (the warning, or null); with a warning a red bar above the editor asks *Run anyway* / *Cancel*, Cancel focused so Enter and Esc are the safe answers, and nothing runs until Run anyway. Editing the text or disconnecting dismisses the bar. `execute` carries `allRowsChecked`; without it the engine checks itself and refuses ("Not run: … Confirm it before it runs.") before interrupting anything, so a client that skips the question cannot skip the warning. **Wording** comes from the engine: "This DELETE has no WHERE, so it deletes every row in orders (about 1.2M rows).", then either "It runs inside the open transaction, so Rollback can still undo it." (an open or automatic transaction) or "No transaction is open, so it cannot be undone."; several tables are listed in one sentence. **No warning** for temporary tables, on read-only connections (the server refuses), in an aborted transaction (it cannot run until the rollback), or with the setting off: **Warn before UPDATE or DELETE without WHERE** (`warnAllRows`, on by default) in the SQL category. The bar also offers *Dry run* (F2) when the statement can have one. Not yet: the per-statement check in a script run (Shift+F5, not built).
- Dry run (F2, §14): provider contract **v6** adds `IProviderConnection.DryRun` (`IDryRunSupport`: `Plan(sql)`, `BeginAsync`, `EndAsync`), null by default. **Old and new values come from Postgres 18's `RETURNING WITH (OLD AS …, NEW AS …)`**, in the one statement, rather than the planned "select the target rows by key first and match them up": nothing to match, no extra query, and exact even for rows an UPDATE ... FROM touches. Before 18 an UPDATE shows the values after only, and the bar says the values before need PostgreSQL 18; a DELETE returns the rows it removes on any version. The dry run **replaces the statement's own RETURNING** (or adds one), found in the scanner's tokens as the RETURNING keyword outside any parentheses, so a CTE's is left alone and comments and strings are never touched; it returns only the **target table's** columns (`alias.*`, or `siftdb_old.*, siftdb_new.*`), since `RETURNING *` would add an UPDATE's FROM tables. **Mechanism:** inside a transaction `savepoint siftdb_dryrun`, then `rollback to savepoint` and `release`; outside one `begin` … `rollback`. The rollback runs once the reader is closed, whether the statement finished, failed (also part way: rolling back to a savepoint works in an aborted transaction) or was cancelled, before the session lock is released, so nothing can run in between. If the rollback itself fails the sheet is told loudly ("could not be undone … roll back the transaction to be sure"). **The bar stays green:** Postgres assigns the whole transaction an id as soon as the savepoint writes, rolled back or not, so the "has written" probe alone would turn an unchanged transaction yellow; the engine keeps it Open when it was Open before (`keepUnchanged` on the observation), and a transaction with earlier changes stays Changed. **Refused** (as the result's error): a read-only connection, a failed transaction, anything but one UPDATE or DELETE (several statements, INSERT, TRUNCATE, DDL, transaction control), a data-changing WITH (its changes would be unseen), SQL that does not parse. **Cap:** `QuerySession` sends the first 10,000 rows and reads the rest without encoding them, so the full count comes back as rows affected. **UI:** Ctrl+Shift+F5 (an editor action, so it is in the editor's right-click menu and F1 with its shortcut, and a page-wide key), the ▾ beside the toolbar's Run button (a split button: Run, Dry run), *Dry run statement* on the margin ▶'s right-click menu (Monaco has no menu of its own on the glyph margin), and *Dry run* on the missing-WHERE bar. The result tab is labelled "Dry run: …", and it and its bar are striped amber; the bar reads "Dry run: would change 42 rows · rolled back, nothing changed" (and "showing the first 10,000" when capped), with a **Before | After** switch for an UPDATE (it opens on After), an ⓘ with the caveats (triggers fire and are rolled back, sequences are not, nothing outside the database is undone, NOTIFY is not sent, rows are locked while it runs), and **Run for real**, which runs the statement straight away (the user has just seen what it does, so the missing-WHERE question is not asked again). The grid shows one half of the columns (`CompareView`: displayed column c reads result column c + base): a value that would change is tinted amber with a mark on its left edge, a row that would be deleted is tinted red, and hovering a changed value shows Before and After; copy, export and the value viewer use the half shown. Not yet: dry runs of INSERT and MERGE (RETURNING would serve them too), DDL (deliberately later), "only changed rows/columns" filters, and EXPLAIN ANALYZE of writes through the dry run (F6).
- Format SQL (F7, §14): the layout is the engine's (`SqlFormatter`), from the provider's scanner tokens only, so it is provider-neutral; the provider adds **`IDatabaseProvider.SameMeaning(a, b)`** (contract **v7**, on the provider rather than `ISqlParser` for the same reason as F1's check, null by default, and then nothing is formatted). **The promise is checked, not assumed:** a statement's new text is applied only if (1) it has exactly the same tokens in the same order with the same text, keywords compared without case (`SqlFormatter.SameTokens`: comments count, which the parse tree cannot see), (2) the provider says it means the same (Postgres: libpg_query's parse trees equal once every position field is removed: `location`, every `*_location`, `list_start`/`list_end`, `rexpr_list_start`/`rexpr_list_end`, `stmt_len`, a list taken from the parser's own message definitions, with a test that fails if a new one appears), and (3) it is still one statement (§6's blank-line rule: a formatted statement never contains a blank line). Otherwise it is left alone and the message bar says so. **What is laid out:** SELECT, WITH, INSERT, UPDATE, DELETE, VALUES, TABLE and EXPLAIN of them. A line per clause (WITH, SELECT, FROM, WHERE, GROUP BY, HAVING, WINDOW, ORDER BY, LIMIT/OFFSET/FETCH, FOR UPDATE, UNION/INTERSECT/EXCEPT, INSERT INTO, VALUES, UPDATE, SET, DELETE FROM, USING, ON CONFLICT, DO, RETURNING) and per join (`left outer join` and the rest, only after FROM); a join's ON, and every AND/OR of WHERE, HAVING and ON, on an indented line (not BETWEEN's AND, nor any inside a CASE); a subquery, CTE body or EXISTS/IN subquery indented in its parentheses, with the closing parenthesis back on the opening line's indent; CTEs one after another at the left. A SELECT, SET, GROUP BY, ORDER BY, VALUES or RETURNING list goes one item per line when the clause would pass the line width (or holds a line comment or a subquery). Clause words inside calls and other parentheses stay where they are (`extract(year from d)`, `over (partition by … order by …)`, `is distinct from`). Spacing: one space between tokens, none inside parentheses and brackets, around `.` and `::`, or before `,`; spaces round binary operators; a sign or prefix operator (`-1`, `@name`), a call's `(`, a subscript and a slice are spaced as written. Comments stay on their line: a comment at the end of a line stays at the end of its item's line, one on a line of its own gets one again; with leading commas, a comma followed by a comment stays at the end of its line, because moving the comment in front of it would change the token order, which check (1) forbids. **Anything else** (CREATE TABLE, GRANT, DO…) is left exactly as written, case included: its layout is usually deliberate, and which of its words are names cannot be told from tokens. **Keyword case** changes reserved and type-or-function keywords plus a few others that are almost never names (BY, SET, VALUES, OVER, PARTITION, BETWEEN, EXISTS, INTERVAL…), never a word that may be a column (`name`, `type`). **Commands:** Shift+Alt+F (taken from Monaco's Format Document, which has no formatter here) formats every statement the selection touches, or the one under the cursor; *Format sheet* (the editor's right-click menu and F1) formats them all. The engine answers with **one edit** covering the first to the last statement it formatted (what lies between, comments and blank lines, kept), so one Ctrl+Z undoes it; it is applied only if the editor's text has not changed since it was asked for. A statement that does not parse is left with "line N does not parse (…): fix it first", and the rest are still formatted ("Formatted 2 of 3 statements; …"). A statement that starts indented keeps that indent on every line. **Settings** (SQL → Format SQL, in `settings.json` as `format`): keywords as typed (default), UPPER or lower; indent 2, 4 (default) or 8 spaces; commas at the end (default) or start of lines; line width 40 to 400 (default 100). Not yet: format on save, CREATE VIEW … AS SELECT laid out, breaking long expressions and CASE across lines.
- Expand `*` (F10, §14): the engine's `StarExpander` finds each `*` that can be expanded and sends it with the sheet analysis as an **action**: a quick fix with no problem behind it, so there is no squiggle, only the lightbulb (and Alt+Enter) while the cursor is on it. It is offered as the second choice ("Expand * to the 3 columns of customer"), never preferred over the fix for a real problem. The columns come from the schema cache in table order, written with the dialect's `IIdentifierPolicy` (quoted only where needed: `"Id"`, `"select"`); a bare `*` over several tables gives every table's columns qualified by its alias as written (or the table's name as the dialect needs it, so a qualifier is never one the identifier checker would correct), `o.*` that table's, qualified the same way, so the same column name in two tables is never ambiguous. The list is on one line if it fits the formatter's line width (F7), otherwise one column per line, indented one step past the line, with the formatter's comma placement. **Offered only where the result is certainly what `*` returns:** a plain SELECT (not INSERT ... SELECT or RETURNING) whose every table is in the schema cache. Not with a CTE, a derived table, a function in FROM, any subquery (the scope analysis is flat, so a subquery's tables would wrongly join the list), UNION/INTERSECT/EXCEPT, a `USING` or `NATURAL` join (`*` shows the shared column once there), or a column alias list (`t AS x(a, b)` renames them); never in `count(*)` or other arguments, nor for `schema.table.*`. Not yet: expanding through CTEs and subqueries (needs a scope analysis that knows nesting), and the `USING` join's merged column.
- Follow foreign keys (F4, §14): provider contract **v8** adds, on `IProviderConnection`, `ColumnOrigins(reader)`, `PeekRowAsync(RowKey, sessionIdle)` and `CountRowsAsync(RowKey, cap, timeout)`, all "not offered" by default. **Origins cost nothing:** Postgres sends each result column's table oid and column number in every RowDescription; Npgsql exposes that publicly only through `GetColumnSchema()`, which runs a catalog query on a second connection per result, so `PostgresRowDescription` reads Npgsql's internal `RowDescription` field by reflection, in one place, with a test that fails loudly if an upgrade moves it (results then simply have no links). The names come from a map the provider keeps from its last introspection (oid to schema and table, column number to name), so a table created since has no origin until the schema is refreshed. The `columns` event now carries each column's `origin`, plus `links` (each foreign key whose every column is in the result: its places, and the table and columns it references) and `keys` (each key of a table, its primary key or a unique one, that a foreign key points at and whose every column is in the result, for "Referenced by"; so `orders.customer_email -> customer.email` works as well as a key on the id); the engine works them out from the schema cache, the UI only draws them. **What can be followed shows before anything is clicked:** the header marks a link column with ↗ and a referenced key column with ↙ (drawn arrows, not font glyphs). **Hover** such a cell (not NULL) and a small ↗ or ↙ appears at its right edge, in room the column always keeps free for it (text stops short of it, numbers end before it, fitting counts it), so it never covers the value; with a hand and a tooltip; clicking ↗ opens the row the link points at, clicking ↙ asks what references this row. A hovered link is also underlined; after the tooltip delay a card shows the row it points at, its first 8 columns as name and value ("No Customer row with Id = 42" when there is none), cached per value for the result. The lookup runs on the **sheet's own session when it is idle**, so its own uncommitted rows are seen; inside a transaction it runs behind `savepoint siftdb_peek` (it only reads, but a value the column's type refuses would otherwise abort the transaction; a test proves the transaction survives one). When the session is busy or its transaction has failed it uses a side connection, and the card says "(committed data)". Values go as parameters of no declared type, so the server reads each as its column's type. **Ctrl+click** (or **Ctrl+Enter** on the focused cell; plain Enter stays the value viewer, so it means the same on every cell) and the grid menu's *Open referenced row* add a real statement at the end of the sheet (`select * from "Customer" where "Id" = 42;`, a number written bare, anything else as a string literal, the schema unless `public`), as its own undoable edit, and run it in a **new** result tab, so the tab it came from stays. **Referenced by…** (the ↙, or the grid menu, on a row whose table has a referenced key in the result; with several keys the menu names each, "email" or "Id, the primary key") finds every foreign key pointing at the table through exactly that key's columns, counts each one's rows on a side connection (to 1,000, then "1,000+"; given up after 2 s: "too slow to count"), and offers them as a menu; choosing one opens those rows the same way. Not yet: links in the record view (F8).
- Schema explorer (F13, §14): provider contract **v9** adds, on `IProviderConnection`, `DescribeRelationAsync(schema, relation)` (`RelationDetail`: comment, row estimate, total size, columns' identity / generated / comment, primary and unique keys, foreign keys with their ON DELETE / ON UPDATE actions and deferrability, indexes with method, keys, predicate, size and the constraint they back, checks and exclusion constraints, triggers with timing, events, function and whether disabled, partitions with their bounds, a view's definition), `DescribeSchemaObjectsAsync(schema)` (functions with arguments and result, sequences with their type and owning column, enum, domain, composite and range types; objects that belong to an extension are left out) and `ScriptAsync(ObjectRef, create)`, all "not offered" by default, each run on a connection of its own. **The fast schema load is unchanged:** details load the first time a table is opened, a schema's other objects the first time the schema is, and both are kept until the schema is refreshed (a new schema object for the root drops them). Definitions are Postgres's own text (`pg_get_constraintdef`, `pg_get_indexdef`, `pg_get_triggerdef`, `pg_get_viewdef`, `pg_get_functiondef`). **The tree:** a schema groups its relations as Tables, Views, Materialized views and Foreign tables, then Functions, Sequences and Types (enums list their values); a table opens into Columns (type, not null, PK / FK / ID / GEN badges, the default dimmed inline, the comment and generated expression in the tooltip), Keys, Foreign keys, Referenced by (worked out from every table's foreign keys in the schema already loaded, so no query), Indexes, Checks, Triggers and Partitions, each with its count and shown only when it has something; a view adds its Definition. Until a table's details arrive it shows what the fast load has, with "loading details…". The table row carries its row estimate and size once known (`≈ 1.2M rows · 340 MB`), and its comment in the tooltip; every item's tooltip is its full definition. A group shows at most 500 rows, with "filter to see more". **Interaction:** a foreign key, a "Referenced by" entry or a partition is drawn as a link, and clicking it (or Enter) opens the way to that table and brings it into view; a foreign key goes on down to the columns it references, and a "Referenced by" entry to the other table's key columns, opening the table on its Columns and highlighting them (a partition lands on its table). Any node can be **dragged into the editor**: a table, function, sequence or type brings its schema-qualified name, a column or item its name, each written as SQL needs it (the engine sends, with the schema and with each detail, the names that must be quoted). **Right-click** (or Shift+F10): *Copy name*, *Copy definition*, *Script as CREATE / SELECT / DROP* and *Refresh schema*. The script goes into the visible sheet at the cursor, on lines of its own and selected, and is **never run**. CREATE scripts are the database's own DDL (a table's columns with identity, generated, defaults, collation and NOT NULL, its constraints, partition key, the indexes that back no constraint, triggers and comments; a view or materialized view with its definition; a function from `pg_get_functiondef`, picked among overloads by its arguments; a sequence; an enum, domain, composite or range type). A test runs each one for real (drop, then create, in a transaction rolled back). DROP never adds CASCADE: a script that goes further than asked is not written for the user. SELECT names every column, laid out by Format SQL. The filter's **Cols** toggle matches column names too, and shows each matching table opened on just its matching columns. `*` on a table opens it and all its groups. Not yet: *Describe* (`\d`, F9), dragging a column as `alias.column` when the statement at the cursor has that table, and scripting foreign tables (they say so instead).
- Release notes and What's New (F17, §14): **commit subjects follow Conventional Commits**, and the rule is in `CLAUDE.md` (feat / fix / perf for what users notice, `(ui)` for small UI improvements, refactor / test / docs / chore / build / ci left out, `!` for anything that breaks files or settings, an optional `Try-it: <command id>` footer). **`cliff.toml`** (git-cliff 2.10.1, installed from npm into the runner's temp folder and its executable called directly: through npx, npm took git-cliff's options as its own) groups the commits since the previous release: anything with `!` as *Breaking changes* first, `feat` as *Highlights*, `perf` and `feat(ui)` / `fix(ui)` as *Improvements*, `fix` as *Fixes*; commits that follow no convention are *Other changes*, so the history from before the rule is not lost; a `Try-it` footer becomes `([Try it](command:id))`. **The release workflow** is now *Actions › Release › Run workflow* (channel stable or beta, bump auto / patch / minor / major). It works the version out itself (from the last stable tag: `!` → major, any `feat` → minor, otherwise patch; beta adds `-beta.N`). **What a release covers is a plain git range the workflow works out**, not git-cliff's "unreleased" (which starts at the newest tag of any kind, so a stable release right after a beta found nothing): a stable release covers everything since the last stable tag, its betas' changes included (git-cliff passes over the beta tags, `--ignore-tags -`); a beta covers what changed since the last tag of either kind. Whether anything user-visible is in that range is decided from the commit subjects with the same rules as cliff.toml, and the run stops with a notice when there is nothing. It writes the release's notes (from that range) and the whole CHANGELOG.md, stops before committing or tagging anything if the notes came out without an entry, commits CHANGELOG.md (`chore(release): vX [skip ci]`) and tags that commit, builds, and publishes; the notes are the GitHub release's description (`gh release edit`) and Velopack's (`vpk pack --releaseNotes`, through `build/release.ps1 -ReleaseNotes`). A tag pushed by hand still releases that tag. The version bump is plain PowerShell in the workflow rather than git-cliff's own, so it is easy to read and to override. **In the app:** CHANGELOG.md is an embedded resource of the engine (only when it exists: a local build has none and shows none), read by `ReleaseNotes` (the changelog's own layout: `## version - date`, `### group`, `- entry`; versions ordered as semantic versions, pre-releases before their release). At startup the UI sends the running version (the host's) with `whatsNew`, and **the engine decides**: a fresh install shows nothing; with no version recorded but files an earlier run left behind (a session, saved connections or settings, as found when the stores are made at startup, before this run writes any), it is an update from a version older than What's New, which recorded nothing, and the window shows the release just installed; a major or minor update opens What's New with every release since the last one seen (skipped ones too, newest first); a patch-only update shows a small notice bottom left ("Updated to 1.4.1, 3 fixes · What's new", gone after 30 s); nothing for an older build or one with no notes for its version. The version is recorded as seen (`lastSeenVersion` in `settings.json`) then, so it never shows twice, and an older build never moves it back. **What's New is a window over the restored sheets, not a tab** as first planned: every part of the app takes the active tab to be a sheet (the toolbar, F5, the connection picker), so a tab that is not one would have needed that reworked, with the risk of a key reaching a hidden sheet. Breaking changes and highlights come first and open; longer sections start folded. Text is set as text, never as HTML, with `code` drawn as code. A Try it button runs one of the app's `COMMANDS` (main.ts: format, formatSheet, dryRun, run, newSheet, settings, refreshSchema, openFile) and closes the window; an id this build lacks shows no button. **Settings › About** (listed last) shows the version, *What's new…* (every release this build has notes for) and **Show What's New**: after major and minor updates (the default), after every update, or never. Not yet: the optional AI polish of the notes, Try it reaching the command palette (F12), and a pack's CHANGELOG.md in the same window (F16).
- Audience: anyone (a public product, not just personal use). This raises the bar on installer, signing, docs and the plugin contract.

**Not discussed yet**
- Schema browser scope: the object tree, definitions, *Script as* and column-name search are built (F13, §11); ER diagrams are still open (the query mini-map, F5, covers one statement only)
- Query history: how it's searched, whether it's per connection
- Licensing (plugin SDK, open source or closed): not a concern for now, and monetisation isn't a goal at this stage

---

## 12. Suggested solution layout

```
SiftDB.sln
├─ src/
│  ├─ SiftDB.Host/                  WPF + WebView2 shell
│  ├─ SiftDB.Engine/                execution, streaming, variables, transactions, local state
│  ├─ SiftDB.Abstractions/          provider contracts (IDatabaseProvider, IIdentifierPolicy, ITransactionMonitor…)
│  ├─ SiftDB.Bridge/                message contracts + serialisation shared by host and engine
│  ├─ SiftDB.Providers.Postgres/    Npgsql + libpg_query
│  └─ ui/                           TypeScript front end (Vite), Monaco, grid
├─ tests/
│  ├─ SiftDB.Engine.Tests/
│  └─ SiftDB.Providers.Postgres.Tests/   (Testcontainers Postgres)
└─ docs/
   └─ DESIGN.md                     this file
```

---

## 13. First piece of work: the spike

Prove the riskiest part before building anything else:

1. A bare WPF host with a full-size WebView2 loading the bundled UI
2. A typed bridge: `connect`, `execute`, `fetchBatch`, `cancel`
3. A minimal Postgres provider that streams a `DbDataReader` in batches
4. A canvas grid showing **1M rows** from `generate_series`, scrolling smoothly
5. Measure the time to first rows, total transfer time, memory use, and how smooth scrolling feels

The spike succeeds if the first rows appear in under 1s, scrolling stays smooth, and memory stays bounded.

**Running it**
1. Optionally set `SIFTDB_SPIKE_CONN` to a Npgsql connection string (it prefills the box).
2. `dotnet run --project src/SiftDB.Host`. The .NET build runs `npm install` and `npm run build` itself when a UI source file is newer than the last bundle, then copies the bundle next to the exe. After a UI change, close the app and run again. Use `-p:SkipUiBuild=true` to skip the npm step.
3. Connect, Run, and use "Scroll benchmark". The stats line shows time to first batch, first paint, transfer complete, bytes, JS heap and draw/scroll frame times.

**Results** (2026-11-11, local Postgres 18.6, `select i, i::bigint*3, random(), md5(i::text) from generate_series(1,1000000)`, 120Hz display)

| Measure | Result | Target |
|---|---|---|
| Time to first batch / first paint | 187 ms / 189 ms | under 1 s |
| Transfer complete (1M rows, 55 batches, 53.4 MB) | **1.07 s in Release** (engine 1.06 s: read 183 ms, encode 706 ms, blocked on UI 2 ms). 5.1 s in a Debug build. | none set |
| Time to first batch / first paint, Release | 125 ms / 135 ms | under 1 s |
| Scroll, top to bottom in 6 s | p50 8.3 ms, p95 8.5 ms, max 8.8 ms, 0 frames over 20 ms | smooth |
| Draw time per frame | avg 0.51 ms, max 2.2 ms | – |
| JS heap after load | 54 MB | bounded |

Reading: first rows and scrolling pass comfortably. The bridge and UI add about 8 ms to the engine's own time, and the engine was blocked on the UI for only 2 ms, so the fetch loop does not stall the reader. The first measurement (5.1 s) was a Debug build; always benchmark in Release (`dotnet run -c Release`). Encoding text columns (one string per cell) is the largest remaining engine cost and the first thing to optimise if needed. For comparison, `psql` fetches the same rows in about 0.97 s. Engine-only numbers can be reproduced without the UI via the `StreamingBenchmark` test (set `SIFTDB_SPIKE_CONN`). Memory scales linearly with rows (heap is about the size of the data), so it is only bounded once the spill-to-disk cap from §11 exists.

**Known spike limitations:** no selection or copy; the scroll height is plain DOM, so it tops out around 33M px (about 1.5M rows at 22px); an oversized single row isn't specially handled; no spill-to-disk.

---

## 14. Planned features

Agreed 2026-11-17, merged here 2026-11-18. Seventeen features, in suggested build order. Each follows the existing rules: the **engine** decides and the UI only draws, **providers** supply anything specific to one database (new optional capabilities in the provider contract), and anything not implemented degrades quietly. References like "feature 9" or "(12)" mean the features below; `§` numbers mean the sections of this document.

**Tracking:** the Status column is the list of what is left to build. When a feature (or part of one) is built, set its status here, and record what was built and decided in §11 the way earlier features are, with a "Not yet:" list for the rest.

| # | Feature | Builds on | Status |
|---|---|---|---|
| F1 | Missing WHERE alarm | Parser, sheet analysis | **Built** (§11) |
| F2 | Dry run for UPDATE / DELETE | Savepoint guard (§7), transaction monitor | **Built** (§11); EXPLAIN ANALYZE use waits for F6 |
| F3 | Who's blocking me | Session per sheet, background-run spinner | Not started |
| F4 | Follow foreign keys | Schema cache, result columns, grid | **Built** (§11); record view (F8) part waits for F8 |
| F5 | Query mini-map | `ScopeAnalyzer`, `StatementSplitter`, sheet analysis | Not started |
| F6 | Visual EXPLAIN | Parser, dry run (for EXPLAIN ANALYZE of writes) | Not started |
| F7 | Format SQL | Parser (scanner tokens, parse trees) | **Built** (§11); format on save later |
| F8 | Record view (one or two rows, vertical) | Results grid, result styling, follow keys | Not started |
| F9 | Backslash commands (`\d`, `\dt`, …) | Schema model, statement splitter, record view | Not started |
| F10 | Expand `*` | Identifier resolver, `ScopeAnalyzer`, formatter settings | **Built** (§11); CTEs and subqueries later |
| F11 | Paste a list as `IN (…)` | Resolver (column types), editor paste handling | Not started |
| F12 | App-wide command palette | Monaco's existing F1 palette, every feature's commands | Not started |
| F13 | Richer schema explorer (keys, constraints, indexes, triggers…) | `SchemaModel`, `PostgresIntrospector`, schema tree (`tree.ts`) | **Built** (§11); *Describe* waits for F9 |
| F14 | Saved queries as palette commands | Command palette (F12), `SheetFile` header, bind variables (§4) | Not started |
| F15 | Link templates (open a URL with values from a result) | Command registry (F12), result styling's column matching, host | Not started |
| F16 | Extension packs, code-free (**low priority**) | F14 and F15, settings store | Not started |
| F17 | Release notes and a What's New tab | Velopack, GitHub Actions release (§9), settings store, tabs, command registry | **Built** (§11), as a window; AI polish later |

Features F7–F12 are small and independent, so they can be slotted in at any point, e.g. as quick wins between the bigger ones.

### F1. Missing WHERE alarm

**What:** `DELETE FROM orders` or `UPDATE orders SET status = 'x'` without a `WHERE` gets a clear warning **before it runs**: "This affects every row in `orders` (about 1.2M rows)."

- **Detected from the parse tree**, not text: an `UpdateStmt` / `DeleteStmt` with no `whereClause`. Also covers `TRUNCATE`. (`DELETE ... USING` with no WHERE counts too.)
- **Row estimate** from the provider (Postgres: `pg_class.reltuples` for the table, which needs no scan). Shown as "about N rows". If the estimate isn't available, say "every row".
- **Confirmation** in a red banner above the editor: *Run anyway* / *Cancel*. The keyboard default is Cancel. Applies in both Strict and Assisted modes.
- Not shown on read-only connections (the server refuses anyway) or for temp tables.
- **Setting:** "Warn before UPDATE/DELETE without WHERE" (on by default), under the SQL category.
- **Ties in with dry run:** the banner offers **Dry run** as a third button.
- Provider-neutral: the provider's parser reports `AffectsAllRows(table)`. The engine owns the decision and the message.

### F2. Dry run for UPDATE / DELETE

**What:** run a data-changing statement for real, show exactly what it would do, then roll it back. "This will change 42 rows", with before and after values.

- **Mechanism:**
  - Transaction already open: `SAVEPOINT siftdb_dryrun` → run → capture → `ROLLBACK TO SAVEPOINT`.
  - No transaction open: `BEGIN` → run → capture → `ROLLBACK`. The session ends up exactly as it was.
- **Capturing results:**
  - The affected rows come from `RETURNING *`, added by the engine if the statement doesn't have it already. For DELETE these are the deleted rows; for UPDATE they're the new values.
  - **Before values** (UPDATE): select the target rows by primary key, or by `ctid` when there's no key, inside the same savepoint *before* running, then match them to the returned rows.
  - Shown in a result tab marked **Dry run** (striped header): changed cells highlighted, old value on hover or in a "before | after" toggle, and a row count at the top.
- **Commands:** `Ctrl+Shift+F5` = dry run the current statement. Also a button on the missing-WHERE banner, and a ▶ margin menu item.
- **Then:** *Run for real* reruns it normally, without reusing the dry-run result.
- **Honest caveats, shown once in the dry-run tab's info tooltip:**
  - Triggers fire, then are rolled back with everything else.
  - Sequences (`nextval`) are **not** rolled back, so IDs get used up.
  - Anything outside the database (dblink, foreign tables, `COPY ... TO PROGRAM`) is not undone.
  - `NOTIFY` is transactional, so nothing is sent.
  - Locks are taken while it runs.
- **Refused for:** statements that can't run in a transaction (`CannotRunInTransaction` already exists), DDL in the first version, read-only connections, and anything that controls transactions.
- **Cap:** show at most the first 10,000 affected rows, but report the full count.
- **Provider contract:** an optional `IDryRunSupport` (wrap, add `RETURNING`, fetch before values). SQL Server would use `OUTPUT deleted.*, inserted.*` inside a transaction that's always rolled back.

### F3. Who's blocking me

**What:** when a statement is stuck waiting on a lock, show **who** holds it, **what** they're running and **for how long**, with an option to cancel them.

- **Detection:** while a statement has been running for more than **2 s**, the engine checks every 2 s on a separate, short-lived **admin connection** (never the sheet's own session, which is busy):
  ```sql
  select pid, usename, application_name, client_addr, state,
         now() - xact_start  as in_transaction_for,
         now() - query_start as running_for,
         wait_event_type, left(query, 500) as query
  from pg_stat_activity
  where pid = any(pg_blocking_pids(@our_pid));
  ```
  `@our_pid` is the sheet connection's backend PID (Npgsql `ProcessID`, recorded when the session opens).
- **UI:** an amber strip under the running statement: "Waiting for a lock held by **dave@10.0.0.4** (pid 4711), in a transaction for 12 min: `update orders set …`". It expands for the full query and the chain if there are several blockers (`pg_blocking_pids` returns all of them).
- **Actions:**
  - *Cancel my statement* (always available)
  - *Cancel their query* (`pg_cancel_backend`)
  - *End their session* (`pg_terminate_backend`)

  The last two always ask for confirmation, and are only shown if the role can do it (the same role, or membership of `pg_signal_backend`). If it's refused, show the server's message.
- **Also when idle:** if a sheet's own open transaction is **blocking someone else**, show a note on that sheet's transaction bar ("Your transaction is blocking 2 sessions"). Check it on the same polling loop while the transaction is open, on a slower schedule (every 10 s).
- **Background tabs:** a sheet stuck on a lock shows an amber lock icon instead of the spinner.
- **Provider contract:** an optional `ILockInspector` (`GetBlockersAsync(sessionId)`, `GetBlockedByMeAsync(sessionId)`, `CancelOtherAsync(pid, terminate)`). SQL Server: `sys.dm_exec_requests.blocking_session_id`, `KILL`.

### F4. Follow foreign keys

**What:** values in foreign key columns become links. Hover to see the referenced row, Ctrl+click to open it, and from any row ask "what references this?"

- **Knowing where a result column came from:** Postgres reports each result column's source table OID and column number. Npgsql exposes these through `GetColumnSchema()` (`BaseSchemaName` / `BaseTableName` / `BaseColumnName`). The engine sends this origin with the `columns` event. Computed columns have no origin and get no link. (This also unlocks per-table styling rules later; see Result styling.) (Resolved: the origin is read from the row description, with no query; see §11.)
- **Matching keys:** the schema cache already has foreign keys. A result column links when it's the *whole* of a single-column FK, or when all columns of a composite FK are in the result.
- **Hover peek:** after about 400 ms, a card shows the referenced row: its first ~8 columns, the table name, and "open". The lookup is `select * from <table> where <pk> = $1 limit 1`.
  - Run it on the **sheet's own session** when that's idle, so you see your own uncommitted rows. When the session is busy, use a short read-only side connection and mark the card "(committed data)".
  - Results are cached per value for the life of the result.
- **Ctrl+click / Enter:** opens the referenced row in a new result tab, as a real query in a new statement at the end of the sheet (`select * from "Customer" where "Id" = 42;`). Nothing hidden: it's in the editor and in history.
- **Reverse ("Referenced by…"):** right-click a row in a table with a primary key to list each table and FK that points at it, with counts (`select count(*) … limit` capped at 1,000, a 2 s timeout per FK, and "1,000+" when capped). Clicking one opens those rows the same way.
- **Look:** FK cells get a subtle underline on hover only, so the grid isn't covered in links. NULLs never link.
- Provider-neutral: uses the existing schema model, plus a new `ColumnOrigin` on result columns.

### F5. Query mini-map

**What:** a small live diagram of the **current statement**: one box per table or alias, lines for joins, drawn as you type.

- **Source:** the engine's existing analysis (`ScopeAnalyzer` for tables and aliases in scope, the parse tree for join conditions). Join edges come from `JOIN … ON a.x = b.y` and from `WHERE` equalities between two tables. Each edge is labelled with its columns.
- **Useful extras:**
  - **Cartesian product warning:** a table with no join edge to the rest shows in red, "No join condition: every row × every row".
  - **Suggested joins:** where the schema has an FK between two tables in scope that the query doesn't use, show a dashed edge. Clicking it inserts the `ON` clause. This is the FK ghost-text idea from §5, made visible.
  - CTEs and subqueries are drawn as grouped boxes, and the `UPDATE`/`DELETE` target table gets a highlighted border.
- **Live but tolerant:** it updates on the same 120 ms analysis pause as the statement highlight. While the SQL is half-typed and unparsable, keep the last good map, greyed out.
- **Interaction:** click a box to select that table reference in the editor. Hover an edge to highlight its join condition.
- **Placement:** a small collapsible panel at the top right of the editor (toggle with `Ctrl+Shift+M`), remembered per user. It's hidden for statements with only one table.
- **Layout:** a simple layered left-to-right layout in the UI (canvas or SVG). There's no layout library and no need for perfection with small graphs (up to ~15 tables).

### F6. Visual EXPLAIN

**What:** the query plan as a **flame (icicle) graph**. Click the slow part and the matching SQL lights up in the editor.

- **Commands:** `Ctrl+E` = EXPLAIN (estimates only, safe, nothing runs). `Ctrl+Shift+E` = EXPLAIN ANALYZE (actually runs).
- **Postgres:** `EXPLAIN (FORMAT JSON, VERBOSE, BUFFERS[, ANALYZE, TIMING])`. The provider turns the JSON into a provider-neutral `PlanNode` tree (operation, relation, alias, estimated and actual rows, costs, times, loops, buffers, filter text).
- **EXPLAIN ANALYZE of a write** actually performs it, so it always goes through the **dry-run wrapper** (savepoint, then roll back), with the same caveats. Read-only queries run normally.
- **Drawing:** an icicle graph on a canvas.
  - Width = total time (ANALYZE) or total cost (estimates).
  - Colour = **self** time or cost, so the hot node stands out.
  - Each node shows its operation and table, e.g. "Seq Scan on orders o".
  - A side panel shows full node details.
- **Highlights that teach:**
  - **Bad estimates:** estimated vs actual rows off by more than 10×, marked with ⚠ ("planner expected 12 rows, got 48,000: stale statistics? try ANALYZE orders").
  - **Seq scans on big tables** with a filter.
  - **Sorts or hashes spilling to disk.**
  - **Nested loops with many loops.**
- **Linking plan to SQL:** Postgres plans don't contain text positions, so match **by relation and alias** (`Relation Name` + `Alias`) to the table references the parser has already located. Filter and index-condition text is matched best-effort to the WHERE/ON expressions. Clicking a node selects its table reference in the editor, and hovering a table in the editor highlights its nodes. Nodes that can't be matched simply don't link.
- **Result tab:** a plan opens as a result tab of type *Plan*, which can be pinned. Pin two plans to compare them (side by side, with the total time difference), which is great for before and after adding an index.
- Also offer **Copy plan as text** (the normal `EXPLAIN` text format) for pasting elsewhere.
- **Provider contract:** an optional `IPlanProvider` (`ExplainAsync(sql, analyze)` → `PlanNode`). SQL Server would use the XML showplan.

### F7. Format SQL

**What:** tidy the current statement, the selection or the whole sheet into a consistent layout, and **never change what the SQL means**.

- **Commands:** `Shift+Alt+F` formats the selection, or the statement the cursor is in. *Format sheet* is in the command menu. Each format is a single edit, so one `Ctrl+Z` undoes it.
- **How it formats:** a token-based layout over the provider's scanner tokens (which include comments), not libpg_query's deparse. Deparse rebuilds SQL from the tree and **drops comments**, which is unacceptable in a user's sheet.
  - **Rules:** a new line before major clauses (`SELECT`, `FROM`, `JOIN`, `WHERE`, `GROUP BY`, `ORDER BY`, `RETURNING`…), one select-list item per line when the list is long, indented subqueries and CTEs, aligned `ON` and `AND`, and spaces around operators.
  - **Left alone:** comments keep their place, and strings, quoted identifiers and `$$` function bodies are untouched.
- **Safety check:** after formatting, parse the original and the result and **compare the parse trees, ignoring positions**. If they differ in any way, don't apply the format and say "Couldn't format this safely". This is how the format can be guaranteed never to change behaviour.
- **Unparsable SQL** (half-typed): refuse with "Fix the syntax error first", rather than guessing.
- **Assisted mode:** formatting changes the real text. The greyed corrections are recomputed afterwards as usual, so they stay correct.
- **Settings** (SQL category): keyword case (UPPER / lower / as typed, default as typed), indent size (default 4), commas at the end or start of lines (default end), and line width for wrapping long lists (default 100).
- **Provider-neutral:** the provider supplies tokens and the "same parse tree" check (`ISqlParser.SameMeaning(a, b)`). The layout rules are shared, with small dialect hooks.
- **Later:** format on save (off by default).

### F8. Record view (one or two rows, vertical)

**What:** show one or two result rows **vertically**, as column name → value, so a 60-column row can be read top to bottom. With two rows, it becomes a **comparison**.

- **Opening it:**
  - Select one or two rows and press `Space`, or use the *Record view* button on the result toolbar.
  - A result with exactly **one row** opens in record view automatically. There's a setting for this (on by default), with a toggle back to the grid.
- **One row:**
  - Two columns: *name* (with its generic type greyed) and *value*.
  - `PgUp` / `PgDn` (or ◀ ▶) steps to the previous or next row of the result without leaving the view.
- **Two rows (compare):**
  - Three columns: *name*, *row A*, *row B*, with the row numbers in the headers.
  - **Differences are highlighted**, both cells marked with a change bar in the margin, and there's a count at the top: "7 of 62 columns differ".
  - An **Only differences** toggle hides the matching columns.
  - **Comparison rules:** values are compared by their actual values, not as displayed text (`1.0` = `1` for Decimal columns). NULL is different from an empty string, and two NULLs are equal.
  - **Swap A/B**, and step either row on its own.
- **Both modes:**
  - A **filter box** narrows by column name (type `addr` to see just the address columns).
  - Long values wrap, with up to 6 lines visible before an expand arrow. JSON values are pretty-printed when expanded.
  - **Result styling rules apply** to values as in the grid. In compare mode, the difference marker is the change bar in the margin, so it never clashes with styling colours.
  - **Follow foreign keys** (feature 4) works on values here too.
  - **Copy:** a record as `name: value` lines, or the comparison as a three-column table.
- **Later:** compare a row with a row from a **different result**, by pinning row A and picking row B elsewhere. For example, the same customer on dev and live, or before and after an UPDATE.

### F9. Backslash commands (`\d`, `\dt`, …)

**What:** psql's describe commands work in any sheet: type `\d orders` and press F5. They work on **every provider**, not just Postgres, because the answers come from the provider-neutral schema model.

- **Recognising them:** a line that starts with `\` is a **command line**. It is a statement of its own, ending at the end of the line, and it's **never sent to the server**. The `StatementSplitter` treats it like any other statement, so the highlight, the ▶ and F5 all work. A `\` line isn't valid SQL on any supported database, so it can't clash.
- **First set:**

  | Command | Shows |
  |---|---|
  | `\d name` | Describe a table, view or sequence |
  | `\d+ name` | The same, plus size, storage and comments (provider extras) |
  | `\d` | All relations |
  | `\dt`, `\dv`, `\ds`, `\di` | Tables, views, sequences, indexes |
  | `\df` | Functions |
  | `\dn` | Schemas |
  | `\x` | Toggle record view (feature 8) for this sheet's results, psql's "expanded" mode |

- **Patterns, as in psql:** `\dt shop.*`, `\d *order*`, `\df get_*`. Matching ignores case unless the name is quoted (`\d "OrderItems"`), so there's no quoting pain here either.
- **Output for `\d name`:** one result tab of type *Describe*, with sections: Columns (name, type, nullable, default), Primary key, Indexes, Foreign keys, Referenced by, Check constraints, Triggers. The list commands give an ordinary result table.
- **Data source:** `\d name` fetches that table's metadata **fresh** (and updates the cache). List commands use the cache, with a *refresh* link.
- **Editor support, for people who don't know the commands or can't remember them:**
  - **Typing `\`** at the start of a line opens a completion list of every command, each with a plain-English description, e.g. `\dt` *List tables*, `\d` *Describe a table, view or sequence*. Typing narrows the list. The descriptions are searchable, so typing `\func` finds `\df`.
  - **After the command,** complete the argument: schema and table names (or function names for `\df`) from the cache, with patterns like `shop.*` suggested.
  - **Hover a command** for a help card: what it does, the arguments it takes, examples, and what the same command does in psql.
  - **Argument hint** while typing, like a function signature: `\d [pattern]   e.g. orders, shop.*, *order*`.
  - **`\?`** (psql's own help command) shows all supported commands and examples as a result tab.
  - **Command lines are highlighted** in their own colour, so they're clearly not SQL.
  - **Unknown commands** get a squiggle as you type, with "did you mean?" (`\dtt` → `\dt`), plus "not supported in SiftDB" for real psql commands that aren't implemented (`\copy`, `\i`).
  - **Learn by doing:** right-clicking a table in the schema tree offers *Describe (`\d orders`)*, which inserts and runs the command, so people pick up the syntax.
- **Every command is also a command palette item** (needs the palette itself):
  - One palette entry per command, named in plain English with its backslash form alongside: *Describe table… `\d`*, *List tables `\dt`*, *List functions `\df`*, *Toggle record view `\x`*, *Show backslash commands `\?`*. They can be found by either name ("describe", "\d").
  - Entries that need an argument open a **picker** straight away: a fuzzy list of tables (or functions, or schemas) from the cache, with a pattern allowed (`shop.*`).
  - **Running from the palette doesn't touch the sheet's text.** It runs on the current sheet's connection and opens a result tab labelled with the equivalent command (`\d shop.orders`), so the shortcut is learnt without cluttering the SQL. *Insert into sheet* (`Alt+Enter` in the palette) puts the command into the editor instead.
  - The palette and the `\` commands share one command definition in the engine (name, description, argument kind, handler), so both always stay in sync.
- **Unknown or unsupported** (`\i`, `\copy`, `\set`, `\timing`…): "SiftDB supports \d, \dt, \dv, \ds, \di, \df, \dn, \x", with no server round trip. `\set` may later map to bind variables (§4).
- **Scripts:** in a whole-sheet run (`Shift+F5`), command lines run as their own steps, in order. Saved `.sql` files that contain them still work in psql.
- **Provider contract:** none needed for the basics (it uses `SchemaModel`). An optional `IDescribeExtras` adds the `\d+` details.

### F10. Expand `*`

**What:** a quick fix on `select *` (or `o.*`) replaces it with the real column list, correctly quoted.

- **Trigger:** a lightbulb on a `*` in a select list (`Alt+Enter`): *Expand to columns*. `count(*)` and other function arguments are never offered.
- **Result:**
  - Columns from the resolved table, **in table order**, quoted where needed by the dialect's `IIdentifierPolicy`.
  - With several tables in scope, a bare `*` expands to every table's columns, **qualified with their alias** (`o."Id", c."Name"`). `o.*` expands only that table.
  - If the same column name appears in more than one table, both are kept and qualified.
- **Layout:** follows the formatter settings (feature 7). On one line if it fits the line width, otherwise one column per line, indented.
- **Only offered when safe:** every table behind the `*` must be known from the schema. A CTE, derived table or function in the statement means no offer. (Expanding CTE columns can come later.)
- **A single edit,** so one `Ctrl+Z` undoes it. In Assisted mode the inserted names are already correct, so no greyed corrections appear.

### F11. Paste a list as `IN (…)`

**What:** paste a column of values (from Excel, an email, a log) and SiftDB offers to turn it into valid SQL.

- **Detection on paste:** the clipboard holds **two or more items** separated by lines, tabs or commas, and doesn't look like SQL (no SQL keywords, no `;`). Then a small popup appears at the cursor. Nothing changes until you choose:
  - **IN list (200 values)**, e.g. `('A12', 'B7', …)`. When the cursor is already inside `in (`, only the values are inserted.
  - **ARRAY**, e.g. `ARRAY[...]`, for `= ANY(...)`.
  - **VALUES rows**, e.g. `(VALUES ('A12'), ('B7')) AS v(id)`, for joining against.
  - **Plain text**, which is the normal paste. `Esc` does the same, and `Ctrl+Shift+V` always pastes plain text.
- **Quoting decided by context:**
  - If the list is going next to a column (`where "OrderId" in (` or `= any(`), the resolver supplies that column's generic type: Integer and Decimal values stay unquoted, everything else is quoted.
  - With no column context, values are left unquoted only if **all** of them are numbers.
  - Quotes inside values are escaped (`O'Brien` → `'O''Brien'`).
- **Clean-up:** surrounding whitespace is trimmed, empty lines dropped, and optionally duplicates removed ("3 duplicates removed"). Long lists wrap at the formatter's line width.
- **Several columns** (a range copied from Excel with tabs): offer **tuple IN** `(a, b) in ((1, 'x'), …)` or VALUES rows with several columns.
- **Very long lists** (over ~1,000 values): suggest VALUES rows, or `= ANY(@ids)` once bind variables exist (§4), since huge IN lists slow down planning.
- **`NULL`** in the list is kept as the text `'NULL'`, with a hint: "Did you mean NULL? IN never matches NULL; use IS NULL."

### F12. App-wide command palette

**What:** one searchable list of **every SiftDB action**, available from anywhere in the app (editor, grid, schema tree, settings), with each action's shortcut shown.

**What exists today:** Monaco's own command palette is already bundled (`F1`, and *Command Palette* in the editor's right-click menu; see `monaco.ts`). It only lists **editor** actions: Monaco's built-ins plus the one SiftDB action registered with `addAction` (*Accept all identifier corrections*). It only works while the editor has focus, and it can't ask for an argument (such as picking a table).

**Plan:** a SiftDB palette at app level that **includes** Monaco's editor actions, rather than a second, competing list.

- **Open:** `Ctrl+Shift+P` from anywhere. `F1` and the editor's *Command Palette* menu item open the same palette, so there's only one.
- **Contents,** grouped and searchable by name, by description and by shortcut text:
  - **Sheets:** new, open, save, save as, close, next/previous, switch mode Assisted/Strict
  - **Run:** run, run script (`Shift+F5`), cancel, dry run, EXPLAIN / EXPLAIN ANALYZE
  - **Transactions:** begin, commit, rollback
  - **Connections:** connect, disconnect, edit, new, switch the sheet's connection
  - **Editor:** format, expand `*`, accept all corrections, plus all of Monaco's editor actions when a sheet is active
  - **Results:** record view, copy as…, pin, close result tabs
  - **Schema:** the backslash commands from feature 9, with pickers, and refresh schema
  - **App:** settings, toggle mini-map, toggle theme, about
- **Arguments:** an action that needs input opens a **second step** in the same palette (a fuzzy picker for tables, connections, sheets or files) instead of a dialog.
- **Context-aware:** actions that can't run right now (*Commit* with no transaction, *Dry run* on a SELECT) are hidden, or shown greyed with the reason.
- **Recently used** actions appear first when the box is empty.
- **One command registry:** every action is defined once (id, label, description, group, shortcut, "can run now?" condition, optional argument kind, handler). Menus, shortcuts, the palette, and the backslash commands (feature 9) all read from it, so a new feature adds its command in one place and it appears everywhere, with the correct shortcut text. The registry lives in the UI. Commands that need the engine send a bridge command, and the engine keeps the backslash-command definitions so a future TUI gets them too.
- **Look:** a centred overlay drawn in SiftDB's theme (not Monaco's quick-input widget, which is tied to the editor), operated entirely from the keyboard: arrows, Enter, `Esc`.

### F13. Richer schema explorer

**What:** the schema tree shows everything about a table's structure, not just its columns: keys, foreign keys in both directions, constraints, indexes, triggers, comments and sizes.

**What exists today** (`tree.ts`, `SchemaModel`): connection → schema → relation (with a kind letter T/P/V/M/F) → columns, each showing its type, "not null", **PK** and **FK** badges (the FK tooltip shows the target), and the default in a tooltip. The model already holds the primary key, foreign keys and **indexes, but indexes aren't shown yet**. There are no check or unique constraints, triggers, comments, sizes or "referenced by". Schemas list only relations, with no functions, sequences or types.

#### What the tree will show

**A table expands into groups** (a group appears only when it has something in it, with a count):
```
▾ T  Order            ≈ 1.2M rows · 340 MB
   ▸ Columns (12)
   ▸ Keys (2)            PK  Order_pkey (Id) · UQ  Order_Number_key (Number)
   ▸ Foreign keys (2)    CustomerId → Customer(Id)  on delete cascade
   ▸ Referenced by (3)   OrderItems(OrderId) · Invoice(OrderId) · …
   ▸ Indexes (4)         btree (CreatedAt) · gin (Tags) · partial (Status) where …
   ▸ Checks (1)          Total >= 0
   ▸ Triggers (1)        before update · set_updated_at()
```
- **Columns:** as now (name, type, not null, PK/FK badges), plus **identity / generated** markers, the **default** shown dimmed inline (shortened, full in the tooltip), and the column **comment** in the tooltip.
- **Keys:** the primary key and unique constraints, each with its name and columns.
- **Foreign keys:** outgoing, with the target table and columns, `ON DELETE` / `ON UPDATE` actions (shown only when not the default), and "deferrable" when set.
- **Referenced by:** incoming foreign keys from other tables. These are **computed from the model already loaded** (every table's FKs), so they need no extra query.
- **Indexes:** method (btree, gin, gist…), columns or expression, unique, partial (`where …`), and size. The one backing the PK is marked.
- **Checks:** name and expression. **Exclusion** constraints are shown here too.
- **Triggers:** timing and events (`before update`), the function it calls, and "disabled" when it's off.
- **On the table row itself:** the estimated row count and total size (dimmed), and the table comment in the tooltip.
- **Views and materialized views:** columns, plus a *Definition* entry that opens the view's SQL. **Partitioned tables** list their partitions.

**Schemas gain groups beyond relations:** Tables, Views, Materialized views, **Functions** (with arguments and return type), **Sequences**, and **Types** (enums show their values, which is handy when writing `WHERE status = '…'`).

#### Interaction
- **Click a foreign key** (or a "referenced by" entry) to **jump to that table** in the tree, expanded and highlighted.
- **Hover** anything for its full definition (`pg_get_constraintdef` / `pg_get_indexdef` / `pg_get_triggerdef` text).
- **Drag** any node into the editor to insert its name, correctly quoted. A table brings its schema-qualified name, and a column `alias.column` when the editor's statement has that table.
- **Right-click:**
  - *Copy name*, *Copy definition*
  - *Script as* CREATE / DROP / SELECT. The script is **inserted into the sheet, never run**.
  - *Describe* (`\d`, feature 9)
  - *Refresh*
- **The filter box** gains an option to match **column names** too ("which tables have `invoice_ref`?"), showing the matching columns under their tables.
- **Keyboard:** arrows, Enter to expand, and `*` to expand a whole table, as in the current tree.

#### Loading
- The schema load stays fast: the current queries load tables, columns, PK, FKs and indexes for the whole database. **Details** (checks, triggers, comments, sizes, partitions, view definitions) load **lazily per table** the first time it's expanded, then are cached and refreshed with the schema.
- **Functions, sequences and types** load per schema when that group is expanded.
- **Very large databases:** groups over 500 items show the first 500, with "filter to see more".

#### Model and providers
- `SchemaModel` additions (provider-neutral): `ConstraintInfo` (kind Unique / Check / Exclusion, name, columns, definition text), `TriggerInfo` (name, timing, events, function, enabled), richer `IndexInfo` (method, expression text, predicate, is-primary, size), FK actions and deferrability on `ForeignKeyInfo`, `Comment` on relations and columns, identity / generated on columns, and `EstimatedRows` / `TotalBytes` on relations. Plus `FunctionInfo`, `SequenceInfo` and `TypeInfo` (with enum values).
- **Provider contract:** an optional `IntrospectDetailAsync(relation)` for the lazily loaded parts, and `IntrospectObjectsAsync(schema, kind)` for functions, sequences and types. Anything not implemented simply isn't shown.
- **Postgres** reads `pg_constraint` (`pg_get_constraintdef`), `pg_index` (`pg_get_indexdef`), `pg_trigger` (`pg_get_triggerdef`, excluding internal triggers), `obj_description` / `col_description`, `pg_class.reltuples` and `pg_total_relation_size`, `pg_inherits` for partitions, `pg_proc`, `pg_sequence` and `pg_enum`.
- **Shared with `\d`** (feature 9), Follow foreign keys (4) and the mini-map (5): they all read the same detail model, so the tree, `\d` output and hovers always agree.

### F14. Saved queries as palette commands

**What:** any saved query can become a **command palette item**. *"Find customer by email"* asks for the email, then runs.

- **Where they live:** every `.sql` file in `%APPDATA%\SiftDB\commands\` (subfolders allowed) is a command. The engine watches the folder, so adding, editing or deleting a file updates the palette straight away. (A per-project folder that a team can share in git can come later.)
- **The file is an ordinary sheet file.** Its details go in the **same header line** `SheetFile` already reads and writes:
  ```sql
  -- db: command="Find customer by email"; group=Support; key=Ctrl+Alt+1; description="Customer and their last 10 orders"
  select * from "Customer" where "Email" = @email;
  ```
  - `command` (required): the name shown in the palette
  - `group`: groups commands in the palette (default *My commands*)
  - `description`: searchable, shown under the name
  - `key`: an optional shortcut. A clash with a built-in shortcut or another command is reported, and the command keeps working from the palette.
- **Parameters are `@variables`** (§4). Running the command adds a palette **picker step for each variable** (`email: _`), with the type inferred as in the §4 prompt, and the **last value used for that command** pre-filled.
  - Until bind variables are built, the same steps work: values are passed as real parameters, never pasted into the SQL as text.
- **Running:**
  - It runs on the **current sheet's connection and session**. If no sheet is connected, the palette asks which connection to use.
  - The result opens in a result tab labelled with the command's name.
  - *Open in sheet* (`Alt+Enter` in the palette) opens the SQL in a new sheet instead, to look at it or adapt it.
- **Writes are never hidden:** if the parser finds the command may change data (or it has several statements), it **isn't run directly**. It opens in a sheet with a note, and you run it there with the transaction bar and the missing-WHERE alarm (feature 1). Read-only connections still refuse writes as usual.
- **Creating and editing:**
  - *Save as command…* (palette, and the sheet's right-click menu) saves the current statement or selection to the commands folder, asking for a name and group.
  - *Edit command…* opens a command's file as a normal sheet.
  - *Delete command…* asks first.
- **Errors:** a command file that can't be read, or has no `command` name, appears in the palette greyed out with the reason, rather than disappearing.

### F15. Link templates (open a URL with values from a result)

**What:** a generic, built-in way to open a web page using values from the results, e.g. `https://admin.example.com/orders/{OrderId}`. It ships in the main app. The work-specific part is only *configuration* (the URLs), which users add themselves or get from an extension pack (feature 16).

- **A link template** has:
  - a **name** (*Open in admin site*)
  - a **URL** with `{column}` placeholders, possibly several (`…/customers/{CustomerId}/orders/{OrderId}`)
  - **where it applies**: columns whose name matches a wildcard pattern, optionally only some generic types. This is the same matching as result styling rules, so it feels familiar.
  - optionally, **which connections** it applies to
- **Where it appears:**
  - In the result grid and record view, right-click a cell or row → *Open in admin site*.
  - It's also in the command palette whenever the current selection has the needed columns.
  - Optionally, a small ↗ icon on matching cells when hovered (off by default).
- **Values:** taken from the selected row and **URL-encoded**. If a needed column is missing or NULL, the item is shown greyed with the reason. With several rows selected: open one tab per row, up to 10, after asking.
- **Safety:**
  - Only `http` and `https` URLs. The **host checks the scheme again** before opening the default browser, so no `file:` or custom protocols get through.
  - The first use of each template asks *"Open https://admin.example.com/…?"*, with "don't ask again for this link".
- **Editing:** a **Links** category in Settings, built like the result styling editor: a list with a live example URL built from a sample row. Links are stored in user settings (`settings.json`), validated by the engine.
- **Registered with the command registry** (12), so links get palette entries, shortcuts and "recently used" for free.

### F16. Extension packs, code-free (low priority)

**What:** a way to **distribute** commands, links and settings (for example a work team's) **separately from the main installer**, with updates, without any server or feed. It follows Home Assistant's HACS model: you add the **repository** once, and SiftDB watches it for new versions.

**Code-free only.** A pack contains files SiftDB interprets, never code, so **no code signing is needed**. Code extensions (DLLs) are out of scope until there's a real need. They would require signing and pinning the publisher.

#### What a pack can contain
- `siftdb-extension.json`, the manifest (required):
  ```json
  {
    "id": "acme.work-tools",
    "name": "Work tools",
    "version": "1.4.0",
    "publisher": "Simon",
    "description": "Support queries and admin-site links",
    "siftdb": ">=1.2 <2",
    "contents": { "commands": "commands/", "links": "links.json", "resultRules": "rules.json" }
  }
  ```
- `commands/*.sql`: saved-query commands, exactly as in feature 14
- `links.json`: link templates (feature 15)
- `rules.json`: result styling rules, added to the user's list as a named group they can switch off or reorder
- `README.md`: shown on the pack's page in Settings
- **Nothing else:** any other file type, or a file outside the folders the manifest lists, gets the whole pack refused.

#### What a pack can't do
- Run code, or contain passwords or connection strings with credentials, or read stored secrets.
- Switch off safety features (the missing-WHERE alarm, read-only connections, write confirmations).
- Get round the existing rules: its commands that might change data open in a sheet rather than running, and its links are `http(s)` only, with the first-use confirmation.

#### Adding a repository
*Settings → Extensions → Add repository…* takes one of:
- **A folder:** a network share, or a OneDrive/SharePoint folder synced to the PC. The newest `.siftdbext` file in it is the current version. No server needed, so this works in locked-down workplaces.
- **A GitHub repo:** each **release** is a version (tag `v1.4.0`), with a `.siftdbext` file attached, or SiftDB downloads the repository's source at that tag, since a code-free pack needs no build.
- **An Azure DevOps repo:** version tags, downloaded through the API.

Private GitHub and Azure DevOps repos take a personal access token, stored with DPAPI like passwords. Requests use the Windows proxy and the Windows sign-in.

SiftDB shows the pack's name, publisher, description and **everything it adds** (commands, links with their domains, rules), and installs only after confirmation. It **remembers the repository** each pack came from. *Install from file…* (or double-clicking a `.siftdbext` file) also works, with no updates unless a repository is added.

#### Checks before every install and update
1. The manifest is valid, and the id matches the installed pack.
2. The version matches the tag (for repos) and is **newer** than the installed one. There are no silent downgrades; rollback is a separate, deliberate action.
3. It's compatible: the `siftdb` range covers this app version. If not, "needs SiftDB 1.3 or later", and the current version is kept.
4. Only allowed files are present. Every file parses: SQL headers, link URLs and rules are validated by the same code as hand-made ones.
5. It's intact: a `checksums.txt` in the release, or next to the file, matches what was downloaded.
6. It's unpacked into `%APPDATA%\SiftDB\extensions\<id>\<version>\` **next to** the current version, then switched over (code-free packs reload instantly). The previous version is kept for **Roll back**.

#### Updates
- Each pack's repository is checked at startup and once a day.
- A newer valid version shows *"Work tools 1.4 is available: Update · What's new"* (release notes from the release, or the pack's `CHANGELOG.md`).
- **Each update shows what it changes:** *"2 new commands, 1 command changed, 1 link changed: `admin.example.com` → `admin2.example.com`"*.
- **A change to a link's domain always needs confirmation**, even with auto-update on, because packs aren't signed. That stops a tampered pack quietly redirecting people.
- **Auto-update** is a setting per pack (default: ask). Packs can also be **pinned** to a version, disabled or removed.

#### Where it shows
- Commands and links from a pack appear in the palette and menus under the pack's name, so it's always clear where an item came from.
- **Settings → Extensions:** installed packs (version, source, last checked, enable/disable, update, roll back, remove), plus *Add repository…* and *Install from file…*.

### F17. Release notes and a What's New tab

**What:** every release has a short summary of what changed, split into major and minor changes. After an update, when SiftDB restarts, a **What's New** tab opens with it.

#### Notes are generated, not written by hand
Nobody writes release notes. They're **generated from commit messages** during the release build, and `CHANGELOG.md` is produced as an output.

**1. Commit messages follow a simple convention** (Conventional Commits). Since Claude Code makes most of the commits, the rule goes in `CLAUDE.md`, so it happens automatically:
```
feat: dry run for UPDATE and DELETE
fix: pasting a list no longer drops the last value
perf: faster schema load on large databases
feat!: sheet files use a new header format        ← breaking change
refactor: / test: / docs: / chore: / build: / ci:  ← not shown to users
```
The CLAUDE.md rule: *"The commit subject is one line, written for users: what they'll notice, not how it was done. Use `feat`, `fix`, `perf` for user-visible changes. Use `refactor`, `test`, `docs`, `chore`, `build`, `ci` for everything else. Add `!` for anything that breaks existing files or settings. Optionally add a footer `Try-it: <command id>`."*

**2. The release workflow builds the notes** with **git-cliff** (a changelog generator with a ready-made GitHub Action), which reads every commit since the previous tag and groups them:

| Commit type | Shown as |
|---|---|
| `feat` | **Highlights** (new features) |
| `perf`, and `feat`/`fix` with scope `ui` | **Improvements** |
| `fix` | **Fixes** |
| anything with `!` | **Breaking changes** (at the top, in amber) |
| `refactor`, `test`, `docs`, `chore`, `build`, `ci` | hidden |

A `Try-it:` footer becomes a **Try it** link (`command:<id>`) on that entry.

**3. The version number is worked out too:** `!` → major, any `feat` → minor, otherwise patch. The release is just "tag whatever's on main": the workflow calculates the version, creates the tag, builds, and publishes. (*release-please* is an alternative if a reviewable release pull request is wanted later.)

**4. Optional, later: an AI polish step.** The workflow sends the grouped list to Claude to merge near-duplicates ("three fixes to paste" → one line) and smooth the wording into friendly sentences, without inventing anything. That needs an API key or a Claude Code token as a repository secret. The plain generated notes are good enough to start with, because the commit subjects are already written for users.

#### Release workflow (GitHub Actions)
On a release run, the workflow:
1. Generates the notes and the version as above. **If there are no user-visible commits** since the last release, it stops with "nothing to release".
2. Commits the updated `CHANGELOG.md` back to the repo, as a record.
3. Uses the generated section **three ways**: as the GitHub Release description, as Velopack's release notes (`vpk pack` with the notes file), and **bundled inside the app** (all versions, as an embedded resource), so What's New works offline and shows exactly what was shipped.

#### In the app
- **Spotting an update:** settings record the **last version the user saw** (`lastSeenVersion`). At startup, if the running version is newer, SiftDB knows it was updated. This is reliable however the update happened. Velopack's after-update hook could be used as well, but isn't needed.
- **What opens:**
  - **A major or minor update:** the **What's New** tab opens on top of the restored sheets, showing **every version since the last one seen** (if several releases were skipped, they're all listed, newest first). Highlights come first and stand out; improvements and fixes are below, collapsed if they're long.
  - **A patch only (just fixes):** no tab, only a small notice, *"Updated to 1.4.1, 3 fixes · What's new"*, so small updates don't interrupt anyone.
  - **A fresh install:** no What's New. `lastSeenVersion` is just set to the current version.
- **The tab** is a special, non-sheet tab: it has no session and isn't autosaved, and it closes like any other tab. The Markdown is rendered **safely** (no raw HTML). Links go through the host's `http(s)` check, and `command:` links run palette commands (**Try it**), so new features can be found in one click.
- **Any time:** *What's new* in the command palette, and on the Help menu, reopens the tab showing every version.
- **Setting** (App category): "Show What's New after updates": *major and minor updates* (default) / *all updates* / *never*.
- After the tab has been shown, `lastSeenVersion` is updated, so it never appears twice for the same version.

#### Extension packs
Pack updates (feature 16) use the same layout: a pack's `CHANGELOG.md` is shown in the same tab style from *What's new* on its Extensions page.


### Provider contract summary (all optional, with default "not supported" implementations)

| Addition | Used by |
|---|---|
| `IDatabaseProvider.ChangesEveryRow` (built, v5) + `IProviderConnection.DescribeTableAsync` (built, v5) / statement target table | 1, 2, 5 |
| `IDatabaseProvider.SameMeaning(a, b)` (built, v7: compare parse trees, ignoring positions) | 7 |
| `IDescribeExtras` (sizes, storage, comments for `\d+`) | 9 |
| `IProviderConnection.DescribeRelationAsync`, `DescribeSchemaObjectsAsync`, `ScriptAsync` (built, v9) | 9, 13 |
| `IDryRunSupport` (built, v6: `IProviderConnection.DryRun`) | 2, 6 |
| `ILockInspector` | 3 |
| `IProviderConnection.ColumnOrigins`, `PeekRowAsync`, `CountRowsAsync` (built, v8; origins from the row description, not the driver's column schema) | 4 |
| `IPlanProvider` + `PlanNode` model | 6 |

### Decisions (2026-11-17)

These may be refined once there's user feedback.

- **Dry run of DDL:** later, not in the first version (DDL takes heavy locks, and previewing it is a less common need).
- **Follow keys, hover peek:** use the sheet's own session when it's idle, so you see your own uncommitted rows. When it's busy, use a short read-only side connection and label the card "(committed data)".
- **Mini-map:** on by default, but shown only for statements with two or more tables.
- **Visual EXPLAIN, plan history:** later. Pinning two plans to compare them covers before and after for now.
