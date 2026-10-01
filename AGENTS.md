# AGENTS.md

Conventions every coding agent (and every person) follows in this repo. Most come from the project overview; the cross-platform rules come from the machines the team uses.

## Where authorization is applied

- Twice, always: ASP.NET Core policies in the app, then row-level security (RLS) in PostgreSQL. A missed check in code must still return no unauthorized rows.
- Policies attach to whole areas by convention (`Areas/Clinical`, `Areas/Portal`, `Api/Sync`), not handler by handler.
- Portal handlers never accept a patient ID. The subject always comes from the session.
- Every PHI read goes through the PHI reader service in `Ehr.Domain`, which records an audit event.
- RLS is enabled and forced on every PHI table. The app connects as `ehr_app` or `ehr_read`, which own nothing and lack `BYPASSRLS`.
- Network position is never identity. Being on the LAN never skips authentication.

## Where raw SQL is allowed

- Dapper, for hot reads, inside `Ehr.Data` only. It borrows EF Core's connection and transaction so the RLS context applies.
- The RLS context is set with `set_config(..., true)` as the first statement of every transaction. Plain `SET` is banned: on a pooled connection it can leak into the next request.
- `db/policies` (RLS, triggers, roles) is hand-written SQL.
- Sync watermarks use transaction IDs, never timestamps.

## How migrations are made

- Schema: EF Core migrations in `db/migrations` (the `Ehr.Migrations` project), added with `dotnet ef migrations add <Name> --project db/migrations`. Every migration is committed with the regenerated `db/migrations/schema.sql`, so reviewers read the real SQL; a test fails if it's out of date.
- RLS, triggers, roles, and grants: hand-written in `db/policies`. Plain SQL only (no psql commands), safe to run again. `00-roles.sql` runs before the migrations; every other file runs after them, in name order.
- Every table gets explicit grants in `db/policies/10-grants.sql` and the same privileges in `ExpectedPrivileges` in `Ehr.Security.Tests`. A table without them fails CI. Runtime roles never get `DELETE` unless a feature needs it.
- The infrastructure roles `replicator` (cloud standby) and `barman` (backups), and the migration role `ehr_owner` with the `ehr` schema it owns, are created when the primary is first initialized, by `infra/compose/postgres/primary/initdb`. `replicator` and `barman` have no table privileges. `ehr_owner` can create roles, which it uses to manage `ehr_app` and `ehr_read` from `db/policies`; PostgreSQL stops it from creating superuser, replication, or `BYPASSRLS` roles.
- Migrations run as `ehr_owner`, only through the migration job (`infra/migrate`): in the local stack, in CI, and at deploy. The running apps never use that role.

## Human review

Every change reaches `main` through a pull request, with the `ci` check passing and approval from someone other than its author. On top of that, changes to the following get line-by-line review by someone other than the person who prompted the agent:

- `db/policies`
- authorization attributes and policies
- the sync conflict resolver
- the dictation prompt and note schema

## What "done" means

The `ci` check passes (`.github/workflows/ci.yml`). Today it runs:

- Restore (a high or critical package advisory fails it), a Release build, `dotnet format --verify-no-changes`, and `dotnet test`.
- No runtime role owns anything, has `BYPASSRLS` or another elevated attribute, or holds a privilege beyond its grants (`Ehr.Security.Tests`).
- Every model change has a migration, and `db/migrations/schema.sql` matches the migrations (`Ehr.Data.Tests`).
- The local server stack: the migration job applies cleanly, and the stack serves all three sites, replicates, and backs up.
- macOS and Windows desktop builds, with `cargo fmt --check` and `cargo clippy -D warnings`.

Gates added to `ci` as the code they check appears:

- Every PHI table has RLS enabled and forced, plus a write-audit trigger.
- Querying a PHI table without `app.user_id` set returns zero rows.
- An architecture test proves PHI reads only go through the PHI reader.
- p99 server render under 20 ms on 50,000 seeded patients; page JavaScript under 50 KB; first contentful paint under 500 ms.
- Dictation eval scores do not drop below the last accepted baseline.

Run `dotnet format` (and `cargo fmt` for the desktop app) before pushing. When `Ehr.Architecture.Tests` gets its first test, delete its `--ignore-exit-code 8` line.

## Cross-platform

The team develops on Apple Silicon Macs and Intel/AMD Windows PCs; servers and CI run Linux.

- Line endings are LF (`.gitattributes`). Only `.cmd` and `.bat` files use CRLF.
- Match the case of file and folder names exactly. Macs and Windows ignore case; the Linux servers don't.
- No bash-only scripts on your machine. Use `dotnet run script.cs` (.NET 10) or PowerShell 7, which run on both. Scripts that run inside Linux containers are POSIX `sh`.
- Container images are built in CI for `linux/amd64` and `linux/arm64`, never pushed from a laptop. Macs build arm64 images that an x86-64 server can't run.
- PostgreSQL data lives in Docker named volumes, not host folders.
- The local stack (`infra/compose`) publishes ports on `127.0.0.1` only, and app images run as a non-root user.
- The desktop app makes network calls from Rust, not from the web view. The web view's origin differs by OS: `tauri://localhost` on macOS, `http://tauri.localhost` on Windows.
- Never hard-code an audio recording format. WebKit (Safari, the macOS app) and Chromium (Chrome, Edge, the Windows app) record different containers; ffmpeg normalizes both on the server.

## Always

- Secrets come from environment configuration, never the repo.
- All data is synthetic.
