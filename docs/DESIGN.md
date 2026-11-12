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

**The mode switch shows both options.** Each sheet toolbar has an `Assisted | Strict` segmented control with the active option filled. An earlier single button changed its own label to the current mode, which read as "click for Strict". That made a correctly blocked Strict run look like Assisted failing. Mode is per sheet, and switching clears a stale "not run" message. **Mode belongs to the sheet, not the connection:** Assisted shows exactly what runs and blocks anything ambiguous, so Strict is a style choice (the file text is the real SQL), not a safety measure, and there is no per-connection default. New sheets open in **Assisted** until user settings (§10) add a preferred mode; then new sheets take that.

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
- **Missing values** are collected in one inline prompt when you run, not one dialog per variable. The input type comes from the parameter type Postgres reports when the query is prepared (date picker, number box, etc.).
- **Execution** uses real parameters (`@surname` becomes `$1`), never pasted text. Arrays support `= ANY(@ids)`.
- **Scopes:** global, per connection, or per tab. The narrowest scope wins.
- **Named variable sets** (e.g. "Test customer Smith") let you switch sets and re-run the same queries against different data. The active set shows in the tab header.
- **All stored values are encrypted** with DPAPI (CurrentUser). Decrypted values are kept in memory only and never logged.
- History stores the parameter values used, encrypted.
- A hide-values toggle masks values on screen, for screen sharing.
- Exporting or sharing a set produces a passphrase-encrypted file.
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

