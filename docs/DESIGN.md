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

**`Shift+F5`** runs the **whole sheet as one script**: sent in one go, with an optional wrapping transaction. All result sets and `NOTICE`/`RAISE` messages are returned, and an error stops the script and jumps to the failing line. Connections marked as production ask for confirmation first. (There's no per-sheet "script mode" setting. That was considered and rejected.)

**`Ctrl+.` / `Esc`** cancels.

**Details**
- Statements are split by the parser (libpg_query reports where each starts and ends), never by looking for `;`.
- The statement that will run is lightly highlighted as the cursor moves.
- A ▶ in the margin next to each statement lets you run it with the mouse.
- Each statement that runs gets its own result tab, labelled with the start of its SQL. Results can be pinned.
- **Each sheet has its own database session**, so `BEGIN`/`COMMIT` across statements, temp tables and `SET` all carry over.

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
- Optionally, a per-connection setting starts a transaction automatically on the first change (e.g. the default for production).

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

## 8. Delivery

- The host is published self-contained, so no .NET runtime install is needed.
- The UI is bundled and served locally, never from a remote URL. Engine and UI are released together.
- If WebView2 is missing, the installer runs Microsoft's bootstrapper.
- **Velopack** handles the installer, background delta updates and stable/beta channels. Releases are hosted on GitHub Releases or Azure Blob Storage, published from an Azure DevOps pipeline.
- **Code signing** with Azure Trusted Signing, to avoid SmartScreen warnings.
- Third-party providers go in a plugins folder (possibly NuGet-format packages) and are checked against the contract version.

---

## 9. Back burner

### Read-only lock
A lock button on the toolbar protects users who have write permissions from themselves.
- **App check:** the parser blocks writes before anything is sent, with a clear message and an "Unlock and run" option that asks for confirmation.
- **Server enforcement where possible:** Postgres uses `SET default_transaction_read_only = on`, and the app also blocks statements that would switch it off. SQL Server has no equivalent, so it relies on the app check.
- A capability flag `ReadOnlyEnforcement = Server | Client` decides whether the icon says "Enforced by server" or "Guarded by app".
- The lock is per tab, with a default per connection (production connections open locked).
- A locked tab can't go yellow. You can't toggle the lock while changes are pending.

### Later
- A TUI front end on the same engine
- AI completions
- A SQL Server provider

---

## 10. Open questions

**Needed before building the editor features**
- Is there a usable libpg_query binding for .NET, or does it need its own wrapper?
- Default mode: Strict or Assisted, and global or per tab?
- Variables with no inferable type: default to text, or ask?

**Decided**
- Front-end framework: **Solid** (fine-grained reactivity, no virtual DOM). The spike uses plain TypeScript, since it needs no framework.
- Grid: **custom canvas grid**, read-only, with virtualised rows, column widths, selection and copy. Library grids (Glide is React-only, AG Grid has licence problems) were rejected to avoid framework mismatch and so the spike measures the real rendering ceiling.
- Bridge transport: **WebView2 shared buffers** (`CreateSharedBuffer` + `PostSharedBufferToScript`) for row batches. `PostWebMessageAsArrayBuffer` does not exist in WebView2, so the earlier plan was wrong. Commands and events are JSON for now (MessagePack deferred; they're low volume). Row batches use a custom columnar binary layout (see `RowBatchWriter`): a header with column types and row count, then typed-array columns, 8-byte aligned so the UI can wrap them directly. The UI copies each batch out and releases the buffer straight away. Arrow can be revisited later for interop.
- Large results: **stream with fetch-on-scroll**. The engine reads ahead from the `DbDataReader` into a capped buffer and pauses when the UI isn't consuming (backpressure). Fetched rows are kept in memory up to a cap, then spilled to a temp file, so scrolling back is instant. The UI shows a "fetched N rows, still loading" state, and cancel is instant. There is no hard row limit by default.
- Grid editing: none for now. The grid is read-only. Revisit later (it would interact with transactions and the lock).
- Export formats: CSV and JSON are enough for now. Import is not decided.
- Query history retention: deferred until later.
- Audience: anyone (a public product, not just personal use). This raises the bar on installer, signing, docs and the plugin contract.

**Not discussed yet**
- Connections: auth methods (password, Entra ID for Azure Postgres, certificates), SSH tunnels, SSL, the connection manager UI
- Schema browser scope: object tree, DDL view, search, ER diagrams
- Query history: how it's searched, whether it's per connection
- Licensing (plugin SDK, open source or closed): not a concern for now, and monetisation isn't a goal at this stage

---

## 11. Suggested solution layout

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

## 12. First piece of work: the spike

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
| Transfer complete (1M rows, 55 batches, 53.4 MB) | 5.1 s (engine reports 5.09 s) | none set |
| Scroll, top to bottom in 6 s | p50 8.3 ms, p95 8.5 ms, max 8.8 ms, 0 frames over 20 ms | smooth |
| Draw time per frame | avg 0.51 ms, max 2.2 ms | – |
| JS heap after load | 54 MB | bounded |

Reading: first rows and scrolling pass comfortably. The UI side is not the bottleneck: the 5.1 s total is almost all spent in the engine/Postgres side (the client and engine totals differ by 11 ms). Memory scales linearly with rows (heap is about the size of the data), so it is only bounded once the spill-to-disk cap from §10 exists.

**Known spike limitations:** NULLs show as `0` / empty; no selection or copy; the scroll height is plain DOM, so it tops out around 33M px (about 1.5M rows at 22px); an oversized single row isn't specially handled; no spill-to-disk.
