# SwiftChart expansion plan

An experimental line of work on `expansion/integration`, building SwiftChart out into a complete EHR. Nothing here reaches main until Tahseen merges it back himself, phase by phase, through reviewed pull requests.

At the start of every session, read this file, its status table and both logs, then resume at the first step that isn't done. AGENTS.md wins wherever it conflicts with this file.

Under each step, "In the repo" says what the step touches as the code stood when the plan was checked against it (2026-11-18, main at `b4ad35b`), and what already exists. Check it again when the step starts: earlier steps change it.

## Status

| Step | What | Status | Branch | Tag | CI run | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | This plan | done | expansion/step-00-plan | expansion-step-00 | [44257188518](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44257188518) | |
| 1 | Change and discontinue orders | done | expansion/step-01-order-changes | expansion-step-01 | [44263160924](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44263160924) | UC 1 (Tahseen). Touches `db/policies` |
| 2 | Editable allergies, medications, diagnoses | done | expansion/step-02-chart-lists | expansion-step-02 | [44271858512](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44271858512) | UC 4 (Diego), UC 7 (Sara), and the banner on UC 9, 13 and 14's pages (Samir, Satwik). Touches `db/policies`. Notes can't be marked sensitive yet: see the log |
| 2b | Sensitive-item and allergy-status fixes | done | expansion/step-02b-sensitive-fixes | expansion-step-02b | [44286735927](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44286735927) | Inserted by Tahseen on 2026-11-18. UC 4 (Diego), UC 7 (Sara), UC 12 (Luke). Touches `db/policies` |
| 2a | Reference data and a coherent synthetic seed | done | expansion/step-02a-reference-data | expansion-step-02a | [44297723824](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44297723824) | LOINC loaded from Tahseen's download. CVX not loaded (optional). Open questions for Tahseen in the log |
| 3 | Clinician inbox | done | expansion/step-03-inbox | expansion-step-03 | [44304586912](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44304586912) | New UC 15 (proposed). Admins have an inbox too. Touches `db/policies` |
| 4 | Appointments | done | expansion/step-04-appointments | expansion-step-04 | [44372811264](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44372811264) | New UC 16 (proposed); UC 6 (Simar), UC 4 (Diego). Touches `db/policies` and authorization |
| 5 | Secure messaging | done | expansion/step-05-messaging | expansion-step-05 | [44382799606](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44382799606) | New UC 17 (proposed); UC 6 (Simar), UC 4 (Diego). Touches `db/policies` and an authorization test |
| 6 | Proxy access | done | expansion/step-06-proxy-access | expansion-step-06 | [44395022419](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44395022419) | New UC 18 (proposed); UC 5, UC 6 (Simar), UC 11 (Luke). Touches `db/policies`, authorization and AGENTS.md |
| 6a | Review aids: generated files collapsed on GitHub | done | expansion/step-06a-review-aids | expansion-step-06a | [44403095126](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44403095126) | Inserted by Tahseen before the phase 1 pull request |
| 7 | Order → result → acknowledgment | done | expansion/step-07-results | expansion-step-07 | [44414981634](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44414981634) | Extends UC 1 (Tahseen); UC 6 (Simar), UC 15. Touches `db/policies` and authorization |
| 6b | Proxy-only portal accounts, and choosing a record after sign-in | done | expansion/step-06b-proxy-accounts | expansion-step-06b | [44423758634](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44423758634) | Inserted by Tahseen; done after step 7. Touches `db/policies` and authorization; UC 5, UC 6 (Simar), UC 11 (Luke) |
| 7a | Chart render headroom | done | expansion/step-07a-chart-headroom | expansion-step-07a | [44429275145](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44429275145) | Added while working: the chart was at the benchmark gate on CI runners. No behaviour change |
| 6c | Security follow-ups: invitation and enrollment lookups, emailed links | done | expansion/step-06c-security-follow-ups | expansion-step-06c | [44436811170](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44436811170) | Inserted by Tahseen; before step 8. Touches `db/policies`; a new required setting; UC 5 (Simar), UC 11 (Luke) |
| 8 | Ordering safety checks | done | expansion/step-08-order-checks | expansion-step-08 | [44445242631](https://github.com/tarnan3751/EHR-Electronic-Health-Record-Project/actions/runs/44445242631) | Extends UC 1 (Tahseen). Touches `db/policies`; three interaction sources |
| 9 | Vital signs and trends | in progress | expansion/step-09-vitals | | | Extends UC 4 (Diego) and UC 6 (Simar). Touches `db/policies` |
| 10 | Addenda to signed notes | not started | | | | |
| 11 | Read-only FHIR API | not started | | | | |
| 12 | Simulated lab | not started | | | | |
| 13a | Visit summaries | not started | | | | |
| 13b | Download my record | not started | | | | |
| 13c | Who viewed my record | not started | | | | |
| 13d | Refill requests | not started | | | | |
| 13e | Pre-visit forms | not started | | | | |
| 14 | SOAP notes and templates | not started | | | | |
| 15 | Dictation proposals beyond the note | not started | | | | asks first |
| 16 | Ambient listening | not started | | | | asks first |
| 17 | Languages beyond English | not started | | | | asks first |
| 18 | Session timeouts, emergency-access review | not started | | | | |
| 19 | Passkeys | not started | | | | |
| 20 | Desktop distribution | not started | | | | part asks first |
| 21 | Real deployment and operations | not started | | | | asks first |

Statuses: not started, in progress, done, blocked (with the reason in Notes).

## Branch workflow, for every step

1. **Bring main in first.** `git switch expansion/integration`, `git pull --ff-only`, `git fetch origin`, `git merge origin/main` (a merge, never a rebase), then push. If main gained a database migration, don't hand-merge the model snapshot: recreate this line's own new migrations after main's, and regenerate `db/migrations/schema.sql`.
2. **Branch.** Create `expansion/step-NN-short-name` from `expansion/integration`.
3. **Work.** Make small commits. Push the step branch so CI runs on it. Never open a pull request. (CI runs on every push to any branch, `.github/workflows/ci.yml`; its image-publishing job runs on main only.)
4. **Merge back.** When the step is done and its CI run is green: `git switch expansion/integration`, `git merge --no-ff <step branch>`, `git tag expansion-step-NN`, then push the branch and the tag.
5. **Record it.** Update this file's status table and log on the step branch, before merging. (The CI run recorded is the one for the step's last commit before that record; see the decision log.)
6. **At the end of a phase:**
   - tag `expansion-phase-N` and push a branch `expansion/phase-N` at that tag;
   - write the pull request title and description in the "Phase merges" section, including the protected paths the phase touched;
   - tell Tahseen, then keep going. He opens that pull request and gets it reviewed himself.

## Ground rules

- **Main is off limits.** Never push to main, merge into main, open a pull request, or change repo settings.
- **Ask Tahseen before** any force-push, rewriting history, or deleting a branch or tag.
- **Git habits.** Never `git stash`. Stage files by name; never `git add -A` or `git add .`. Leave `.claude/` alone and never commit it.
- **Files.** LF line endings. No bash scripts in the repo: tooling is `dotnet run script.cs` or PowerShell 7, as AGENTS.md says.
- **Data and secrets.** Synthetic data only. Never commit secrets, model weights, signing keys, `.env` files or `test-accounts.json`. Read test passwords and authenticator codes at runtime, and never write them to files or logs.
- **Scratch work.** Throwaway scripts, screenshots and notes go in a scratch folder outside the repo, and are never committed.

## Every step must meet these

**The brand kit**
- **Follow the guide.** All UI follows `docs/brand/README.md`. `docs/brand/tokens.json` is the source of truth for color, type, spacing, radius and size; after changing tokens, the shared stylesheet, a font or a logo, run `dotnet run tools/brand/sync.cs`.
- **Use the existing styles.** Use the guide's `sc-` components and the token variables only, never raw colors or one-off styles. A new component goes into the shared stylesheet in the guide's style, with a note in `docs/brand`.
- **Make it look and work right.**
  - Both themes must be equally finished, with WCAG AA contrast.
  - Every interactive element shows the focus ring, and reduced motion is respected.
  - Clinician screens are keyboard-first (NFR-12).
  - The portal uses its own sizes: 18px text, 44px controls, one column, working down to 360px wide.
- **Write in the guide's voice:**
  - sentence case;
  - notices start with their state word ("Read-only:");
  - buttons name their action;
  - dictation output is "proposed", never "decided";
  - mono type for MRNs and IDs.
- **Check against the kit.** Look at every new or changed screen in light and dark, and compare it with the kit. If `~/Downloads/swiftchart-brand-kit/components/index.html` is still there, use its reference renders. (It was there on 2026-11-18.)

**Performance (the course document and CI)**
- **Speed targets:**
  - NFR-01: on-site page navigation under 15 ms;
  - NFR-02: desktop navigation under 5 ms;
  - NFR-03: off-site navigation in one network round trip plus under 20 ms;
  - NFR-04: all of them holding with 50,000 synthetic patients.
- **CI gates:**
  - p99 server render under 20 ms, excluding the commit, at 50,000 patients (`bench/Ehr.Bench`);
  - page JavaScript under 50 KB compressed, as served;
  - first contentful paint under 500 ms.
  (Only the first is checked by CI today. AGENTS.md lists the other two as gates to add; until a step adds them, measure both by hand for each step and hold them as if CI did. See the decision log.)
- **The benchmark must cover new work.** Add every new page to the render benchmark, and extend the synthetic seed (`bench/Ehr.Seed`) with every new kind of data, so the 50,000-patient numbers include it.
- **Measure and record.** Record p99, page JavaScript size and first contentful paint in the log for each step that changes pages.
- **Don't add weight.** No new JavaScript or CSS framework, no CSS build step, and htmx stays. Draw charts as server-rendered SVG, not with a charting library.
- **Work offline.** Nothing is loaded from another host: everything on-site keeps working without internet (NFR-08).

**Security and data**
- **Every new table** gets:
  - a protection decision (`ExpectedProtection` in `tests/Ehr.Security.Tests/ProtectionTests.cs`);
  - for patient data, row-level security enabled and forced (`db/policies/30-rls.sql`), plus the write-audit trigger (`db/policies/40-audit.sql`);
  - explicit grants in `db/policies/10-grants.sql` and the same privileges in `ExpectedPrivileges`.
- **The existing rules still hold:**
  - permissions are checked in both the app and the database;
  - patient data is read only through the PHI reader (`src/Ehr.Domain/Phi/PhiReader.cs`), which logs every read;
  - portal handlers never accept a patient ID (`tests/Ehr.Architecture.Tests/PortalSubjectTests.cs`);
  - every `SECURITY DEFINER` function sets `search_path` and revokes EXECUTE from PUBLIC.
- **Prove each new rule** with security tests, and prove that everyone else's isolation still holds.
- **Sensitive items stay protected.** Diagnoses, medications and notes marked sensitive (step 2) are never exposed to proxies without the patient's explicit permission, carry their own audit marker, and are shown as `docs/brand` describes. (`docs/brand/README.md` doesn't describe sensitive items yet: step 2 adds that section before it builds the screens.)

**The other surfaces**
- **Off-site:** reads work through the cloud copy and changes are forwarded (`src/Ehr.Web/Forwarding`). While the clinic's server is unreachable, the new feature's save actions are hidden with an explanation. Add the feature to the off-site tests (`OffSiteOutageTests.cs`, `CloudForwardingTests.cs`, and `ShownIfNotSaved` for free text a person typed).
- **Desktop:** if a feature adds chart data, decide whether the offline copy needs it, update the offline copy (`/api/desktop/offline-panel`, `PhiReader.OfflinePanelAsync`, `src/Ehr.Desktop/src-tauri/src/charts.rs`, `frontend/app.js`), and keep NFR-02.

**Tests and docs**
- **Test cases:**
  - extending an existing use case continues its test-case numbering;
  - a new use case gets four functional test cases (successful flow, alternative flow, invalid input, exceptional condition) and two non-functional ones, one file each, in `tests/Ehr.Web.Tests/TestCases/UCnn/`;
  - browser tests walk the keyboard path.
- **Docs:**
  - update `docs/UCnn.md` for each new or changed use case (today only UC 1, 2, 6, 9, 10, 12, 13 and 14 have one: create the file when a step first changes another use case);
  - propose requirement IDs (FR-37 onward, UC 15 onward) in "Requirements to propose";
  - keep the README's description of what's built current.
- **Protected paths:** if a step touches any path AGENTS.md puts under line-by-line review (`db/policies`, authorization attributes and policies, the sync conflict resolver, the dictation prompt and note schema), say so in its commit messages and in the phase's pull request draft.

**Done, for each step**
- `dotnet format --verify-no-changes`, the Release build and the full `dotnet test` pass locally;
- the render benchmark's `--assert` passes;
- `cargo fmt --check` passes when the desktop app changed;
- you've looked at the screenshots in both themes;
- CI is green on the step branch;
- the log entry is written.

## Reference data and synthetic data

**How every code list is handled**
- Each list is loaded as data by a committed `dotnet run` import script, from a pinned source version.
- Its license and attribution are recorded in `docs/expansion/reference-data.md`.
- Nothing is fetched at runtime: on-site keeps working without internet (NFR-08).
- Ask Tahseen before using any source whose license isn't clearly free (NFR-16), and before signing up for anything (for example a UMLS account for SNOMED CT).

**Sources**
- **Drugs and drug allergies:** RxNorm's prescribable content, which NLM publishes without a license.
- **Drug classes, for allergy cross-checks** (a penicillin allergy against an amoxicillin order): MED-RT, distributed by NLM. Confirm its license first.
- **Non-drug allergens** (foods, latex, insect stings, environmental): the FDA's UNII substance list, which is public domain. Add a short curated list of reactions (hives, anaphylaxis, rash and so on) and FHIR's criticality and severity values.
- **Conditions, physical and mental:** ICD-10-CM, which is public domain, including chapter F (mental, behavioral and substance-use disorders).
- **Lab tests:** LOINC (free license, attribution required), using LOINC's Universal Lab Order Codes for the orderable catalog.
- **Units:** UCUM.
- **Vital signs:** LOINC's vital-signs codes, with UCUM units.
- **Reference ranges:** a curated table, labeled as this clinic's ranges, noting the source of each range.
- **Immunizations (optional):** CDC's CVX vaccine codes.
- **Screening questionnaires:** PHQ-9 (depression) and GAD-7 (anxiety), which are free to use, with their LOINC codes.
- **No SNOMED CT or CPT** unless Tahseen approves: both need licenses.

**Synthetic patients**
- The seed is clinically coherent: each patient's conditions come with matching medications, allergies, labs, vitals and visits. For example, a patient with type 2 diabetes is on metformin and has HbA1c results.
- Evaluate Synthea (Apache 2.0) as the generator, or as the model for the seed's own generator. Synthea codes conditions in SNOMED CT, so either map them to ICD-10-CM or ask Tahseen about a UMLS license.
- Whatever generates the data, the 50,000-patient seed keeps `bench/Ehr.Seed`'s rules from AGENTS.md and stays fast enough for CI.

**In the repo today**
- `bench/Ehr.Seed/SyntheticClinic.cs` and `Seeder.cs` generate patients with allergies, medications, diagnoses, visits and test results; `tests/Ehr.Security.Tests/SeedTests.cs` checks the seed's rules.
- The chart tables already have nullable `code` columns, with comments naming the intended code system: ICD-10-CM for `conditions`, RxNorm for `medication_statements`, LOINC for `observations`, and SNOMED CT for `allergy_intolerances`, whose "No known allergies" row uses SNOMED CT code 716186003 (`AllergyIntolerance.NoKnownAllergy`). Step 2 moves allergies off SNOMED CT (see the decision log).
- The order catalog (`ehr.order_catalog_items`, 29 items) uses the app's own `SYN-...` codes, seeded by its migration through `HasData`.
- There are no reference-data tables, import scripts or `docs/expansion/reference-data.md` yet.

## Stop and ask Tahseen before

- spending money or creating any account: hosting, Apple Developer, code-signing certificates, domains, UMLS;
- changing an existing requirement (an FR or NFR) or a documented use case's behavior beyond adding to it (steps 15 and 17 do both);
- adding an AI model, a dataset, a code list, or a dependency whose license isn't clearly free and open (NFR-15, NFR-16), or downloading new model weights;
- committing any recording of a voice;
- anything on main, a force-push, deleting a branch or tag, or changing repo settings;
- meeting a performance or security gate only by cutting scope;
- CI failing for a reason two honest attempts haven't fixed.