**`Shift+F5`** runs the **whole sheet as one script**: sent in one go, with an optional wrapping transaction. All result sets and `NOTICE`/`RAISE` messages are returned, and an error stops the script and jumps to the failing line. (There's no per-sheet "script mode" setting. That was considered and rejected.)

**`Ctrl+.` / `Esc`** cancels.

**Details**
- Statements are split by the parser (libpg_query reports where each starts and ends), never by looking for `;`.
- The statement that will run is lightly highlighted as the cursor moves.
- A ▶ in the margin next to each statement lets you run it with the mouse.
- Each statement that runs gets its own result tab, labelled with the start of its SQL. Results can be pinned.
- **Each sheet has its own database session**, so `BEGIN`/`COMMIT` across statements, temp tables and `SET` all carry over.
- **Each sheet has its own connection string too.** The connection box describes the visible sheet and Connect acts on that sheet only; a new sheet starts on the connection of the sheet you were on. The schema tree has one root per distinct connection string: sheets on the same string share a root (and a schema), a different string adds a root, and the last sheet leaving a connection removes it. Roots are labelled host / database (the user name is added when two would read the same); the password never reaches the DOM.

**Saving**
- Plain `.sql` files, so they open in other tools.
- Sheet settings go in a header comment, e.g. `-- db: connection=dev-pg; vars=Smith; mode=assisted`.
- Variable values are never written to the file, only the name of the set.
- Unsaved sheets are autosaved and restored on restart.

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
- Optionally, a per-connection setting (**auto-transaction**, off by default) starts a transaction automatically on the first change, so a missing `WHERE` can still be rolled back. It applies to writable connections only (§8).

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
- **Read-only.** The session opens with `default_transaction_read_only = on`, passed in the startup options (`-c ...`) rather than a `SET`, so `RESET`, `RESET ALL` and `DISCARD ALL` fall back to read-only. The server then refuses writes, including inside a typed `BEGIN`. The app blocks statements that would switch it off, recognised by the parser from the statement's structure rather than its text: `SET`/`SET SESSION CHARACTERISTICS ... READ WRITE`, `BEGIN`/`START TRANSACTION`/`SET TRANSACTION ... READ WRITE`, and `set_config('default_transaction_read_only' | 'transaction_read_only', ...)`. Anything inside a `DO` block or a function body gets through. Note the server also refuses every `CREATE`/`ALTER`/`DROP` (including `CREATE TEMP TABLE`) and `nextval` on ordinary sequences. There is **no per-tab lock or unlock**: to write, open the sheet on a writable connection. Users who want protection save a read-only and a writable connection to the same database and choose deliberately. This is a guard against mistakes, not security; real protection is a database role without write privileges. A read-only sheet can't go yellow. A lock icon and the words *Read-only* show on the tab, the tree root and the connection picker, so a refused write is never a mystery. SQL Server has no server-side equivalent and would rely on the app check (capability flag `ReadOnlyEnforcement = Server | Client`).
- **Auto-transaction** (§7), for writable connections.

There is no "production" flag or environment concept, and no per-connection default mode (§3).

**First version**
- Auth: password, and SSL modes including `verify-full` with a CA file. The stored fields leave room for Entra ID (Azure Postgres), client certificates and SSH tunnels, which come later.
- The store file carries a format version and is written atomically (temp file, then replace).
- UI: the schema tree stays the list of *open* connections. Above it, a picker: a dropdown of saved connections with *New…* and *Edit…*; the edit form is a modal with a **Test** button. Grouping or folders come later.

---

## 9. Delivery

- The host is published self-contained, so no .NET runtime install is needed.
- The UI is bundled and served locally, never from a remote URL. Engine and UI are released together.
- If WebView2 is missing, the installer runs Microsoft's bootstrapper.
- **Velopack** handles the installer, background delta updates and stable/beta channels. Releases are hosted on GitHub Releases or Azure Blob Storage, published from an Azure DevOps pipeline.
- **Code signing** with Azure Trusted Signing, to avoid SmartScreen warnings.
- Third-party providers go in a plugins folder (possibly NuGet-format packages) and are checked against the contract version.

---

## 10. Back burner

The read-only lock that used to be here became the read-only connection setting (§8).

### Later
- **User settings**, a feature of its own. First known setting: the preferred mode for new sheets (§3).
- A TUI front end on the same engine
- AI completions
- A SQL Server provider

---

## 11. Open questions

**Needed before building the editor features**
- Variables with no inferable type: default to text, or ask?

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
- Strict vs Assisted (§3): **Assisted is the default**, switched per sheet from the toolbar; there is no per-connection default (§3), and new sheets open in Assisted until user settings exist. The resolver (`IdentifierResolver`) follows the database's own rules so it never "fixes" valid SQL (`ORDERS` already means `orders`). Findings: *needs quotes* (`Person` for `"Person"`, or a bare reserved word) is auto-applied in Assisted as greyed, non-editable quotes drawn around the word, and what runs is exactly what is shown; in Strict it is a squiggle with a quick fix (Alt+Enter) and running is blocked. *Case mismatch* (`person` for `"Person"`), *quote mix-up* (`name = "bob"` where `bob` is no column; offers `'bob'`) and *ambiguous column* (two tables in scope have it) are squiggles in both modes and never applied silently. Ambiguity blocks running even in Assisted ("nothing is filled in; needs a choice"). Ctrl+Alt+Q accepts all corrections as real text. Resolver limits, deliberately conservative: names it cannot resolve get no comment; derived tables/CTEs suppress guesses; output aliases and function names are skipped; a *bare* reserved-word column (`select user from t`) is not rewritten, because telling it from the clause keyword is unsafe. An *unknown column* (a name that is no column of any table in the statement, e.g. `id` when the key is `"PersonId"`) gets a hint with up to three ranked candidates (ends with what was typed, starts with it, a near miss, contains it; a name two tables share is offered qualified) and a quick fix per candidate, but is **never applied automatically** (a fuzzy guess silently changing a query could run the wrong one) and **never blocks, even in Strict**: only the server knows for sure, and a wrong squiggle must not stop valid SQL. It is silent unless every table in the statement is known (a derived table, CTE or function could own the column) and skips function names, casts, whole-row references (`select p from "Person" p`), aliases given without AS, named arguments, `extract(epoch from x)`, and bare names in ORDER BY / GROUP BY (which may be output columns). Not yet: unknown *tables* (CTE names make that unsafe without a CTE scanner), Copy/save output of corrected SQL, history recording both forms.
- Transaction bar (§7): state always comes from the **server's protocol status** (ReadyForQuery), observed after every statement and after Begin/Commit/Rollback, so the button and a typed `BEGIN` cannot disagree; "changed" is a read-only `pg_current_xact_id_if_assigned()` probe that runs only inside an open block. Green = open, yellow = changes pending (`3 statements · 42 rows changed`, open for N), red = aborted (Rollback only); each state has colour, icon and words. Npgsql exposes the status only on an internal class, so it is read by reflection in **one place** (`PostgresTransactionMonitor`) with a test that fails loudly, with an explanation, if an Npgsql upgrade moves it. (Timestamp tricks were tried and do not work: Npgsql's extended protocol refreshes `statement_timestamp()` on every message.) Everything on a session connection is serialised by a lock held for a statement's whole streaming life, so a probe, a Commit and a stream cannot collide; a new statement cancels the running one. Shortcuts: Commit Ctrl+Shift+Enter, Rollback Ctrl+Shift+R. The host warns before the window closes while changes are pending. Not yet: auto-transaction on first change per connection, the SQL Server write-detection fallback (the interface flag exists).
- Sheets and results (§6): the whole right-hand area is a **tab**, and each tab is a sheet with **its own database session**, so BEGIN/COMMIT, temp tables, SET, the transaction bar and the Strict/Assisted mode are all per sheet. New sheets (+ New sheet, the + on the strip, Ctrl+T) connect themselves on the shared connection; Ctrl+W closes, Ctrl+Tab and Ctrl+Shift+Tab cycle; closing the last sheet leaves a fresh one. A tab shows a coloured dot while its session is in a transaction, so one can't be forgotten behind another tab. Closing a sheet (or reconnecting) with uncommitted changes asks first, because closing the session rolls them back; the host warns the same way before the window closes. Under each editor is a second strip of **result tabs**, each labelled with the start of the SQL that ran (after any corrections): a run fills the unpinned tab, or opens a new one when every tab is pinned, so pinning keeps a result across runs. The engine gained `disconnect` and a quiet `introspect` (refresh a session's schema cache without re-sending the schema), and now releases finished queries. Engine events are routed by connection id (transactions) or query id (results and row batches). Two Monaco facts worth remembering: its keybindings and its completion/quick-fix providers are **global to the page**, not per editor, so each editor tags its own context key and scopes its F5/Ctrl+Enter/Ctrl+. bindings to it (otherwise the last editor created owns F5 for every sheet), and the providers register once and route by model. **The result status bar** is one line above each grid saying what happened, in four states: running (`Running… 120,000 rows so far · 1.4 s`, with a spinner), complete (`1,000,000 rows · 1.07 s`, or `3 rows affected · 4 ms` for INSERT/UPDATE/DELETE), cancelled (`Cancelled after 1.2 s`) and error. A **failure is red** (background, edge, ✖ and words, so it never depends on colour alone) with the server's message as the headline and its code and extra lines (`42703 · POSITION: 30`, `HINT: ...`) beside it, announced to screen readers as an alert; the failed result's **tab also carries a red ✖**, so it is visible with several tabs open. The detailed timings (first batch, first paint, engine split, drawing, the scroll benchmark) are behind a **Details** toggle on the same bar, which stays open across results. The sheet's **message bar** (between the editor and the results) uses the same tones: **amber** with a ⚠ for "cannot run yet" (not connected, including trying to run while disconnected, or a run blocked by identifier problems), **red** with a ✖ when something failed (a connection), neutral for information (`Connecting…`, `nothing to run`). **Cancel works before the first row.** A statement is registered under a query id only once the database starts answering, so a slow aggregate or `pg_sleep` has no id, yet that is when cancelling matters most (and it holds the session lock, so nothing else on that sheet works, and closing the tab would wait it out). The engine therefore keeps a handle on a statement that is still starting: \`cancel\` can name a connection instead of a query, and Cancel, Ctrl+., closing the sheet, a new statement and Begin/Commit/Rollback all cancel it. A statement cancelled before any result reports \`cancelled\`, not an error; inside a transaction the server aborts the transaction, and the bar shows that. Not yet: saving and opening `.sql` files, autosave and restore, a script run (Shift+F5) with several result sets per statement, renaming or reordering tabs, and sharing one schema cache between sessions on the same server (each session introspects for itself, about 0.2 s for 2,000 tables).
- Grid editing: none for now. The grid is read-only. Revisit later (it would interact with transactions and the lock).
- Export formats: CSV and JSON are enough for now. Import is not decided.
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
