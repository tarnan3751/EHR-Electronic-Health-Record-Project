# Development

The conventions every change follows are in [AGENTS.md](../AGENTS.md). The local server stack is in [infra/compose/README.md](../infra/compose/README.md).

## Repository layout

```
src/
  Ehr.Web/          ASP.NET Core Razor Pages host: Areas/Clinical, Areas/Portal, Api/Sync, Api/Dictation, Api/Fhir, Api/Lab, Fhir (the FHIR mapping)
  Ehr.Domain/       services, authorization policies, PHI reader, FHIR-shaped models
  Ehr.Data/         EF Core + Dapper on Npgsql: read and write contexts, RLS interceptor
  Ehr.Design/       Razor class library: the SwiftChart tokens, shared CSS, fonts and logos (wwwroot), shared partials, htmx
  Ehr.Desktop/      Tauri 2 app: src-tauri (Rust shell, SQLCipher, sync) + frontend (with its copy of the shared CSS, fonts and logos)
  Ehr.Lab.Simulator/ the simulated lab: asks the on-prem app for lab orders and files synthetic results, over HTTP only
  Ehr.Dictation/    setup_models.py (downloads the models) and its tests; sidecar (Python: MedASR, K2 Horizon, the checks), schema (note schema and prompt), contract (the sidecar's answers, pinned), llama-server config
tests/
  Ehr.Security.Tests/      RLS, role, and audit gates against real PostgreSQL 18
  Ehr.Architecture.Tests/  layering, and PHI reads only through the PHI reader
  Ehr.Web.Tests/           the web app, run in memory as each server, against real PostgreSQL 18
  Ehr.Data.Tests/          the data layer and migrations
  Ehr.Testing/             shared by the test projects and the benchmark: a migrated PostgreSQL 18 in a container
db/
  migrations/       EF Core migrations (the Ehr.Migrations project) and the SQL they produce, schema.sql
  policies/         RLS, triggers, roles, grants (hand-written, human-reviewed)
  reference/        the standard codes chart entries are picked from, as SQL: generated, but for the hand-written files docs/expansion/reference-data.md names
docs/               each use case's guide, and brand (the SwiftChart brand guide and tokens.json, the source of truth for the UI's styling)
tools/              brand/sync.cs: writes the tokens' CSS and the desktop app's copy of the shared CSS, fonts and logos
                    reference/import.cs: writes db/reference from pinned releases of the standard code lists
                    reference/import-catalogs.cs: writes the rest of the reference data from pinned source files
eval/               dictation test set, scoring and results
bench/              the synthetic clinic and its seed (Ehr.Seed), and the render-time benchmark (Ehr.Bench)
infra/              compose (local server stack), migrate (migration job), barman (backup image), monitoring (what Prometheus watches, and the alerts), tailscale (access rules), deploy (the deployment guide and its site check)
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

**Desktop app:** it syncs with the server stack, so start the stack first, after its one-time setup (certificates, including `mkcert -install`, and the hosts file; see [infra/compose/README.md](../infra/compose/README.md#one-time-setup)). The app checks HTTPS against the certificates your operating system trusts, as a browser does, so `mkcert -install` matters here.

```
cd src/Ehr.Desktop/src-tauri
cargo tauri dev
```

The first build takes several minutes because it downloads the pinned Rust and compiles SQLCipher and OpenSSL; later builds are quick. To build and install SwiftChart as a clinician would, unsigned, see [docs/desktop-install.md](desktop-install.md). The window shows the quick text list and whether it's online. It tries https://ehr.example.com:8443 (the on-prem app) first, then https://ehr-remote.example.com:9443 (the cloud app), and the sign-in form names them. A build's servers are fixed when it's compiled: set `EHR_BUILD_SERVERS` to a comma-separated list of HTTPS addresses for `cargo tauri build` (or `cargo build --release`) to make a release for other servers. A release build reads nothing else, so it can't be started pointing at another server. In a development build (`cargo tauri dev`), `EHR_SYNC_SERVERS` replaces them for that run. A sign-in is tied to the servers it was made with, so starting with others signs the app out, keeping what it holds; a clinic that moves its address ships a build for the new one, and each clinician signs in again. Without a server it still works, and says why it's offline.

At first launch it asks you to sign in, with the email, password and authenticator code of a clinician account. The account must have finished its setup in the web app first (an admin creates it at **Staff**; its setup link sets the password, and its first sign-in there enrolls the authenticator app), or come from the stack's example accounts (see "Test accounts" in [infra/compose/README.md](../infra/compose/README.md#test-accounts)). Until you sign in, and after you sign out, the list can be read but not changed.

The local database is `ehr.db` in the app's data folder, named by its identifier, `io.github.tarnan3751.swiftchart` (`tauri.conf.json`): `~/Library/Application Support/io.github.tarnan3751.swiftchart` on macOS, `%APPDATA%\io.github.tarnan3751.swiftchart` on Windows. Its key, and the session from the last sign-in, are in the login Keychain or Credential Manager, under the same name. Builds from before the app was named SwiftChart (the plan's step 20) kept theirs under `com.example.ehr`: a SwiftChart build starts empty beside them, and [docs/desktop-install.md](desktop-install.md) says how to send what an old copy holds and then remove it. On macOS, after a rebuild the Keychain may ask whether the app can use them, since to macOS a rebuilt app is a different program: choose **Always Allow** each time. If the key is missing or can't open the database, the app reports an error and leaves the encrypted file and unsent changes in place. It needs the original key to open that file. On Linux, which is for development only, the key, the session and the database live in memory, so each launch starts empty, asks you to sign in, and fills from the server.

**Web app:** it uses the server stack's primary database, so start the stack first (see [infra/compose/README.md](../infra/compose/README.md)). Once, tell the app how to connect, with the `EHR_APP_PASSWORD` from `infra/compose/.env`. This stores it in your user profile, outside the repository:

```
dotnet user-secrets set ConnectionStrings:Ehr "Host=127.0.0.1;Port=15432;Database=ehr;Username=ehr_app;Password=<EHR_APP_PASSWORD>" --project src/Ehr.Web
```

Then run it:

```
dotnet run --project src/Ehr.Web
```

and open http://localhost:5080. It avoids port 5000, which macOS's AirPlay Receiver uses. It runs as the on-prem app. The first build needs internet access to download htmx.

To sign in, the app needs an admin account. Give it the settings the stack gets from `.env`, once, the same way:

```
dotnet user-secrets set Ehr:Bootstrap:AdminEmail admin@example.com --project src/Ehr.Web
dotnet user-secrets set Ehr:Bootstrap:AdminPassword <a password of 12 or more characters> --project src/Ehr.Web
```

The account is created at the next start if there are no staff accounts yet. The first sign-in asks for a new password and an authenticator app. Without a mail server configured (`Ehr:Smtp:Host`), the portal's emails are written to the console, and without a certificate in `Ehr:DataProtection:Certificate` and `Ehr:DataProtection:CertificateKey` (PEM files; the stack passes its own), the keys that protect cookies are stored in the clear, which the log says at startup.

Outside Development the app needs more settings: [configuration.md](configuration.md) lists them.

**In VS Code:** open the repository folder, then use Terminal → Run Task:

| Task | What it does |
|---|---|
| Run desktop app | `cargo tauri dev` |
| Run web app | `dotnet run --project src/Ehr.Web` |
| Server stack: start | Builds and starts the local server stack |
| Server stack: stop | Stops it and keeps its data |
| Server stack: reset | Stops it and deletes all its data |

## Tests

`dotnet test` runs every test project. Build first, then run `pwsh tests/Ehr.Web.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium` for the real-browser clinical workflow tests (also installed in CI); alternatively set `PLAYWRIGHT_CHROMIUM_EXECUTABLE` to a compatible local Chromium. Docker must be running: the security and web tests start a real PostgreSQL 18 in a container. For the desktop app, run `cargo test` from `src/Ehr.Desktop/src-tauri`; it needs no Docker. For the dictation model script, run `uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests`; it needs no network. For the dictation sidecar and the eval, with ffmpeg installed, run `uv run --project src/Ehr.Dictation/sidecar pytest src/Ehr.Dictation/sidecar/tests` and `uv run --project eval pytest eval/tests`; they use stand-ins for the models.

## Database changes

The schema comes from EF Core migrations in `db/migrations`; roles, grants, row-level security and triggers are hand-written SQL in `db/policies`. Reference data, the standard codes chart entries are picked from, is generated SQL in `db/reference` (`docs/expansion/reference-data.md`). The migration job (`infra/migrate`) applies all three to the primary, as `ehr_owner`, every time the server stack starts, and then stores the hash of the lab's key from `EHR_LAB_KEY`, or none when it isn't set (see [Lab interface](apis.md#lab-interface)); the replica gets them by replication. CI's stack job runs the same job.

Run `dotnet tool restore` for the pinned `dotnet-ef`: once, and again whenever `.config/dotnet-tools.json` changes. Then, from the repository folder:

1. Change the model in `src/Ehr.Data`.
2. Add a migration: `dotnet ef migrations add <Name> --project db/migrations`
3. Regenerate the SQL that reviewers read: `dotnet ef migrations script --project db/migrations --output db/migrations/schema.sql`
4. For a new table, add its grants to `db/policies/10-grants.sql` and the same privileges to `ExpectedPrivileges` in `tests/Ehr.Security.Tests/RuntimeRoleTests.cs`, and decide its protection in `ExpectedProtection` in `ProtectionTests.cs`. A PHI table also gets its policies in `30-rls.sql` and the write-audit trigger in `40-audit.sql`.
5. Restart the server stack to apply it, and run `dotnet test`.

Tests fail if the model has changes no migration covers, if `schema.sql` is out of date, or if a table has no grant or protection decision. To undo a migration you haven't pushed, delete its two files and run `git restore db/migrations/EhrDbContextModelSnapshot.cs`. (`dotnet ef migrations remove` expects a database at the default port, which the local stack doesn't use.)

## Synthetic data and the benchmark

`bench/` holds a synthetic clinic: patients with allergies, medications, diagnoses, visits and test results, the same every time, and made up: ordinary names drawn from the seed's own lists, and every MRN starting `SYN`. It is clinically coherent and coded from the reference data: a patient's conditions bring the medications that treat them and the results that follow them, so someone with type 2 diabetes is on metformin and has raised hemoglobin A1c results.

To fill the local stack's primary with 50,000 of them, start the stack, then, from the repository folder, point `EHR_SEED_CONNECTION` at the primary as `ehr_owner`, with the `EHR_OWNER_PASSWORD` from `infra/compose/.env`, and run the seed:

```
export EHR_SEED_CONNECTION="Host=127.0.0.1;Port=15432;Database=ehr;Username=ehr_owner;Password=<EHR_OWNER_PASSWORD>;SSL Mode=VerifyFull;Root Certificate=infra/compose/certs/rootCA.pem"
dotnet run -c Release --project bench/Ehr.Bench -- seed
```

In PowerShell, set it with `$env:EHR_SEED_CONNECTION = "..."` instead. `--patients <n>` loads another number. The seed fills only a clinic with no patients; otherwise it refuses and changes nothing, and to start again you reset the stack (`docker compose --profile "*" down -v`). It runs as `ehr_owner` because nothing else may write these tables, and loads with binary COPY, which PostgreSQL refuses under forced row-level security: so, in one transaction, it lifts that and turns off the write-audit trigger on each table it fills (and the history trigger on allergies, medications and diagnoses), loads them, and turns them back on before it commits (`bench/Ehr.Seed/Seeder.cs`). No other session ever sees the tables unprotected, a failure leaves nothing behind, and the seeded rows have no write-audit events.

The benchmark measures the server render time of the search and chart pages in a clinic of 50,000 patients, against AGENTS.md's budget of a p99 under 20 ms. It needs Docker, and from the repository folder:

```
dotnet run -c Release --project bench/Ehr.Bench
```

starts its own PostgreSQL 18, seeds it, runs the on-prem app in memory, and prints the p50, p95, p99 and slowest time of each kind of request: searches by name, date of birth and MRN, results as the clinician types, the portal's results page as a patient, the clinician's panel of 100 patients, charts, the order form, a patient's discontinued orders, the forms that change and discontinue an order, the chart with its sensitive items shown, the form that adds a diagnosis, the code search as a clinician types, the history of the chart's lists, and the FHIR API's reads (a patient, their results and vital signs, their diagnoses with sensitive ones asked for, and an observation by ID). Then it makes the same requests off-site, its rows' names starting "Off-site": to the cloud app in memory, reading as `ehr_read` with its link to the on-prem app down, as during an on-site outage (UC 2). The cloud app records those reads in its audit queue, flushed to disk before the page is sent, rather than in the database, so in those rows the audit insert is 0 and the queue's append counts as the commit. Last, on-site again, it measures the lab's door with the lab's key: handing over ten newly signed lab orders, and filing one order's results (not with `--stack`, whose lab key is in the stack's `.env` alone). Each page commits the audit events of its reads, and its PostgreSQL has `fsync`, `synchronous_commit` and `full_page_writes` on, as the stack's primary does (the tests' databases have them off, for speed), so every time includes the wait for those events to reach the disk. Beside it, it prints the p50 and p99 excluding the commit: each request's time less its own commit, with the audit events' insert still in it. It prints the three settings as it read them, and when any is off the table says "durability off" and `--assert` refuses to run. `--assert` exits with 1, naming each kind of request that failed, when a p99 excluding the commit is 20 ms or more; it doesn't check the full time, whose 20 ms target is checked on the clinic server, because CI runners' shared disks make commit times unsteady, but with or without `--assert` it marks a kind of request whose full-time p99 is 20 ms or more with a warning, in its row and after the table, which never fails the run. A kind of request whose 1,100 requests take over a minute in all stops the run with an error, since at 20 ms each they take 22 seconds. `--patients <n>` seeds another number. `--results <file>` also writes each row's percentiles as JSON, which the performance test cases in `tests/Ehr.Web.Tests/TestCases` check against their own requirements' limits (they are skipped in the normal test run; `PerformanceResults.cs` there says how to run them). With `--stack` it measures on-site only, against the stack's primary instead, seeded already with as many patients, with the web app's own settings from "Running the apps"; it refuses to start, before the app does, without the Data Protection certificate among them, and once started when the database's statistics don't show a clinic of that size. It never writes a Data Protection key: its own cookies and form tokens use keys kept in memory, and it creates none in the key ring the stack's apps share. Each run creates a clinician of its own, an administrator (the plan's step 18), and two portal accounts, one for a seeded patient with results and one for the seeded patient whose record has the most entries, each with a password nobody keeps, and locks all four at the end. A run against the stack leaves its writes there: that clinician, that administrator and those patient accounts, 100 panel assignments with their write-audit events, and an audit event for every patient each page listed, about 195,000 at 50,000 patients, which nothing deletes.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to any branch, and on every pull request into `main`.

| Job | What it checks |
|---|---|
| Web app | Restores packages (a high or critical security advisory fails the run), builds in Release, installs Chromium for the browser tests, then runs `dotnet format --verify-no-changes`, the brand check (`dotnet run tools/brand/sync.cs -- --check`: the tokens' CSS and the desktop app's copy of the shared CSS, fonts and logos are up to date), the Tailscale policy file's syntax check (`infra/tailscale/check.cs`, which proves nothing about a real tailnet) and `dotnet test` |
| Server stack | Checks the monitoring profile's configuration and holds each alert rule to its threshold (`promtool test rules`), then starts the local server stack (`infra/compose`) with that profile, including the migration job, then checks that the clinic's home sends an anonymous browser to sign in, the sign-in pages load over HTTPS on all three sites, and the sync API refuses an anonymous request (the on-prem app reads the primary; the cloud app reads the replica), the deployment's site check passes against the stack (`infra/deploy/check-sites.cs`), the lab's door hands over orders on the on-prem site with the lab's key and isn't on the cloud site, and the lab service runs, both databases accept connections over TLS only, the replica and Barman are streaming, both databases hold the apps' roles' one-minute limit on a transaction left idle, and `barman check` passes; then a point-in-time restore from those backups (`infra/compose/restore-drill.cs`: a row written before the chosen time is restored and one written after isn't, and a few tables come back as they were), then that Barman's policy is the 6-day recovery window, and, with Barman briefly set to take a base backup at every pass of its loop and keep two (`infra/compose/compose.backup-drill.yaml`), that the oldest backups are deleted with the WAL before them; and last, that stopping the replica emails the administrators an alert, through Alertmanager to the mail catcher, within 5 minutes |
| Desktop | `cargo fmt --check`, `cargo clippy` (warnings count as errors), `cargo build` and the tests, then the unsigned installers (`cargo tauri build`: a `.dmg` on macOS, an NSIS setup on Windows), on macOS (Apple Silicon) and Windows (x64). A run started by hand with **upload_installers** keeps them as the run's artifacts for 7 days, as unsigned test builds for a local stack; no other run does (see [docs/desktop-install.md](desktop-install.md)) |
| Dictation | The tests for `src/Ehr.Dictation/setup_models.py`, against a fake Hugging Face Hub, and for `measure_memory.py`'s parts that need no stack; the dictation sidecar's and the eval's tests, with the packages their `uv.lock` files pin and stand-ins for the models; and the eval gate, which checks that the committed results in `eval/results/latest.json` come from this commit's prompt, note schema, parameters, sidecar code, eval code and test set, hold exactly the recordings the scripts define, every one sorted, show no order or allergy change and no retracted value without a warning that the owner hasn't accepted in `eval/known-limitations.toml`, say what the dictation proposed, every measure of it as the stored recordings give it again, with no false or misheard proposal the owner hasn't accepted there and at least 14 right, and score no lower than the accepted baseline; and the same of the visits' results in `eval/results/ambient-latest.json`, which also hold exactly the recordings the visits define, every measure as the stored answers give it again, and a contention probe, and show no hedged or denied diagnosis offered as certain, no question quoted as a finding, no denied symptom offered as present, no value quoted from someone its field may not quote without a warning, nothing proposed for the chart and no patient's request listed as the clinician's instruction; and the same for the Spanish test set's results in `eval/results/latest-es.json` (the plan's step 17): its own prompt hash and test set, every recording and text sorted, no instruction shown, no mistranslation without a warning that the owner hasn't accepted, and values filed in the wrong field within English's rate on the same kind of text plus 2 percentage points (the English text run's committed in `eval/results/latest-en-text.json`); and that no wrong translation of the review's (`eval/wrong-translations.toml`) that reverses the meaning passes the Spanish checks without a warning unless the owner has accepted it; and that the trick sentences in `eval/trick-sentences.toml`, the set the gate records, weighed again by this commit's checks, give no wrong proposal but those the owner has accepted by name there, no more wrong proposals than the set records, and every control (see [Dictation](dictation.md)) |
| Dictation image | Builds the dictation sidecar's image on an x86-64 and an arm64 runner, and checks that it runs as a non-root user, has ffmpeg and the note schema, and refuses to start without its keys. Nothing is pushed. |
| Render-time benchmark | Runs the benchmark (`bench/Ehr.Bench`, see [Synthetic data and the benchmark](#synthetic-data-and-the-benchmark)) at 50,000 synthetic patients with `--assert`, and puts its times in the run's summary whether it passes or fails. It fails when a kind of request's p99 excluding the commit is 20 ms or more, on-site or off-site, and stops at once when one kind of request takes over a minute in all. The full time, commit included, is reported beside it, with a warning when its p99 is 20 ms or more, but not checked here: its 20 ms target is checked on the clinic server. |
| `ci` | Passes only when every job above passes. It's the one check `main` requires. |
| Publish image | On `main` only, after `ci` passes: pushes `ghcr.io/tarnan3751/ehr-web:<commit>` for `linux/amd64` and `linux/arm64` |

The Windows desktop build is the slowest job: about 25 minutes when nothing was cached, before it built the installer as well (the plan's step 20), which adds the Tauri CLI's install the first time and the installer's own build. Later runs reuse compiled dependencies and the CLI, and are faster.

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
| htmx | `src/Ehr.Design/libman.json`, restored into `wwwroot/lib` at build and served from our own origin |
| Fonts (Sora, IBM Plex Sans, IBM Plex Mono) and logos | Committed in `src/Ehr.Design/wwwroot/fonts` (WOFF2, with their SIL Open Font Licenses) and `wwwroot/img`, served from our own origin; the brand guide and tokens are in `docs/brand` |
| Desktop | `src/Ehr.Desktop/src-tauri/Cargo.toml` |
| Dictation sidecar | `src/Ehr.Dictation/sidecar/pyproject.toml`, locked in its `uv.lock` |
| Dictation models | `src/Ehr.Dictation/setup_models.py` (repositories and quantization, and the speaker model's pinned commit); what each machine installed is in `infra/compose/models/models-manifest.json` |
| llama.cpp build | `infra/compose/compose.yaml` and `infra/compose/compose.nvidia.yaml` (container images); [Dictation server hardware](dictation.md#dictation-server-hardware) for the native builds |
| Dictation eval | `eval/pyproject.toml`, locked in `eval/uv.lock`, where every package it shares with the sidecar has the sidecar's version |
| Container images for the local stack | `infra/compose/compose.yaml`, `infra/barman/Dockerfile`, `src/Ehr.Web/Dockerfile`, `src/Ehr.Dictation/sidecar/Dockerfile` |
| Barman | `infra/barman/Dockerfile` (`BARMAN_VERSION`, pinned: the Dockerfile says what to check before moving it) |
| GitHub Actions used by CI | `.github/workflows/ci.yml`, each pinned to a commit. Dependabot (`.github/dependabot.yml`) proposes updates weekly. |

## Runtime dependencies outside the package managers

For local development, PostgreSQL 18, Barman and the WAN simulation (Toxiproxy 2.12) come as containers in the local server stack, so there's nothing to install. The real deployment also needs:

- PostgreSQL 18 (primary and hot standby)
- Barman 3.20.1, in streaming mode (the version `infra/barman/Dockerfile` pins)
- Tailscale
- llama.cpp `llama-server`, build b11669 (see [Dictation server hardware](dictation.md#dictation-server-hardware))
- ffmpeg
- Model weights, downloaded by `src/Ehr.Dictation/setup_models.py` and never committed (see [Dictation models](dictation.md#dictation-models)):
  - MedASR 1.0. The weights are gated: accept the Health AI Developer Foundations terms on Hugging Face first.
  - K2 Horizon 7B, Q4_K_M GGUF (about 5.6 GB), under Apache 2.0. Ship its license and notices with the file.
  - Whisper large-v3-turbo (about 1.62 GB), under MIT, only where dictation in Spanish is offered.