When you stop, and also whenever this session's context is running low:
- finish or commit the work in progress;
- push it;
- update the status table and the log, saying exactly where to resume;
- give Tahseen a short summary.

## Use-case owners

These are from the course document. When a step changes someone's area, list them in the step's notes, so Tahseen can give them a heads-up.

| Use cases | Owner | Area |
| --- | --- | --- |
| UC 1, UC 2 | Tahseen Arnan | orders, off-site outage |
| UC 3, UC 4 | Diego Valenzuela | search, chart |
| UC 5, UC 6 | Simar Rekhi | portal registration, results |
| UC 7, UC 8 | Sara Lelo | desktop offline, sync |
| UC 9, UC 10 | Samir Mukshe | dictation service |
| UC 11, UC 12 | Luke Garcia | sign-in, emergency access |
| UC 13, UC 14 | Satwik Yash Padhy | AI review, notes |

## Phases and steps

### Phase 1: finish the specified scope, plus the foundations

**Step 1. Change and discontinue orders** (UC 1, FR-05)

If step 1's own prompt gave more detail, that detail wins.
- **A signed order is never edited in place.**
  - Discontinue records a status of `discontinued`, a required reason, who and when. (Stored as FHIR's request status `revoked`, which the `orders` status constraint already allows, and shown as "Discontinued"; see the decision log.)
  - Change places a new order and discontinues the old one as replaced, in one transaction, linked both ways.
  - Nothing reactivates an order or changes any other column.
- **The database enforces it.** Use the narrowest privilege that works (a column-level UPDATE grant or a `SECURITY DEFINER` function), an RLS policy limited to clinicians who can already order for that patient, and the write-audit trigger.
- **The duplicate check** applies to a replacement but ignores the order it replaces. Dictation still never touches orders (FR-14).
- **Chart screens:**
  - "Change" and "Discontinue…" on each active order, usable from the keyboard;
  - a view of discontinued orders with who, when and why;
  - off-site, these actions are hidden with an explanation.
- **Touches:** `db/policies`, which needs line-by-line review.
- **In the repo:**
  - *Exists:* `ehr.orders` (`src/Ehr.Data/Orders/Order.cs`, `OrderConfiguration.cs`), append-only, always `active`, with `SELECT, INSERT` for `ehr_app`; `orders_clinician_select` and `orders_own_insert` (a clinician, signing as themselves, for a patient on their active panel) in `db/policies/30-rls.sql`; the `ehr.sign_order()` trigger in `db/policies/60-orders.sql` (patient-row lock, active only, duplicate check, signing time); `src/Ehr.Domain/Orders/ClinicalOrders.cs` and `OrderForms.cs`; `PhiReader.OrderFormAsync` and the chart's active orders in `PhiReader.ChartAsync`; the order form `Areas/Clinical/Pages/Patients/Orders/New.cshtml(.cs)` with `js/orders.js`; the chart's "Active orders" section in `Patients/Chart.cshtml`.
  - *New:* columns on `orders` for the discontinuation (reason, who, when) and the two links (`replaces_order_id`, `replaced_by_order_id` or one of them with a unique index), in an EF migration with `schema.sql` regenerated; the discontinue path in `60-orders.sql`, its grant in `10-grants.sql` and `ExpectedPrivileges`; `ClinicalOrders.DiscontinueAsync` and `ChangeAsync`; pages `Orders/Discontinue` and `Orders/Change` (or `New` with `?replaces=`); the chart's discontinued-orders view; the bench's seed and render rows for them.
  - *Tests:* `tests/Ehr.Security.Tests/OrderSecurityTests.cs`, `tests/Ehr.Web.Tests/OrderTests.cs`, `OffSiteOutageTests.cs`, `tests/Ehr.Architecture.Tests/DictationOrdersTests.cs` (must still pass untouched), and `TestCases/UC1/` continuing at TC-UC1-07. `docs/UC1.md` and the README's "Orders can't be changed or discontinued yet".

**Step 2. Editable allergies, medications and diagnoses, with standard codes**
- **Editing:** clinicians add, edit and mark inactive (never delete) allergies (with reaction and criticality, drug and non-drug), medications, and diagnoses as a problem list. History is kept and every change is audited.
- **Codes:** from "Reference data and synthetic data". If step 2a hasn't loaded them yet, start with the minimal subsets this step needs and leave the full import to 2a.
- **Search:** keyboard-first type-ahead search for codes, within the performance budget.
- **Sensitive items:**
  - mark mental-health, substance-use and other sensitive diagnosis categories (by ICD-10-CM range) as sensitive, and allow marking individual items sensitive;
  - record the rule in the decision log;
  - show sensitive items as `docs/brand` describes, so they're never exposed by accident.
- **Banner:** the patient banner shows allergies on every patient page wherever the read stays audited and within the render budget.
- **Offline:** the desktop app's offline copy includes these lists, read-only.
- **Dictation still never changes allergies** (FR-14).
- **Touches:** `db/policies`. **Affects:** UC 4, UC 7.
- **In the repo:**
  - *Exists:* `ehr.allergy_intolerances`, `ehr.medication_statements` and `ehr.conditions` (`src/Ehr.Data/Patients/`), each PHI with forced RLS and the write-audit trigger, `SELECT` only for both runtime roles, with FHIR status columns and a nullable `code`; the chart reads them in `PhiReader.ChartAsync` (`src/Ehr.Domain/Phi/PatientChart.cs`) and shows what the patient has now; the portal home shows the patient's own; the patient banner (`Pages/Shared/PatientBanner.cs`, `_PatientBanner.cshtml`) shows allergies only on the chart and the order form, because the notes, dictation and review pages don't read them; the desktop's offline chart already carries the three lists as the chart shows them.
  - *New:* write grants and RLS write policies for clinicians (panel rule, as for orders); history (a row per version, or a history table written by trigger); `sensitive` on conditions, medications and notes with the ICD-10-CM range rule; the reference tables this step needs (or 2a's); a type-ahead endpoint and its small script; edit forms on the chart; a "Sensitive items" section in `docs/brand/README.md` and its component in `ehr.css`; replacing the SNOMED CT "No known allergies" code.
  - *Tests:* `PatientChartTests.cs`, `PatientWriteTests.cs`, `RowLevelSecurityTests.cs`, `WriteAuditTests.cs`, `ProtectionTests.cs`, `SeedTests.cs`, `TestCases/UC4/` continuing its numbering, the desktop's Rust tests. `docs/UC4.md` doesn't exist yet.

**Step 2b. Sensitive-item and allergy-status fixes** (inserted by Tahseen after step 2; done before step 2a)
- **Allergy status:** a patient's allergy list is either "No allergy information recorded", "No known allergies" or a list of allergies. Clinicians can record "No known allergies", which is audited, and adding an allergy clears it. The banner and chart show the right state. Update the desktop app's offline copy and the tests.
- **Admins and sensitive values:** in the admin audit view, a write to a sensitive item shows that a sensitive item changed and by whom, never its values. The full record stays in the audit store. Add security and UI tests.
- **Sensitive codes:** add T14.91 (suicide attempt), R45.851 (suicidal ideation), Z91.51 and Z91.52 (history of suicidal behavior, history of non-suicidal self-harm), the intentional self-harm poisoning and toxic-effect codes (T36–T65 with "2" as the sixth character), and B20 and Z21 (HIV). Document the full rule in `docs/expansion/reference-data.md`, and migrate existing rows that the new rule marks.
- **Touches:** `db/policies`. **Affects:** UC 4, UC 7, UC 12.

**Step 2a. Reference data and a coherent synthetic seed**
- **Tahseen's decisions for this step (2026-11-18):**
  - *LOINC:* Tahseen downloads it himself into `~/swiftchart-expansion-scratch/reference/loinc/`. Until the files are there, do the rest of 2a without LOINC and mark the LOINC parts as waiting on him. When they are, load the subset the plan needs (lab orderables, results, vitals, PHQ-9 and GAD-7), with LOINC's copyright notice in `docs/expansion/reference-data.md`, carrying each term's EXTERNAL_COPYRIGHT_NOTICE where it has one.
  - *Drug classes:* not MED-RT, and not RxClass (its FDASPL classes come from MED-RT, and data.gov lists RxClass under ODbL). Use the FDA's Established Pharmacologic Class indexing files and DailyMed's mapping files to RxNorm, downloaded once at import time from a pinned date, plus a small curated table of which classes cross-react, each entry citing its source. If that join can't be made reliably, stop and tell Tahseen.
  - *Interactions (for step 8):* DDInter 2.0 as the only source, at a pinned download version, mapped to RxNorm. Its license is CC BY-NC-SA 4.0: keep the imported table in its own folder with that license and DDInter's citation, and record it in `docs/expansion/reference-data.md` and the decision log as an accepted exception to NFR-16. Show each warning's severity and DDInter's management advice, and label the checks in the UI as decision support for this demo system, not a complete clinical reference.
- Load every source in "Reference data and synthetic data" with its import script, and write `docs/expansion/reference-data.md`.
- Upgrade the seed so all 50,000 patients have coherent coded conditions, medications, allergies, labs, vitals and visits, with a realistic share of sensitive items.
- Keep the benchmark and CI times in budget, and record seed size and seeding time in the log.
- Steps 2, 8, 9, 12 and 13e build on this.
- **In the repo:** see "In the repo today" under "Reference data and synthetic data". *New:* reference tables (not PHI: grants only, as `order_catalog_items`), import scripts under `tools/`, the pinned source files or their checksums, and the order catalog's move from `SYN-...` codes to LOINC and RxNorm where they apply. Decide where the loaded rows live so that tests, the bench and the stack all have them without the internet (see the decision log when made).

**Step 3. Clinician inbox**
- One inbox per clinician for items that need action. Item types plug in: messages (step 5), results to acknowledge (step 7), refill requests (13d), and emergency-access reviews for admins (step 18).
- A count in the app bar, keyboard navigation, and each item linking to its patient and source.
- It starts empty; each later step adds its own item type.
- **In the repo:** nothing exists. *New:* an inbox table (PHI: it names patients), a page under `Areas/Clinical/Pages/Inbox`, the app bar link and count in `Pages/Shared/_Layout.cshtml` (the count is a read on every clinical page: keep it inside the render budget, and off the pages `PatientSearchTests` holds to an exact list of requests), `LayoutTests.cs`, a new use case with its six test cases, and bench rows.

**Step 4. Appointments** (FR-17)
- **Scheduling:** staff create, reschedule and cancel appointments, with a day view per clinician, linked to visits.
- **Portal:** patients see their upcoming and past appointments. There's no self-scheduling yet.
- **Placeholder:** replace the portal's "Appointments and messages will appear here." as appointments and messages arrive.
- **Affects:** UC 6.
- **In the repo:** `ehr.encounters` holds visits (FHIR Encounter statuses, including `planned`), read-only at runtime; the placeholder is at `Areas/Portal/Pages/Index.cshtml`; `docs/UC6.md` lists appointments (FR-17), messages (FR-18) and proxy access (FR-19) as what's missing from UC 6. *New:* an appointments table, clinical schedule pages, the portal's list, seed and bench rows, `TestCases/UC6/` continuing at TC-UC6-07.

**Step 5. Secure messaging** (FR-18)
- Threads between a patient and their care team. The clinician side runs through the inbox, and every read is audited.
- The notification email says only that there's a new message and links to the portal. It contains no health details.
- **Affects:** UC 6.
- **In the repo:** email goes through `IEmailSender.SendAsync(to, subject, body, EmailAction, …)` (`src/Ehr.Domain/Accounts/IEmailSender.cs`, `src/Ehr.Web/Email/`); the care team is the clinicians with the patient on their panel (`ehr.patient_assignments`). *New:* thread and message tables, portal pages, the inbox item type, the off-site "messages cannot be sent until the server is back" wording `docs/UC6.md` already names.

**Step 6. Proxy access** (FR-19)
- **The relationship:** a proxy relationship, recorded by the clinic or granted by the patient, has a scope and an expiry.
- **Acting for someone:** the proxy's session chooses whose record they're acting for. Every portal page, query and RLS policy uses that person, and the audit log records both the proxy and the patient.
- **Sensitive items:** proxies don't see sensitive items (step 2) unless the patient explicitly allows it, and that permission is enforced in the database and audited.
- **This changes the rule that a patient sees only their own row.** Update RLS, `PortalSubjectTests`, `PortalResultsIsolationTests` and the security tests deliberately, and prove isolation still holds for everyone else.
- **Touches:** `db/policies` and authorization. **Affects:** UC 5, UC 6, UC 11.
- **In the repo:** the portal's subject is `ehr.current_patient_id()` (`db/policies/30-rls.sql`), set from the session by `UserContextMiddleware` and `UserContextInterceptor`; every `*_own_select` policy keys on it; `PortalSubjectTests` is in `tests/Ehr.Architecture.Tests`, `PortalResultsIsolationTests` in `tests/Ehr.Security.Tests`.

At the end of phase 1, every FR in the course document is built. Write the phase pull request draft. Also note that this is a good time for step 21's deployment, but don't start it without Tahseen.

### Phase 2: close the clinical loops

**Step 7. Order → result → acknowledgment**
- Results attach to the order that requested them, and the order moves to resulted.
- The ordering clinician gets an inbox item. Acknowledging it records who and when.
- Abnormal results, judged against step 2a's reference ranges, are flagged and sorted first. The portal keeps showing results as it does now.
- **Affects:** UC 1, UC 6.
- **In the repo:** `ehr.observations` already has `order_id` (same patient enforced), `reference_low`/`reference_high` and `issued_at`; nothing at runtime writes observations. "Resulted" is FHIR's `completed` in `OrderStatus`. The portal's results are `PhiReader.ResultsAsync`, `Results/ResultExplanation.cs` and `Areas/Portal/Pages/Results.cshtml`.

**Step 6b. Proxy-only portal accounts, and choosing a record after sign-in** (inserted by Tahseen on 2026-11-19; done after step 7 and before step 8)
- **Proxy-only accounts.** A patient, or a clinician with the patient on their panel, invites an email address. The invitation link is single-use and expires. It creates a portal account with no record of its own and the grant already attached. Such an account sees only Access and the records shared with it. Existing proxies who are patients keep working as now.
- **Choosing after sign-in.** An account with grants in force chooses whose record to view after signing in ("My record" first, when it has one), as UC 6's alternative course describes. The switch under Access stays.
- **Security.** It touches `db/policies` and authorization: list them for review, and extend the security tests to prove a proxy-only account can't reach anything else.
- **Touches:** `db/policies` and authorization. **Affects:** UC 5, UC 6 (heads-up for Simar in the step's notes).
- **In the repo:** portal accounts are `ehr.patient_users` with `patient_id NOT NULL` and a unique index on it; the Patient policy requires the `EhrClaims.PatientId` claim (`EhrPolicies`), which `PatientClaimsPrincipalFactory` adds; `ehr.own_patient_id()` reads that column; `PortalSubject` and `PortalSubjectFilter` (step 6) decide a page's patient; registration is UC 5's (`Areas/Portal/Pages/Account`), with single-use codes; step 6's limit "a proxy must already be a patient" is what this step lifts.

**Step 6c. Security follow-ups** (inserted by Tahseen on 2026-11-19; done after step 6b and before step 8)
- **Invitations.** The accept page looks up a single invitation by its token through a `SECURITY DEFINER` function owned by the table's owner (`search_path` set, `EXECUTE` revoked from `PUBLIC` and granted only to the app role). The app role's direct access to the invitations table is revoked. Only a hash of each token is stored.
- **Enrollment codes** get the same treatment, since they follow the same pattern. They are short enough to type, so guessing is the risk, not timing: confirm failed entries are rate-limited, add a test if not, record their length and entropy, and store them as a keyed hash (HMAC-SHA-256 with a server-side key from configuration, never committed) so a leaked table can't be brute-forced offline. Invitation tokens stay plain SHA-256.
- **Prove with security tests** that the app role, with nobody signed in, can't list invitations or enrollment codes, and that the function returns only the one row for a valid, unexpired token.
- **Emailed links.** The portal's base address is a required setting outside Development: the app refuses to start without it, with a clear message. A link (password reset, confirmation, invitation) is never built from the request's Host header. Tests for both. The bug is also on `main`: note it for Luke in the phase 2 pull request description.
- **Touches:** `db/policies` (list it for review).
- **In the repo:** `ehr.proxy_invitations` and `ehr.patient_enrollments` are Internal tables read directly by `ProxyInvitations`, `PatientRegistration` and `PatientEnrollments`; both already hold only SHA-256 hashes (`EnrollmentCodes.Hash`). Links are built by `Links.Build(baseUrl, request, path)` in 12 places, for staff set-up links as well as the portal's, from `SiteAddresses` (`Ehr:Links`), falling back to the request when the setting is empty. Anonymous account POSTs are limited per client address by `AccountRateLimits`.

**Step 8. Ordering safety checks**
- **The checks:** a medication order is checked against the patient's coded allergies, including drug-class cross-sensitivity from step 2a's class data, and against active medications for known interactions.
- **The warning** names the problem. Overriding it requires a reason, which is recorded and audited.
- **The data** stays local, with no outside service (NFR-08). Interactions come from DDInter 2.0, supplemented where it has gaps (decision log, 2026-11-19, "Step 8, DDInter's gaps"): the ONC high-priority pairs from Wikidata's "significant drug interaction" (P769) statements, and a small hand-written table citing FDA labels. Each warning names its source. **Before building:** measure coverage and report to Tahseen (decision log, 2026-11-19). **Advice shown:** the severity, one short generic line per severity level written for this project, and a link to the pair's DDInter page for when there is internet. Never scrape DDInter's website.
- **Affects:** UC 1.
- **In the repo:** the order form already loads the patient's allergies for its banner (`PhiReader.OrderFormAsync`) and has the duplicate warning with a required reason (`duplicate_reason`), the pattern the override follows.

**Step 9. Vital signs and trends**
- **Recording vitals:** blood pressure, heart rate, temperature, respiratory rate, SpO2, weight and height, with BMI calculated. They're stored as observations with LOINC codes and UCUM units, and shown in the chart as a flowsheet.
- **Trend charts:**
  - for vitals and lab values, on the chart and on the portal's results;
  - server-rendered SVG, colored with tokens;
  - with each reference range shown;
  - each with a data table as the accessible alternative.
- **Affects:** UC 4, UC 6.
- **In the repo:** blood pressure and heart rate are columns of `ehr.encounters` today (`SystolicMmHg`, `DiastolicMmHg`, `HeartRatePerMinute`), shown with the chart's latest visit and in the desktop's offline chart; `ehr.observations` holds lab results only and has no category column. This step moves vitals to observations and migrates those columns.

**Step 10. Addenda to signed notes**
- **Sensitive notes (placed here by Tahseen, 2026-11-18):** a note or addendum can be marked sensitive, and it follows the same rules as other sensitive items. It must be done before step 13a.
- Signed notes stay locked. An addendum is a new signed text attached to the note, shown in order with its author and time, and audited.
- The desktop app's offline view shows addenda too.
- **Affects:** UC 14.
- **In the repo:** `ehr.clinical_notes` is insert-only, with `ehr.reject_clinical_note_change()` in `db/policies/50-clinical-notes.sql`; the signed note page is `Patients/Notes/View.cshtml`. The desktop app syncs drafts, not signed notes: its offline chart would need the signed notes first.

### Phase 3: connect to other systems

**Step 11. Read-only FHIR R4 API**
- **Resources:** Patient, AllergyIntolerance, MedicationStatement, Condition, Observation (results and vitals) and Appointment, using the codes from step 2a.
- **Access:** staff only, through the existing sign-in and authorization. Every read goes through the PHI reader and is audited, RLS applies, and sensitive items follow the same rules as everywhere else.
- **Testing and dependencies:** responses are validated against the FHIR specification in tests. Prefer small, well-known open-source libraries, and note any you add in the decision log.
- **In the repo:** the entities are already shaped on FHIR R4 with its status codes. APIs live under `src/Ehr.Web/Api/` with a policy per group (`Program.cs`, `EhrPolicies`); a new group is an authorization change, which needs line-by-line review.

**Step 12. Simulated lab**
- A small service or command that receives orders and returns synthetic results through an authenticated inbound endpoint.
- Values come from step 2a's reference ranges and fit each patient's conditions, with delays and occasional abnormal values.
- It replaces seeded-only results for new orders, and is audited.
- **In the repo:** the cloud audit shipper (`Api/Audit/CloudAuditApi.cs`, a shared key) is the existing example of a service-to-service inbound endpoint; the local stack is `infra/compose/compose.yaml`.

### Phase 4: portal extras

All of these use the portal's sizes, work for proxies (step 6) under the sensitive-item rules, and put no health details in emails.
- **13a. Visit summaries,** written for patients from signed notes, with no internal codes.
- **13b. Download my record,** as a FHIR bundle (step 11) and as a printable page.
- **13c. Who viewed my record,** from the audit log: staff name and role, when, and which part, with emergency access marked and proxies included.
- **13d. Refill requests.** A patient asks to renew an active medication, which creates an inbox item. The clinician approves it, creating an order through the step 1 and step 8 checks, or declines it with a message.
- **13e. Pre-visit forms:**
  - questionnaires tied to appointments (step 4), whose answers appear in the chart for that visit;
  - PHQ-9 and GAD-7 built in, scored automatically, with the score and its severity band in the chart, and trended with step 9's charts;
  - their answers are sensitive items.

**Affects:** UC 5, UC 6.

**In the repo:** patients can't read `ehr.audit_events` (admins only, `audit_events_admin_select`) or `ehr.clinical_notes`, so 13a and 13c each need a new, narrow way for a patient to read their own (a policy or a `SECURITY DEFINER` function: `db/policies`). The portal has no print styles yet.

### Phase 5: dictation and AI

**Step 14. SOAP notes and visit-type templates**
- **The note format:** Subjective, Objective, Assessment and Plan, plus templates per visit type. Objective can include step 9's vitals.
- **What changes:** the structuring prompt, validation, the review page, the editor, signing and desktop drafts.
- **The eval:** regenerate the committed dictation eval results and keep their CI check green.
- **Touches:** the dictation prompt and note schema. **Affects:** UC 9, UC 10, UC 13, UC 14.
- **In the repo:** notes are History, Assessment and Plan today (`src/Ehr.Domain/Notes/NoteContent.cs`, columns on `clinical_drafts` and `clinical_notes`, `src/Ehr.Dictation/schema/note-fields.schema.json` and `structuring-prompt.md`, the sidecar, `eval/`, `eval/results/latest.json`, the desktop's drafts, whose plain-text Body contract UC 14 kept). Regenerating the eval needs the models running on this machine or the server; the accepted baseline is recorded on the server machine. If that can't be done here, stop and ask.

**Step 15. Dictation proposals beyond the note.** Ask Tahseen before starting: it changes FR-14.
- The proposed design: dictation may propose orders or allergy changes only as drafts that a clinician reviews and signs through the normal step 1, 2 and 8 flows.
- Nothing is placed automatically, and the same grounding checks as for note fields apply.
- **In the repo:** `tests/Ehr.Architecture.Tests/DictationOrdersTests.cs` and the eval gate (`test_an_order_or_allergy_change_shown_fails`) enforce FR-14 today.

**Step 16. Ambient listening.** Ask Tahseen before adding any model.
- **Capture:** conversations are recorded only after a consent prompt, with speakers separated.
- **The note** is drafted into the same review flow as dictation.
- **Testing** uses only synthetic voices generated locally. No recording is committed without Tahseen's say-so.

**Step 17. Languages beyond English.** Ask Tahseen before starting: it changes NFR-14, and it needs a new speech model, whose license has to fit NFR-15.

### Phase 6: desktop, security and operations

**Step 18. Session timeouts and emergency-access review**
- An idle timeout with a warning, for staff and for the portal.
- An admin workflow to mark each emergency access as reviewed, with a note, through the inbox.
- It can run any time after step 3.
- **In the repo:** cookie lifetimes are set in `src/Ehr.Domain/Accounts/AccountServiceCollectionExtensions.cs`; emergency access is `ehr.break_glass_accesses` and the flagged rows at `/admin/audit` (`Admin/Audit.cshtml`), with no reviewed state. Admins have no access to patient data: the review shows the patient by ID, as the audit page does.

**Step 19. Passkeys**
- Passkeys as a second factor, using the framework's built-in support if this .NET version has it. Recovery paths don't change.
- **Touches:** authentication.
- **In the repo:** .NET 10 (`global.json`), ASP.NET Core Identity 10.0.13. Confirm Identity's passkey support and its schema needs when the step starts.

**Step 20. Desktop distribution**
- **Auto-update** with Tauri's updater. Its signing key is never committed: ask Tahseen where it's kept.
- **Remote wipe:** revoking a desktop session makes the app delete its local data and key the next time it connects.
- **OS code signing:** ask Tahseen first, because it costs money (NFR-16).
- **In the repo:** `ehr.desktop_sessions` and `src/Ehr.Domain/Accounts/DesktopSessions.cs`; the app's key and store are `src-tauri/src/key.rs` and `store.rs`. The bundle is still named "EHR" (`productName`) with identifier `com.example.ehr`; renaming either moves the app's data folder, so decide it here with Tahseen.

**Step 21. Real deployment and operations.** Ask Tahseen before creating anything: there are accounts to create, costs, and NFR-16's budget of roughly $50.
- Until he approves, prepare these only as code and docs:
  - deployment documentation;
  - monitoring and alerting with open-source tools;
  - an automated restore drill that proves point-in-time recovery works.
- **In the repo:** `infra/compose` (primary, replica, Barman with point-in-time restore, simulated WAN), `infra/tailscale` (empty), and CI's stack job, which already checks replication and backups.

## Requirements to propose

New FR IDs (FR-37 onward) and use cases (UC 15 onward), as each step defines them, so the course document can be updated later.

| ID | Step | Proposed requirement |
| --- | --- | --- |
| FR-37 | 1 | The system shall let clinicians discontinue a signed order with a reason, and change one by signing a replacement, without ever editing or deleting the signed order; it shall keep who, when and why. (Extends UC 1; test cases TC-UC1-07 to TC-UC1-12.) |
| FR-38 | 2 | The system shall let clinicians add and change a patient's allergies, medications and diagnoses, each picked from a standard code list, never deleting an entry and keeping the history of every change. (Extends UC 4; test cases TC-UC4-07 to TC-UC4-12.) |
| FR-48 | 9 | The system shall let a clinician record vital signs (blood pressure, heart rate, temperature, respiratory rate, oxygen saturation, weight and height) as coded observations, calculate the body mass index, show them as a flowsheet, and show vital signs and laboratory values over time as charts with their reference ranges and an equivalent table, to clinicians and to the patient in the portal. |
| FR-47 | 8 | The system shall check a medication order against the patient's coded allergies, including drugs of the same or a cross-reactive class, and against the patient's active medications for known interactions; shall show each warning with its severity and source; and shall require and record a reason before an order with a serious warning is signed. (Extends UC 1: test cases TC-UC1-18 to TC-UC1-20.) |
| FR-46 | 6b | The system shall let someone who is not a patient act as a proxy through a portal account with no record of its own, created from a single-use, expiring invitation sent to the address the access names, and shall ask an account with shared records whose record to view after sign-in. (Extends UC 18 and UC 6: test cases TC-UC18-07, TC-UC18-08 and TC-UC6-13.) |
| FR-45 | 7 | The system shall attach each result to the order that requested it and mark the order resulted, flag values outside their reference range and list them first, notify the ordering clinician, and record who acknowledged the results and when. (Extends UC 1: test cases TC-UC1-13 to TC-UC1-17, TC-UC6-12 and TC-UC15-07.) |
| UC 18, FR-44 | 6 | Give someone proxy access to a record. The system shall let a patient, or the clinic for them, give another portal account access to the patient's record with a scope and an expiry, and let either remove it; shall hold back sensitive items unless the patient allows them; and shall record everything a proxy does under both the proxy and the patient. (FR-19 covers a proxy choosing whose record to view; test cases TC-UC18-01 to TC-UC18-06, TC-UC6-10 and TC-UC6-11.) |
| UC 17, FR-43 | 5 | Answer a patient's message. The system shall deliver a patient's message to the inbox of each clinician on their care team, let any of them reply, record every reading of a conversation, and never put a message's content in an email or in the administrators' audit view. (FR-18 covers sending and receiving; test cases TC-UC17-01 to TC-UC17-06, TC-UC6-08 and TC-UC6-09.) |
| UC 16, FR-42 | 4 | Schedule an appointment. The system shall let clinicians book, reschedule, cancel (with a reason) and check in appointments, show any clinician's day, and never double-book a clinician. (FR-17 covers the portal's list; test cases TC-UC16-01 to TC-UC16-06, and TC-UC6-07.) |
| UC 15, FR-41 | 3 | Work through the inbox. The system shall give each staff member one list of the items that need their action, show how many on every clinical page, link each item to its patient, and record when each is done. (Test cases TC-UC15-01 to TC-UC15-06.) |
| FR-40 | 2b | The system shall show an administrator reviewing the audit log that a sensitive item changed and who changed it, and never the item's values. |
| FR-39 | 2 | The system shall treat mental-health, substance-use and other designated diagnoses, and any item a clinician marks, as sensitive: left out of a chart until asked for, with each such view recorded. |

## Phase merges

For each phase:
- its tag and branch;
- a pull request title and description;
- the protected paths it touched;
- a summary of its tests.

### Phase 1: finish the specified scope, plus the foundations

- **Tag and branch:** `expansion-phase-1`; branch `expansion/phase-1` (the same commit as `expansion-step-06`). Open the pull request from `expansion/phase-1` into `main`. Tahseen opens it; nothing here did.
- **Title:** Expansion phase 1: order changes, editable chart lists, reference data, the inbox, appointments, messages and proxy access

**Description**

Phase 1 of `docs/expansion/PLAN.md`: steps 0 to 6. With it, every functional requirement in the course document is built.

What it adds:
- **Orders** can be changed and discontinued, with a reason, and never deleted (UC 1; FR-37 proposed).
- **Allergies, medications and diagnoses** are edited from the chart with coded entries, history and "no known allergies"; sensitive items are marked, shown apart, and hidden from administrators in the audit view (UC 4, UC 12; FR-38 to FR-40 proposed).
- **Reference data:** ICD-10-CM, RxNorm, LOINC, UNII, FDA drug classes and DDInter 2.0 loaded from pinned files by `tools/reference`, and a synthetic seed whose patients' problems, medications and results fit together. `docs/expansion/reference-data.md` has the sources and licenses. DDInter is CC BY-NC-SA 4.0, an accepted exception to NFR-16 for this non-commercial project.
- **A clinician inbox** (UC 15, FR-41 proposed), **appointments** with a day schedule (FR-17; UC 16, FR-42 proposed), **secure messages** between a patient and their care team (FR-18; UC 17, FR-43 proposed) and **proxy access** to the portal (FR-19; UC 18, FR-44 proposed).

Protected paths, for line-by-line review by someone other than Tahseen (AGENTS.md):
- `db/policies`: `10-grants.sql`, `30-rls.sql`, `40-audit.sql`, `60-orders.sql` changed; `65-chart-lists.sql`, `70-inbox.sql`, `80-appointments.sql`, `90-messages.sql`, `95-proxy-access.sql` new. About 1,180 lines added. The largest single change in meaning is step 6's: `ehr.current_patient_id()` now answers with another patient under a proxy grant the database has checked. Three functions run as their owner: `ehr.deliver_message`, `ehr.shown_audit_details` and `ehr.proxied_records`, with owner-only policies on the tables they read or write.
- Authorization: `Program.cs` (area policies for the new folders, the new filters), `UserContext.cs` and `UserContextInterceptor.cs` (a second setting, `app.acting_grant`), `UserContextMiddleware.cs`, `PortalSubject.cs`, `InboxCountFilter.cs`, the gate in `Areas/Clinical/Pages/Patients/Messages/MessagePage.cs`, and `CloudAuditEvents.cs`.
- Tests about authorization that were changed on purpose: `PortalSubjectTests` (a portal request may name a thread or a proxy grant, never a patient; portal pages take their patient from `PortalSubject`), `HandledOffSiteTests` (the proxy's record switch is handled on the cloud app and records nothing), `PortalResultsIsolationTests` (a proxy's attempt added beside the patient's). `AGENTS.md` was updated to say what a portal session now sees.
- Not touched: the sync conflict resolver, the dictation prompt and the note schema.

Migrations: `OrderChanges`, `ChartLists`, `AuditSensitive`, `ClinicalCodeSearch`, `ReferenceTables`, `InboxItems`, `Appointments`, `AppointmentVersion`, `Messages`, `ProxyGrants`.

Tests: 780 in the solution; 750 pass and 30 performance cases are skipped unless a benchmark results file is given (CI's benchmark job checks them). New: test cases TC-UC1-07 to 12, TC-UC4-07 to 13, TC-UC12-07, TC-UC6-07 to 11, and six each for UC 15 to UC 18, each with a keyboard-only browser test; security tests against the database for every new table (`OrderChangeSecurityTests`, `ChartListSecurityTests`, `SensitiveAuditSecurityTests`, `InboxSecurityTests`, `AppointmentSecurityTests`, `MessageSecurityTests`, `ProxyAccessSecurityTests`). The render benchmark has a row for every new page, on-site and off-site, and `--assert` passes at 50,000 patients.

Generated files (`db/reference/**`, the migrations' designer files and the model snapshot) are marked `linguist-generated` in `.gitattributes`, so GitHub collapses them: about 260,000 of this pull request's added lines are reference data. `expansion/phase-1` was fast-forwarded once after its tag to add that rule and the plan's notes on it (step 6a); the tag `expansion-phase-1` is one merge behind the branch.

Each step was also read by a review agent before it was merged; what it found and what was left open is in the decision log. That does not replace the human review above.

Open questions for Tahseen are listed at the end of the log's step entries; the main ones: DDInter's downloads have no management advice and no license text on the download page; whether a proxy may be someone who isn't a patient of the clinic; the limits chosen for messages and grants; whether administrators should still see audit values for orders, results and note text.

Heads-up to use-case owners: Diego (UC 3/4: the chart gained sections and editing), Simar (UC 5/6: the portal gained appointments, messages, Access and a changed app bar), Luke (UC 11/12: the audit view hides sensitive values and message text), everyone (the app bar gained Schedule and Inbox).

### Phase 2: close the clinical loops (draft, to be completed at the end of the phase)

- **For Luke (UC 11, UC 12), about `main`:** before step 6c, every link the app emails (password reset, confirmation, staff set-up) was built from the request's Host header whenever `Ehr:Links` wasn't set, so a request with a forged Host could make the app email someone a link to another site, carrying their token. That code is on `main` today (`Links.Build` in `src/Ehr.Web/Accounts/Links.cs`). Step 6c makes both base addresses required outside Development and removes the fallback. Until this phase is merged, a deployment from `main` must set `Ehr__Links__ClinicalBaseUrl` and `Ehr__Links__PortalBaseUrl`.

## Decision log

One entry per decision: date, step, the decision, and why.

- **2026-11-18, step 0. The plan starts from main at `b4ad35b`,** which includes the brand work (pull request 56). `expansion/integration` was created from it. No expansion branch or tag existed before, and step 1 had not started.
- **2026-11-18, step 0, correction (step 1). A discontinued order is stored with FHIR's request status `revoked`** and shown as "Discontinued". `Order.Status` holds FHIR R4 request status codes behind a check constraint (`OrderStatus.All`); FHIR has no `discontinued` code, and `revoked` is its code for a stopped request. "Resulted" (step 7) is `completed` for the same reason.
- **2026-11-18, step 0, correction (performance). Only the render benchmark is a CI gate today.** Page JavaScript size and first contentful paint are listed in AGENTS.md as gates to add "as the code they check appears"; nothing in `ci.yml` checks them. Each step measures both and records them in the log. Adding the two checks to CI is a follow-up for Tahseen to place: it isn't one of the plan's steps.
- **2026-11-18, step 0, correction (sensitive items). `docs/brand/README.md` says nothing about sensitive items.** Step 2 writes that section, in the guide's style, before building the screens, and the later steps follow it.
- **2026-11-18, step 0, correction (allergy codes). The allergy table was built for SNOMED CT codes,** and its "No known allergies" row uses SNOMED CT 716186003. The plan allows no SNOMED CT without approval, so step 2 codes allergens with RxNorm and UNII and replaces the "No known allergies" code with a marker of the app's own. No other SNOMED CT content is in the repo.
- **2026-11-18, step 0, correction (vitals). Blood pressure and heart rate are columns of `ehr.encounters`,** not observations. Step 9 moves them, and until then steps that show vitals read the visit's columns.
- **2026-11-18, step 0, correction (docs and tests). Not every use case has a `docs/UCnn.md` or a `TestCases/UCnn/` folder.** Docs exist for UC 1, 2, 6, 9, 10, 12, 13 and 14; test-case folders for UC 1 to 6, 11 and 12. A step that changes a use case without one creates it for what the step adds, and doesn't backfill the rest.
- **2026-11-18, step 0, note (requirement IDs). The plan's FR and NFR numbers are the ones the repo's test cases and docs use** (FR-05 orders, FR-14 dictation never orders, FR-16 to FR-19 the portal). They were matched to the course document in pull requests 52 to 55.
- **2026-11-18, step 0. Which CI run a step records.** The status table's record is itself a commit, so its own run can't be in it. A step records the run of its last commit before the record; the record commit changes only this file. After the merge, the run on `expansion/integration` is checked too, and a failure there is fixed before the next step starts.
- **2026-11-18, step 0, note (README). The README's "Not built yet" paragraph is out of date** (it still lists dictation's screens). Step 1 corrects it while updating the orders paragraph.

- **2026-11-18, step 1. Discontinuing is a column-level UPDATE grant with a trigger, not a `SECURITY DEFINER` function.** `ehr_app` may update `status`, `discontinue_reason` and `replaced_by_order_id` only; `orders_panel_update` limits rows to patients on the clinician's active panel; `ehr.discontinue_order()` runs as the caller and holds the update to active → revoked, once, with a reason, setting who and when. Nothing runs with more privilege than the caller, which is the narrowest that works. `RuntimeRoleTests` now lists every column grant (`ExpectedColumnPrivileges`) and fails on any other, because `has_table_privilege` doesn't see them.
- **2026-11-18, step 1. A change is enforced as a whole at commit.** A deferred constraint trigger refuses a replacement whose old order isn't discontinued and naming it; the discontinue trigger accepts "replaced by" only from the order that names this one, signed by the same clinician. A change keeps the order's type.
- **2026-11-18, step 1. Any clinician with the patient on their panel may discontinue or change a colleague's order,** and is the one recorded. That is the existing rule for who may order for a patient; limiting it to the signer would leave orders nobody on duty can stop.
- **2026-11-18, step 1. The reason for a change is optional;** without one the old order's reason reads "Replaced by a changed order". The reason for discontinuing is required, in the page and in the database.
- **2026-11-18, step 1. The chart links to discontinued orders and doesn't load or count them,** so the chart's render time is unchanged. The off-site sentence for the two actions is its own ("Changing or discontinuing an order needs the clinic's server."), beside the existing one that TC-UC1-04 and the brand guide quote.
- **2026-11-18, step 1. Orders aren't in the desktop app's offline copy** (they weren't before), so the desktop app didn't change. Whether offline charts should list orders is open for UC 7's owner.
- **2026-11-18, step 1. An independent review agent reads each step's protected-path changes before the merge.** It isn't the human line-by-line review AGENTS.md requires, which still happens on the phase pull request.

- **2026-11-18, step 2, design (decided before building; change it only with a new entry).**
  - *Reference codes.* One table, `ehr.clinical_codes` (kind: condition, medication or allergen; system: `icd-10-cm`, `rxnorm` or `unii`; code; display; category for allergens; sensitive). Not PHI: grants only. Its rows are generated SQL in `db/reference/`, written by `dotnet run tools/reference/import.cs` from pinned source files (checksums in the script; the files are downloaded to a folder outside the repo, never fetched at runtime). The migration job, the tests' database and the benchmark apply `db/reference/*.sql` after `db/policies`. Step 2 loads a curated subset (the codes the synthetic clinic uses and common primary-care ones); step 2a widens the filter to the full lists and adds the search index that needs.
  - *Sources used in step 2, none needing an account:* RxNorm Current Prescribable Content of 2026-11-02 (SCD for medications, IN for drug allergens), ICD-10-CM FY2027 code descriptions, FDA UNII (non-drug allergens). Attribution goes in `docs/expansion/reference-data.md`, started in this step.
  - *Writes.* `ehr_app` gets INSERT and UPDATE on `allergy_intolerances`, `medication_statements` and `conditions`, never DELETE. RLS: a clinician, for a patient on their active panel (the orders rule). A trigger on each refuses a change of the row's patient or ID.
  - *History.* One PHI table, `ehr.chart_history`, written only by a `SECURITY DEFINER` trigger on the three tables (who, when, the row before and after). Clinicians read it; nothing at runtime may insert, update or delete it. The write-audit trigger still records every change for admins.
  - *Sensitive rule.* A diagnosis is sensitive when its ICD-10-CM code is in F01–F99 (mental, behavioral and neurodevelopmental disorders, which include substance use F10–F19), A50–A64 (sexually transmitted infections), B20 and Z21 (HIV), O04 and Z33.2 (termination of pregnancy), T74 and T76 (abuse and neglect), X71–X83 and R45.851 (self-harm and suicidal ideation), or when a clinician marks it. A medication is sensitive when a clinician marks it (a rule by drug class waits for step 2a's class data). The database sets the flag from the code, in a trigger, so the app can't clear it for a code in a sensitive range.
  - *Showing sensitive items.* The server leaves them out of the chart unless the clinician asks ("Show sensitive items", a link that reloads the chart). Asking is recorded as its own audit action, `read_sensitive`. They never appear in the patient banner. `docs/brand/README.md` gets a "Sensitive items" section.
  - *Notes marked sensitive* (named in "Every step must meet these") are built with this step if it stays inside the budget, and otherwise recorded as a follow-up for Tahseen to place: the note pipeline is UC 13 and UC 14's.
- **2026-11-18, step 2. A view that shows sensitive items is one audit event, `read_sensitive`, in place of `read`,** not one of each. Two events doubled the cloud app's queue write and took the off-site chart over 20 ms with the commit included; one event says the same thing.
- **2026-11-18, step 2. An entry that is sensitive now is sensitive throughout its history.** The history page leaves out every change to it, including those made before it was marked. (The first build judged each change by the row as it was then; the review found the leak.)
- **2026-11-18, step 2. Existing diagnoses are marked on every pass of the migration job** (`65-chart-lists.sql`), not once in a migration: so widening the rule later reaches the rows already there. It lifts forced row-level security for that one statement, inside a transaction, as the seed does.
- **2026-11-18, step 2. The desktop app's offline copy never holds sensitive items.** It is told how many it wasn't given, and says so. A laptop is the likeliest place for a chart to be seen by the wrong person.
- **2026-11-18, step 2. New entries must be coded; old ones keep their names.** The form refuses a name that isn't a reference code, but an entry recorded before the codes keeps its name and its lack of a code until someone changes the name. The seed's own entries are still uncoded; step 2a's coherent seed codes them.
- **2026-11-18, step 2. The code search is a page of the clinical area (`/codes`),** not a new API group, so the area's existing policy covers it and no authorization code was added. Suggestions use the browser's own list (`datalist`), filled by a 1 KB script: no listbox widget to build or maintain.
- **2026-11-18, step 2. Admins see the values of sensitive entries in the write audit's details** at `/admin/audit`, as they see any changed value there. That page is the one place admins see patient data, by the team's earlier decision; it isn't a `read_sensitive`. Tahseen may want to revisit it.
- **2026-11-18, step 2. Not built, for Tahseen to place:** marking a *note* sensitive (the note pipeline is UC 13 and UC 14's; it fits step 10, addenda, or step 6, when proxies first need it); a button to record "No known allergies"; and three ranges the review suggested for the sensitive rule (T14.91 suicide attempt, self-harm poisoning codes in T36–T65, Z91.51 history of self-harm).
- **2026-11-18, Tahseen's decisions after step 2.**
  - *A step 2b is inserted before 2a:* allergy status ("No known allergies" recorded by clinicians, cleared by adding an allergy), admins never see a sensitive item's values in the audit view, and more sensitive codes (T14.91, R45.851, Z91.51, Z91.52, T36–T65 with "2" as the sixth character, B20, Z21).
  - *LOINC:* Tahseen creates the loinc.org account and downloads the files himself. They were in the scratch folder the same day.
  - *Drug classes:* the FDA's Established Pharmacologic Class indexing files joined to RxNorm through DailyMed's mapping files; not MED-RT, not RxClass. A small curated cross-reactivity table, each entry cited. Stop if the join isn't reliable.
  - *Interactions:* DDInter 2.0 only, pinned, mapped to RxNorm. **An accepted exception to NFR-16:** DDInter is CC BY-NC-SA 4.0, free but non-commercial and share-alike. Its table lives in its own folder with that license and DDInter's citation, and must be replaced if the project ever goes commercial. Warnings show severity and DDInter's management advice, and the UI labels the checks as decision support for this demo system, not a complete clinical reference.
  - *Sensitive notes* go in step 10, before step 13a.
  - *Review:* the review agent doesn't replace the human line-by-line review of `db/policies`; that happens on phase 1's pull request. Keep listing the protected paths in commit messages.
- **2026-11-18, step 2b. Admins are kept from sensitive values in the database, not only on the page.** The runtime roles lost SELECT on `audit_events.details` (a column grant on every other column). The page asks `ehr.shown_audit_details`, a `SECURITY DEFINER` function that answers only an admin and never for an event marked sensitive or for the lists' history copies. The trigger marks an event when the item written is sensitive, and marks the item's earlier events on the write that makes it sensitive. Nothing unmarks an event; a trigger refuses any other change to one, from anyone. The full record stays in `details`.
- **2026-11-18, step 2b. The self-harm poisoning rule takes two forms of the code.** Tahseen's wording was "T36–T65 with 2 as the sixth character". Checked against the FY2027 code file, that is 870 of its 954 intentional self-harm codes in the range; the other 84 are codes of a four-character subcategory, where the 2 is the fifth character and an X fills the sixth (T36.92XA). The rule takes both and matches exactly those 954.
- **2026-11-18, step 2b. "No known allergies" is kept to one state by the database:** refused beside an active allergy, and made inactive when one is added, with the patient's row locked so two writes at once can't both get through. The lock is a row lock: an advisory lock per patient ran PostgreSQL out of shared memory when the seed loaded 50,000 patients in one transaction, which CI caught.
- **2026-11-18, step 2b. The recurring passes that mark existing rows lift forced row-level security only on the two lists,** never on `audit_events`, where the owner's own policies are enough: locking the audit table first could deadlock with a page recording its read (found by the review agent).
- **2026-11-18, step 2b, open for Tahseen.** (1) The audit page still shows the values of writes to tables with no sensitive flag: orders (a buprenorphine order, an HIV test), results, and note text. Notes get a flag in step 10; orders and results have none planned. (2) The review suggested two more self-harm codes for the rule: R45.88 (nonsuicidal self-harm) and T71.1x2/T71.2x2 (asphyxiation, intentional self-harm).
- **2026-11-18, step 2a, design and progress (update as parts land).** Source files are in `~/swiftchart-expansion-scratch/sources/` (RxNorm, ICD-10-CM, UNII, `epc/` for the FDA class files, `ddinter/`) and `~/swiftchart-expansion-scratch/reference/loinc/` (LOINC 2.83, Tahseen's download); checksums are pinned in `tools/reference/import.cs` as each is used. `~/swiftchart-expansion-scratch/reference-data-research.md` has the notes.
  - *Part A, done and pushed:* the full ICD-10-CM and RxNorm lists (100,847 codes) in `db/reference/10-clinical-codes.sql`, as a COPY block; `tests/Ehr.Testing/ReferenceData.cs` loads such files for the tests; the code search uses three indexes, the third PostgreSQL's own text search (no extension, so nothing for `ehr_owner` to install). 136 product names over 300 characters are left out. Chart entry names widened to 300.
  - *Part B, next: LOINC.* Tables (all reference data, grants only): `observation_codes` (a LOINC result or vital-sign code, its name, kind lab, vital or survey, UCUM unit, this clinic's reference range with the source of the range, and LOINC's EXTERNAL_COPYRIGHT_NOTICE where the term has one); `order_result_codes` (which results an orderable produces: CBC to hemoglobin, white cells and platelets); `questionnaires` (PHQ-9 44249-1 and GAD-7 69737-5 as one JSON definition each: items with their LOINC codes, answer options with scores, severity bands, the copyright notice). Lab orderables are LOINC's Universal Lab Orders Value Set (1,519 codes), loaded into `order_catalog_items`.
  - *The order catalog.* It keeps its table. The 29 `SYN-...` items stay as the clinic's common items (the tests and the benchmark order them by name) and gain a standard system and code where one applies (LOINC for the eight labs, RxNorm for the eight medications). LOINC's lab orders and RxNorm's clinical drugs are added as items under their own codes. The order form keeps its list of the common items and gets the rest from the code search as the clinician types.
  - *Part C: drug classes.* `drug_ingredients` (a RxNorm product to its ingredients, from RXNREL: product `constitutes` SCDC, SCDC `ingredient_of` IN) and `drug_classes` (ingredient to FDA Established Pharmacologic Class name). Route A, Tahseen's: DailyMed's `pharmacologic_class_mappings` and `rxnorm_mappings`, through labels with exactly one ingredient, keeping a class that at least 80% of the ingredient's labels carry. Route B, for drugs sold only in combinations: the class file's UNII through the FDA UNII list's RXCUI column. Classes are keyed by name; the class files' own codes are MED-RT's and aren't stored. `drug_class_cross_reactions`: a small curated table, each row citing its source.
  - *Part D: DDInter,* in `db/reference/ddinter/` with its license and citation, mapped to RxNorm ingredients by name. Its files hold two drugs and a level only (see the step 2b log entry): the management advice is an open question for Tahseen.
  - *Part E: the seed.* The synthetic clinic becomes coherent and coded: each patient's conditions bring matching medications, labs, vitals and visits, with real codes from the reference lists. Synthea is the model, not the generator (it codes conditions in SNOMED CT).
  - *Part F:* `docs/expansion/reference-data.md` (LOINC's notice, each license), tests, benchmark rows, seed size and time in the log.
  - *Not planned unless Tahseen asks:* CVX (the plan calls it optional).
- **2026-11-18, step 2a, as built.** Parts A to F of the design above are done as written, with these choices. The code and catalog searches use PostgreSQL's own text search for words inside a name: no extension to install. The order catalog keeps the 29 common items, which the form lists, and finds the 13,490 standard ones by search. Reference ranges are this clinic's own, labeled as such and as not validated. A drug class is kept when four in five of an ingredient's single-ingredient labels carry it; classes are stored by name, never by MED-RT's code. The cross-reactivity table has two rows, both from the 2022 drug allergy practice parameter. Synthea is the seed's model, not its generator. The review agent wasn't run: the only protected-path change is one read-only grant.
- **2026-11-18, step 2a, open for Tahseen.** (1) DDInter's download files hold two drug names and a level, and no management advice: step 8 can show severity only, unless he chooses otherwise. (2) No license text was found on DDInter's download page; `db/reference/ddinter/LICENSE.md` records CC BY-NC-SA 4.0 as his decision, to be confirmed. (3) Only 81,610 of DDInter's 222,383 rows are usable (the rest are of unknown level or name a drug that isn't a RxNorm ingredient). (4) 3,941 of RxNorm's 5,886 ingredients have no FDA class, so get no class check.
- **2026-11-18, step 3. The inbox is every staff member's, not only clinicians'.** Step 18 puts emergency-access reviews in admins' inboxes, so the table, the policies and the page take any staff account. An admin's items show a patient by ID only and record no read, as the audit page does; so an item's summary must stay free of clinical detail.
- **2026-11-18, step 3. No runtime role can create an inbox item yet.** Each later step (5, 7, 13d, 18) adds the narrowest grant or trigger for its own kind. The four kinds are already in the table's check constraint.
- **2026-11-18, step 3. Marking done is a column grant on `done_at` with a trigger,** the pattern of step 1: once, at the database's time, nothing else changes, nothing reopens. Items are never deleted.
- **2026-11-18, step 3. The app bar's count is one index lookup made by a page filter inside the page's transaction,** on full clinical GET pages only. It isn't an audited read: it says nothing about a patient. `LayoutTests.cs`, which the plan named, wasn't changed: the UC 15 test cases cover the link and the count.
- **2026-11-18, step 3. Not in the desktop app.** Nothing creates items yet, and an offline inbox would need its own sync; revisit with step 5.
- **2026-11-18, step 4, design (decided before building).**
  - *Table* `ehr.appointments` (PHI): patient, clinician, start, length in minutes, reason, status (FHIR's `booked`, `cancelled`, `fulfilled`, `noshow`), the cancellation reason, the visit it became (`encounter_id`), who booked it and when. Never deleted.
  - *Who.* Clinicians schedule, for a patient on their panel, with any clinician (admins have no access to patient data, and there is no front-desk role yet: a follow-up for Tahseen). Clinicians read every appointment, as they read charts; a patient reads their own.
  - *Changes.* Reschedule changes the start and length; cancel needs a reason; "Check in" creates the visit (an encounter, in progress) and marks the appointment fulfilled, linking the two; "No-show" marks it. A trigger holds updates to those moves and refuses two booked appointments that overlap for one clinician, locking the clinician's row so two bookings at once can't both get through.
  - *Pages.* `/schedule`: a clinician's day, any clinician and any day. `/patients/{id}/appointments/new`, and `/appointments/{id}` to reschedule, cancel, check in or mark a no-show. Portal: upcoming and past on the home page and at `/portal/appointments`; no self-scheduling.
  - *Times* are UTC, as everywhere else in the app.
  - *Use cases.* Scheduling is a new use case, UC 16 (six test cases). The portal's list extends UC 6 (TC-UC6-07 on).
  - *Desktop.* Not in the offline copy for now: the offline chart is for reading a record and drafting, not for the day's schedule. Open for Sara.
- **2026-11-19, step 4, as built (beyond the design above).** Every change to an appointment names the version it began from (a `version` column the database counts), so a stale page can't undo a colleague's reschedule. The guard refuses any isolation level but READ COMMITTED, as the order trigger does. Only a disabled clinician account can't be booked with; one locked for minutes can. The appointment page gates before it reads. Checking in is the first thing at runtime to write a visit: `ehr_app` gained INSERT on `encounters` for panel patients.
- **2026-11-19, step 4, open for Tahseen.** (1) A colleague's day hides a visit's reason unless the patient is on the viewer's panel; the plan said only "a day view per clinician", and the stricter reading was chosen. (2) Only clinicians schedule: there is no front-desk role. (3) Times are UTC. (4) A visit that check-in starts is never finished by the app. (5) Appointments aren't in the desktop app's offline copy.
- **2026-11-19, step 5, design (decided before building).**
  - *Tables* (PHI): `ehr.message_threads` (patient, subject, when started) and `ehr.messages` (thread, patient, who sent it: the patient or a staff member, the text, when sent, when the other side first read it). Never edited or deleted.
  - *Who.* A patient writes to their care team: the clinicians with them on their active panel. Any of those clinicians reads and replies, and can start a thread. Clinicians read every thread in the database, as they read charts; the pages ask for the panel or emergency access, and only a panel clinician writes. A patient reads and writes only their own.
  - *Inbox.* A patient's message puts one item in each care-team clinician's inbox, written by a `SECURITY DEFINER` trigger (no runtime role may insert inbox items). When any of them replies, the thread's open items are marked done for all of them. The item's summary says only that a patient sent a message.
  - *Reads are audited:* opening a thread is a recorded read of that thread, for staff and for the patient, on-site and through the cloud queue.
  - *Email.* A staff message sends the patient "You have a new message" with a link to the portal: no subject, no text, no names.
  - *Admins.* The audit page never shows a message's text or a thread's subject: `ehr.shown_audit_details` leaves both tables out, as it does sensitive items.
  - *Pages.* Portal: `/portal/messages`, `/portal/messages/new`, `/portal/messages/{thread}`. Clinical: the chart's Messages section, `/patients/{id}/messages/new`, `/patients/{id}/messages/{thread}`; the inbox item links to the thread.
  - *Off-site.* Threads are read from the cloud copy; sending is forwarded. With the clinic's server unreachable the forms are replaced by "Messages can't be sent until the clinic's server is back.", the wording UC 6 names.
  - *Use cases.* The patient's side extends UC 6 (TC-UC6-08 on). The clinician's side is a new use case, UC 17 (six test cases).
  - *Not built:* attachments, messages between staff, and a count of unread messages in the portal's app bar.
- **2026-11-19, step 5, as built.**
  - *Limits on a patient* (from the review): the database holds a patient to 10 new conversations a day and 30 messages an hour, so one account can't bury a care team's inboxes. The numbers are mine; say if the clinic wants others.
  - *One notice email per unread run:* a reply sends its email only when the patient has no other staff message unopened. Without that, a fourth reply in an hour would have been dropped by the per-address email cap, along with the patient's password-reset emails.
  - *The portal's rule about IDs in addresses* (`PortalSubjectTests`) now allows a `threadId`, never a patient's ID, with a test that such a page keeps its patient out of the request. Row-level security is what stops another patient's thread being read. The review calls the companion test weak (any non-public `PatientId` property satisfies it); left as is, for the phase's line-by-line review.
  - *Policies for the tables' owner alone* were added on `message_threads`, `inbox_items` and `patient_assignments`, so the delivery trigger can work under forced row-level security. The review confirmed the runtime roles can't become `ehr_owner` and can't call the function.
  - *Different from A3's UC 6:* a message is started from the Messages page, not from a result.
  - *Not in the desktop app:* no offline copy of messages; reading them offline would need its own sync and a decision on keeping message text on a laptop.
  - *Left open:* the chart lists the five latest conversations and the portal 50, with no page of older ones; a reply clears the inbox item of a patient message that arrived a moment before it; the notice email is queued just before the reply's transaction commits; nobody is notified when a patient with no care team writes; a patient's database context could read which staff account wrote a message (the pages show "Your care team"), a rule older than this step.
- **2026-11-19, step 6, design and as built.**
  - *The grant* (`ehr.proxy_grants`, PHI): patient, the proxy's portal email, scope (`record` or `record-and-messages`), whether sensitive items are included, who gave it and when, an expiry (always; at most a year), and when and by whom it was revoked. Addressed to an email, not an account, so the form can't be used to find out who has an account.
  - *Acting.* The session's cookie names the grant; the app passes it to the database as `app.acting_grant`; `ehr.current_patient_id()` answers with the grant's patient only while the grant is this account's, not revoked and not expired, and otherwise with nobody. One record at a time: not the proxy's own beside it, and not another shared record.
  - *Sensitive items and messages* are held back by row-level security unless the grant includes them; only the patient, as themselves, turns sensitive items on.
  - *Who gives and removes:* the patient for their own record, or a clinician with the patient on their panel. A proxy never passes access on.
  - *The rule that changed.* "A patient sees only their own row" is now "a portal session sees only the one patient it is looking at". `PortalSubjectTests`, `PortalResultsIsolationTests`, `HandledOffSiteTests` and AGENTS.md were changed to say so.
  - *From the review:* the names for "records you can view" come from a function that answers only for the caller's address (the first build let a proxy read the whole patients row of every record shared with them); the 20-grant limit counts only grants in force that the patient gave; a proxy's reading doesn't mark messages read; plain ASCII addresses only, so a look-alike address can't match a grant; the cookie is cleared at sign-in.
  - *Numbers that are mine:* expiry at most a year, offered as 30 days, 90 days, 6 months, 1 year; 20 grants in force per patient.
  - *Different from A3's UC 6:* the record is chosen under Access, not on a picker straight after sign-in.
  - *Left open:* a proxy must be a patient of the clinic (portal accounts belong to patients); the portal shows no diagnoses or medications, so the sensitive rule has nothing on a page yet; results and appointments have no sensitive mark; two scopes only; the reply notice goes to the patient, not the proxy; switching records is not an audit event; off-site a removed proxy can read until the cloud copy catches up; `PortalSubjectTests` allows `grantId` by name without a companion check. **Step 10 (sensitive notes) must apply `ehr.portal_sees_sensitive()` to notes if notes reach the portal.**
  - *Not in the desktop app:* it has no portal.
- **2026-11-19, Tahseen's decisions before the phase 1 pull request.**
  - *Generated files are collapsed in GitHub's review* (step 6a): `.gitattributes` marks `db/reference/**`, `db/migrations/*.Designer.cs` and `EhrDbContextModelSnapshot.cs` as `linguist-generated`. A migration's own file and `schema.sql` stay visible, since reviewers read them; so does DDInter's `LICENSE.md`. `expansion/phase-1` is fast-forwarded to include it, with a normal push. The tag `expansion-phase-1` stays where it was (moving a tag needs a force-push): the branch is what the pull request uses.
  - *DDInter's license* is CC BY-NC-SA 4.0, stated on https://ddinter.scbdd.com/terms/; cited in `docs/expansion/reference-data.md` and `db/reference/ddinter/LICENSE.md`.
  - *DDInter coverage is measured before step 8 is built.* The downloads are eight files, ATC groups A, B, D, H, L, P, R and V. Check which medication pairs in the order catalog and the seed have no DDInter entry, especially where both drugs are cardiovascular, anti-infective or nervous-system drugs (a statin with clarithromycin, an SSRI with tramadol). Report the numbers and notable gaps to Tahseen before deciding anything. Don't scrape DDInter's website.
  - *Interaction advice* (step 8): show the severity, one short generic line per severity level that we write ourselves, and a link to the pair's DDInter page for when there is internet. This answers the open question that DDInter's files carry no management advice.
  - *Step 6b is added,* after step 7 and before step 8: proxy-only portal accounts by single-use, expiring invitation, and choosing whose record to view after sign-in. Its text is under phase 1's steps.
- **2026-11-19, DDInter coverage, measured and reported to Tahseen; nothing decided.** The eight files hold only pairs where at least one drug has an ATC code in groups A, B, D, H, L, P, R or V; pairs where neither does are not in the files (checked in the raw downloads, so it is not the import). 1,299 of the catalog's 2,581 medication ingredients appear at all (50%; 67% weighted by catalog products). All 16 seeded ingredients appear; 22 of their 120 pairs have an entry. Present, mostly as major: statins with clarithromycin, erythromycin, itraconazole, amlodipine, amiodarone, diltiazem; warfarin with ibuprofen, fluconazole, clarithromycin, amiodarone; clopidogrel with omeprazole; sulfonylureas with quinolones. No entry: any SSRI with tramadol; SSRI with SNRI (0 of 40 pairs); fluoxetine with amitriptyline; oxycodone with alprazolam; ACE inhibitor with ARB (0 of 88); lisinopril with spironolactone; beta blocker with calcium channel blocker (0 of 160); digoxin with amiodarone or verapamil; antipsychotic with antiarrhythmic (0 of 228); citalopram with azithromycin; sertraline with linezolid; tizanidine with ciprofloxacin; sildenafil with nitroglycerin. Lithium has no entries here because DDInter names it "Lithium carbonate": check the name mapping in step 8. The measuring script and its report are in the scratch folder (`ddinter-coverage/`). **Step 8 waits for Tahseen's decision on these gaps.**
- **2026-11-19, step 7, design and as built.**
  - *Who files results.* Until step 12's simulated lab, a clinician with the patient on their panel types in what the lab reported, on the order's results page. Step 12 should file through the same database rules.
  - *What the database holds a result to* (`75-results.sql`): it answers an open lab order of the same patient, for one of the tests that order's catalog item reports, once per test; its name, unit and reference range are the database's own (`ehr.observation_codes`), never the client's, because the range decides what is abnormal; it is final and released at once.
  - *Resulted* is FHIR's `completed`. The order becomes completed with its first result, as the clinician filing it; the order trigger allows that one more change only when a final result exists. A completed order can't be discontinued or changed.
  - *Results in parts:* more can be added until the order is acknowledged; then nothing.
  - *Inbox.* The clinician who signed the order gets one item, "Results are in", with no values, written by a trigger that runs as its owner. Abnormal ones sort first, worked out when the inbox is read. A result item has no "Mark done": it is done by acknowledging.
  - *Acknowledgment* is its own table (`ehr.result_acknowledgments`): once per order, by any clinician with the patient on their panel, with who and when set by the database. It clears the inbox item.
  - *Abnormal* means a value outside the test's reference range from step 2a: one adult range per test.
  - *The portal is unchanged:* a filed result is released at once and shows as results always have, before anyone acknowledges it. Say if results should wait for acknowledgment instead.
  - *Dropped while building:* a rule that a sample is collected after its order was signed; orders are sometimes entered after the draw.
  - *Not in the desktop app:* results aren't in its offline copy.
  - *From the review* (no way in through the app itself; each needed a crafted request or raw SQL as the app's role): the filing rules are now tied to the role the apps connect as, not to who a statement says is signed in, because one statement can sign itself out between its own rows while row-level security asks who the user is only once; an acknowledgment carries what the page showed and is refused if a result arrived since; a result's inbox item can't be marked done directly, in the database, not just by hiding the button; filing and acknowledging need READ COMMITTED, as signing an order does; when the signer's account is disabled or no longer a clinician's, the item goes to the patient's panel instead.
  - **For the human review of `db/policies`:** that first point is a class, not one trigger. Every trigger that decides something from `ehr.current_user_id()` row by row sits beside policies that ask `(SELECT ehr.current_user_id())` once for the statement. It needs raw SQL as `ehr_app`, which can already name any user, so it is defence in depth; the older triggers (orders, chart lists, appointments, messages, proxy grants) were not changed here.
  - **For step 9:** vital signs are observations with no order, which `ehr.file_result` refuses from the apps as written. Step 9 has to widen it deliberately.
  - *Left open:* only 12 lab catalog items have result fields; the chart lists eight resulted orders with no page of older ones; the inbox checks the oldest 500 waiting results for abnormal values; no correcting or withdrawing a filed result; the item goes to the signer even if they left the panel; times are UTC.
- **2026-11-19, step 6b, design and as built.**
  - *A portal account's patient is optional.* A proxy-only account has none: no patient claim in its session, and in the database it is nobody's patient, so by itself it reads no row of any patient table and can share nothing. Step 6's rules do the rest unchanged, since a grant is addressed to an email.
  - *The invitation is the access email.* Giving access, by the patient or the clinic, already emails the address. That email now carries a button that creates an account: a code stored only as a hash, used once, good for 7 days (my number). Opening the link proves the address, so the account starts confirmed. The page asks for a name and a password, never an address.
  - *One email whoever the address belongs to,* so it still doesn't reveal who has an account. Someone who has one signs in; if they follow the link anyway, they are told to sign in.
  - *The portal's policy* now accepts any portal account (a new `ehr:portal` claim, or the old patient claim for sessions from before). What an account sees is decided by `PortalSubjectFilter`, which sends a session with no record to "Whose record?" before any other handler runs, and by row-level security.
  - *After sign-in* the portal goes to `/portal/choose`, which lists "My record" first and goes straight on to it when nothing is shared. Opening that page always starts from the account itself.
  - *A name for the account* (`patient_users.display_name`), typed when accepting, is how staff see who wrote for a patient: "Name (proxy)".
  - *From the review:* an unconfirmed account that names the invited address now gives it up when the invitation is accepted (before, the page told the mailbox's owner they "already had an account", steering them into confirming one registered by someone else, who could then sign in and see what was shared with that address); access is refused for addresses with characters a portal account can't have; the invitation link is in the email's text as well as its button; the code is claimed in one statement.
  - **For Tahseen to decide:** `ehr.proxy_invitations` is an Internal table without row-level security, as enrollment codes are, because the accepting page runs before anyone is signed in. Each row holds the invited address beside its grant's ID, so a session of the app's database role can list who was invited (not to whose record) with nobody signed in. Closing that needs the lookup moved into a function that runs as its owner.
  - **Older than this step, noted by the review:** when `Ehr:Links:PortalBaseUrl` isn't set, emailed links (password reset, confirmation, and now invitations) are built from the request's Host header. The stack sets it; a deployment must.
  - *Left open:* opening "Whose record?" (a GET) drops the choice of record, by design; an invitation isn't cancelled when its access is removed (the account it creates sees nothing); no way to turn a proxy-only account into a patient's; the clinic has no list of proxy-only accounts; no keyboard-only browser test, as these are portal pages.
- **2026-11-19, step 7a, added while working (not in Tahseen's plan): the chart's render time.** After step 6b's CI run failed once and then passed at 19.7 ms against the 20 ms gate, the chart was measured: one page was about sixteen database round trips, and that is most of its time (CI runners take about twice this Mac for each). The chart's first query now also says which occasional sections have anything in them (upcoming appointments, conversations, resulted orders, signed notes, the reader's drafts), and the page skips the queries for the empty ones. Nothing a clinician sees changes, and the audit event is the same one. It helps the usual chart, where most of those are empty; a patient with every section filled costs what it did. If the chart reaches the gate again, the next step is fetching several sections in one round trip, which is a bigger change to how the PHI reader is written.
- **2026-11-19, Tahseen's decisions on step 6b's open points: step 6c.**
  - *Step 6c is added,* after 6b and before step 8; its text is under phase 1's steps.
  - *Looking up by hash is enough; no constant-time comparison is added.* A row is found by the hash of the token through the table's unique index, so the stored hash always equals the computed one, and comparing them again would check nothing. The comparison the index makes is of hashes of random tokens: someone guessing can't choose an input whose hash starts with a wanted prefix, so its timing gives them nothing to work with.
  - *Enrollment codes are short enough to type, so guessing is the risk.* They are 12 characters from a 32-symbol alphabet, about 60 bits, valid for 14 days. Failed entries are limited per source: registration is one of the anonymous account POSTs held to 30 per 5 minutes per client address ("Too many attempts"); a test for failed codes at `/portal/register` is added, since the existing one posts to sign-in. There is nothing per account to count against: no account exists until a code is accepted, and a wrong code names no patient. Online guessing is not practical at that rate; a leaked table could be searched offline, so codes are stored as HMAC-SHA-256 with a key from configuration. Invitation tokens are 160 random bits and stay plain SHA-256.
  - *Emailed links:* agreed to make both base addresses (clinical and portal) required outside Development and to remove the Host-header fallback from every link the app sends, staff set-up links included.
- **2026-11-19, Tahseen's decision on step 8, DDInter's gaps: supplement DDInter rather than ship around the gaps.**
  - Add the ONC high-priority pairs from Wikidata's "significant drug interaction" (P769) statements, which are CC0, pinned to a dated query result committed under `db/reference`, and mark them "high priority".
  - For the remaining important pairs in the order catalog that neither source covers, add a small hand-written table. Each entry cites the drug's FDA label (DailyMed, public domain) and quotes or paraphrases its Drug Interactions section.
  - Each warning names its source: DDInter, ONC via Wikidata, or the FDA label.
  - Report coverage of the catalog's medication pairs before and after, and the pairs still uncovered.
  - Then build step 8 as planned, and keep going with step 9 after it.
- **2026-11-19, step 6c, as built.**
  - *Both tables are closed to the app roles.* `ehr_app` and `ehr_read` have no privilege on `ehr.patient_enrollments` or `ehr.proxy_invitations`. Six functions in the new `db/policies/45-codes.sql` run as the tables' owner, set `search_path`, and are executable only by the app roles: issue, find and redeem for each. Find and redeem answer for one code; redeem marks it used in the same statement, so two posts of one code can't both succeed. Issuing is checked in the database too: an enrollment code by a clinician, an invitation only for access the signed-in user gave, to the address that access names (the caller no longer passes an address).
  - *No rows needed migrating:* both tables already held only hashes.
  - *Enrollment codes are keyed from now on* (HMAC-SHA-256, `Ehr:Enrollment:Key`, required on the on-prem app outside Development). **A code issued before this is stored as a plain SHA-256 and can't be converted without the code itself.** Lookup therefore accepts either form, so codes already handed out keep working until they expire, 14 days at most; after that no plain hash can match anything. Tahseen was asked whether to invalidate outstanding codes at deploy instead and hasn't said; this is the gentler of the two and is one line to tighten (drop the second argument of the two functions).
  - *Rate limits:* failed codes at `/portal/register` are limited per client address (now tested, with the invitation page). Nothing per account exists to count against before a code is accepted.
  - *Links:* `Ehr:Links:ClinicalBaseUrl` and `PortalBaseUrl` are checked at startup; outside Development a missing one stops the app with the setting's name and what it is for. `Links.Build` no longer takes the request. In Development an unset address is the local one from `launchSettings.json`, not the request's.
  - *New setting for deployments and the stack:* `EHR_ENROLLMENT_KEY` in `infra/compose/.env` (in `.env.example`; an older `.env` needs the line added).
  - *From the review* (no security defect found; it confirmed that an existing database loses the old grants, that nothing can be listed or guessed through the functions, and that issuing is stricter than before, when any app session could plant a known code): registration now uses the code up first and undoes that on every path that doesn't register, so several registrations with one code at once get "not valid" instead of an error page; **only the on-prem app holds the enrollment key**, since the cloud app forwards registration and has a copy of the codes' table, where the key beside it would undo its purpose; the base addresses must be exactly a site's address, https outside Development, with no spaces, user name, path, query or fragment.
  - *Left open:* the app roles can still read PostgreSQL's row counters for the two tables (how many codes exist and how often they are used, never a row); the panel check for issuing an enrollment code stays in the app, as the database's rule is "a clinician", matching how charts are read.
- **2026-11-19, step 8, as built.**
  - *Coverage, before and after* (catalog: 2,581 medication ingredients). Pairs between catalog ingredients with a warning: 78,166 from DDInter alone; 78,587 with the supplements (398 more from ONC, 23 from labels). Of the 18 gap pairs reported earlier, ONC covers 2 and the hand-written table the other 16.
  - *Still uncovered, reported to Tahseen.* Labels speak of classes and the table lists pairs, so classmates of a written pair are not covered: ACE inhibitor with angiotensin receptor blocker 1 of 88 catalog pairs, beta-blocker with calcium channel blocker 2 of 160, SSRI with SNRI 0 of 40, antipsychotic with antiarrhythmic 18 of 228, opioid with benzodiazepine 58 of 280. Examples with no warning: venlafaxine or duloxetine with tramadol, enalapril with valsartan, atenolol with diltiazem, carvedilol with verapamil, quetiapine with sotalol, sertraline with venlafaxine, lithium with furosemide or losartan, enalapril with spironolactone.
  - *Two ways to close those, for Tahseen to choose:* (a) Wikidata's statements also cite a second source, a 2022 reference set of clinically relevant interactions (*Scientific Data*; CRESCENDDI), CC0 like every Wikidata statement: 1,869 new pairs for the catalog, 10 of the 18 gap pairs. (b) Write the label rows at the level the labels speak, class with class (every ACE inhibitor with every angiotensin receptor blocker, citing the label that says so), using the FDA classes already loaded. Neither is done.
  - *The ONC pairs* are the Wikidata statements whose reference is the ONC paper: 1,798 statements, 717 pairs between RxNorm ingredients.
  - *The label table:* 23 pairs from ten FDA labels fetched from DailyMed's public service and read; each row paraphrases the label closely and names it with its set ID. The level on those rows is this project's reading (major where the label contraindicates, has a boxed warning or reports a serious outcome; moderate where it says to monitor or adjust; minor otherwise).
  - *DDInter's pair pages:* the download files don't give their addresses, and its website isn't read (one attempt to look at a page's address pattern was refused by this session's permissions and not retried). A DDInter warning links to DDInter's interaction checker. **If Tahseen has the address pattern for a pair's page, the link can be made exact.**
  - *Where the check lives:* in the database. `ehr.order_safety_warnings` finds the warnings; the signing trigger calls it, keeps what it found on the order (`safety_warnings`) whatever the client sent, and requires `safety_override_reason` for serious ones. The order form calls the same function, so it shows what the trigger will require.
  - *Mine to confirm:* **only serious warnings need a reason** (an allergy, a related class, a major or high-priority interaction). Moderate and minor interactions are shown and kept without one: DDInter alone marks metformin with lisinopril "moderate", and a reason demanded for everything is a reason typed without reading. The plan's sentence "overriding it requires a reason" is kept for the serious ones.
  - *Notes that don't stop the order are still shown before it is signed.* The first build signed straight through a moderate interaction, so nobody saw it (found when taking the screenshots). Now the first "Sign order" brings the form back with the notes and the cursor on the button, and the second signs.
  - *One general line of advice for each level,* written for this project, and the disclaimer on every medication order form, as decided.
  - *From the review,* all fixed with tests unless noted: **the benchmark crashed** (it signs lisinopril for seeded patients who take losartan, now a major pair; CI's benchmark job failed on it, and the ordinary test run doesn't include the benchmark); **an administrator could read the name of a medication marked sensitive** in an order's audit details, so what the order keeps now says "a medication marked sensitive"; **sharing an FDA class with an allergy stopped far too much** (a prednisone allergy flagged 380 catalog products; a heparin allergy flagged the drugs used instead of heparin), so it is now a note unless the curated cross-reactivity table names the class; a combination product no longer interacts with itself; bosentan's ONC pairs were being dropped because Wikidata's code for it isn't the ingredient's (717 pairs now, from 709); two label rows' levels didn't follow the file's own rule; the hand-written files were hidden in GitHub review by step 6a's rule; the discontinue trigger's "never edited" comparison now includes the two new columns.
  - *Class allergies, as now built:* an allergy to another drug of the same class stops the order only for the classes the curated table names (penicillins, cephalosporins, penems). The plan's step 2a note said a class always cross-reacts with itself; that held for those and not for FDA's mechanism classes.
  - *Left open:* the chart doesn't yet show an order's override reason; adding a medication at the same instant an order is signed can slip past the check; medications "on hold" are not counted as taken; an item with no drug code (the albuterol inhaler) can't be checked; food allergies aren't checked against drugs; no dose, renal, age or pregnancy checks.
  - *Not in the desktop app:* it places no orders.

- **2026-11-19, step 9: how vital signs are stored and shown.** Mine, none of it asked of Tahseen.
  - *Vital signs are observations.* `ehr.observations` gains `category` (`laboratory` or `vital-signs`, FHIR's names) and a nullable `encounter_id`. The three columns on `ehr.encounters` are gone; the migration copies their values into observations first (292,730 of each on the local stack), lifting forced row-level security for the owner while it copies and pausing the audit trigger, because moving a value is not a clinical write.
  - *The database decides what a vital sign is.* `ehr.file_result` (`db/policies/75-results.sql`) now accepts an observation with no order only when its category is `vital-signs` and its code is one of the nine vital-sign codes; it sets the name, unit and reference range itself, refuses a future time, a value of zero or less, or another patient's visit, and releases it at once. No new grant or policy: the existing panel-insert policy applies.
  - *Body mass index is the database's arithmetic,* from the weight and height recorded for the same moment, whatever number the client sends, and refused without them.
  - *Released to the patient at once,* like a filed result (still open with Tahseen from step 7: results reach the portal before acknowledgment).
  - *A set belongs to the visit it was taken during.* The form doesn't ask which visit: the database ties a vital sign to the patient's held visit that began in the twelve hours before it was taken (fifteen minutes after, at most), the latest of them, and to none otherwise. The chart's latest visit and the offline copy read a visit's signs that way. The twelve hours are my number.
  - *Bounds:* each sign has a least and a most that no person is outside of, the same in the form and the trigger (the body mass index 1 to 500, checked after it is worked out). They are my numbers, wide on purpose: they stop slips of the keyboard, not unusual patients.
  - *A set sent twice is recorded once:* a unique index on patient, code, time and visit for vital signs (migration `VitalSignOnce`), since nothing can be taken back.
  - *The portal reads results and vital signs apart,* so no number of vital signs can crowd a result off the page (the first build read both under one limit; the review caught it).
  - *Rolling back `VitalSigns`* returns the three values to the visits and deletes the vital-sign observations, which the older schema would show as laboratory results; signs not taken at a visit are lost by a rollback.
  - *A vital sign can't be corrected yet.* The apps have no UPDATE or DELETE on observations. A wrong entry stays, and a new set can be recorded. "Entered in error" for observations is not built: **open with Tahseen** whether it should be, and in which step.
  - *Trend charts* are SVG drawn in a Razor partial (`_Trend.cshtml`), styled only by classes that use tokens; the reference range is a band; a value outside it differs by shape (a diamond) as well as color; a sentence and a table of the values go with every chart. At most the latest 24 values per chart.
  - *On the chart page:* the newest value of each sign, and a link to the vital signs page. The chart makes one query for the patient's newest 120 vital-sign values and takes both the short flowsheet and each visit's blood pressure and heart rate from it. The full flowsheet (six sets side by side) and the trend charts are on that page, an order's results page (each test with earlier values) and the portal's results page. **For Tahseen to confirm:** the plan says the flowsheet is "in the chart" and trends are "on the chart"; I read that as the clinician's chart area, and kept the charts themselves off the chart page for its speed. Say if they should be on the page itself.
  - *Units* are stored as UCUM and shown the usual way for four of them (mmHg, °C, kg/m², per minute).
  - *Reference ranges for vital signs* are the adult ones in `db/reference`, applied to everyone. Ranges by age are not built; a child's values will be flagged against adult ranges.
  - *Desktop:* the offline copy keeps a visit's blood pressure and heart rate, with the API's contract unchanged (read from observations now). The full flowsheet is not in the offline copy.
  - *CI's web app job has 30 minutes, up from 15.* Step 9's first run was cancelled at the limit with no test failing: the step before it finished in 14 minutes 43 seconds, and this one adds tests. No test was removed or skipped to fit.
  - *The entry form is first on the page:* with it at the bottom, the starting cursor scrolled the page past the flowsheet (found in the screenshots).

## Log

One entry per session or step: date, step, what was done, measurements (p99, page JavaScript size, first contentful paint), follow-ups, and where to resume.

- **2026-11-18, step 0.** Created `expansion/integration` from main (`b4ad35b`) and `expansion/step-00-plan` from it. Saved this plan and checked it against the repo: added "In the repo" to each step and the corrections above. No code changed, so there are no measurements. CI green on the plan commit; merged into `expansion/integration` and tagged `expansion-step-00`. Resume at step 1.
- **2026-11-18, step 1. Change and discontinue orders (UC 1, proposed FR-37).** Migration `OrderChanges` (five columns on `ehr.orders`, three check constraints); `db/policies` 10, 30 and 60; `ClinicalOrders.DiscontinueAsync` and a change through `SignAsync`; pages `Orders/Discontinue` and `Orders/Discontinued`, the order form's change mode, the chart's "Change" and "Discontinue…" links; new component `sc-list__actions` (noted in `docs/brand/README.md`). Tests: 14 in `OrderChangeSecurityTests`, TC-UC1-07 to TC-UC1-12 (the last a keyboard-only browser test), four more in `OrderTests`; 621 tests, 607 passed, 14 skipped (the performance cases, which need a benchmark run). `docs/UC1.md` and the README updated. Looked at every new and changed screen in light and dark, and at 640px; compared with the kit's notices, fields, table and buttons.
  - *Review.* The review agent could not break the database rules. Its fixes are in: the deferred trigger is replaced inside one transaction; column grants are now checked; the chart's confirmation is verified; three more tests.
  - *Measurements* (this Mac, 50,000 patients, durability on; p99 of the whole request / excluding the commit, ms). On-site: discontinued orders 8.0 / 7.1, change form 5.0 / 4.1, discontinue form 3.6 / 3.1; chart 7.8 / 7.1 and order form 3.0 / 2.7, as before. Off-site: 14.4 / 8.6, 17.3 / 12.5, 13.1 / 8.6. `--assert` passed. Page JavaScript as served, compressed: chart, change form and discontinue form 1,980 bytes, discontinued orders 1,058 (no script added or changed). First contentful paint, cold cache, median of 7 on the local stack: 16 ms on loopback, about 120 ms with 40 ms latency and 10 Mbit/s.
  - *Heads-up:* none beyond UC 1 (Tahseen).
  - *Follow-ups:* the chart's confirmation after discontinuing doesn't name the order; the race paths in `ClinicalOrders` (insert succeeds, update refused) are proven by the review's probes and the security tests but have no committed web test, because a stale page is refused earlier; the benchmark's table now has room for the longer row names.
  - *For step 2 and 2a,* license research is done (not committed): RxNorm's prescribable content, ICD-10-CM, UNII, UCUM and CVX need no account. **LOINC needs a free loinc.org account to download, MED-RT's terms are unclear, and no interaction list with a clearly free license was found: ask Tahseen before step 2a loads any of the three.** Step 2 needs none of them.
  - *Resume at step 2.*
- **2026-11-18, step 2. Editable allergies, medications and diagnoses, with standard codes (UC 4, proposed FR-38 and FR-39).** Migration `ChartLists` (tables `clinical_codes` and `chart_history`; `sensitive` on conditions and medications; `severity` and `code_system` on allergies; the SNOMED CT "No known allergies" code replaced). `db/policies` 10, 30, 40 and the new 65. `tools/reference/import.cs` and `db/reference/10-clinical-codes.sql`: 1,177 codes from RxNorm (2026-11-02), ICD-10-CM (FY2027) and UNII (2026-08-04), applied by the migration job, the tests and the benchmark; `docs/expansion/reference-data.md`. One entry form for the three lists with a code search (`/codes`, `js/code-search.js`), a history page, the sensitive-items rule on the chart, history and offline copy, allergies in the banner on the note and dictation pages, and a "Sensitive items" section in the brand guide. Tests: 677, 659 passed, 18 skipped (performance cases); 12 new security tests, TC-UC4-07 to TC-UC4-12 (the last a keyboard-only browser test), five more in `ChartListTests`. `cargo fmt`, `clippy` and the desktop tests pass. Looked at every new and changed screen in light and dark, and at 640px.
  - *Review.* The review agent failed the first build on two leaks of sensitive items (the history page, and unmarked existing rows after an upgrade). Both are fixed, with tests; its smaller points are in too.
  - *Measurements* (this Mac, 50,000 patients, durability on; p99 whole request / excluding the commit, ms). On-site: chart 7.2 / 6.4, chart with sensitive items 6.4 / 6.0, entry form 3.7 / 3.3, code search 1.5 / 1.4, history 2.6 / 2.3. Off-site: chart 12.1 / 7.7, entry form 12.0 / 7.5, code search 3.0 / 2.9, history 13.9 / 7.5; the chart with sensitive items was 24.1 / 15.2 with two audit events and was not measured again after going to one (CI's run passed). `--assert` passed. Page JavaScript as served, compressed: entry form 1,634 bytes (the new `code-search.js` is about 0.6 KB of it), chart 1,980, history 1,058. First contentful paint, cold cache, median of 7: 16 to 20 ms on loopback, about 116 ms with 40 ms latency and 10 Mbit/s. Seeding 50,000 patients is unchanged in size; its time wasn't recorded (step 2a records it).
  - *Heads-up:* Diego (UC 4: the chart gained edit links, a notice and a history link), Sara (UC 7: the offline chart contract gained `sensitiveNotKept`), Samir and Satwik (the banner on dictation and note pages now shows allergies).
  - *Follow-ups:* the items in the decision log's "Not built" entry; the code search needs an index before step 2a loads the full lists (about 100,000 rows); the seed's entries are uncoded until 2a.
  - *Resume at step 2a.* **Before it loads LOINC, drug classes or an interaction list, Tahseen has to decide** (see the step 1 log entry and `docs/expansion/reference-data.md`). RxNorm, ICD-10-CM, UNII, UCUM and CVX can be loaded in full without him.
- **2026-11-18, step 2b. Sensitive-item and allergy-status fixes (UC 4, UC 12; proposed FR-40).** Migration `AuditSensitive` (`audit_events.sensitive`). `db/policies` 10, 30, 40 and 65. The audit page shows a "Sensitive" tag and no values for writes to sensitive items, and none for the lists' history copies. "Record no known allergies" on the chart; the three allergy states on the chart, the banner and the desktop app's offline copy (`allergyStatus`). The sensitive rule gained T14.91, Z91.51, Z91.52 and the T36–T65 self-harm codes; the reference subset grew to 2,268 codes so those can be picked; `docs/expansion/reference-data.md` has the full rule. Tests: 706, 688 passed, 18 skipped (performance cases); new `SensitiveAuditSecurityTests` (5), four more and 16 more rule cases in `ChartListSecurityTests`, TC-UC4-13 and TC-UC12-07. `cargo fmt`, `clippy` and the desktop tests pass. Looked at the audit page in both themes on the stack, where the migration job had marked the earlier events of step 2's screenshots; the chart's "No known allergies" states use the existing styles and were checked in the tests, not in a screenshot (no panel patient on the stack lacked allergy information).
  - *Review.* The review agent found no way for an admin to read a sensitive value, and two defects, both fixed: the recurring pass could deadlock with live traffic, and "No known allergies" could race an allergy. CI then caught my first fix for the race (see the decision log).
  - *Measurements* (this Mac, 50,000 patients; p99 whole request / excluding the commit, ms): chart 5.5 / 5.1 on-site; the earlier run of this step had chart with sensitive items 5.3 / 4.6, entry form 2.2 / 2.0, history 2.5 / 2.3, and off-site chart 9.6 / 5.4, chart with sensitive items 12.5 / 8.2. `--assert` passed. No script changed; page JavaScript and first contentful paint are as in step 2.
  - *Heads-up:* Luke (UC 12: the audit page's details column), Diego (UC 4: the allergy button), Sara (UC 7: `allergyStatus` in the offline chart contract).
  - *For step 2a, found while this step's CI ran:* the drug-class join works by the route Tahseen named (1,418 RxNorm ingredients get an FDA class; for 1,413 at least 80% of their single-ingredient labels agree), with FDA's substance list as the fallback for drugs sold only in combinations. **DDInter's download files hold only the two drugs and a severity level: no management advice.** That text is only on the site's pages. Ask Tahseen before step 8 needs it.
  - *Resume at step 2a,* on its branch, after merging `expansion/integration` into it.

- **2026-11-18, step 2a. Reference data and a coherent synthetic seed.** Migrations `ClinicalCodeSearch` and `ReferenceTables`. `db/reference`: 100,847 clinical codes (ICD-10-CM FY2027, RxNorm 2026-11-02, UNII), 13,490 standard order items (LOINC 2.83 lab orders, RxNorm clinical drugs), 33 observation codes with UCUM units and the clinic's ranges, PHQ-9 and GAD-7, 25,490 product ingredients, 2,105 drug classes for 1,945 ingredients, two cross-reactions, and 81,610 DDInter interactions in their own folder with their license. `tools/reference/import-catalogs.cs` beside `import.cs`; `tests/Ehr.Testing/ReferenceData.cs` loads the COPY-format files for tests; the migration job applies them. The order form searches the catalog. `bench/Ehr.Seed` is coded and coherent, with a test of that. `docs/expansion/reference-data.md` rewritten. Tests: 708, 690 passed, 18 skipped (performance cases). `db/policies`: one grant line in `10-grants.sql`.
  - *Measurements* (this Mac, 50,000 patients; p99 whole request / excluding the commit, ms). On-site: chart 5.6 / 5.3, order form 9.1 / 8.0, entry form 2.5 / 2.2, code search by start 1.6 / 1.5 and by words 1.9 / 1.8, history 2.6 / 2.3. Off-site: chart 11.7 / 7.5, code search 1.9 / 1.7. `--assert` passed. Seed: 3.6 s for 50,000 patients and about 673,000 rows (57,548 allergies, 48,098 medications, 68,588 conditions, 223,392 visits, 225,736 results). The full test suite takes about 2 min 15 s locally, up from about 1 min 30 s, because each test database loads the reference data. No page script changed apart from the order form now loading `code-search.js` (about 0.6 KB); first contentful paint not measured again.
  - *CI.* The first run failed on a timing flake in a step 2 test of mine (it counted a shared clinician's audit events while an earlier test's off-site reads were still arriving); fixed by counting on-site events only.
  - *Not checked on the stack:* no screenshots this step; the one UI change is the order form's suggestions, covered by a test.
  - *Heads-up:* none beyond UC 1 (Tahseen).
  - *Resume at step 3,* the clinician inbox.
- **2026-11-18, step 3. Clinician inbox (new UC 15, proposed FR-41).** Migration `InboxItems`; `db/policies` 10, 30, 40 and the new `70-inbox.sql`; `StaffInbox`, `PhiReader.InboxAsync` and `InboxCountAsync`, `InboxCountFilter`, the page at `/inbox` (needs action, done), the app bar link with its count (`sc-appbar__count`, noted in the brand guide). Tests: 721, 701 passed, 20 skipped (performance cases); 6 in `InboxSecurityTests`, TC-UC15-01 to 06 (the last a keyboard-only browser test). `docs/UC15.md`, README. Looked at the inbox, its focus ring and the count in light and dark, and at 640px, on the stack, where two synthetic items were added by hand for the test clinician (they are still there).
  - *Review.* The review agent passed it with minor points; three are fixed (an index-friendly filter, the count on the page a refused "Mark done" returns, truer wording for that notice). Left as they are: the count also runs on GETs that only redirect, and `/codes` is skipped by its path.
  - *Measurements* (this Mac, 50,000 patients; p99 whole request / excluding the commit, ms). On-site: inbox with 40 items 3.9 / 3.6; chart 6.6 / 5.6, panel 10.4 / 10.0, order form 4.1 / 3.6, each now counting the inbox. Off-site: inbox 12.1 / 6.8, chart 14.1 / 9.8. `--assert` passed. The inbox page loads no script of its own; first contentful paint not measured.
  - *Heads-up:* everyone, lightly: every clinical page's app bar gained an "Inbox" link.
  - *Resume at step 4,* appointments.
- **2026-11-19, step 4. Appointments (FR-17; new UC 16, proposed FR-42).** Migrations `Appointments` and `AppointmentVersion`; `db/policies` 10, 30, 40 and the new `80-appointments.sql`; `Program.cs` puts `/Schedule` under the clinician policy. `Ehr.Domain.Scheduling.Appointments`; reads in `PhiReader`; pages `/schedule`, `/patients/{id}/appointments/new`, `/appointments/{id}`; the chart's Appointments section; the portal's home list and `/portal/appointments`, replacing half the placeholder; app bar links. Tests: 741, 718 passed, 23 skipped (performance cases); 11 in `AppointmentSecurityTests`, TC-UC16-01 to 06 (the last a keyboard-only browser test), TC-UC6-07. `docs/UC16.md`, `docs/UC6.md`, README. Looked at the booking form, its double-booking error, the schedule, an appointment and its cancelled state, the chart, and the portal's pages at 1280 and 360px, in light and dark, on the stack (three synthetic appointments for the test patient remain there).
  - *Review.* The review agent failed the first build on two bugs, both fixed with tests: a stale reschedule silently undid a colleague's, and the overlap guard held only at READ COMMITTED. Its smaller points are in too (see the decision log).
  - *Measurements* (this Mac, 50,000 patients, before the review's fixes; p99 whole request / excluding the commit, ms). On-site: schedule with 24 appointments 3.3 / 3.0, booking form 2.8 / 2.5, appointment 3.9 / 3.4, chart (now with upcoming appointments) 6.1 / 5.7, portal appointments 2.4 / 2.1, portal home 2.2 / 1.9. Off-site: schedule 14.3 / 8.7, booking form 14.3 / 8.7, appointment 15.3 / 10.4. `--assert` passed locally and in CI. No new script; first contentful paint not measured.
  - *Heads-up:* Simar (UC 6: the portal's home and a new page), Diego (UC 4: the chart gained an Appointments section), everyone (the app bar gained "Schedule").
  - *Resume at step 5,* secure messaging.
- **2026-11-19, step 5. Secure messaging (FR-18; new UC 17, proposed FR-43).** Migration `Messages`; `db/policies` 10, 30, 40 and the new `90-messages.sql`. `Ehr.Domain.Messaging.SecureMessages`; reads in `PhiReader`; portal pages `/portal/messages`, `/new` and `/{thread}`; clinical pages `/patients/{id}/messages/new` and `/{thread}`; the chart's Messages section; the inbox's "Open the conversation"; `sc-thread` and `sc-message` in the brand guide. Tests: 758, 732 passed, 26 skipped (performance cases); 6 in `MessageSecurityTests`, TC-UC17-01 to 06 (the last a keyboard-only browser test), TC-UC6-08 and 09. `docs/UC17.md`, `docs/UC6.md`, README. Looked at the portal's list, new-message form and conversation at 1280 and 360px, and the clinician's inbox item, conversation, sent reply, new-message form and chart section, in light and dark, on the stack (one synthetic conversation for the test patient remains there); the email in Mailpit says only that there is a message.
  - *Review.* The review agent found no authorization hole and failed the build on two app-level points, both fixed with tests: replies could use up the patient's hourly emails, and a patient's sending had no limit. See the decision log for what was left.
  - *Measurements* (this Mac, 50,000 patients, before the review's fixes; p99 whole request / excluding the commit, ms). On-site: conversation 2.9 / 2.6, portal messages 2.0 / 1.8, portal conversation 2.1 / 1.8, chart (now with conversations) 7.1 / 6.7, inbox 3.2 / 2.9. Off-site chart 21.2 / 17.1 in this run, with one slow outlier (12 to 15 in earlier runs); under the 20 ms gate, which excludes the commit. `--assert` passed locally and in CI. No new script; first contentful paint not measured.
  - *Heads-up:* Simar (UC 6: the portal gained Messages, and its "what's left" rows for FR-18 are gone), Diego (UC 4: the chart gained a Messages section).
  - *Resume at step 6,* proxy access.
- **2026-11-19, step 6. Proxy access (FR-19; new UC 18, proposed FR-44).** Migration `ProxyGrants`; `db/policies` 10, 30, 40 and the new `95-proxy-access.sql`. `UserContext.ActingGrant` and the interceptor's second setting; `PortalSubject`, its cookie and filter; `Ehr.Domain.Proxies.ProxyAccess`; reads in `PhiReader` and `Ehr.Data.Proxies.ProxiedRecords`; portal pages `/portal/access` and `/portal/acting`, the band on every portal page; clinical page `/patients/{id}/proxies` and the chart's link. Tests: 780, 750 passed, 30 skipped (performance cases); 9 in `ProxyAccessSecurityTests`, one more in `PortalResultsIsolationTests`, TC-UC18-01 to 06 (the last a keyboard-only browser test), TC-UC6-10 and 11. `docs/UC18.md`, `docs/UC6.md`, README, AGENTS.md. Looked at the clinic's page, the portal's Access page, and the home and results pages as a proxy, at 1280 and 360px, in light and dark, on the stack (synthetic patient SYN0000002 is on the test clinician's panel there, with the test patient account as their proxy). Two things the screens showed and I changed: the portal's fifth link was off the edge on a phone (the links now wrap), and a proxy's pages called the record "yours".
  - *Review.* The review agent found no way to reach a record that wasn't shared, and failed the build on the points listed in the decision log, all fixed with tests.
  - *Measurements* (this Mac, 50,000 patients, before the review's fixes; p99 whole request / excluding the commit, ms). On-site: the clinic's proxy page 2.7 / 2.5, portal Access 2.8 / 2.4, portal home as a proxy 3.0 / 2.7 (2.2 / 2.0 on one's own record), portal results as a proxy 3.3 / 2.8 (3.0 / 2.6). `--assert` passed locally and in CI. No new script; first contentful paint not measured.
  - *Heads-up:* Simar (UC 5 and UC 6: sign-in clears the proxy cookie; the portal gained Access, a band, and a subject that isn't always the account's own), Luke (UC 11: audit events by a proxy have the proxy's account as actor and another patient as subject).
  - *End of phase 1.* Tag `expansion-phase-1`, branch `expansion/phase-1`; the pull request draft is under "Phase merges". A good time for step 21's deployment, which waits for Tahseen.
  - *Resume at step 7,* order → result → acknowledgment.
- **2026-11-19, step 6a. Review aids.** `.gitattributes` marks `db/reference/**`, `db/migrations/*.Designer.cs` and `EhrDbContextModelSnapshot.cs` as `linguist-generated`; DDInter's terms page is cited; step 6b and Tahseen's decisions of the day are in the plan. No code changed. `expansion/phase-1` fast-forwarded to this merge with a normal push. *Resume at step 7.*
- **2026-11-19, step 7. Order → result → acknowledgment (extends UC 1; proposed FR-45).** Migration `OrderResults` (`ehr.result_acknowledgments`; one result per order and test); `db/policies` 10, 30, 40, 60, 70 and the new `75-results.sql`. `Ehr.Domain.Results.LabResults`; `PhiReader.OrderResultsAsync`, resulted orders on the chart, abnormal-first in `InboxAsync`; page `/patients/{id}/orders/{order}/results`; the chart's Results section and "Enter results"; the inbox's result items. Tests: 795, 763 passed, 32 skipped (performance cases); 7 in `ResultSecurityTests`, TC-UC1-13 to 17 (the last a keyboard-only browser test), TC-UC6-12, TC-UC15-07. `docs/UC1.md`, `docs/UC15.md`, README. Looked at the results page empty, filed and acknowledged, the inbox and the chart, in light and dark, and the results page at 640px, on the stack (a resulted, acknowledged blood count for SYN0000002 remains there).
  - *Review.* The review agent found nothing reachable through the app and failed the build on three points that needed a crafted request or raw SQL, all fixed with tests; see the decision log, which also has a note for the human review of `db/policies` and one for step 9.
  - *Caught by the benchmark:* the inbox took about 300 ms once it held results, because sorting abnormal ones first read every result on file. Reworked; `--assert` failed before and passes now.
  - *Measurements* (this Mac, 50,000 patients, before the review's fixes; p99 whole request / excluding the commit, ms). On-site: an order's results 10.1 / 9.7 in the last run (3.5 / 3.2 in the one before; the machine was busy), inbox with 24 results to acknowledge 5.4 / 4.9, inbox with 40 items 5.0 / 4.7 (one more query than before), chart with its Results section 7.2 / 6.9. `--assert` passed locally and in CI. No new script; first contentful paint not measured.
  - *Also fixed:* `30-rls.sql` dropped a function before the policy that depended on it, which failed only on a database that had run step 6's first build (the local stack). The fix is on this step, not on `expansion/phase-1`.
  - *Heads-up:* Simar (UC 6: a result filed at the clinic appears in the portal at once), everyone (the chart gained a Results section; resulted orders leave the active list).
  - *Resume at step 6b,* proxy-only portal accounts and choosing a record after sign-in. Step 8 waits for Tahseen's decision on DDInter's gaps.
- **2026-11-19, step 6b. Proxy-only portal accounts, and choosing a record after sign-in (extends UC 18 and UC 6; proposed FR-46).** Migration `ProxyOnlyAccounts` (`patient_users.patient_id` nullable, `display_name`, `ehr.proxy_invitations`); `db/policies/10-grants.sql`. `EhrPolicies`, `EhrClaims`, `PatientClaimsPrincipalFactory`, `UserContextMiddleware`, `PortalSubject.cs`; `Ehr.Domain.Proxies.ProxyInvitations`; pages `/portal/invitation` and `/portal/choose`; sign-in goes to the chooser; the access email carries the invitation. Tests: 800, 767 passed, 33 skipped (performance cases); one more in `ProxyAccessSecurityTests`, TC-UC18-07 and 08, TC-UC6-13. `docs/UC18.md`, `docs/UC6.md`, README. Looked at the invitation page (open, created, used), the chooser for a patient with a shared record and for a proxy-only account, Access for a proxy-only account, and a proxy-only account viewing a record, at 1280 and 360px, in light and dark, on the stack (one proxy-only synthetic account remains there, with a password that was never stored).
  - *Review.* The review agent confirmed the authorization holds (a proxy-only session with no record chosen is sent to the chooser from every page and handler; a forged cookie shows nothing; staff sessions can't pass as portal accounts) and failed the build on two points, both fixed with tests; see the decision log, which also has one decision for Tahseen about the invitations table.
  - *Measurements* (this Mac, 50,000 patients, before the review's fixes; p99 whole request / excluding the commit, ms). On-site: "Whose record?" with one record shared 1.4 / 1.3; portal home as a proxy 2.9 / 2.6; portal Access 6.4 / 5.2 in this run (2.8 / 2.4 in step 6's; the machine was busy). `--assert` passed locally and in CI. No new script; first contentful paint not measured.
  - *Heads-up:* **Simar (UC 5, UC 6):** sign-in now goes to `/portal/choose`, which goes straight on to "My record" unless a record is shared with the account; a portal account can exist without a patient (anything that read the account's patient must allow for none); `/portal/invitation` is a second way an account is created, with its address confirmed by the emailed link instead of by the confirmation step; an unconfirmed registration loses its address if the address's owner accepts an invitation. Luke (UC 11): audit events from accounts with no patient, and the new action `proxy_account_created`.
  - *CI.* The benchmark job failed once on the last commit (chart p99 excluding the commit 28.1 ms; the whole run was slow, on pages this step doesn't touch too) and passed when rerun (19.7 ms with the commit). The commit before it passed, and nothing on the chart's path changed between them. **The chart has almost no headroom left on CI runners:** its median there is 10 to 14 ms, twice this Mac's, and most steps of this plan have added a section to it. Its queries are to be reduced before step 9 adds to it.
  - *Resume at step 8,* ordering safety checks, **which waits for Tahseen's decision on DDInter's gaps**. If that hasn't come, go on to step 9 (vital signs and trends) and come back.
- **2026-11-19, step 7a. Chart render headroom.** `PhiReader.ChartAsync` and the chart page skip the queries of empty sections. No migration, no policy change, no new page. Tests: 800, 767 passed, 33 skipped. *Measurements* (this Mac, 50,000 patients; p50 / p99 whole request, ms): chart 4.1 / 7.5 (5.5 / 7.2 before; the median is the steadier number here), chart with sensitive items 3.4 / 4.7. `--assert` passed locally and in CI. On the CI runner the chart's median was 10.8 ms, inside the 10 to 14 ms of the runs before it: the runners vary more from run to run than this change saves, so the gain shows on this Mac and not yet there, and the chart is still the page closest to the gate. Not sent to the review agent: a read-path change with unchanged results, covered by the existing chart tests. *Resume at step 8 if Tahseen has decided on DDInter's gaps, otherwise step 9.*
- **2026-11-19, step 6c. Security follow-ups.** No migration (both tables already held only hashes). `db/policies/10-grants.sql` (grants removed) and the new `45-codes.sql` (six functions that run as their owner). `Ehr.Data.Accounts.CodeStore`; `CodeHasher` and `Ehr:Enrollment:Key`; `SiteAddresses.Checked` and the startup checks in `Program.cs`; `Links.Build` without the request; `PatientRegistration`, `PatientEnrollments`, `ProxyInvitations`. Tests: 809, 776 passed, 33 skipped (performance cases); `CodeLookupSecurityTests` (3), `EmailedLinkTests` (5), one more in `RateLimitTests`. README, `infra/compose` (`EHR_ENROLLMENT_KEY`, for the on-prem app only), `docs/UC18.md`. No screen changed, so nothing to look at; the local stack was upgraded in place and checked: the old grants are gone, both apps start, and only the on-prem app has the key.
  - *Review.* No security defect; four small fixes made (decision log).
  - *Measurements:* no page's read path changed; the benchmark's `--assert` passed in CI.
  - *Heads-up:* Simar (UC 5: registration uses the code up first; codes are keyed; the stack and any deployment need `EHR_ENROLLMENT_KEY`), Luke (UC 11, UC 12: the Host-header note under "Phase merges", phase 2), everyone who runs the stack (add `EHR_ENROLLMENT_KEY` to an older `infra/compose/.env`).
  - *Open with Tahseen:* whether codes issued before the key should stop working at deploy instead of being accepted until they expire.
  - *Resume at step 8,* ordering safety checks, with DDInter supplemented as decided.
- **2026-11-19, step 8. Ordering safety checks (extends UC 1; proposed FR-47).** Migrations `InteractionSources` (`drug_interactions` gains a source, a detail and a citation) and `OrderSafety` (`orders.safety_warnings`, `safety_override_reason`). `db/policies/60-orders.sql` and the new `61-order-safety.sql`. `db/reference`: the ONC pairs (`50-onc-interactions.sql`, from the committed Wikidata result of 19 November 2026), the hand-written label table (`60-label-interactions.sql`), the DDInter file regenerated with its source. `Ehr.Data.Orders.OrderSafety`, `OrderSafetyText`, the order form. Tests: 818, 785 passed, 33 skipped (performance cases); 6 in `OrderSafetySecurityTests`, TC-UC1-18 to 20 (the last a keyboard-only browser test). `docs/UC1.md`, `docs/expansion/reference-data.md`, README. Looked at the order form with high-priority, major and moderate interactions, a cross-reactivity warning and notes only, in light and dark at 1280 and 640px, on the stack (SYN0000002 has a coded cephalexin allergy and three coded medications there now).
  - *Review.* The review agent failed the build on real problems, all fixed (decision log): the benchmark crashed, a sensitive medication's name reached administrators through the order's audit, and class allergies stopped far too many orders.
  - *Found by looking at the screens:* a note that didn't stop the order was never shown; a long citation ran out of its box.
  - *Measurements* (this Mac, 50,000 patients; p99 whole request / excluding the commit, ms): order form 2.9 / 2.6; chart 11.1 / 10.4 in this run (median 4.0). The check itself runs when an order is signed, about a third of a millisecond for a patient with 32 medications (the reviewer's measurement). `--assert` passed locally and in CI. No new page; the order form's script grew by two lines.
  - *Heads-up:* everyone who orders (a medication order can now come back asking for a reason, or once to show notes). No other owner's use case changed.
  - *Open with Tahseen:* how to close the class-level gaps; whether moderate and minor interactions should need a reason; the address pattern of DDInter's pair pages.
  - *Resume at step 9,* vital signs and trends. It must widen `ehr.file_result` for observations without an order (step 7's note), and it adds to the chart, the page closest to the benchmark gate (step 7a's note).
- **2026-11-19, step 9. Vital signs and trends (extends UC 4 and UC 6; proposed FR-48).** Migration `VitalSigns`. **Protected path: `db/policies/75-results.sql`** (`ehr.file_result` widened for vital signs with no order; body mass index worked out there). No new table, grant, policy or authorization attribute. New page `/patients/{id}/vitals` under the clinical area's policy, with the panel rule other write pages use; `PhiReader.VitalsAsync`; the chart query reads each visit's blood pressure and heart rate with the visit, so the chart makes no more round trips than before.
  - *Tests:* `VitalSignSecurityTests` (8), `VitalSignMigrationTests` (the migration down and up on a database with policies in place); TC-UC4-14 to 18, TC-UC1-21, TC-UC6-14; the seed test checks the seeded vital signs. Full run: 834 tests, none failing.
  - *From the review* (no way found past the database's rules; failed on correctness), all fixed with tests: a set recorded on the page never reached the chart's visit line or the offline copy; one row limit shared by results and vital signs could hide every result in the portal; a weight and height the form accepted crashed the page on the body mass index; a double submit stored a set twice for good; the cut-off "Not saved" page showed only the time; the chart's per-visit lookups had no order; the rollback lost the values; comments in `10-grants.sql`, `30-rls.sql` and the entity classes still described observations as lab results only. **Left open:** the visit link is enforced by the trigger for the apps, not by a composite foreign key as orders are; the screen reader hears a short label on each chart and the sentence under it.
  - *Measurements* (this Mac, 50,000 patients; p99 whole request / excluding the commit, ms; the last two runs): vital signs page 3.2 / 2.8, off-site 14.1 / 9.1; chart 8.7 / 6.6 and 7.8 / 6.8; **off-site chart 16.9 / 12.4 and 17.6 / 13.4** (step 8: 12.0 / 7.6; its median went from 4.0 to 7.0 excluding the commit); order results 4.3 / 3.8; portal results 2.6 / 2.3. `--assert` passed. No script added; the stylesheet grew by the trend and form rules.
  - *The off-site chart is now the row nearest the gate, and why.* The review's first fixes took it to 18.9 and then 20.9 ms excluding the commit (a failed gate, found before pushing). Seven benchmark runs later: with SQL timing on, **every statement the off-site app sends takes about 0.2 to 1.3 ms where on-site they take almost nothing**, so each extra statement on the off-site chart costs milliseconds, and so did making the visits' statement more complex. The chart was reduced to one plain query for vital signs. I did not find the cause of the off-site cost per statement; my guess, not checked, is that the cloud app runs each statement on its own (no request-long transaction), so PostgreSQL re-plans the row-level-security helper functions every time. **For Tahseen and Luke:** that predates this step and is probably worth more than anything done here; fixing it would bring every off-site page down.
  - *Screens looked at* in both themes at 1280, 640 and 360 (portal): no sideways scrolling.
  - *Heads-up:* Diego (UC 4: the chart has a Vital signs section, and a visit's vital signs are observations now), Simar (UC 6: the results page has trend charts and vital signs), Sara (UC 7 and 8: the offline panel's contract is unchanged, but its values come from observations).
  - *Open with Tahseen:* whether the trend charts belong on the chart page itself; correcting a wrongly entered vital sign; reference ranges by age; times are typed in UTC on clinician pages (as since step 7), which a clinician typing local time will get wrong.
  - *Resume at step 10,* addenda to signed notes, with sensitive notes.
