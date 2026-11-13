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

---

## 9. Delivery

- The host is published self-contained, so no .NET runtime install is needed.
- The UI is bundled and served locally, never from a remote URL. Engine and UI are released together.
- If WebView2 is missing, the installer runs Microsoft's bootstrapper.
- **Velopack** handles the installer, background delta updates and stable/beta channels. Releases are hosted on the public repository's GitHub Releases, published by a GitHub Actions workflow when a version tag is pushed.
- **Code signing** with Azure Trusted Signing, to avoid SmartScreen warnings. Deferred until the signing account is approved; until then the installer is unsigned and SmartScreen warns.
- Third-party providers go in a plugins folder (possibly NuGet-format packages) and are checked against the contract version.

---

## 10. Back burner

User settings are built (see §11, *User settings*). Every later feature adds its settings there; result styling below is the first expected user.

**Settings stay database-neutral.** A setting that only makes sense for one kind of database is **described by its provider** (key, label, help, and choices each with its own explanation), the way a provider describes its connection fields. The engine stores the values per provider and hands them back to the provider, never interpreting them; the settings editor draws **one category per provider that has settings**, after the app's own (SQL, Results), so the UI knows nothing about any database. The first is **PostgreSQL**: "When a statement fails inside a transaction" (§7).

The read-only lock that used to be here became the read-only connection setting (§8).

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
- Result styling (§10): provider contract **v3** adds `GenericTypeOf(dataTypeName)` and the `GenericType` enum in Abstractions; the engine sends each result column's generic type (`type`) in the `columns` event, from `DbDataReader.GetDataTypeName` (checked against a real server: `int8` → integer, `numeric(10,2)` → decimal, `bit(1)` → boolean, `timestamp` and `timestamptz` → dateTime, arrays and `uuid` → text). The engine owns the rule model (`StyleRules`): it validates user rules on save (a bad rule is refused with the reason, nothing written) and drops bad ones when reading a file; preset entries store only id and on/off. Settings carry the list as `resultRules`. The UI matches rules to columns once per result (`styles.ts`: a per-column styler, null when no rule applies, so an unstyled column costs nothing to draw) and again when the rules change, so a save restyles open results at once. The **Results** settings page lists every rule with an example cell in its colours; new rules go to the top. The rule form shows a live example and warns below 4.5:1 contrast (WCAG AA); the four presets all pass it. Colours are picked with the native colour input (so `#rrggbb`; `#rgb` is accepted from the file). Dates and times are shown in ISO form (`2026-11-12`, `2026-11-12 14:05:09.5`, a trailing `Z` for a UTC timestamp) rather than the invariant culture's US style, chosen by the column's type (`date` shows no time) so a column never mixes formats; date ranges compare these. Not yet: by-table rules (`orders.status`), which need each result column's source table: Npgsql offers it only through `GetColumnSchema()`, which costs a catalog query per result, so it needs a look at reading it from the row description instead.
- Delivery (§9): **`build/release.ps1`** makes a release: `dotnet publish` of the host, self-contained for win-x64 (not single-file, since Velopack's deltas work per file), then `vpk pack` (Velopack 1.2.161; the CLI is pinned in `dotnet-tools.json`) into `artifacts/releases`: `SiftDB-win-Setup.exe`, full and delta `.nupkg`s, `releases.<channel>.json` and a portable zip. The pack id is `SiftDB`, so it installs per user into `%LOCALAPPDATA%\SiftDB` with no admin prompt. The exe is `SiftDB.exe` (the host's assembly name). `--framework webview2` makes Setup install the WebView2 Runtime when it is missing; the host also checks at startup (a copy run without installing) and offers Microsoft's download page instead of crashing. **Versions come from git tags:** `.github/workflows/release.yml` runs on a `v*` tag, so `v0.2.0` is a stable release on the default channel (`win`) and any prerelease suffix (`v0.2.0-beta.1`) goes to the `beta` channel as a GitHub prerelease. It downloads the previous release first so deltas are built, then `vpk upload github` publishes. A local build reports the csproj's `<Version>`. Velopack runs first in `Program.Main` (it handles the installer's hooks and applies an update that was downloaded but not restarted into). **Updates** (`Updater`): only an installed copy checks, 10 s after the page loads and then every 4 hours, against GitHub Releases without a token (prereleases included; the channel the copy was installed from decides which feed it reads, so stable never sees beta). A newer release downloads quietly, then an accent-coloured **Restart to update to X** button appears in the toolbar. Clicking it goes through the usual close (the uncommitted-changes warning, the last autosave), and the updater applies the update and restarts SiftDB; declining the warning cancels the update too. Ignoring the button is fine: the update is applied the next time SiftDB starts. Failures (offline, rate-limited) are silent and retried at the next check. WebView2's data folder (the page's localStorage) is `%LOCALAPPDATA%\SiftDB\WebView2`, not the default next to the exe, which every update replaces. **CI** (`.github/workflows/ci.yml`, on every push to main and pull request, and called by the release workflow so a release waits for it): the engine and provider tests on Linux against a `postgres:18` service container (`SIFTDB_SPIKE_CONN` points at it, so no database test skips), and a Windows build of the host and UI. Test waits that only matter when something hangs allow 15 s, so a slow runner does not fail them. **The app icon** is a database whose base narrows into a funnel, sifting out a drop, white on a blue tile: drawn in `src/SiftDB.Host/Assets/SiftDB.svg` and rendered to `SiftDB.ico` (16 to 256 px) by `build/icon` (run by hand after changing the SVG); it is the exe's, the window's and Setup's icon. Not yet: code signing, a setting to turn update checks off, release notes.
- Toolbar icons: the toolbar buttons are icons, not words, each 16 px in **one colour that says what it does**: green to go (Run ▶, Begin ⊕, End ✓), amber to Commit ✓ (the colour of changes pending), red to stop (Cancel ■, Rollback ✕), the accent for Connect, and the text colour for the rest (edit, new and save connection, open, save, new sheet, settings). They use the transaction bar's colours, so they follow light and dark. Each has a tooltip with its shortcut and an `aria-label`. The icons live in `icons.ts` (`applyIcons` fills each `[data-icon]` element). Assisted | Strict stays as words: it is a mode, and the words are the meaning.
- Grid editing: none for now. The grid is read-only. Revisit later (it would interact with transactions and the lock).
- Export formats: CSV and JSON are enough for now. Import is not decided.
- Export (built): **Export…** on the result status bar (once the statement has finished, a cancelled one too) and **Export result… / Export selection…** in the grid menu. The host's Save dialog lists *CSV* and *JSON* as its file types, and that is the format choice (a typed `.json` or `.csv` wins over the list); the file name is suggested from the first table the statement reads (`select * from sales.orders` → `orders.csv`), and the format and folder are remembered for the run. The rows live in the page (the engine lets a statement go once streamed), so **the page formats and the engine writes**: `exportBegin {path, byteOrderMark}` → `exportStarted {exportId}`, then `exportWrite {exportId, text}` a piece (about 1 M characters) at a time, each awaited so pieces arrive in order and never pile up, then `exportEnd {exportId, cancel}` → `exportFinished`. The engine writes to `<file>.siftdb-export.tmp` beside the target and moves it into place only at the end, so a failed or cancelled export leaves no half-written file and the file it would replace is untouched (a target open in Excel fails at that point, with the old file kept). While it runs the button reads *Cancel export · 45%*; a new run on the tab, or closing it, cancels; unfinished exports are thrown away when the engine stops. **CSV:** RFC 4180, comma, CRLF, a header line, UTF-8 **with** a byte-order mark (Excel needs it); NULL is an empty field and an empty string is `""`, so they stay apart. **JSON:** UTF-8 without a BOM, an array with one object per row, one per line; numbers stay numbers with the database's own digits (a bigint or numeric keeps every digit), NaN and Infinity become strings, booleans become true/false, everything else (dates in ISO form, json columns too) is a string; a repeated column name becomes `id_2`, `id_3`, since JSON readers keep one of two equal keys. Not yet: json/jsonb columns written as JSON values (needs the database type, not just the generic one), other delimiters, exporting straight from the database without running first (re-running a statement could repeat its side effects).
- Query history retention: deferred until later.
- Audience: anyone (a public product, not just personal use). This raises the bar on installer, signing, docs and the plugin contract.

**Not discussed yet**
- Schema browser scope: object tree, DDL view, search, ER diagrams
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
