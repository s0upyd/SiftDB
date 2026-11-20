Read docs/DESIGN.md first

## Commit messages

Release notes are generated from commit subjects (design section 14, F17; cliff.toml), so:

- The commit subject is one line, written for users: what they'll notice, not how it was done.
- Use `feat`, `fix`, `perf` for user-visible changes (`feat(ui)` / `fix(ui)` for small UI improvements).
- Use `refactor`, `test`, `docs`, `chore`, `build`, `ci` for everything else: these are left out of the notes.
- Add `!` (`feat!:`) for anything that breaks existing files or settings.
- Optionally add a footer `Try-it: <command id>` for a feature worth trying: What's New shows a Try it button that runs it. The ids are the command registry's (`commands.add(...)` in src/ui/src/main.ts, e.g. `format`, `dryRun`, `openRecent`, `settings`).
