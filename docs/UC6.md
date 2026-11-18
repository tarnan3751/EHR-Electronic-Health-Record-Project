# UC 6 — View test results

SwiftChart EHR · Simar Rekhi · CS 3354 Software Engineering, Fall 2026

## Patient workflow

Sign in to the portal and choose **Results** in the navigation, or **See your test results** on the portal home (`/portal/results`). Each released result shows the test, the date the sample was taken (in UTC, labelled, as every date in the app is), the value with its unit, and whether it is low, normal or high. Open a result to read its normal range, what the result means in plain language, what the test measures, and any note from the care team. With no released results, the page says so and points the patient to their care team.

## Decisions

- **Who.** Patients only, through the Portal area's Patient policy. The page takes the patient from the session, never from the request: it has no ID parameter, which `PortalSubjectTests` checks for every portal handler.
- **Data.** One table, `ehr.observations`, shaped on FHIR's Observation (laboratory category): patient, LOINC code, display name, status, value, unit, reference range, sample time, release time (`issued_at`), the care team's note, and the lab order it answers (`order_id`, optional). The order link is keyed on `(order_id, patient_id)`, so a result can't name another patient's order. Check constraints hold the status to FHIR's codes and the range's low end at or below its high end.
- **Released.** A patient sees a result only once it has a release time and its status is `final`, `amended` or `corrected`. The rule is in the row-level security policy (`observations_own_released_select`) and repeated in `PhiReader.ResultsAsync`.
- **Database access.** Row-level security is enabled and forced. Clinicians read every result, as for the rest of the chart; a patient reads only their own released ones. Grants: `ehr_app` and `ehr_read` SELECT only. Nothing at runtime writes results yet. The standard write-audit trigger records any write.
- **Not cached.** The results page and the portal home send `Cache-Control: no-store`, as the note pages do, so Back after signing out on a shared computer doesn't show them again.
- **Reads.** Through `PhiReader.ResultsAsync`. Opening the page is one recorded read of the patient, as for the chart. `CloudAuditEvents` did not need widening.
- **Off-site.** Like every page that reads PHI, the results page is served from the cloud replica when the on-site server is down. It shows the read-only banner, and its read goes to the cloud audit queue and arrives on-site with source `cloud`.
- **Plain language (FR-16).** `Results/ResultExplanation.cs` flags the value against its range, writes a range with one open end the way people say it ("below 100 mg/dL"), drops the zeros the database keeps, and says what 14 common tests measure, by LOINC code.
- **Seed.** The synthetic clinic gives each patient up to six results from 13 common tests after their visits. 90% are released, and some results outside their range carry a care-team note. Seeded results don't link to orders, since the seed has none.

## Courses of events and their tests

The numbered test cases are the ones in the SE document (Assignment 4, sections 7.2 and 7.3), with the same IDs.

