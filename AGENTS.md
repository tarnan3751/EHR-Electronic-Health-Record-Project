# AGENTS.md

Conventions every coding agent (and every person) follows in this repo. Most come from the project overview; the cross-platform rules come from the machines the team uses.

## Where authorization is applied

- Twice, always: ASP.NET Core policies in the app, then row-level security (RLS) in PostgreSQL. A missed check in code must still return no unauthorized rows.
- Policies attach to whole areas by convention (`Areas/Clinical`, `Areas/Portal`, `Api/Sync`, `Api/Dictation`, `Api/Fhir`), not handler by handler.
- Portal handlers never accept a patient ID. The subject always comes from the session: pages take it from `PortalSubject`, which is the account's own patient or, under a proxy grant the database has checked, that grant's patient.
- Every PHI read goes through the PHI reader service in `Ehr.Domain`, which records an audit event.
- RLS is enabled and forced on every PHI table. The app connects as `ehr_app` or `ehr_read`, which own nothing and lack `BYPASSRLS`.
- Network position is never identity. Being on the LAN never skips authentication.

## Where raw SQL is allowed

- Dapper, for hot reads, inside `Ehr.Data` only. It borrows EF Core's connection and transaction so the RLS context applies.
- The RLS context is set with `set_config(..., true)` as the first statement of every transaction. Plain `SET` is banned: on a pooled connection it can leak into the next request. `UserContextInterceptor` (`Ehr.Data`) does this, and `DatabaseTransactionFilter` (`Ehr.Web`) runs each page handler in one transaction.
- With nobody signed in, `app.user_id` is an empty string, not NULL, so policies read it as `NULLIF(current_setting('app.user_id', true), '')::uuid`.
- `db/policies` (RLS, triggers, roles) is hand-written SQL.
- Sync watermarks use transaction IDs, never timestamps (`ChangeLog` in `Ehr.Data`). A trigger writes the change log (`db/policies/20-change-log.sql`); the app roles can only read it.

## How migrations are made

- Schema: EF Core migrations in `db/migrations` (the `Ehr.Migrations` project), added with `dotnet ef migrations add <Name> --project db/migrations`. Every migration is committed with the regenerated `db/migrations/schema.sql`, so reviewers read the real SQL; a test fails if it's out of date.
- RLS, triggers, roles, and grants: hand-written in `db/policies`. Plain SQL only (no psql commands), safe to run again. `00-roles.sql` runs before the migrations; every other file runs after them, in name order.
- A function that runs as its owner (`SECURITY DEFINER`) sets `search_path` and revokes `EXECUTE` from `PUBLIC`. `Ehr.Security.Tests` fails otherwise.
- A policy calls the user-context functions as scalar subqueries, `(SELECT ehr.is_clinician())`, so PostgreSQL runs each once per statement instead of once per row. `Ehr.Security.Tests` fails otherwise.
- Every table gets explicit grants in `db/policies/10-grants.sql` and the same privileges in `ExpectedPrivileges` in `Ehr.Security.Tests`. A table without them fails CI. The file revokes everything from the runtime roles first, so it is the complete list: never grant privileges anywhere else. Runtime roles never get `DELETE` unless a feature needs it.
- The infrastructure roles `replicator` (cloud standby) and `barman` (backups), and the migration role `ehr_owner` with the `ehr` schema it owns, are created when the primary is first initialized, by `infra/compose/postgres/primary/initdb`. `replicator` and `barman` have no table privileges. `ehr_owner` can create roles, which it uses to manage `ehr_app` and `ehr_read` from `db/policies`; PostgreSQL stops it from creating superuser, replication, or `BYPASSRLS` roles.
- Migrations run as `ehr_owner`, only through the migration job (`infra/migrate`): in the local stack, in CI, and at deploy. The running apps never use that role.
- The synthetic seed (`bench/Ehr.Seed`) is the one other use of `ehr_owner`, and only on a clinic with no patients: in one transaction it lifts forced row-level security, the write-audit triggers, the chart lists' history triggers and the blood pressure readings' check on the tables it fills, loads them, and restores them all before committing, so seeded rows have no write-audit events and `SeedTests` checks every seeded reading in the check's place. `Ehr.Security.Tests` checks that it leaves both in place, refuses a clinic with patients, and leaves nothing behind when it fails.

## How the UI is styled

- The brand guide is `docs/brand/README.md`, and `docs/brand/tokens.json` is the source of truth for every color, type style, space, radius and size. After changing it, the shared stylesheet, a font or a logo, run `dotnet run tools/brand/sync.cs`: it writes `src/Ehr.Design/wwwroot/css/tokens.css` and the desktop app's copy of the shared CSS, fonts and logos (`src/Ehr.Desktop/frontend/css`, `fonts`, `img`). Never edit those by hand.
- One shared stylesheet, `src/Ehr.Design/wwwroot/css/ehr.css`, in layers: base, layout, components, pages. It uses the token variables only, never a raw color, so light and dark both work. Components are the guide's, with its `sc-` classes.
- No CSS or JavaScript framework, no CSS build step, no inline `style` attributes or inline scripts, and nothing loaded from another host: fonts, logos and scripts are served by the apps themselves.

## Human review

Every change reaches `main` through a pull request, with the `ci` check passing and approval from someone other than its author. On top of that, changes to the following get line-by-line review by someone other than the person who prompted the agent:

- `db/policies`
- authorization attributes and policies
- the sync conflict resolver
- the dictation prompt and note schema

