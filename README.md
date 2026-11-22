# EHR platform

An electronic health record with a web app (clinical and patient portal) and a Mac/Windows desktop app. It is designed to be secure first and as fast as the hardware allows. One on-prem PostgreSQL primary owns every write; everything else is a copy, a cache, or a proxy.

## Status

Early foundation, with the first EHR feature: finding a patient and opening their chart. The walking skeleton (one trivial feature, team quick text, through every layer) is done: the database foundation, the on-site page, the off-site host, the sync API, and the desktop app. Dictation's server side is built and measured on a test set (see [Dictation](#dictation)); dictation, persisted AI field review (UC 13), and manual note-writing/signing (UC 14) now form one clinical workflow.

**Working today**

- AI field review (UC 13): dictate from a patient chart or draft, compare source-highlighted suggestions, explicitly accept/edit/reject every field, and add only approved text to the existing clinical draft. Decisions and provenance persist under forced RLS and auditing; authoritative app/database checks block signing pending reviews. See [UC 13 implementation and assignment guide](docs/UC13.md).
- Clinical notes (UC 14): clinicians create and autosave Subjective, Objective, Assessment and Plan from a patient chart (notes and drafts written before the plan's step 14 keep their History, Assessment and Plan), reopen private drafts, and explicitly confirm a signed note whose text never changes (only its sensitivity can, with a reason: the plan's step 10b). Signing validates the saved version and server-held pending AI review, requires the primary, and records the signer, time and audit history. The editor preserves displayed work on failed requests. It extends the offline draft-sync model without changing its plain-text Body contract; see [UC 14 implementation and review guide](docs/UC14.md).
- Orders (UC 1): from a patient's chart, a clinician with the patient on their panel signs a lab, imaging, medication or referral order, picked from a small synthetic catalog. The chart lists active orders. The database sets the signer and the time, records the write, and refuses a second active order for the same item without a reason. A signed order is never edited: a clinician discontinues it with a reason, or changes it, which signs a new order and discontinues the old one as replaced, and the chart keeps the discontinued orders with who, when and why. See `docs/UC1.md`.

