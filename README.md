# EHR platform

An electronic health record with a web app (clinical and patient portal) and a Mac/Windows desktop app. It is designed to be secure first and as fast as the hardware allows. One on-prem PostgreSQL primary owns every write; everything else is a copy, a cache, or a proxy.

## Status

Early foundation. No EHR features exist yet. The walking skeleton (one trivial feature, team quick text, through every layer) is done: the database foundation, the on-site page, the off-site host, the sync API, and the desktop app. Next is the model spike: both dictation models running on the demo laptop, with a measured time for a two-minute dictation.

**Working today**

- The desktop app (Tauri) shows the quick text list, and works offline. Phrases are kept in an encrypted local database (SQLCipher) whose key is made on first launch and kept in the macOS Keychain or Windows Credential Manager. Adds and edits are saved there first, in an outbox; a background sync, every 10 seconds and straight after a change, sends them and brings back what changed, from the on-prem app when it can be reached and through the cloud app otherwise. A change the server refuses comes back with both versions and **Keep mine** and **Keep theirs**, as on the page. Syncing needs a clinician signed in, with email, password and the code from their authenticator app; the session is kept in the Keychain or Credential Manager beside the database key, every sync request carries it, and when the server ends it the app asks to sign in again. Signing out keeps the local copy. The list can be read offline and signed out; changes need someone signed in. The window shows whether it's online and how many changes are waiting. Every network call is made in Rust, not in the web view. Verified on Linux against the local server stack, including offline changes, conflicts and the off-site path; sign-in so far only against a stand-in server in its tests. CI builds it on macOS and Windows; it hasn't been run on a Windows PC yet.
- The web app (ASP.NET Core Razor Pages with htmx) serves the quick text page at `/quick-texts`: a shared list of phrases, each with a shortcut such as `.nad`. On-site, phrases can be added and edited; a save that started from an older version is refused and shows both versions, with **Keep mine** and **Keep theirs**. Off-site, the cloud app shows the list from the replica and forwards saves to the on-prem app; right after a save, that person's pages come from on-prem until the replica has the change. If the on-prem server can't be reached, off-site pages still load and a save says it wasn't saved. Tests run the page against a real PostgreSQL 18.
- Sign-in and authorization. Staff and patients have separate accounts, cookies and sign-in pages (`Areas/Clinical/Pages/Account`, `Areas/Portal/Pages/Account`). Every staff sign-in takes a password and a code from an authenticator app, wherever it comes from; a new account enrolls its app at its first sign-in, and sees recovery codes once. The on-prem app creates the first admin from `Ehr:Bootstrap` settings when there are no staff accounts; admins create the rest at **Staff**, each with a one-time setup link, and look after them there: a new setup link for a forgotten password, an authenticator reset for a lost phone, and disabling an account, which locks it out and ends its sessions until it is enabled again. Policies attach to whole areas (`Program.cs`): any staff account for the clinic's root (which sends a clinician to their patients and an admin to the staff page), clinicians for patients, admins for staff, patient accounts for the portal, and nothing anonymous but the sign-in pages and static files. The desktop app signs in at `/api/desktop/sign-in` with the same two factors and gets a session token, which every sync request carries; the token is a clinician's, lasts thirty days, and ends at sign-out or when the password changes (`ehr.desktop_sessions`). The signed-in account reaches the database as `app.user_id`, where row-level security is the second layer.
- The first PHI table, patients, with row-level security enabled and forced, a write-audit trigger, and the PHI reader in `Ehr.Domain` that every read goes through and that records each one (`db/policies`, `Phi/PhiReader.cs`). A clinician's home is their panel: the patients they have added to it from the chart (`ehr.patient_assignments`, PHI like the patients themselves). A search by name or MRN covers the whole clinic and marks the patients on their panel. The chart shows demographics, puts the patient on the panel or takes them off, and issues a portal enrollment code. Opening the chart of a patient who is not on the clinician's panel first asks for a reason (emergency, or break-glass, access): the reason is recorded as a `break_glass` audit event for admins to review, and the chart stays open to that clinician for four hours (`ehr.break_glass_accesses`). The database still lets every clinician read every patient, as the team decided; the reason and its review are the control. A patient registers at `/portal/register` with that code, confirms their email, signs in, and sees their own record and nobody else's; a forgotten password is reset through a one-time link sent to their address (`/portal/forgot`). The portal takes the patient from the session, never from a request. Pages that read PHI always come from the on-prem app, since reading PHI writes an audit event the replica can't take. Every view and change of patient information, and every sign-in, is in `ehr.audit_events`, which nothing at runtime can change. Admins review it at **Audit** (`/admin/audit`): the newest events first, narrowed by action, staff account, patient ID and dates, with emergency access flagged. Admins have no access to patient data, so a patient appears there by ID; the values a write changed, which the trigger records, are the one place an admin sees any.
- The sync API for the desktop app, at `/api/sync/quick-texts` on both servers (`src/Ehr.Web/Api/Sync`), for a desktop session token; the cloud app checks the token against its replica. A pull returns the phrases changed since a transaction-ID watermark, fed by a change log that a database trigger writes; a push applies batches of changes, each with an idempotency key, and returns applied, duplicate, conflict (with the saved phrase) or invalid for each.
- The local server stack runs the whole server architecture in Docker Compose: the PostgreSQL 18 primary, its streaming replica, Barman backups with point-in-time restore, and a simulated WAN. Verified on Linux, and on an Apple Silicon Mac for replication, isolation, backups and restore; Windows is not yet verified.
- The database foundation: the app's roles (`ehr_owner` for migrations, `ehr_app` and `ehr_read` for the apps), the first table (`quick_texts`), and a migration job that brings the primary up to date every time the server stack starts. Security tests check the roles against a real PostgreSQL 18. See [Database changes](#database-changes).
- CI (GitHub Actions) checks every push and pull request. `main` only accepts reviewed pull requests that pass it, and each commit on `main` publishes the web image. See [Continuous integration](#continuous-integration).
- The dictation models download with one script, `src/Ehr.Dictation/setup_models.py`, which checks every file against the hash Hugging Face publishes: K2 Horizon 7B for note structuring and MedASR for speech to text. Nothing uses them yet. See [Dictation models](#dictation-models).
- Dependency manifests and toolchain versions are in place. Python packages use version ranges until a lockfile is added.

**Not built yet:** the rest of the patient data model (encounters, notes, orders, results), the clinical workflows beyond finding a patient and opening a chart, the portal's results, appointments and messages, dictation itself (the MedASR sidecar, the note schema and prompt, the review screen), the desktop app syncing anything but quick texts, and the cloud app's own queue of audit events (until then PHI pages are forwarded to on-prem and are unavailable off-site when the link is down).

## Layout

Folders describe their intended contents; most are still empty.

```
src/
  Ehr.Web/          ASP.NET Core Razor Pages host: Areas/Clinical, Areas/Portal, Api/Sync, Api/Dictation
  Ehr.Domain/       services, authorization policies, PHI reader, FHIR-shaped models
  Ehr.Data/         EF Core + Dapper on Npgsql: read and write contexts, RLS interceptor
  Ehr.Design/       Razor class library: design tokens, CSS, sprite sheet, shared partials, htmx, Alpine.js
  Ehr.Desktop/      Tauri 2 app: src-tauri (Rust shell, SQLCipher, sync) + frontend
  Ehr.Dictation/    setup_models.py (downloads both models) and its tests; sidecar (Python, MedASR), llama-server config, note schema
tests/
  Ehr.Security.Tests/      RLS, role, and audit gates against real PostgreSQL 18
  Ehr.Architecture.Tests/  layering, and PHI reads only through the PHI reader
  Ehr.Web.Tests/           the web app, run in memory as each server, against real PostgreSQL 18
  Ehr.Data.Tests/          the data layer and migrations
  Ehr.Testing/             shared by the test projects: a migrated PostgreSQL 18 in a container
db/
  migrations/       EF Core migrations (the Ehr.Migrations project) and the SQL they produce, schema.sql
  policies/         RLS, triggers, roles, grants (hand-written, human-reviewed)
eval/               dictation gold set and scoring
bench/              load tests and budget assertions
infra/              compose (local server stack), migrate (migration job), barman (backup image), tailscale (access rules)
.github/            CI workflow and Dependabot settings
.vscode/            shared settings, recommended extensions, and run tasks
.editorconfig       editor and formatting settings (checked in CI)
AGENTS.md           conventions every coding agent follows
```

## Supported machines

Development and the desktop app: Apple Silicon Macs, and Windows PCs with Intel or AMD CPUs and NVIDIA or AMD GPUs. Servers and CI run Linux.

## Setup

Everyone:

| Tool | Version | Pinned in, or how to install |
|---|---|---|
| .NET SDK | 10.0 (LTS) | `global.json` |
| Rust, via rustup | 1.98.1 | `src/Ehr.Desktop/src-tauri/rust-toolchain.toml`; installs itself on the first build |
| Tauri CLI | 2.x | `cargo binstall tauri-cli --version "^2"` (prebuilt, seconds) or `cargo install tauri-cli --version "^2" --locked` (compiles, minutes) |
| uv, for Python 3.12+ | | `pyproject.toml` in `src/Ehr.Dictation/sidecar` and `eval`; `src/Ehr.Dictation/setup_models.py` lists its own dependencies |
| Docker Desktop | Compose v2 | For the local server stack |
| mkcert | | Local HTTPS certificates for the server stack |
| VS Code | | Accept the recommended extensions: C# Dev Kit, rust-analyzer, Tauri |

**Apple Silicon Mac**

- Xcode Command Line Tools (`xcode-select --install`) for the compiler and `make`. macOS already ships the Perl that the vendored OpenSSL build needs.
- Docker Desktop: open it once, and under Settings → Advanced choose **System** for the CLI tools, so every shell (including VS Code's) finds `docker`. Its default memory is enough for the server stack; only the `ai` profile needs more (see `infra/compose/README.md`).

**Windows PC**

- Visual Studio Build Tools with the "Desktop development with C++" workload. Keep rustup's default MSVC toolchain.
- [Strawberry Perl](https://strawberryperl.com/), ahead of Git's Perl on `PATH`. The vendored OpenSSL build rejects Git Bash's Perl, so build from PowerShell, not Git Bash.
- Docker Desktop with the WSL2 backend (virtualization enabled in the firmware). Its default memory is enough for the server stack; for the `ai` profile, raise `memory` in `%UserProfile%\.wslconfig`.
- Clone to a short path outside OneDrive, such as `C:\src\ehr`. The OpenSSL build nests deep enough to hit Windows' 260-character path limit, and OneDrive locks files mid-build.
- NASM is not needed: `src/Ehr.Desktop/src-tauri/.cargo/config.toml` lets the TLS library use its prebuilt objects.

**If VS Code can't find a tool** (`command not found` for `cargo`, `dotnet` or `docker`): it was opened before the tool was installed. Quit VS Code completely (Cmd+Q on macOS) and reopen it.

## Running the apps

**Desktop app:** it syncs with the server stack, so start the stack first, after its one-time setup (certificates, including `mkcert -install`, and the hosts file; see [infra/compose/README.md](infra/compose/README.md#one-time-setup)). The app checks HTTPS against the certificates your operating system trusts, as a browser does, so `mkcert -install` matters here.

```
cd src/Ehr.Desktop/src-tauri
cargo tauri dev
```

The first build takes several minutes because it downloads the pinned Rust and compiles SQLCipher and OpenSSL; later builds are quick. The window shows the quick text list and whether it's online. It tries https://ehr.example.com:8443 (the on-prem app) first, then https://ehr-remote.example.com:9443 (the cloud app); to use others, set `EHR_SYNC_SERVERS` to a comma-separated list. Without a server it still works, and says why it's offline.

At first launch it asks you to sign in, with the email, password and authenticator code of a clinician account. The account must have finished its setup in the web app first (an admin creates it at **Staff**; its setup link sets the password, and its first sign-in there enrolls the authenticator app). Until you sign in, and after you sign out, the list can be read but not changed.

The local database is `ehr.db` in the app's data folder: `~/Library/Application Support/com.example.ehr` on macOS, `%APPDATA%\com.example.ehr` on Windows. Its key, and the session from the last sign-in, are in the login Keychain or Credential Manager. On macOS, after a rebuild the Keychain may ask whether the app can use them, since to macOS a rebuilt app is a different program: choose **Always Allow** each time. If the key is deleted, the app starts its copy again from the server and says so; changes it hadn't sent are lost. On Linux, which is for development only, the key, the session and the database live in memory, so each launch starts empty, asks you to sign in, and fills from the server.

**Web app:** it uses the server stack's primary database, so start the stack first (see [Local server stack](#local-server-stack)). Once, tell the app how to connect, with the `EHR_APP_PASSWORD` from `infra/compose/.env`. This stores it in your user profile, outside the repository:

```
dotnet user-secrets set ConnectionStrings:Ehr "Host=127.0.0.1;Port=15432;Database=ehr;Username=ehr_app;Password=<EHR_APP_PASSWORD>" --project src/Ehr.Web
```

Then run it:

```
dotnet run --project src/Ehr.Web
```

and open http://localhost:5080. It avoids port 5000, which macOS's AirPlay Receiver uses. It runs as the on-prem app. The first build needs internet access to download htmx and Alpine.js.

To sign in, the app needs an admin account. Give it the settings the stack gets from `.env`, once, the same way:

```
dotnet user-secrets set Ehr:Bootstrap:AdminEmail admin@example.com --project src/Ehr.Web
dotnet user-secrets set Ehr:Bootstrap:AdminPassword <a password of 12 or more characters> --project src/Ehr.Web
```

The account is created at the next start if there are no staff accounts yet. The first sign-in asks for a new password and an authenticator app. Without a mail server configured (`Ehr:Smtp:Host`), the portal's emails are written to the console.

**In VS Code:** open the repository folder, then use Terminal → Run Task:

| Task | What it does |
|---|---|
| Run desktop app | `cargo tauri dev` |
| Run web app | `dotnet run --project src/Ehr.Web` |
| Server stack: start | Builds and starts the local server stack |
| Server stack: stop | Stops it and keeps its data |
| Server stack: reset | Stops it and deletes all its data |

**Tests:** `dotnet test` runs every test project. Docker must be running: the security and web tests start a real PostgreSQL 18 in a container. For the desktop app, run `cargo test` from `src/Ehr.Desktop/src-tauri`; it needs no Docker. For the dictation model script, run `uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests`; it needs no network.

## Local server stack

`infra/compose` runs the whole server architecture on one machine with Docker Compose: the on-prem primary and app, the cloud replica and app, the isolated Barman backup host, and a simulated WAN with latency between them. It also covers the failure drills and a point-in-time restore. Setup (certificates and hosts-file entries) and every command are in [infra/compose/README.md](infra/compose/README.md).

## Dictation models

Only dictation work needs these; everything else runs without them. Both run on the on-prem server only, with no cloud AI.

| Model | Job | Source | Size | License |
|---|---|---|---|---|
| K2 Horizon 7B, Q4_K_M GGUF | Turns a transcript into proposed note fields, in `llama-server` | [IFM/K2-Horizon-7B-GGUF](https://huggingface.co/IFM/K2-Horizon-7B-GGUF) (llama.cpp calls it `IFM/K2-Horizon-7B-GGUF:Q4_K_M`) | About 5.6 GB | Apache 2.0 |
| MedASR | Speech to text, in a Python sidecar | [google/medasr](https://huggingface.co/google/medasr) | Small (105M parameters) | Health AI Developer Foundations terms; gated |

Once: create a Hugging Face account, accept the MedASR terms on its page, create a read-only access token, and sign in with `uvx --from huggingface_hub hf auth login`. The token stays in your user profile, never in the repository. Then, from the repository folder:

```
uv run src/Ehr.Dictation/setup_models.py --dry-run   # what it will download, and how big
uv run src/Ehr.Dictation/setup_models.py             # both models
```

- K2 Horizon goes in `infra/compose/models/`, and MedASR in `infra/compose/models/medasr/`. The folder is gitignored, and the script refuses one that Git would track: weights are never committed.
- Every file is checked against the SHA-256 Hugging Face publishes. A second run downloads nothing unless a file is missing or different, and an interrupted run can be restarted.
- `infra/compose/models/models-manifest.json` records the repository, commit, file and SHA-256 of what was installed, for the provenance each dictated note stores. `--verify` re-checks the files against it, offline.
- If you already have the K2 Horizon file (for example from `llama-server -hf`), pass it with `--from-file <path>`. It's used only if it's byte-for-byte the published file.
- The K2 Horizon license and model card are saved in `infra/compose/models/notices/`. Apache 2.0 asks that they go along with the file.

To run K2 Horizon in the local stack, set `LLAMA_MODEL_FILE` (the script prints the value) and `LLAMA_API_KEY` in `infra/compose/.env`, then start the `ai` profile (see [infra/compose/README.md](infra/compose/README.md#ai-profile)). K2 Horizon "thinks" at full effort by default, which is slow; the `llama` service sets its reasoning effort to low.

K2 Horizon "7B" stores about 9 billion parameters. With an 8K context it needs about 6 to 7 GB of memory, so a computer with 8 GB of RAM struggles. MedASR is small enough for any machine's CPU.

## Database changes

The schema comes from EF Core migrations in `db/migrations`; roles, grants, row-level security and triggers are hand-written SQL in `db/policies`. The migration job (`infra/migrate`) applies both to the primary, as `ehr_owner`, every time the server stack starts; the replica gets them by replication. CI's stack job runs the same job.

Run `dotnet tool restore` for the pinned `dotnet-ef`: once, and again whenever `.config/dotnet-tools.json` changes. Then, from the repository folder:

1. Change the model in `src/Ehr.Data`.
2. Add a migration: `dotnet ef migrations add <Name> --project db/migrations`
3. Regenerate the SQL that reviewers read: `dotnet ef migrations script --project db/migrations --output db/migrations/schema.sql`
4. For a new table, add its grants to `db/policies/10-grants.sql` and the same privileges to `ExpectedPrivileges` in `tests/Ehr.Security.Tests/RuntimeRoleTests.cs`, and decide its protection in `ExpectedProtection` in `ProtectionTests.cs`. A PHI table also gets its policies in `30-rls.sql` and the write-audit trigger in `40-audit.sql`.
5. Restart the server stack to apply it, and run `dotnet test`.

Tests fail if the model has changes no migration covers, if `schema.sql` is out of date, or if a table has no grant or protection decision. To undo a migration you haven't pushed, delete its two files and run `git restore db/migrations/EhrDbContextModelSnapshot.cs`. (`dotnet ef migrations remove` expects a database at the default port, which the local stack doesn't use.)

## Continuous integration

`.github/workflows/ci.yml` runs on every push to any branch, and on every pull request into `main`.

| Job | What it checks |
|---|---|
| Web app | Restores packages (a high or critical security advisory fails the run), builds in Release, then runs `dotnet format --verify-no-changes` and `dotnet test` |
| Server stack | Starts the local server stack (`infra/compose`), including the migration job, then checks that the clinic's home sends an anonymous browser to sign in, the sign-in pages load over HTTPS on all three sites, and the sync API refuses an anonymous request (the on-prem app reads the primary; the cloud app reads the replica), both databases accept connections, the replica and Barman are streaming, and `barman check` passes |
| Desktop | `cargo fmt --check`, `cargo clippy` (warnings count as errors) and `cargo build`, on macOS (Apple Silicon) and Windows (x64) |
| Dictation scripts | The tests for `src/Ehr.Dictation/setup_models.py`, against a fake Hugging Face Hub |
| `ci` | Passes only when every job above passes. It's the one check `main` requires. |
| Publish image | On `main` only, after `ci` passes: pushes `ghcr.io/tarnan3751/ehr-web:<commit>` for `linux/amd64` and `linux/arm64` |

The Windows desktop build is the slowest job: about 25 minutes when nothing is cached. Later runs reuse compiled dependencies and are faster.

To add a check, add a job to the workflow and to the `ci` job's `needs` list. Branch protection doesn't need to change.

### How changes reach `main`

`main` is protected by a ruleset:

1. Work on a branch and open a pull request into `main`.
2. `ci` must pass on the pull request.
3. Someone other than the author must approve it. Pushing new commits dismisses earlier approvals.
4. The branch must be up to date with `main`. If `main` has moved on, click **Update branch** on the pull request (or merge `main` into your branch and push). CI runs again and must pass before the merge.

Nobody can force-push to `main` or delete it.

Before you push, fix formatting and run the tests from the repository folder:

```
dotnet format
dotnet test
```

If you changed the desktop app, also run `cargo fmt` and `cargo test` from `src/Ehr.Desktop/src-tauri`.

### Published images

Each commit on `main` that passes `ci` publishes `ghcr.io/tarnan3751/ehr-web:<full commit ID>`, for `linux/amd64` and `linux/arm64`. The package is public, so pulling it needs no login:

```
docker pull ghcr.io/tarnan3751/ehr-web:<full commit ID>
```

There is no `latest` tag: a deployment names the exact commit it runs.

### Repository security settings

- **Secret scanning with push protection.** GitHub blocks a push that contains a recognizable token or key. It can't recognize a plain password, so secrets stay in `.env` files, which are gitignored.
- **Dependabot alerts** for known vulnerabilities in the .NET, Rust and Python dependencies. Separately, Dependabot opens a weekly pull request that updates the GitHub Actions the workflow uses.
- **Workflows from forks** wait for approval before they run.

## Where dependencies are declared

| Area | File |
|---|---|
| .NET package versions | `Directory.Packages.props` |
| .NET tools (`dotnet-ef`) | `.config/dotnet-tools.json`; run `dotnet tool restore` once |
| htmx, Alpine.js (CSP build) | `src/Ehr.Design/libman.json`, restored into `wwwroot/lib` at build and served from our own origin |
| Desktop | `src/Ehr.Desktop/src-tauri/Cargo.toml` |
| MedASR sidecar | `src/Ehr.Dictation/sidecar/pyproject.toml` |
| Dictation models | `src/Ehr.Dictation/setup_models.py` (repositories and quantization); what each machine installed is in `infra/compose/models/models-manifest.json` |
| llama.cpp build | `infra/compose/compose.yaml` and `infra/compose/compose.nvidia.yaml` (container images); this README for the native builds |
| Dictation eval | `eval/pyproject.toml` |
| Container images for the local stack | `infra/compose/compose.yaml`, `infra/barman/Dockerfile`, `src/Ehr.Web/Dockerfile` |
| GitHub Actions used by CI | `.github/workflows/ci.yml`, each pinned to a commit. Dependabot (`.github/dependabot.yml`) proposes updates weekly. |

## Dictation server hardware

K2 Horizon 7B runs in llama.cpp's `llama-server`, pinned to build **b11669** on every machine. Support for K2 Horizon arrived in b11454; older builds fail with `unknown model architecture: 'k2-horizon'`. In the local stack it's the opt-in `ai` profile. MedASR will run in Docker on CPU, since the model is small; its sidecar isn't written yet.

| Server machine | How `llama-server` runs |
|---|---|
| Windows, NVIDIA GPU | In Docker: the `ai` profile plus `infra/compose/compose.nvidia.yaml`, which switches to `server-cuda-b11669`. Docker Desktop passes NVIDIA GPUs through WSL2; keep the NVIDIA driver current. The model file alone is about 5.6 GB, so plan on 8 GB of GPU memory. |
| Windows, AMD GPU | Natively: the b11669 Windows Vulkan build from the llama.cpp GitHub release. Docker Desktop can't use AMD GPUs. |
| Apple Silicon Mac | Natively: the b11669 macOS arm64 build, which uses Metal. Or Podman with krunkit running `server-vulkan-b11669`, which keeps it in a container at roughly 75–80% of native speed. Docker Desktop can't use the Mac's GPU. |
| No usable GPU | In Docker: the `ai` profile as is (`server-b11669`), on CPU. |

- A native `llama-server` sits outside the Compose network, so nothing enforces "no internet egress" for it. Start it with the local model file, never with `-hf` or another flag that downloads one, and keep the same safeguards as the container: listen on `127.0.0.1` only, require an API key, turn off the web UI (`--no-webui`), set the reasoning effort to low, and never use `--tools` or `--log-prompts-dir` (it would write what patients say to disk). For example:

  ```
  llama-server -m infra/compose/models/<file> --host 127.0.0.1 --port 8080 --api-key <key> --no-webui -c 8192 -np 1 --chat-template-kwargs '{"reasoning_effort":"low"}'
  ```
- Record the dictation eval baseline on the server machine itself. CUDA, Vulkan, Metal and CPU builds can produce slightly different output, even at temperature 0.

## Runtime dependencies outside the package managers

For local development, PostgreSQL 18, Barman and the WAN simulation (Toxiproxy 2.12) come as containers in the local server stack, so there's nothing to install. The real deployment also needs:

- PostgreSQL 18 (primary and hot standby)
- Barman, in streaming mode
- Tailscale
- llama.cpp `llama-server`, build b11669 (see above)
- ffmpeg
- Model weights, downloaded by `src/Ehr.Dictation/setup_models.py` and never committed (see [Dictation models](#dictation-models)):
  - MedASR 1.0. The weights are gated: accept the Health AI Developer Foundations terms on Hugging Face first.
  - K2 Horizon 7B, Q4_K_M GGUF (about 5.6 GB), under Apache 2.0. Ship its license and notices with the file.
