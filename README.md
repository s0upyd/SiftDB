# SiftDB

**A Windows desktop SQL client that takes the friction out of everyday database work.**

SiftDB is a fast, keyboard-friendly SQL client for Windows. PostgreSQL is supported today, and support for other databases plugs in. It's built around three everyday problems:

- **Identifier casing and quoting.** Postgres folds unquoted names to lowercase, so a table created as `"OrderItems"` needs quotes forever. SiftDB knows your schema and handles the quotes for you.
- **Knowing your transaction state.** A bar above the editor always shows whether you're in a transaction, whether anything has changed, and whether an error has aborted it.
- **Big results.** Rows stream into a canvas-drawn grid that stays smooth with a million rows.

> **Status:** early development. There is no installer or release yet. Build it from source (see below).

---

## Features

### Write SQL the way you think it
- **Assisted mode** (the default): type `select * from OrderItems` and SiftDB shows `"OrderItems"`, with the added quotes greyed out. What you see is exactly what runs.
- **Strict mode:** what you type is what runs. Problems are underlined, with quick fixes (`Alt+Enter`), and running is blocked until they're fixed.
- It catches common mistakes: a name in the wrong case, `"bob"` where you meant the string `'bob'`, ambiguous columns, and unknown columns (with suggestions).
- `Ctrl+Alt+Q` accepts all corrections as real text. Saving an Assisted sheet writes the corrected SQL, so the file runs in psql or any other tool.
- Schema-aware autocomplete for tables and columns, which follows aliases and quotes names correctly.
- Statements are split by PostgreSQL's own parser (libpg_query), so strings, comments and function bodies never confuse it. A blank line also ends a statement, and SiftDB tells you when it looks like a `;` is missing.

### Sheets
- Each tab is a **sheet** with its **own database session**, so `BEGIN`/`COMMIT`, temp tables and `SET` carry over between statements.
- `F5` / `Ctrl+Enter` runs the selection if there is one, otherwise the statement the cursor is in. The statement that will run is highlighted, and each statement has a ▶ in the margin.
- Each run gets its own result tab, and results can be pinned.
- Sheets save as plain `.sql` files. Every sheet is autosaved and restored on restart, without reconnecting automatically.
- Long-running statements show a spinner on their tab, and leave a ✓ / ✖ mark when they finish while you're on another sheet.

### Transactions you can see
- 🟢 transaction open · 🟡 changes pending, with Commit / Rollback · 🔴 aborted, with Rollback only
- The state comes from the server itself, so the buttons and a typed `BEGIN` always agree.
- **A failed statement doesn't throw away your transaction:** SiftDB undoes just that statement (using a savepoint) and keeps your earlier changes pending.
- Optional **auto-transaction** per connection: every change waits for Commit, so a missing `WHERE` can still be rolled back.

### Connections
- Saved connections. Passwords are encrypted with Windows DPAPI and never stored in the connection file.
- **Read-only connections**, enforced by the server (`default_transaction_read_only`), with an app-side guard against statements that would switch it off.
- SSL modes, including `verify-full` with a CA file.

### Results
- A canvas grid with streaming results, column auto-fit and resizing, and clearly drawn NULLs.
- **Result styling rules:** colour cells by column name, type and condition (NULL, ranges, past/future dates, patterns…), with built-in presets and drag-to-reorder priority.

### Planned
Bind variables (`@surname`, with values shown greyed inline and stored encrypted), whole-sheet script runs (`Shift+F5`), JOIN suggestions from foreign keys, spill-to-disk for huge results, a signed installer with auto-update, and more database providers.

---

## Architecture

| Layer | Technology | Role |
|---|---|---|
| Host | C# / WPF + WebView2 | A thin native shell: window, dialogs, credential storage |
| Engine | C# (.NET 10) | Connections, parsing, execution, streaming, transactions, settings. Headless. |
| UI | TypeScript + Monaco | Everything you see, including a custom canvas results grid |
| Providers | Plugins | One per database. The first is PostgreSQL (Npgsql + libpg_query). |

Result rows travel from the engine to the UI in a columnar binary format over WebView2 shared buffers. In testing, 1M rows arrive in about 1 second, with the first rows in about 125 ms.

The full design and the reasoning behind decisions are in [`docs/DESIGN.md`](docs/DESIGN.md).

---

## Building from source

**Requirements**
- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for the UI build)
- Microsoft Edge WebView2 Runtime (already installed on most Windows 10/11 machines)
- A PostgreSQL server to connect to

**Run**
```powershell
git clone https://github.com/s0upyd/SiftDB.git
cd SiftDB
dotnet run --project src/SiftDB.Host
```
The build runs `npm install` and `npm run build` for the UI automatically when UI sources change. Add `-p:SkipUiBuild=true` to skip that step. For performance testing, use a Release build (`-c Release`).

**Sample data**

Two scripts in `tools/` create tables with deliberately awkward names, for trying out the quoting features:
```powershell
psql -U postgres -h localhost -f tools/seed-demo.sql       # "shop" schema, mixed naming styles
psql -U postgres -h localhost -1 -v ON_ERROR_STOP=1 -f tools/seed-contacts.sql   # EF Core style "Person", "Address"…
```

**Tests**
```powershell
dotnet test
```
Tests that need a live database read a Npgsql connection string from `SIFTDB_SPIKE_CONN`.

---

## Contributing

Issues and suggestions are welcome. The project is young and the design is still moving, so please read [`docs/DESIGN.md`](docs/DESIGN.md) before starting anything big, and open an issue to discuss it first.

## Licence

[MIT](LICENSE)