- The desktop app (Tauri) shows the quick text list, and works offline. Phrases are kept in an encrypted local database (SQLCipher) whose key is made on first launch and kept in the macOS Keychain or Windows Credential Manager. Adds and edits are saved there first, in an outbox; a background sync, every 10 seconds and straight after a change, sends them and brings back what changed, from the on-prem app when it can be reached and through the cloud app otherwise. A change the server refuses comes back with both versions and **Keep mine** and **Keep theirs**, as on the page. Syncing needs a clinician signed in, with email, password and the code from their authenticator app; the session is kept in the Keychain or Credential Manager beside the database key, every sync request carries it, and when the server ends it the app asks to sign in again. Signing out keeps the local copy. The list can be read offline and signed out; changes need someone signed in. The window shows whether it's online and how many changes are waiting. Every network call is made in Rust, not in the web view. Verified on Linux against the local server stack, including offline changes, conflicts and the off-site path; sign-in so far only against a stand-in server in its tests. CI builds it on macOS and Windows; it hasn't been run on a Windows PC yet.
- The web app (ASP.NET Core Razor Pages with htmx) serves the quick text page at `/quick-texts`: a shared list of phrases, each with a shortcut such as `.nad`. On-site, phrases can be added and edited; a save that started from an older version is refused and shows both versions, with **Keep mine** and **Keep theirs**. Off-site, the cloud app shows the list from the replica and forwards saves to the on-prem app; right after a save, that person's pages come from on-prem until the replica has the change. If the on-prem server can't be reached, or can't save because its database is down, off-site pages still load, every page says the system is read-only, a save says it wasn't saved, and signing out still works (see [UC 2](docs/UC2.md)). Tests run the page against a real PostgreSQL 18.
- Sign-in and authorization. Staff and patients have separate accounts, cookies and sign-in pages (`Areas/Clinical/Pages/Account`, `Areas/Portal/Pages/Account`). Every staff sign-in takes a password and a code from an authenticator app, wherever it comes from; a new account enrolls its app at its first sign-in, and sees recovery codes once. The on-prem app creates the first admin from `Ehr:Bootstrap` settings when there are no staff accounts; admins create the rest at **Staff**, each with a one-time setup link, and look after them there: a new setup link for a forgotten password, an authenticator reset for a lost phone, and disabling an account, which locks it out and ends its sessions until it is enabled again. Policies attach to whole areas (`Program.cs`): any staff account for the clinic's root (which sends a clinician to their patients and an admin to the staff page), clinicians for patients, admins for staff, patient accounts for the portal, clinicians for dictation (`/api/dictation`, with the staff cookie and the page's form token in a header, where a refusal is a 401 or 403 rather than a redirect, and an account disabled or locked out, or whose password changed, is refused on its next call), clinicians for the read-only FHIR API (`/fhir`, with the staff cookie, refused with a 401 or 403 rather than a redirect; see [FHIR API](#fhir-api)), and nothing anonymous but the sign-in pages and static files. Either sign-in, at its last step or when already signed in, goes on to the page it was sent from without that page's query, but for a patient search's criteria through the staff sign-in, so a link can't ask through it for a download or sensitive items (step 13b's review). The desktop app signs in at `/api/desktop/sign-in` with the same two factors and gets a session token, which every sync request carries; the token is a clinician's, lasts thirty days, and ends at sign-out or when the password changes (`ehr.desktop_sessions`). The signed-in account reaches the database as `app.user_id`, where row-level security is the second layer.
- The first PHI table, patients, with row-level security enabled and forced, a write-audit trigger, and the PHI reader in `Ehr.Domain` that every read goes through and that records each one (`db/policies`, `Phi/PhiReader.cs`). A clinician's home is their panel: the patients they have added to it from the chart (`ehr.patient_assignments`, PHI like the patients themselves). A search by name, date of birth and MRN, combined, covers the whole clinic: it marks the patients on the clinician's panel as theirs and the rest as restricted, since opening one of those asks for a reason, says what to correct when criteria can't be searched (such as a date that doesn't exist), and the chart links back to the same results. Results also come as the clinician types, up to 20 at a time, since each patient listed is a recorded read; Enter lists up to 50, and asks for more letters or a date of birth when more match. The name field has the focus on arrival, `/` reaches the search from any clinical page, and the arrow keys move through the results. When the database fails, the search says it couldn't be completed and the chart that it couldn't be loaded, rather than showing a blank page; a chart that doesn't exist says so too. The chart shows demographics, the allergies, medications and diagnoses the patient has now, and their five latest visits with the latest one's blood pressure and heart rate, each section saying when nothing is recorded (and "No known allergies" apart from no allergy information); these live in their own PHI tables, shaped on FHIR like patients (allergies, medications and diagnoses can be edited, as below; a visit is created by checking a patient in for an appointment), and opening the chart is one recorded read of the patient. From the chart a clinician puts the patient on the panel or takes them off, and issues a portal enrollment code. Opening the chart of a patient who is not on the clinician's panel first asks for a reason (emergency, or break-glass, access): the reason is recorded as a `break_glass` audit event for admins to review, and the chart stays open to that clinician for four hours (`ehr.break_glass_accesses`). The database still lets every clinician read every patient, as the team decided; the reason and its review are the control. A patient registers at `/portal/register` with that code, confirms their email (a link that expired can be sent again at `/portal/resend`, an address that already has an account gets the same page as any other while its owner is told by email, and a new code replaces a registration that was never confirmed, such as one with a mistyped address), signs in, and sees their own record and nobody else's, and their test results at `/portal/results`: released results only, newest first, each with its normal range and what it means in plain language (`ehr.observations`, `Results/ResultExplanation.cs`; see `docs/UC6.md`); a forgotten password is reset through a one-time link sent to their address (`/portal/forgot`). The portal takes the patient from the session, never from a request. Off-site, pages that read PHI come from the cloud app's replica like any other page. The replica takes no writes, so the cloud app records those reads, and the sign-outs it handles itself, in its own audit queue, a file on its disk that is flushed before the page is sent, and ships them to the on-prem app every couple of seconds with a key both apps share (`src/Ehr.Web/CloudAudit`, `Api/Audit`), where they arrive with source `cloud`. So charts and the portal stay readable off-site while the on-prem server is down, and nothing read there goes unrecorded. Every view and change of patient information, and every sign-in, is in `ehr.audit_events`, which nothing at runtime can change (the database itself marks an event as about a sensitive item, and nothing else). Admins review it at **Audit** (`/admin/audit`): the newest events first, narrowed by action, staff account, patient ID and dates, with emergency access flagged. A patient sees who viewed their own record in the portal (step 13c, below), through one function that answers only for the record the session is looking at. Admins have no access to patient data, so a patient appears there by ID; the values a write changed, which the trigger records, are the one place an admin sees any, and never those of a sensitive item.
- The sync API for the desktop app, at `/api/sync/quick-texts` on both servers (`src/Ehr.Web/Api/Sync`), for a desktop session token; the cloud app checks the token against its replica. A pull returns the phrases changed since a transaction-ID watermark, fed by a change log that a database trigger writes; a push applies batches of changes, each with an idempotency key, and returns applied, duplicate, conflict (with the saved phrase) or invalid for each.
- The local server stack runs the whole server architecture in Docker Compose: the PostgreSQL 18 primary, its streaming replica, Barman backups with point-in-time restore, and a simulated WAN. Every connection in it is TLS, verified against the setup's certificate authority, apart from the `ai` profile's calls to the models inside the on-prem server; the keys that protect cookies and form tokens are encrypted at rest with the sites' certificate; "Encryption" in [infra/compose/README.md](infra/compose/README.md#encryption) says what the host's disk has to provide for the rest. Verified on Linux, and on an Apple Silicon Mac for replication, isolation, backups and restore; Windows is not yet verified.
- The database foundation: the app's roles (`ehr_owner` for migrations, `ehr_app` and `ehr_read` for the apps), the first table (`quick_texts`), and a migration job that brings the primary up to date every time the server stack starts. Security tests check the roles against a real PostgreSQL 18. See [Database changes](#database-changes).
- CI (GitHub Actions) checks every push and pull request. `main` only accepts reviewed pull requests that pass it, and each commit on `main` publishes the web image. See [Continuous integration](#continuous-integration).
- The dictation models download with one script, `src/Ehr.Dictation/setup_models.py`, which checks every file against the hash Hugging Face publishes: K2 Horizon 7B for note structuring and MedASR for speech to text, and, only when asked, Whisper large-v3-turbo for dictation in Spanish. See [Dictation models](#dictation-models).
- Dictation, server side: MedASR transcribes as the clinician speaks, marking passages too quiet, too noisy or too uncertain to trust; K2 Horizon sorts the finished transcript into Subjective, Objective, Assessment and Plan (the plan's step 14); and every proposed value is checked against the transcript before anyone can see it, so one that isn't grounded in it, or that orders something or changes the allergy list, is never shown. What passes is pending until a clinician approves it, and nothing is stored. The on-prem app's dictation API, for clinicians, is what the coming screens will use; off-site, the cloud app forwards it. A test set of synthetic dictations measures it all, and CI checks the committed results against the prompt and checks in each commit. Measured on an Apple Silicon Mac; the accepted baseline is recorded on the server machine. See [Dictation](#dictation).
- Dependency manifests and toolchain versions are in place. The dictation sidecar's and the eval's Python packages are locked, each in its `uv.lock`; `setup_models.py` lists its own version ranges.

- Editable allergies, medications and diagnoses, with standard codes. From the chart, a clinician with the patient on their panel adds and changes entries, picking each from ICD-10-CM, RxNorm or FDA UNII codes as they type; nothing is deleted, an entry gets another status, and a page lists the history of every change. Mental-health, substance-use and some other diagnoses are sensitive by their code, and a clinician can mark any diagnosis or medication sensitive: sensitive items are left out of the chart until asked for, and asking is recorded. An admin's audit page shows that a sensitive item changed and who changed it, never its values. The allergy list says whether nothing is recorded, there are no known allergies, or what the allergies are. The codes are the full ICD-10-CM and RxNorm lists, loaded from pinned source files by a script, never at runtime, beside LOINC lab orders and results, FDA drug classes and DDInter's interactions, which later steps use. See `docs/UC4.md` and `docs/expansion/reference-data.md`.

- A staff inbox (`/inbox`): what needs each clinician's or admin's action, with a count in the app bar, each item linking to its patient, and "Mark done". It starts empty; later features put their items in it. See `docs/UC15.md`.

- Appointments (FR-17): clinicians book an appointment from a patient's chart, with any clinician, and reschedule it, cancel it with a reason, check the patient in (which starts their visit) or mark a no-show; `/schedule` shows any clinician's day; the database refuses a double booking. Patients see their upcoming and earlier appointments in the portal. There is no self-scheduling. See `docs/UC16.md`.

- Secure messages (FR-18): a patient writes to their care team from the portal, the message arrives in the inbox of each clinician who has them on their panel, and any of them replies from the conversation; clinicians can also start one from the chart. Every opening of a conversation is audited, the email about a reply says only that there is one, and the audit page never shows a message's text. See `docs/UC17.md` and `docs/UC6.md`.

- Proxy access (FR-19): a patient gives a family member or carer access to their record in the portal, or the clinic records it, with a scope and always an end date. The proxy signs in with their own account, which someone who isn't a patient creates from the emailed invitation, and chooses whose record to view; the database checks on every query that the access is theirs and in force, holds back sensitive items and messages unless the access includes them, and records everything under the proxy's account and the patient. See `docs/UC18.md` and `docs/UC6.md`.

- Results (the plan's step 7): a clinician files a lab order's results against it, the order becomes resulted, values outside their reference range are flagged and listed first, the clinician who signed the order finds them in their inbox, they or a colleague on the patient's care team acknowledges them, and the patient sees them in the portal. See `docs/UC1.md`.

- Ordering safety checks (the plan's step 8): a medication order is checked against the patient's coded allergies and drug classes, and against what they take for known interactions from DDInter, the ONC high-priority list and FDA labels. Serious warnings need a recorded reason to override. Decision support for this demonstration system, not a complete clinical reference. See `docs/UC1.md` and `docs/expansion/reference-data.md`.
- Vital signs and trends (the plan's step 9): blood pressure, heart rate, temperature, respiratory rate, oxygen saturation, weight and height are recorded as coded observations, with the body mass index worked out by the database; the chart lists the newest value of each, its vital signs page has the flowsheet, and vital signs and lab values have trend charts drawn by the server as SVG, with the reference range and a table of the numbers, for clinicians and in the portal (`docs/UC4.md`, `docs/UC6.md`).
- Addenda to signed notes (the plan's step 10): a signed note's text stays locked, and a clinician with the patient on their panel signs addenda that are shown after it with author and time. A note can be signed as sensitive and an addendum marked sensitive; both follow the rules for sensitive items. The desktop app's offline chart shows signed notes and their addenda, except sensitive ones (`docs/UC14.md`).
- Vital-sign corrections and ranges by age (the plan's step 9b): a wrong reading is marked entered in error with a reason, kept in the record and left out of every view unless asked for, and the correct value is a new reading; heart rate, respiratory rate and blood pressure are flagged against the range for the patient's age (`docs/UC4.md`, `docs/expansion/reference-data.md`).
- Vital-sign readings (the plan's step 9c): a sign has one standing value for a patient at a moment, whatever the visit, and a moment's visit is decided from the time it was taken, for all its values at once (a check-in ties the sets taken up to fifteen minutes before it to its visit, after Tahseen's answers); a blood pressure is one reading of a systolic and a diastolic, which the database keeps whole and marks entered in error whole, and which every page shows as one value, "126/80" (`docs/UC4.md`).
- Children's vital-sign ranges (the plan's step 9d): a child's heart and respiratory rates are flagged against Fleming et al.'s centiles for their age group, and a child's blood pressure against the AAP's screening table for their year of age and sex, as "needs follow-up", never High; readings already recorded were ranged again (`docs/UC4.md`, `docs/expansion/reference-data.md`).
- Note follow-ups (the plan's step 10b): an addendum written by someone other than the note's signer is labeled and reaches the signer's inbox; a signed note or addendum can be marked sensitive after signing, with a reason, and only the person who marked it or the note's signer can unmark it (`docs/UC14.md`). Marking a note marks its addenda as inherited from it, and unmarking the note clears only those marks (step 10e).
- Off-site audit cost (the plan's step 10c): off-site, a request's audit events are written to the cloud app's queue together, with one flush to disk, before any of the page is sent; a page whose events can't be written is not sent (`docs/UC2.md`).
- A read-only FHIR R4 API (the plan's step 11): a clinician's client reads a patient on their panel, their allergies, medications, diagnoses, results, vital signs (a blood pressure as one reading) and appointments, coded as the record is, with the clinical app's sign-in; each read is recorded as a page's is, and sensitive items are left out unless asked for. See [FHIR API](#fhir-api) and `docs/UC19.md`.
- Results from a lab (the plan's step 12): a lab's system, with a key of its own, is handed the lab orders waiting for results, with only what it needs, and files their results through the same database rule as a clinician's filing; each order handed over and each result filed is audited as the lab's. The simulated lab (`src/Ehr.Lab.Simulator`) plays one, in the local stack too, with results that fit each patient's diagnoses. See [Simulated lab](#simulated-lab) and `docs/UC20.md`.
- Visit summaries (the plan's step 13a): from a signed note, a clinician with the patient on their panel writes and releases a summary for the patient in three plain-language parts, refused while it has an internal code, an identifier, the patient's record number or a whole section of the note in it; the patient, and whoever they share their record with, reads it in the portal under the rules for sensitive items, and the patient is emailed that there is something new, with no health details. A released summary is never edited: a wrong one is withdrawn with a reason and leaves a dated placeholder in the portal. The notes themselves never reach the portal. See `docs/UC14.md` and `docs/UC6.md`.
- Download my record (the plan's step 13b): from the portal's home, the patient, or a proxy as their access allows, downloads their record as one FHIR R4 file (allergies, medications, diagnoses, released results, vital signs and appointments, from the FHIR API's own mapping, with ids made for the file) or opens it as a page laid out to print, with the visit summaries. Sensitive items only when asked for; never notes, entries in error or unreleased results; each download recorded apart from a view. Pages print without the app bar or buttons, keeping the bands that say whose record it is and that it is read-only. See `docs/UC6.md`.
- Who viewed my record (the plan's step 13c): the portal lists the views of the record in the audit log over the last 12 months, newest first, 50 a page: a staff member by name and role, "You", someone the record is shared with by the address the patient gave access to, or the lab's hand-over of a lab order, with which part, as each read records it (step 13c2: the chart, the lists, results, a visit note, the portal's own pages and so on; "Your record" for most views recorded before that step), and a view through emergency access marked, never its reason. A proxy sees staff members' views, the lab's and their own: with sensitive items, every staff member's, as the patient does; without them, only those of staff who had the patient on their panel at the time, so never emergency access, and no views of sensitive items or of notes, and a part that could hint at something kept from them reads "The record". The database answers it, for the session's own record only (`db/policies/42-record-views.sql`). See `docs/UC6.md`.
- Refill requests (the plan's step 13d): from the portal's home, the patient, or a proxy whose access includes messages, asks the care team to renew an active medication coded as a product the clinic orders, with a short note, one open request a medication; each clinician with the patient on their panel finds it in their inbox, and one of them approves it by signing an order for the same medication product on the order form, with every check of an order, or declines it with a message the patient reads in the portal. The patient sees each request waiting, approved with what was ordered, or declined, and is emailed that there is something new, naming nothing. Sensitive medications follow the rules for sensitive items. See `docs/UC21.md` and `docs/UC6.md`.
- Pre-visit forms (the plan's step 13e): a clinician with the patient on their panel asks for the PHQ-9 or the GAD-7, worded as their authors publish them, before a booked appointment; the patient, or a proxy allowed sensitive items, fills it in in the portal from 7 days before the appointment until it ends; the database scores it, and the clinician sees each visit's score, severity band and need for follow-up, and a trend. Every form is a sensitive item. The forms say they aren't watched as they arrive and where to get help now; a PHQ-9 answer about self-harm also reaches the care team's inboxes. Nobody is emailed. See `docs/UC22.md`.
- SOAP notes and visit-type templates (the plan's step 14): a note is written in Subjective, Objective, Assessment and Plan, each required to sign, and the editor says what each holds; a clinician picks a visit type (follow-up, new patient, acute, well-child or telephone, kept as reference data) to see what to cover in each section, which is never put into the note; and asks for the latest day's vital signs (none entered in error, a blood pressure as one reading, each with its time) as text, which goes into Objective only when they add it. Nothing written before was moved: a note signed then keeps its History, Assessment and Plan, shown as signed, and a draft written then opens in the editor with its History in Subjective, to sort, and changes only when its author saves it. Dictation sorts into the same four sections; a review of a dictation sorted before them that is still waiting shows its History, which goes into Subjective once approved. See `docs/UC14.md`.
- Orders and allergies a dictation proposes (the plan's step 15, FR-14 as reworded): dictation never places, saves or signs an order or an allergy change, but the review of a dictation lists each plain instruction to place an order or to add an allergy as a proposal, in the clinician's words marked in the sentence they said them in, to open in the order form or the allergy form, which fill in nothing from it and sign or save through every check they have, or to dismiss; each is decided once, and none holds up the review or signing (`docs/UC13.md`, `docs/UC1.md`, `docs/UC4.md`). The dictation service proposes them by its own rules, never the model's, and only an instruction said plainly: the clinician's own direct instruction, with nothing in its sentence but a closed lead-in, the instruction and its own words. A condition, hedge or negation, someone else or someone else's say-so, words to the software, a comma clause or a retraction in the next sentence holds it back, as does an unclear passage; the eval measures them (`docs/UC10.md`).
- Dictation in Spanish (the plan's step 17, proposed FR-61): the Dictate page asks the language of each dictation, English first and never remembered. A dictation in Spanish is transcribed by Whisper large-v3-turbo, finished passages only, up to 2 minutes, with a passage not heard as Spanish marked and left out of what is sorted; K2 Horizon proposes the four fields in English, each sentence with the Spanish passages it was translated from; and the dictation service checks every sentence against that Spanish (its numbers, units, how often, dates, drugs, sides, routes, severity, abbreviations, medication changes, negation, certainty, history and relatives) before anyone sees it. The review shows each sentence beside its Spanish. Nothing beyond the note is proposed from Spanish: its instructions are listed for the clinician to place. Whisper is optional, and a server without it refuses Spanish; the desktop app's offline recordings stay English. The Spanish eval (`eval/results/latest-es.json`) hasn't been run on audio yet, so the eval gate fails until it is, and until the English eval is run again on this code.

**Not built yet:** editing visits, sending orders to a pharmacy, a real lab's interface (step 12's door is there for one; a simulated lab uses it), and the later steps of the plan. `docs/expansion/PLAN.md` is the plan for these and the rest.

## Layout

Folders describe their intended contents; most are still empty.

```
src/
  Ehr.Web/          ASP.NET Core Razor Pages host: Areas/Clinical, Areas/Portal, Api/Sync, Api/Dictation, Api/Fhir, Api/Lab, Fhir (the FHIR mapping)
  Ehr.Domain/       services, authorization policies, PHI reader, FHIR-shaped models
  Ehr.Data/         EF Core + Dapper on Npgsql: read and write contexts, RLS interceptor
  Ehr.Design/       Razor class library: the SwiftChart tokens, shared CSS, fonts and logos (wwwroot), shared partials, htmx, Alpine.js
  Ehr.Desktop/      Tauri 2 app: src-tauri (Rust shell, SQLCipher, sync) + frontend (with its copy of the shared CSS, fonts and logos)
  Ehr.Lab.Simulator/ the simulated lab: asks the on-prem app for lab orders and files synthetic results, over HTTP only
  Ehr.Dictation/    setup_models.py (downloads both models) and its tests; sidecar (Python: MedASR, K2 Horizon, the checks), schema (note schema and prompt), contract (the sidecar's answers, pinned), llama-server config
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
eval/               dictation test set, scoring and results
bench/              the synthetic clinic and its seed (Ehr.Seed), and the render-time benchmark (Ehr.Bench)
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

At first launch it asks you to sign in, with the email, password and authenticator code of a clinician account. The account must have finished its setup in the web app first (an admin creates it at **Staff**; its setup link sets the password, and its first sign-in there enrolls the authenticator app), or come from the stack's example accounts (see "Test accounts" in [infra/compose/README.md](infra/compose/README.md#test-accounts)). Until you sign in, and after you sign out, the list can be read but not changed.

The local database is `ehr.db` in the app's data folder: `~/Library/Application Support/com.example.ehr` on macOS, `%APPDATA%\com.example.ehr` on Windows. Its key, and the session from the last sign-in, are in the login Keychain or Credential Manager. On macOS, after a rebuild the Keychain may ask whether the app can use them, since to macOS a rebuilt app is a different program: choose **Always Allow** each time. If the key is missing or can't open the database, the app reports an error and leaves the encrypted file and unsent changes in place. It needs the original key to open that file. On Linux, which is for development only, the key, the session and the database live in memory, so each launch starts empty, asks you to sign in, and fills from the server.

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

The account is created at the next start if there are no staff accounts yet. The first sign-in asks for a new password and an authenticator app. Without a mail server configured (`Ehr:Smtp:Host`), the portal's emails are written to the console, and without a certificate in `Ehr:DataProtection:Certificate` and `Ehr:DataProtection:CertificateKey` (PEM files; the stack passes its own), the keys that protect cookies are stored in the clear, which the log says at startup.

**Settings a deployment must give.** Run this way, in Development, the app fills in local values. Anywhere else it refuses to start without these, and says which one is missing:

| Setting | What it is |
|---|---|
| `Ehr:Links:ClinicalBaseUrl` and `Ehr:Links:PortalBaseUrl` | The addresses people reach the clinical app and the portal at. Every link the app hands out (the emailed ones for password reset, confirmation and invitation, and the staff set-up link shown to an admin) is built from these and never from the request, whose Host header is its sender's to write. |
| `Ehr:Clinic:TimeZone` | The clinic's time zone, an IANA name such as `America/Chicago`. Clinician pages and the desktop app show and take times in it, labeled; everything is stored in UTC, and the audit log and the portal stay in UTC. Required outside Development, where it defaults to UTC. `Ehr:Clinic:TimeZoneAbbreviations` (optional, such as `CST,CDT`: standard, then daylight) is the label after a time; without it the label is the offset, such as `UTC-6`. The local stack sets both, from `EHR_CLINIC_TIME_ZONE` (default `America/Chicago`) and `EHR_CLINIC_TIME_ZONE_ABBREVIATIONS` (default `CST,CDT`) in `infra/compose/.env`: change one and change the other with it. |
| `Ehr:Enrollment:Key` | A secret of at least 32 characters that portal enrollment codes are hashed with, so a copy of the table can't be searched for codes. For the on-prem app only: the cloud app forwards registration and is not given it. Changing it makes codes already handed out stop working. |

The local server stack sets them from `infra/compose/.env` (`EHR_ENROLLMENT_KEY`; the addresses are in `compose.yaml`).

**Optional:** `Ehr:Lab:Key`, the key a lab's system calls the on-prem app with; without it there is no lab and its door isn't there (see [Simulated lab](#simulated-lab)). `RateLimits:LabRequestsPerMinute` (default 120) is how many requests with the lab's key that door takes a minute, and `RateLimits:LabKeylessRequestsPerMinute` (default 30) how many others, in a window of their own.

**In VS Code:** open the repository folder, then use Terminal → Run Task:

| Task | What it does |
|---|---|
| Run desktop app | `cargo tauri dev` |
| Run web app | `dotnet run --project src/Ehr.Web` |
| Server stack: start | Builds and starts the local server stack |
| Server stack: stop | Stops it and keeps its data |
| Server stack: reset | Stops it and deletes all its data |

**Tests:** `dotnet test` runs every test project. Build first, then run `pwsh tests/Ehr.Web.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium` for the real-browser clinical workflow tests (also installed in CI); alternatively set `PLAYWRIGHT_CHROMIUM_EXECUTABLE` to a compatible local Chromium. Docker must be running: the security and web tests start a real PostgreSQL 18 in a container. For the desktop app, run `cargo test` from `src/Ehr.Desktop/src-tauri`; it needs no Docker. For the dictation model script, run `uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests`; it needs no network. For the dictation sidecar and the eval, with ffmpeg installed, run `uv run --project src/Ehr.Dictation/sidecar pytest src/Ehr.Dictation/sidecar/tests` and `uv run --project eval pytest eval/tests`; they use stand-ins for the models.

## Local server stack

`infra/compose` runs the whole server architecture on one machine with Docker Compose: the on-prem primary and app, the cloud replica and app, the isolated Barman backup host, and a simulated WAN with latency between them. It also covers the failure drills and a point-in-time restore. Setup (certificates and hosts-file entries) and every command are in [infra/compose/README.md](infra/compose/README.md).

## Dictation models

Only dictation work needs these; everything else runs without them. They run on the on-prem server only, with no cloud AI.

| Model | Job | Source | Size | License |
|---|---|---|---|---|
| K2 Horizon 7B, Q4_K_M GGUF | Turns a transcript into proposed note fields, in `llama-server` | [IFM/K2-Horizon-7B-GGUF](https://huggingface.co/IFM/K2-Horizon-7B-GGUF) (llama.cpp calls it `IFM/K2-Horizon-7B-GGUF:Q4_K_M`) | About 5.6 GB | Apache 2.0 |
| MedASR | Speech to text, in a Python sidecar | [google/medasr](https://huggingface.co/google/medasr) | Small (105M parameters) | Health AI Developer Foundations terms; gated |
| Whisper large-v3-turbo | Speech to text for dictation in Spanish (the plan's step 17), in the same sidecar; optional | [openai/whisper-large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) at commit `41f01f3fe87f28c78e2fbf8b568835947dd65ed9` | About 1.62 GB (809M parameters) | MIT; not gated |

Once: create a Hugging Face account, accept the MedASR terms on its page, create a read-only access token, and sign in with `uvx --from huggingface_hub hf auth login`. The token stays in your user profile, never in the repository. Then, from the repository folder:

```
uv run src/Ehr.Dictation/setup_models.py --dry-run   # what it will download, and how big
uv run src/Ehr.Dictation/setup_models.py             # K2 Horizon and MedASR
uv run src/Ehr.Dictation/setup_models.py --only whisper   # Whisper, for dictation in Spanish; no sign-in while it isn't gated
```

- K2 Horizon goes in `infra/compose/models/`, MedASR in `infra/compose/models/medasr/`, and Whisper, when asked for, in `infra/compose/models/whisper/`, at the commit the script pins. Without Whisper the sidecar starts and refuses a dictation in Spanish; with it, the sidecar hashes its weights at startup and refuses to start if they aren't the ones the manifest records, as for MedASR. The folder is gitignored, and the script refuses one that Git would track: weights are never committed.
- Every file is checked against the SHA-256 Hugging Face publishes. A second run downloads nothing unless a file is missing or different, and an interrupted run can be restarted.
- `infra/compose/models/models-manifest.json` records the repository, commit, file and SHA-256 of what was installed, for the provenance each dictated note stores. `--verify` re-checks the files against it, offline.
- If you already have the K2 Horizon file (for example from `llama-server -hf`), pass it with `--from-file <path>`. It's used only if it's byte-for-byte the published file.
- The K2 Horizon license and model card are saved in `infra/compose/models/notices/`. Apache 2.0 asks that they go along with the file.
- The dictation sidecar doesn't use MedASR's optional 6-gram language model (`lm_6.kenlm` and `lm_6.arpa.xz`, about 945 MB together). Add `--medasr-exclude 'lm_6.*'` to leave it out.

To run K2 Horizon in the local stack, set `LLAMA_MODEL_FILE` (the script prints the value) and `LLAMA_API_KEY` in `infra/compose/.env`, then start the `ai` profile (see [infra/compose/README.md](infra/compose/README.md#ai-profile)). K2 Horizon "thinks" before it answers, at length whichever effort its template is given (high, medium or low: at low, over 1,000 tokens and 80 seconds for a two-minute dictation on an M5's GPU), so the `llama` service turns reasoning off (`--reasoning off`), as the sidecar asks on every request. Off, a two-minute dictation takes about 30 seconds there.

K2 Horizon "7B" stores about 9 billion parameters. With an 8K context it needs about 6 to 7 GB of memory, so a computer with 8 GB of RAM struggles. MedASR is small enough for any machine's CPU. Whisper takes about 3.3 GB more in fp32 on a CPU (about half that in fp16 on a GPU), which is why the `dictation` service's memory cap is 5 GB.

## Dictation

A clinician dictates, and MedASR transcribes as they speak. When they stop, K2 Horizon sorts the transcript into Subjective, Objective, Assessment and Plan, and every proposed value is checked against the transcript before anyone can see it. What passes is pending until a clinician approves it; nothing is stored. This is the server side and the API the coming screens will use. A clinician may choose, for each dictation, to dictate in Spanish (the plan's step 17): Whisper transcribes it, K2 Horizon proposes the fields in English sentence by sentence, each with the Spanish it was translated from, and the sidecar checks each sentence against that Spanish.

- **The sidecar** (`src/Ehr.Dictation/sidecar`, Python) does all of it, on the on-prem server only. ffmpeg decodes whatever the browser recorded, in memory. MedASR transcribes it, cut at pauses, and marks passages too quiet, too noisy or too uncertain to trust, which are left out of what K2 Horizon sorts. llama-server runs K2 Horizon with the note schema (`src/Ehr.Dictation/schema`) as its response format. `validation.py` throws away any value whose words aren't all in its sources in the transcript, in order, and cuts out any instruction to order something or change the allergy list, keeping only the reason. A value that passes but uses a number, dose, date or drug with another of its kind said just after it and a correction-like word between them, which may be a correction speech recognition misheard ("10 mg no weight 20 mg"), carries a possible-correction warning pointing at that place in the transcript; one sorted from words within five of a passage marked unclear carries a near-unclear-passage warning pointing at that passage; and the only value offered, whether the other three fields were not dictated or discarded by the checks, whose sources cover nine in ten of the transcript's words or more, carries a one-field-holds-dictation warning: the whole dictation, plan and findings alike, went into one section, to be sorted before it is accepted. The review screen must show each warning beside its field. What neither warning catches, the clinician's review of each value against the transcript must: a correction more than about eight words after what it corrects, or with no correction-like word between; a correction of a different kind of item (a dose corrected by a date); the corrected value said first; a retraction with no replacement ("scratch the 10 mg"); and a number misheard with no cue at all. Each retracted value the eval shows without a warning is listed, with why, in `eval/known-limitations.toml`. Of the instructions to order something or change the allergy list, the checks propose, once the transcript is final, each order or allergy to add said plainly (the expansion plan's step 15): its kind, its place and its warnings, never words, and never anything K2 Horizon wrote. Plainly is one form: the clinician's own direct instruction, its sentence holding nothing but a closed lead-in, the instruction and its own words (`validation.py`'s header has it word for word); anything else, a condition, hedge or negation, someone else or someone else's say-so, words to the software, a comma clause, another step of the plan, a retraction in the next sentence, an unclear passage, is held back, still listed as not carried over (`validation.proposals()`; a kind of session that must propose nothing sets `Session.proposes` to false). A dictation started in Spanish (`{"spokenLanguage": "es"}`) goes another way from start to end (`ehr_dictation/sessions.py`): Whisper (`whisper_asr.py`) transcribes finished passages only, greedy, with Spanish and transcription forced, and marks a passage it didn't hear as Spanish `other_language` and one whose words look made up `low_confidence`; spoken punctuation is only "punto y aparte", "nuevo párrafo" and "punto y seguido". K2 Horizon answers with the Spanish prompt and schema (`structuring-prompt-es.md`, `note-fields-es.schema.json`) as one list of statements, each its Spanish passages, its field and its English, and `translation.py` checks each statement against the Spanish it cites, discarding the whole field if one fails, as `validation.py` discards an English value; the Spanish instructions are listed by `spanish.py`'s detector and nothing is proposed. The result says `"spokenLanguage": "es"`, each field's statements, and Whisper in its provenance. The sidecar has no database access, and its logs carry no audio or text. Its API is in the header of `ehr_dictation/api.py`, and its limits in `ehr_dictation/settings.py`.
- **The on-prem app's API** (`src/Ehr.Web/Api/Dictation`) is for clinicians signed in to the web app, and the cloud app forwards it to the on-prem app. Every call answers within 20 seconds; if K2 Horizon is unavailable or too slow, the transcript comes back as plain text with a note that the fields couldn't be sorted. A dictation can be up to 3 minutes: K2 Horizon repeats each field's sources in its answer, so a genuine 5-minute dictation took 98 to 114 seconds to sort on an M5's GPU, over its 90 seconds, and the test set's 3-minute dictations 74 with reasoning on at low; those were measured with the three fields of before the expansion plan's step 14, while a stray test process kept one of the M5's ten CPU cores busy, and haven't been measured again. With reasoning off, in the eval's run on the four fields, the two 3-minute dictations took 48.9 and 55.3 seconds (49.8 on average in its run on three). A dictation in Spanish can be up to 2 minutes: its answer carries English beside every Spanish passage, about 4 tokens a spoken word, so 3 minutes (about 450 words, 1,900 tokens, 95 seconds on an M5's GPU) would outrun the 90 seconds, where 2 minutes (about 300 words) took about 64 seconds in run A2 (the plan's decision log, step 17, decision 20). The Dictate page asks for the language (`spokenLanguage` with the start of a note's dictation), and the plain start and the desktop app's offline recordings are always English. Every result also lists each instruction the clinician dictated to order something or change the allergy list, which no field carries, so the screen can say what still needs placing in Orders or the allergy list, and the orders and allergies to add among them that the dictation proposes, which staging accepts only as listed instructions of the same kind, each once, at most twenty, from a final transcript. Every kind of answer the sidecar gives it is pinned in `src/Ehr.Dictation/contract`: the sidecar's tests write those files from its own code, and the web tests read each one through the API, so neither side can change shape without a test failing.
- **In the local stack** the sidecar is the `ai` profile's `dictation` service (see [infra/compose/README.md](infra/compose/README.md#ai-profile)).

To use the sidecar with `dotnet run`, start llama-server natively as under [Dictation server hardware](#dictation-server-hardware). Then set `DICTATION_API_KEY` (any long random value), `LLAMA_API_KEY` (llama-server's), `DICTATION_MODELS_DIR=infra/compose/models`, `DICTATION_LLAMA_URL=http://127.0.0.1:8080` and, on a Mac, `DICTATION_DEVICE=mps` in the environment, and from the repository folder run:

```
uv run --project src/Ehr.Dictation/sidecar uvicorn --factory ehr_dictation.api:create_app --host 127.0.0.1 --port 8090
```

and tell the web app where it is, once:

```
dotnet user-secrets set Ehr:Dictation:Url http://127.0.0.1:8090 --project src/Ehr.Web
dotnet user-secrets set Ehr:Dictation:ApiKey <the same DICTATION_API_KEY> --project src/Ehr.Web
```

**The eval** (`eval/`) measures the pipeline on a test set of synthetic dictations (`eval/scripts`), recorded by text-to-speech voices with background noise mixed in (`eval/README.md` says how to make the recordings, and how to record the scripts in your own voice). Some voices stand in for accents; they are synthetic stand-ins, not real regional accents. With the models installed, llama-server running and `LLAMA_API_KEY` set:

```
uv run --project eval python -m ehr_eval.run --llama-url http://127.0.0.1:8080 --device mps
```

It writes `eval/results/latest.json`: word error rate overall and per tag, date and drug-name accuracy, note-field precision, recall and F1 against the rule-following gold (the best answer the checks allow, which the gate compares) and beside it against the clinical gold (what a careful clinician would accept), what the checks passed and discarded and why, the share of offered words the transcript doesn't support, any order or allergy change offered (there must be none), any retracted value offered, with and without a warning (without one, there must be none but those the owner has accepted in `eval/known-limitations.toml`), what each warning catches and how often it flags a right value, how many of the dictated orders and allergy changes were reported as not carried over, what the order-and-allergy rule discarded that was legitimate, what the dictation proposed against each script's gold proposals (right, misheard where the item it names wasn't heard word for word, or false; each misheard or false one listed, and there must be none but those the owner has accepted in `eval/known-limitations.toml`, a warning or not), and the time per stage, separately for the two-minute dictations and those at the limit. It is stamped with the models' hashes, the prompt, schema and parameters hash, a pipeline hash (the sidecar's code apart from its checks, the eval's `answers.py`, and both lockfiles: what makes the transcript and the answers), a scoring hash (the checks and the rest of the eval's code), the test set's recordings hash (what each script says, and the voices and noise) and gold hash (the rest of each script, both gold sets included), and the machine. CI's gate needs that file to match the commit: a change to what makes the answers or the recordings means running the eval again, and a change only to the checks, the scoring or the gold means scoring the stored answers again (`uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json`); either way the new results are committed, and every recording in them must have been sorted. `--reasoning low256` runs the comparison with K2 Horizon's reasoning on at low, capped at 256 tokens, into `eval/results/compare-low256.json`, which the gate doesn't check. `--set es` runs the Spanish test set (`eval/scripts-es`, its audio made with `generate_audio.py --set es`) into `eval/results/latest-es.json`, which the gate checks beside `latest.json`: the Spanish word error rate, drug and date accuracy, the English fields' F1, every translated sentence compared with the Spanish it cites, no instruction shown, and two recordings dictated in the wrong language; `eval/README.md` ("The Spanish test set") says what it measures and checks. The accepted baseline, `eval/results/baseline.json`, is recorded by the owner on the server machine; after that, the gate also fails any run that scores lower, lacks a metric the baseline has, or comes from another machine.

## FHIR API

A read-only FHIR R4 API, on both servers, for clinicians (`src/Ehr.Web/Api/Fhir`, UC 19 in `docs/UC19.md`). Its resources come from a mapping of their own (`src/Ehr.Web/Fhir`), which the portal's "Download my record" uses too, with ids made for the patient's file (`docs/UC6.md`). It uses the clinical app's own sign-in: sign in there as a clinician, and the API reads that cookie. A refusal is a 401 or 403 with an OperationOutcome, never a redirect.

| Request | What it returns |
|---|---|
| `GET /fhir/metadata` | The capability statement: what is served, and how |
| `GET /fhir/Patient/{id}`, `GET /fhir/Patient?identifier={MRN}` | The patient. The MRN can also be given as `http://swiftchart.example/fhir/sid/mrn\|{MRN}`. A search by MRN finds a patient only when you may read their record: any other MRN, a patient's or nobody's, gets the same empty Bundle, and nothing is recorded |
| `GET /fhir/{type}?patient={id}` | A `searchset` Bundle of the patient's entries: `AllergyIntolerance`, `MedicationStatement`, `Condition`, `Observation` (also `&category=laboratory` or `vital-signs`) and `Appointment` |
| `GET /fhir/{type}/{id}` | One entry |

- **Only a patient on the clinician's panel**, or under emergency access already started in the clinical app; otherwise 403 (or, for a search by MRN, nothing found). Each request is one recorded read of the patient, as a page's is, and off-site it goes through the cloud app's audit queue before the answer is sent.
- **Sensitive diagnoses and medications** are left out unless `show-sensitive=true` is added, which is recorded as the chart's "Show sensitive items" is; they then carry `meta.security` R (restricted).
- **A blood pressure is one Observation for each reading**: LOINC 85354-9 with its systolic and diastolic as components, claiming R4's `bp` profile, and its id is the reading's (step 9c); its two values are not Observations of their own. The other vital signs claim their R4 profiles too.
- **A search** returns at most 600 entries, with `total` giving how many matched, and no paging. Each type comes newest first by its own date: allergies by when they were recorded, medications by when they were started, diagnoses by onset, results and vital signs by when they were taken, appointments by their start; entries with no date last. A parameter the capability statement doesn't list is refused with 400, at `/fhir/metadata` too; any method but GET with 405.
- **Every answer** is `application/fhir+json`, `Cache-Control: no-store`, with no CORS headers. A GET whose `Accept` header doesn't take `application/fhir+json` (the most specific matching range decides, as RFC 9110 has it; `application/json` counts as asking for it) is refused with 406, and `_format=json` overrides it; a write is 405 whatever it accepts.
- **Conformance** is checked in the tests with the Firely .NET SDK (BSD-3-Clause), offline: every answer is parsed strictly and validated against R4's definitions and the profiles it claims (`tests/Ehr.Web.Tests/FhirConformance.cs`). The app itself writes the JSON and has no FHIR package.

## Simulated lab

A lab's system is handed the lab orders waiting for results and files their results through a door of its own on the on-prem app (`src/Ehr.Web/Api/Lab`, UC 20 in `docs/UC20.md`). The database checks it too (`db/policies/76-lab.sql`).

| Request | What it does |
|---|---|
| `GET /api/lab/orders?since={watermark}` | The lab orders signed since the lab last asked that still wait for results, and the watermark to ask from next time. `since` is a transaction ID; without it, every order still waiting. Whole signing transactions, as many as come to at most 100 orders (or one alone that signed more; the pages sign one order a transaction), with `more` set when there are more |
| `POST /api/lab/orders/{id}/results` | One order's results, `{"collectedAt": ..., "results": [{"code", "value", "unit"}]}`, all or none: 204 when filed, and again when the same is sent twice. `collectedAt` carries a `Z` or any offset (with neither, it is `malformed`); every time the door writes is UTC with a `Z`. Refused with a word and a sentence: `malformed` (400), `unknown-order` (404), `discontinued`, `closed` or `different-result` (409), `not-reported`, `wrong-unit` or `in-the-future` (422); a failure is `failed` (500), in every environment |

- **Who:** the key in the `X-Lab-Key` header, the on-prem app's `Ehr:Lab:Key`, checked first, and none of the app's credentials beside it (no `Authorization` header, no staff, portal or sign-in cookie, whatever the case of its name); anything else is 401. The database is given the hash of the key the caller presented and lets nothing through without the right one, whatever the app does. The cloud app has no lab endpoints and forwards nothing under `/api/lab`, so the door is the clinic's network's alone.
- **Rate limits:** requests with the lab's key are counted in their own window; every other request to the door, in a small window of its own that can only earn a 401 or a 429. A flood without the key can't make the lab's requests wait or fail.
- **What a lab is handed:** each order's ID, when it was signed, the tests still waiting for a result (an order partly filed by hand gets the rest), each with its LOINC code, unit, the clinic's reference range and its decimals, and the codes of the patient's current diagnoses that aren't sensitive. Never the patient's name, MRN, birth date, sex or ID, the order's details, medications or allergies.
- **Filing** goes through the same database rule as a clinician's filing: the clinic's name, unit and range, the order resulted, the clinician who signed it told in the inbox. A result can't change one already filed, by hand or by the lab.
- **Recorded:** each order handed over is a `sent_to_lab` audit event, and every write a filing makes is audited, all with no account and the source `lab`; the admin audit page shows them as "Lab". With at most 100 orders an answer (while no transaction signs more than 100 lab orders, which the pages never do) and 120 requests a minute, the door adds at most 12,000 hand-over events a minute, and only with the key.
- **Key:** the migration job stores the SHA-256 of `EHR_LAB_KEY`, which must be the on-prem app's `Ehr:Lab:Key`; without it no hash is stored and nothing gets in. To change it, change both, run the migration job again and restart the on-prem app; calls in between are refused and a lab tries again.
- **The results page** of an order still waiting says "Lab connected:" when a key is stored; entering results by hand stays, for when the lab can't send them.
- **Orders signed, nothing handed over:** the hand-over stops short of the oldest transaction still running on the database server that has written something, so one session left open in a transaction (`idle in transaction`, a long report) holds back every lab order signed after its first write, with no error anywhere, until it ends; then the lab is handed them all. On the primary, `select pid, usename, application_name, state, xact_start from pg_stat_activity where backend_xid is not null order by age(backend_xid) desc` shows it first.

**The simulated lab** (`src/Ehr.Lab.Simulator`) is a lab's system for development and the local stack: it asks every few seconds, keeps each order until its results are due, 5 to 30 seconds after it was signed, and posts values drawn from the clinic's own ranges, as the hand-over gives them: above or below for the diagnoses that move a test (type 1 or 2 diabetes, lipid disorders, hypothyroidism, vitamin D deficiency, chronic kidney disease, anemias: the table is in `docs/UC20.md`), and out of range now and then otherwise. The same order gets the same values every time, so two labs at once, or one started again, file each result once. The local stack runs it as its `lab` service when `EHR_LAB_KEY` is set in `infra/compose/.env`. To run it yourself against the stack, put the stack's key in the environment without typing it on the command line, where the shell's history would keep it (PowerShell 7: `$env:EHR_LAB_KEY = Read-Host -MaskInput "Lab key"`; zsh or bash: `read -rs EHR_LAB_KEY && export EHR_LAB_KEY`), then:

```
dotnet run --project src/Ehr.Lab.Simulator -- --base-url https://ehr.example.com:8443 --certificate infra/compose/certs/example.com.pem
```

`--certificate` trusts the stack's certificate as it is, for a machine that doesn't trust mkcert's root. The address must be `https://`: `--allow-http` lets an `http://` one through, for testing on one machine only. `--poll-seconds`, `--min-delay-seconds`, `--max-delay-seconds` and `--abnormal-rate` change its pace and its odds (`src/Ehr.Lab.Simulator/Program.cs`). Without `EHR_LAB_KEY` it says so and stops. It never prints the key.

## Database changes

The schema comes from EF Core migrations in `db/migrations`; roles, grants, row-level security and triggers are hand-written SQL in `db/policies`. Reference data, the standard codes chart entries are picked from, is generated SQL in `db/reference` (`docs/expansion/reference-data.md`). The migration job (`infra/migrate`) applies all three to the primary, as `ehr_owner`, every time the server stack starts, and then stores the hash of the lab's key from `EHR_LAB_KEY`, or none when it isn't set (see [Simulated lab](#simulated-lab)); the replica gets them by replication. CI's stack job runs the same job.

Run `dotnet tool restore` for the pinned `dotnet-ef`: once, and again whenever `.config/dotnet-tools.json` changes. Then, from the repository folder:

1. Change the model in `src/Ehr.Data`.
2. Add a migration: `dotnet ef migrations add <Name> --project db/migrations`
3. Regenerate the SQL that reviewers read: `dotnet ef migrations script --project db/migrations --output db/migrations/schema.sql`
4. For a new table, add its grants to `db/policies/10-grants.sql` and the same privileges to `ExpectedPrivileges` in `tests/Ehr.Security.Tests/RuntimeRoleTests.cs`, and decide its protection in `ExpectedProtection` in `ProtectionTests.cs`. A PHI table also gets its policies in `30-rls.sql` and the write-audit trigger in `40-audit.sql`.
5. Restart the server stack to apply it, and run `dotnet test`.

Tests fail if the model has changes no migration covers, if `schema.sql` is out of date, or if a table has no grant or protection decision. To undo a migration you haven't pushed, delete its two files and run `git restore db/migrations/EhrDbContextModelSnapshot.cs`. (`dotnet ef migrations remove` expects a database at the default port, which the local stack doesn't use.)

## Synthetic data and the benchmark

`bench/` holds a synthetic clinic: patients with allergies, medications, diagnoses, visits and test results, the same every time, and obviously synthetic (every name carries a number, every MRN starts `SYN`). It is clinically coherent and coded from the reference data: a patient's conditions bring the medications that treat them and the results that follow them, so someone with type 2 diabetes is on metformin and has raised hemoglobin A1c results.

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

starts its own PostgreSQL 18, seeds it, runs the on-prem app in memory, and prints the p50, p95, p99 and slowest time of each kind of request: searches by name, date of birth and MRN, results as the clinician types, the portal's results page as a patient, the clinician's panel of 100 patients, charts, the order form, a patient's discontinued orders, the forms that change and discontinue an order, and the FHIR API's reads (a patient, their results and vital signs, their diagnoses with sensitive ones asked for, and an observation by ID). Then it makes the same requests off-site, its rows' names starting "Off-site": to the cloud app in memory, reading as `ehr_read` with its link to the on-prem app down, as during an on-site outage (UC 2). The cloud app records those reads in its audit queue, flushed to disk before the page is sent, rather than in the database, so in those rows the audit insert is 0 and the queue's append counts as the commit. Last, on-site again, it measures the lab's door with the lab's key: handing over ten newly signed lab orders, and filing one order's results (not with `--stack`, whose lab key is in the stack's `.env` alone). Each page commits the audit events of its reads, and its PostgreSQL has `fsync`, `synchronous_commit` and `full_page_writes` on, as the stack's primary does (the tests' databases have them off, for speed), so every time includes the wait for those events to reach the disk. Beside it, it prints the p50 and p99 excluding the commit: each request's time less its own commit, with the audit events' insert still in it. It prints the three settings as it read them, and when any is off the table says "durability off" and `--assert` refuses to run. `--assert` exits with 1, naming each kind of request that failed, when a p99 excluding the commit is 20 ms or more; it doesn't check the full time, whose 20 ms target is checked on the clinic server, because CI runners' shared disks make commit times unsteady, but with or without `--assert` it marks a kind of request whose full-time p99 is 20 ms or more with a warning, in its row and after the table, which never fails the run. A kind of request whose 1,100 requests take over a minute in all stops the run with an error, since at 20 ms each they take 22 seconds. `--patients <n>` seeds another number. `--results <file>` also writes each row's percentiles as JSON, which the performance test cases in `tests/Ehr.Web.Tests/TestCases` check against their own requirements' limits (they are skipped in the normal test run; `PerformanceResults.cs` there says how to run them). With `--stack` it measures on-site only, against the stack's primary instead, seeded already with as many patients, with the web app's own settings from "Running the apps"; it refuses to start, before the app does, without the Data Protection certificate among them, and once started when the database's statistics don't show a clinic of that size. It never writes a Data Protection key: its own cookies and form tokens use keys kept in memory, and it creates none in the key ring the stack's apps share. Each run creates a clinician of its own, and two portal accounts, one for a seeded patient with results and one for the seeded patient whose record has the most entries, each with a password nobody keeps, and locks all three at the end. A run against the stack leaves its writes there: that clinician and those patient accounts, 100 panel assignments with their write-audit events, and an audit event for every patient each page listed, about 195,000 at 50,000 patients, which nothing deletes.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to any branch, and on every pull request into `main`.

| Job | What it checks |
|---|---|
| Web app | Restores packages (a high or critical security advisory fails the run), builds in Release, then runs `dotnet format --verify-no-changes` and `dotnet test` |
| Server stack | Starts the local server stack (`infra/compose`), including the migration job, then checks that the clinic's home sends an anonymous browser to sign in, the sign-in pages load over HTTPS on all three sites, and the sync API refuses an anonymous request (the on-prem app reads the primary; the cloud app reads the replica), the lab's door hands over orders on the on-prem site with the lab's key and isn't on the cloud site, and the lab service runs, both databases accept connections over TLS only, the replica and Barman are streaming, and `barman check` passes |
| Desktop | `cargo fmt --check`, `cargo clippy` (warnings count as errors) and `cargo build`, on macOS (Apple Silicon) and Windows (x64) |
| Dictation | The tests for `src/Ehr.Dictation/setup_models.py`, against a fake Hugging Face Hub; the dictation sidecar's and the eval's tests, with the packages their `uv.lock` files pin and stand-ins for the models; and the eval gate, which checks that the committed results in `eval/results/latest.json` come from this commit's prompt, note schema, parameters, sidecar code, eval code and test set, cover every script with every recording sorted, show no order or allergy change and no retracted value without a warning that the owner hasn't accepted in `eval/known-limitations.toml`, say what the dictation proposed with no false or misheard proposal the owner hasn't accepted there, and score no lower than the accepted baseline; and the same for the Spanish test set's results in `eval/results/latest-es.json` (the plan's step 17): its own prompt hash and test set, every recording and text sorted, no instruction shown, and no mistranslation without a warning that the owner hasn't accepted (see [Dictation](#dictation)) |
| Dictation image | Builds the dictation sidecar's image on an x86-64 and an arm64 runner, and checks that it runs as a non-root user, has ffmpeg and the note schema, and refuses to start without its keys. Nothing is pushed. |
| Render-time benchmark | Runs the benchmark (`bench/Ehr.Bench`, see [Synthetic data and the benchmark](#synthetic-data-and-the-benchmark)) at 50,000 synthetic patients with `--assert`, and puts its times in the run's summary whether it passes or fails. It fails when a kind of request's p99 excluding the commit is 20 ms or more, on-site or off-site, and stops at once when one kind of request takes over a minute in all. The full time, commit included, is reported beside it, with a warning when its p99 is 20 ms or more, but not checked here: its 20 ms target is checked on the clinic server. |
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
| Fonts (Sora, IBM Plex Sans, IBM Plex Mono) and logos | Committed in `src/Ehr.Design/wwwroot/fonts` (WOFF2, with their SIL Open Font Licenses) and `wwwroot/img`, served from our own origin; the brand guide and tokens are in `docs/brand` |
| Desktop | `src/Ehr.Desktop/src-tauri/Cargo.toml` |
| Dictation sidecar | `src/Ehr.Dictation/sidecar/pyproject.toml`, locked in its `uv.lock` |
| Dictation models | `src/Ehr.Dictation/setup_models.py` (repositories and quantization); what each machine installed is in `infra/compose/models/models-manifest.json` |
| llama.cpp build | `infra/compose/compose.yaml` and `infra/compose/compose.nvidia.yaml` (container images); this README for the native builds |
| Dictation eval | `eval/pyproject.toml`, locked in `eval/uv.lock`, where every package it shares with the sidecar has the sidecar's version |
| Container images for the local stack | `infra/compose/compose.yaml`, `infra/barman/Dockerfile`, `src/Ehr.Web/Dockerfile`, `src/Ehr.Dictation/sidecar/Dockerfile` |
| GitHub Actions used by CI | `.github/workflows/ci.yml`, each pinned to a commit. Dependabot (`.github/dependabot.yml`) proposes updates weekly. |

## Dictation server hardware

K2 Horizon 7B runs in llama.cpp's `llama-server`, pinned to build **b11669** on every machine. Support for K2 Horizon arrived in b11454; older builds fail with `unknown model architecture: 'k2-horizon'`. In the local stack it's the opt-in `ai` profile. MedASR runs in the dictation sidecar, in Docker on the CPU, since the model is small; natively on a Mac it can use the GPU (`DICTATION_DEVICE=mps`). Whisper, for Spanish, runs beside it on the same device, in fp32 on a CPU and fp16 on a GPU; it reads a 30-second window for every passage, so a CPU transcribes Spanish more slowly, finished passages only.

| Server machine | How `llama-server` runs |
|---|---|
| Windows, NVIDIA GPU | In Docker: the `ai` profile plus `infra/compose/compose.nvidia.yaml`, which switches to `server-cuda-b11669`. Docker Desktop passes NVIDIA GPUs through WSL2; keep the NVIDIA driver current. The model file alone is about 5.6 GB, so plan on 8 GB of GPU memory. |
| Windows, AMD GPU | Natively: the b11669 Windows Vulkan build from the llama.cpp GitHub release. Docker Desktop can't use AMD GPUs. |
| Apple Silicon Mac | Natively: the b11669 macOS arm64 build, which uses Metal. Or Podman with krunkit running `server-vulkan-b11669`, which keeps it in a container at roughly 75–80% of native speed. Docker Desktop can't use the Mac's GPU. |
| No usable GPU | In Docker: the `ai` profile as is (`server-b11669`), on CPU. |

- A native `llama-server` sits outside the Compose network, so nothing enforces "no internet egress" for it. Start it with the local model file, never with `-hf` or another flag that downloads one, and keep the same safeguards as the container: listen on `127.0.0.1` only, require an API key, turn off the web UI (`--no-webui`), turn reasoning off (`--reasoning off`), and never use `--tools`, `--log-prompts-dir` or `-v` (they would write what patients say to disk or the log). Keep the context the stack uses, 8192 shared by 2 slots: a dictation at the 3-minute limit fits the 4,096 tokens each request gets. For example:

  ```
  llama-server -m infra/compose/models/<file> --host 127.0.0.1 --port 8080 --api-key-file <file holding the key> --no-webui --no-slots -c 8192 -np 2 --reasoning off
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
  - Whisper large-v3-turbo (about 1.62 GB), under MIT, only where dictation in Spanish is offered.