| ID | Course of events | Test |
|---|---|---|
| TC-UC6-01 | Successful flow: released results, newest first, each with its range and meaning; one recorded read; not cached; the date labelled UTC | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_01_SuccessfulFlow.cs` |
| TC-UC6-02 | Alternative flow: no results yet | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_02_AlternativeFlow.cs` |
| TC-UC6-03 | Invalid input: signed out, or a clinician, can't reach the page: sent to the portal sign-in, on the on-site and the cloud app | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_03_InvalidInput.cs` |
| TC-UC6-04 | Exceptional condition: on-site server down; read-only, the read queued and shipped | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_04_ExceptionalCondition.cs` |
| TC-UC6-05 | NFR-01: the results page on-site, p99 of the whole request, the commit included, under 15 ms at 50,000 patients with durability on. It runs only against a run of the render benchmark (`tests/Ehr.Web.Tests/TestCases/PerformanceResults.cs` says how) and is skipped in the normal test run | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_05_Nfr01_OnSiteNavigation.cs` |
| TC-UC6-06 | NFR-11: the results page in modern browsers (manual, with the results from PR #43); the same run is TC-UC5-05 for the portal sign-in (`tests/Ehr.Web.Tests/TestCases/UC5/TC_UC5_05_Nfr11_ModernBrowsers.md`) | `tests/Ehr.Web.Tests/TestCases/UC6/TC_UC6_06_Nfr11_ModernBrowsers.md` |

Supporting tests, not numbered in the SE document:

| Rule | Test |
|---|---|
| Sign-in fails: "Wrong email or password.", and the results page sends the patient to sign in | `PortalResultsTests.A_wrong_password_is_refused_and_shows_no_results` |
| A patient can't read another patient's results (the page takes no result ID): zero rows, through both roles | `tests/Ehr.Security.Tests/PortalResultsIsolationTests.cs`, and the `observations` case of `RowLevelSecurityTests.Chart_sections_are_read_by_clinicians_and_by_their_own_patient_only` |
| A patient sees a result only once released and finished, through both roles | `RowLevelSecurityTests.A_patient_sees_their_result_only_once_it_is_released_and_finished` |
| A result answers only its own patient's order (FR-31) | `RowLevelSecurityTests.A_result_answers_only_its_own_patients_order` |
| Results can't be changed at runtime, and any write is audited | the `observations` rows in `RuntimeRoleTests`, `ProtectionTests`, `PolicyPlanTests`, `WriteAuditTests` and `RowLevelSecurityTests` |
| FR-16's plain-language rules: low, normal and high against both ends; a range with one end; numbers without trailing zeros; no number or no range; what the test measures | `ResultExplanationTests` |

## Human review

These need line-by-line review by someone other than Simar (AGENTS.md):

- `db/policies/10-grants.sql`, `30-rls.sql` and `40-audit.sql`: the grants, the two policies and the audit trigger for observations.
- `PhiReader.ResultsAsync`.

## Limits

- Nothing writes results yet: they will come from a lab interface, which isn't built. The seed and the tests insert them directly.
- The portal doesn't show which order a result answers, and the clinician's chart doesn't list results yet.
- Results are numeric only. A text result such as "negative" gets no flag and the "ask your care team" wording.
- Dates are shown in UTC, labelled, since the app has no clinic time zone yet. A sample taken on a Chicago evening shows the next day's date.

## Not built

The A3 UC 6 specification includes these; they are listed as remaining work in the SE document (section 7.5).

| Part of UC 6 | Requirement | What's left |
|---|---|---|
| Step 1 alternative: a proxy chooses which patient's records to view | FR-19 | A proxy authorization table, a patient picker after sign-in, and row-level security keyed on the chosen patient rather than the account's own |

**Appointments (FR-17) are built** (the expansion plan's step 4, by Tahseen Arnan). The portal's home page lists the patient's next three appointments, and `/portal/appointments` lists all of them: those coming up, soonest first, and earlier ones, with "Attended", "Missed" or "Cancelled". Each shows the date written out, the time in UTC, the length, the clinician and the reason the clinic entered. There is no self-scheduling: the page says to contact the clinic. The patient comes from the session; row-level security returns only their own appointments; the page is one recorded read. Staff book and change appointments as `docs/UC16.md` describes. Test case TC-UC6-07 (`TC_UC6_07_Appointments.cs`) covers the portal's side, off-site included.

**Secure messages (FR-18) are built** (the expansion plan's step 5, by Tahseen Arnan). **Messages** in the portal lists the patient's conversations with their care team, the latest first, with "New" on any that has an unread reply. **Write to your care team** starts one with a subject and a message; a conversation's page shows every message in order, the patient's as "You" and staff's as "Your care team", and has a reply box. The patient comes from the session; a conversation that isn't theirs is not found. Opening one is a recorded read. When the clinic's server can't be reached, the pages stay readable and say "Read-only: messages can't be sent until the clinic's server is back." A reply from the clinic sends an email that says only that there is a new message. The clinician's side is `docs/UC17.md`. Test cases TC-UC6-08 (`TC_UC6_08_SendMessage.cs`) and TC-UC6-09 (`TC_UC6_09_MessagesRefused.cs`). One difference from the A3 specification: a message is started from the Messages page, not from a result.

Outside UC 6 itself: nothing writes results (no lab interface), and the clinician's chart doesn't list them (see Limits).