## What "done" means

The `ci` check passes (`.github/workflows/ci.yml`). Today it runs:

- Restore (a high or critical package advisory fails it), a Release build, `dotnet format --verify-no-changes`, and `dotnet test`.
- No runtime role owns anything, has `BYPASSRLS` or another elevated attribute, or holds a privilege beyond its grants, and every `SECURITY DEFINER` function is locked down (`Ehr.Security.Tests`).
- Every table has a protection decision; every PHI table has RLS enabled and forced plus the write-audit trigger; querying a PHI table without `app.user_id` set returns zero rows; a patient sees only their own row (or, while acting under a proxy grant in force, that one patient's instead) and an admin none (`Ehr.Security.Tests`).
- PHI reads only go through the PHI reader, portal handlers take no patient from a request, and no layer depends on the one above it (`Ehr.Architecture.Tests`).
- Anonymous requests are sent to sign in, each policy refuses the accounts it should, a staff sign-in needs its second factor, and a patient sees only their own record, or the one record they are acting for as a proxy (`Ehr.Web.Tests`).
- Every model change has a migration, and `db/migrations/schema.sql` matches the migrations (`Ehr.Data.Tests`).
- The tokens' CSS matches `docs/brand/tokens.json`, and the desktop app's copy of the shared CSS, fonts and logos matches the web apps' (`dotnet run tools/brand/sync.cs -- --check`, and `Ehr.Web.Tests`).
- The local server stack: the migration job applies cleanly, and the stack serves all three sites, replicates, backs up, and restores to a point in time.
- macOS and Windows desktop builds, with `cargo fmt --check` and `cargo clippy -D warnings`.
- The tests for `src/Ehr.Dictation/setup_models.py` and `measure_memory.py`, the dictation sidecar and the eval, and the sidecar's image built for `linux/amd64` and `linux/arm64`.
- The committed dictation eval results (`eval/results/latest.json`) come from the current prompt, note schema, generation parameters, sidecar code, eval code and test set, hold exactly the recordings the scripts define, every one sorted, show no order or allergy change and no retracted value without a warning that the owner hasn't accepted in `eval/known-limitations.toml`, say what the dictation proposed, every measure of it as the stored recordings give it again, with no false or misheard proposal the owner hasn't accepted there and at least 14 right, and score no lower than the last accepted baseline. The same holds of the visits' results for listening to a visit (`eval/results/ambient-latest.json`), which also hold a contention probe and show no question quoted as a finding, no denied symptom offered as present, no hedged or denied diagnosis offered as certain, no value quoted from a speaker its field may not quote without a warning, nothing proposed for the chart and no patient's request listed as the clinician's instruction, unless the owner has accepted the case. The Spanish results (`eval/results/latest-es.json`) come from the current Spanish prompt, note schema and test set and the same code, cover every Spanish script's recordings and text and the two recordings in the wrong language with every one sorted, show no instruction to order something or change the allergy list and no mistranslation without a warning that the owner hasn't accepted in `eval/known-limitations.toml`, show values filed in the wrong field (one its field's gold lacks and another field's gold has) without a warning in no larger a share of statements than English's on the same kind of text plus 2 percentage points, and score no lower than the last accepted Spanish baseline. Of the review's wrong translations (`eval/wrong-translations.toml`), none that reverses the meaning passes the Spanish checks without a warning unless the owner has accepted it by its id there. The trick sentences (`eval/trick-sentences.toml`), the set the gate records, weighed again by the current checks, give no wrong proposal but those the owner has accepted by name there, no more wrong proposals than the set records, and every control.
- p99 server render time excluding the commit under 20 ms on 50,000 seeded patients, on-site and off-site (`bench/Ehr.Bench`). The full time, commit included, is reported beside it; its 20 ms target is checked on the clinic server, because CI runners' disks make commit time unsteady.

Gates added to `ci` as the code they check appears:

- Page JavaScript under 50 KB, compressed, as served; first contentful paint under 500 ms.

Run `dotnet format` (and `cargo fmt` for the desktop app) before pushing.

## Cross-platform

The team develops on Apple Silicon Macs and Intel/AMD Windows PCs; servers and CI run Linux.

- Line endings are LF (`.gitattributes`). Only `.cmd` and `.bat` files use CRLF.
- Match the case of file and folder names exactly. Macs and Windows ignore case; the Linux servers don't.
- No bash-only scripts on your machine. Use `dotnet run script.cs` (.NET 10) or PowerShell 7, which run on both. Dictation and eval tooling, which is already Python, may also use Python run with `uv run`, with its dependencies declared in the script. Scripts that run inside Linux containers are POSIX `sh`.
- Container images are built in CI for `linux/amd64` and `linux/arm64`, never pushed from a laptop. Macs build arm64 images that an x86-64 server can't run.
- PostgreSQL data lives in Docker named volumes, not host folders.
- The local stack (`infra/compose`) publishes ports on `127.0.0.1` only, and app images run as a non-root user.
- The desktop app makes network calls from Rust, not from the web view. The web view's origin differs by OS: `tauri://localhost` on macOS, `http://tauri.localhost` on Windows.
- Never hard-code an audio recording format. WebKit (Safari, the macOS app) and Chromium (Chrome, Edge, the Windows app) record different containers; ffmpeg normalizes both on the server.

## Always

- Secrets come from environment configuration, never the repo.
- All data is synthetic.
