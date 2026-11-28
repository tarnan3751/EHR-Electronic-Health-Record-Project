# SwiftChart

SwiftChart is an electronic health record with a clinician web app, a patient portal and a desktop app for macOS and Windows. It is a course project. All data in it is synthetic, and it is not for clinical use.

One PostgreSQL 18 primary at the clinic owns every write. A cloud server serves off-site staff and the portal from a read-only replica, and dictation runs on models hosted at the clinic.

## Features

- **Clinicians** search the clinic's patients, keep a panel, and work from a chart with allergies, medications, diagnoses, vital signs, results, notes and appointments. They sign orders with allergy and interaction checks, write and sign SOAP notes, dictate into a draft they review field by field, and work through the inbox, messages and refill requests.
- **Patients** register with a code from the clinic, then see their results, appointments and visit summaries, message the care team, request refills, fill in pre-visit forms, download their record, see who viewed it, and share access with a family member or carer.
- **Administrators** manage staff accounts, read the audit log and review every break-the-glass access.
- **The desktop app** keeps quick text and offline charts in an encrypted local database, synced with the servers.

[docs/features.md](docs/features.md) lists what is built and what isn't. Each use case has its own document (see [Documentation](#documentation)).

## Quick start

You need Docker Desktop, the .NET 10 SDK and [mkcert](https://github.com/FiloSottile/mkcert). The full list for development is under [Setup](docs/development.md#setup).

1. One-time setup, from `infra/compose`. Copy the settings file and make the certificates:

   ```
   cp .env.example .env
   mkcert -install
   mkcert -cert-file certs/example.com.pem -key-file certs/example.com-key.pem example.com "*.example.com" onprem-db cloud-db wan localhost 127.0.0.1
   cp "$(mkcert -CAROOT)/rootCA.pem" certs/rootCA.pem
   ```

   On macOS, also run `chmod 644 certs/example.com-key.pem`. Then add this line to `/etc/hosts` (macOS) or `C:\Windows\System32\drivers\etc\hosts` (Windows, as administrator):

   ```
   127.0.0.1 ehr.example.com ehr-remote.example.com portal.example.com
   ```

2. Start the stack, from `infra/compose`. The first start takes a few minutes.

   ```
   docker compose up -d --build --wait
   ```

3. Fill it with the synthetic clinic, from the repository folder. Use the `EHR_OWNER_PASSWORD` from `infra/compose/.env`. In PowerShell, set the variable with `$env:EHR_SEED_CONNECTION = "..."`.

   ```
   export EHR_SEED_CONNECTION="Host=127.0.0.1;Port=15432;Database=ehr;Username=ehr_owner;Password=<EHR_OWNER_PASSWORD>;SSL Mode=VerifyFull;Root Certificate=infra/compose/certs/rootCA.pem"
   dotnet run -c Release --project bench/Ehr.Bench -- seed
   ```

4. Create the test accounts, then the demonstration data, from `infra/compose`. Run `setup` before anyone signs in as the admin by hand.

   ```
   dotnet run test-accounts.cs -- setup
   dotnet run demo-data.cs
   ```

| Address | What it is |
|---|---|
| https://ehr.example.com:8443 | Clinical app, on-site (the on-prem app) |
| https://ehr-remote.example.com:9443 | Clinical app, off-site (the cloud app, reading the replica) |
| https://portal.example.com:9443 | Patient portal |
| http://127.0.0.1:8025 | Mailpit, which catches the email the stack sends |

| Account | Email | Password |
|---|---|---|
| Admin | `admin@example.com` | `local-admin-test-1234` |
| Clinician | `clinician@example.com` | `local-clinician-test-1234` |
| Patient, MRN `SYN0000001` | `patient@example.com` | `local-patient-test-1234` |

Staff sign-ins also ask for an authenticator code. `dotnet run test-accounts.cs -- code`, from `infra/compose`, prints the current ones. Every patient's MRN starts with `SYN`.

To stop the stack and keep its data, run `docker compose --profile "*" down` from `infra/compose`. Add `-v` to delete all its data. After that, delete `test-accounts.json` and start again from step 3. [infra/compose/README.md](infra/compose/README.md) covers the rest: what runs, the demonstration data, failure drills, backups and restore, monitoring and the `ai` profile.

## What's in the repository

| Folder | Contents |
|---|---|
| `src/` | The web app (ASP.NET Core Razor Pages with htmx), its domain and data layers, the shared design library, the desktop app (Tauri 2), the dictation sidecar (Python) and the simulated lab |
| `tests/` | Security, architecture, data and web tests, run against a real PostgreSQL 18 in a container |
| `db/` | EF Core migrations, hand-written policies (row-level security, triggers, roles, grants) and reference data |
| `infra/` | The local server stack, the migration job, the backup image, monitoring, Tailscale rules and the deployment guide |
| `bench/` | The synthetic clinic's seed and the render-time benchmark |
| `eval/` | The dictation test set, its scoring and results |
| `tools/` | Scripts for the brand tokens and the reference data |
| `docs/` | Use cases, the brand guide, the expansion plan and the guides below |

The full layout is in [docs/development.md](docs/development.md#repository-layout).

## Development

From the repository folder, with Docker running:

```
dotnet build Ehr.slnx --configuration Release
dotnet test --solution Ehr.slnx --configuration Release --no-build
dotnet format Ehr.slnx
```

The tests start their own PostgreSQL 18. The browser tests need Chromium, installed after the build with `pwsh tests/Ehr.Web.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`. For the desktop app, run `cargo test` from `src/Ehr.Desktop/src-tauri`. The Python suites run from the repository folder:

```
uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests
uv run --project src/Ehr.Dictation/sidecar pytest src/Ehr.Dictation/sidecar/tests
uv run --project eval pytest eval/tests
```

After changing `docs/brand/tokens.json`, the shared stylesheet, a font or a logo, run `dotnet run tools/brand/sync.cs`.

[AGENTS.md](AGENTS.md) has the conventions every change follows. [docs/development.md](docs/development.md) has the rest:

- [Running the apps](docs/development.md#running-the-apps) outside the stack, and the VS Code tasks
- [Database changes](docs/development.md#database-changes): migrations, policies and grants
- [Synthetic data and the benchmark](docs/development.md#synthetic-data-and-the-benchmark)
- [Continuous integration](docs/development.md#continuous-integration) and how changes reach `main`

CI (`.github/workflows/ci.yml`) runs on every push. It builds and tests the web app, starts the local stack and rehearses a restore, builds the desktop app on macOS and Windows, runs the dictation tests and eval gate, and runs the render-time benchmark. `main` accepts only reviewed pull requests that pass it.

## Desktop app

The desktop app (`src/Ehr.Desktop`) is a Tauri 2 app. It keeps quick text, offline charts and unsent work in a SQLCipher database whose key is in the macOS Keychain, Windows Credential Manager or, on Linux, the Secret Service (GNOME Keyring or KDE Wallet). It syncs with the on-prem app, or with the cloud app when the clinic can't be reached, and makes every network call from Rust. With the stack running:

```
cd src/Ehr.Desktop/src-tauri
cargo tauri dev
```

[docs/desktop-install.md](docs/desktop-install.md) covers installers, unsigned builds and updates. [UC 7](docs/UC7.md) and [UC 8](docs/UC8.md) describe working offline and sync.

## Dictation

Dictation runs on the clinic's server only. MedASR transcribes English, Whisper large-v3-turbo transcribes Spanish (experimental), and K2 Horizon 7B in llama.cpp sorts the transcript into Subjective, Objective, Assessment and Plan. The sidecar checks every proposed value against the transcript, and nothing goes into a note until the clinician accepts it. Listening to a visit is off unless the clinic turns it on. In the local stack, dictation is the `ai` profile.

[docs/dictation.md](docs/dictation.md) covers the models, how the pipeline works, running the sidecar outside the stack, the eval and the [dictation server hardware](docs/dictation.md#dictation-server-hardware). [eval/README.md](eval/README.md) describes the test set.

## Deployment and operations

A deployment is two machines joined by Tailscale: a clinic server that owns every write, and a cloud server with the read-only replica, the Barman backups and monitoring. [infra/deploy/README.md](infra/deploy/README.md) is the guide. It has never been run against a server. The local stack rehearses failures, backups, point-in-time restore and alerts ([infra/compose/README.md](infra/compose/README.md)). [docs/configuration.md](docs/configuration.md) lists the settings the apps need outside Development.

## Security model

- Authorization is applied twice. ASP.NET Core policies attach to whole areas of the app, and PostgreSQL row-level security is enabled and forced on every table of patient data. A patient sees only their own record, or under a proxy grant the one record they act for. An administrator sees no patient data, and a query with nobody signed in returns nothing.
- Every read of patient data goes through one reader that records an audit event. Administrators review the log, which nothing at runtime can change.
- The database lets every clinician read every patient, by the team's decision. In the app, opening the chart of a patient outside one's panel means breaking the glass with a reason, and an administrator reviews each access.
- Staff and patients have separate accounts and sign-in pages. Every staff sign-in needs a password and a second factor: an authenticator code, or a passkey where the clinic allows them.

[AGENTS.md](AGENTS.md) states the rules. [UC 11](docs/UC11.md) covers sign-in, [UC 12](docs/UC12.md) and [UC 24](docs/UC24.md) cover breaking the glass and its review, and "Encryption" in [infra/compose/README.md](infra/compose/README.md#encryption) covers TLS and data at rest.

## Releases and container images

[Release v1.0.0](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/releases/tag/v1.0.0) has desktop installers for macOS, Windows and Linux, with their checksums in `SHA256SUMS.txt`:

| Platform | File |
|---|---|
| macOS, Apple Silicon | `SwiftChart_1.0.0_aarch64.dmg` |
| Windows, x64 | `SwiftChart_1.0.0_x64-setup.exe` |
| Linux, x86-64 | `SwiftChart_1.0.0_amd64.deb` or `SwiftChart_1.0.0_amd64.AppImage` |

The installers are unsigned test builds. They connect only to the local stack's addresses, https://ehr.example.com:8443 and https://ehr-remote.example.com:9443, so they need the stack from the quick start. [docs/desktop-install.md](docs/desktop-install.md) says how to open an unsigned build on a Mac and on Windows, and how to install on Linux. The Linux app is built and tested in CI. Nobody has run it in a desktop session yet, so its keyring prompts and recording are unverified.

The release also has four container images in the GitHub Container Registry:

| Image | What it is |
|---|---|
| `ghcr.io/tarnan3751/ehr-web` | The web app. The on-prem and cloud sites both run it |
| `ghcr.io/tarnan3751/ehr-dictation` | The dictation sidecar |
| `ghcr.io/tarnan3751/ehr-migrate` | The migration job |
| `ghcr.io/tarnan3751/ehr-lab` | The simulated lab |

Each is built for `linux/amd64` and `linux/arm64` and tagged `1.0.0` and `latest`, for example `docker pull ghcr.io/tarnan3751/ehr-web:1.0.0`. Each commit on `main` that passes CI also publishes all four, tagged with the full commit ID and `edge` ([Published images](docs/development.md#published-images)).

## Documentation

| Document | What it covers |
|---|---|
| [docs/features.md](docs/features.md) | What is built, and what isn't yet |
| [docs/development.md](docs/development.md) | Setup, running the apps, tests, database changes, the benchmark, CI |
| [docs/configuration.md](docs/configuration.md) | Settings |
| [docs/dictation.md](docs/dictation.md) | Dictation models, pipeline, eval and server hardware |
| [docs/apis.md](docs/apis.md) | The FHIR API and the lab interface |
| [docs/desktop-install.md](docs/desktop-install.md) | Installing and updating the desktop app |
| [infra/compose/README.md](infra/compose/README.md) | The local server stack |
| [infra/deploy/README.md](infra/deploy/README.md) | Deploying to a clinic server and a cloud server |
| [docs/brand/README.md](docs/brand/README.md) | The brand guide |
| [docs/expansion/PLAN.md](docs/expansion/PLAN.md) | The expansion plan and its decision log |
| [docs/expansion/reference-data.md](docs/expansion/reference-data.md) | The standard code lists and where they come from |
| [eval/README.md](eval/README.md) | The dictation test set |
| [AGENTS.md](AGENTS.md) | Conventions for people and coding agents |

Use cases:

| Number | Use case |
|---|---|
| [UC 1](docs/UC1.md) | Place an order |
| [UC 2](docs/UC2.md) | Read records during an on-site outage |
| [UC 4](docs/UC4.md) | View a patient's chart: editing its lists |
| [UC 6](docs/UC6.md) | View test results |
| [UC 7](docs/UC7.md) | Work offline in the desktop app |
| [UC 8](docs/UC8.md) | Sync the desktop app |
| [UC 9](docs/UC9.md) | Transcribe dictated audio |
| [UC 10](docs/UC10.md) | Structure and validate note fields |
| [UC 11](docs/UC11.md) | Sign in with multi-factor authentication |
| [UC 12](docs/UC12.md) | Open a chart through emergency access |
| [UC 13](docs/UC13.md) | Review AI-proposed note fields |
| [UC 14](docs/UC14.md) | Write and sign a clinical note |
| [UC 15](docs/UC15.md) | Work through the inbox |
| [UC 16](docs/UC16.md) | Schedule an appointment |
| [UC 17](docs/UC17.md) | Answer a patient's message |
| [UC 18](docs/UC18.md) | Give someone proxy access to a record |
| [UC 19](docs/UC19.md) | Read a patient's record through the FHIR API |
| [UC 20](docs/UC20.md) | Receive results from a lab |
| [UC 21](docs/UC21.md) | Answer a refill request |
| [UC 22](docs/UC22.md) | Answer a pre-visit form |
| [UC 23](docs/UC23.md) | Draft a note from a recorded visit |
| [UC 24](docs/UC24.md) | Review an emergency access |
