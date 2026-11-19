# UC 4 — View a patient's chart: editing its lists

SwiftChart EHR · UC 4 is Diego Valenzuela's · this part (the expansion plan's step 2) by Tahseen Arnan

This page covers what the expansion added to the chart: editing allergies, medications and diagnoses, their standard codes, their history, and sensitive items. The chart itself is described in the README.

## Clinician workflow

Open a patient on your panel. Each of **Allergies**, **Medications** and **Diagnoses** has an **Add** button and, after each entry, a **Change** link, while the clinic's server can be reached.

- **Add** opens a form with the patient's banner. Type two letters or more of the name and pick from the list: diagnoses are ICD-10-CM (a code can be typed too), medications are RxNorm clinical drugs, and allergens are RxNorm ingredients or FDA substances. The form refuses a name that isn't one of them.
- **Change** opens the same form filled in. Nothing is deleted: give the entry another status (inactive, resolved, stopped, entered in error). The chart lists what the patient has now.
- An entry recorded before there were codes keeps its name until you change it.
- **Allergies are in one of three states.** "No allergy information recorded" means nobody has asked. **Record no known allergies** records that someone asked and there are none. Adding an allergy clears "No known allergies" by itself. The chart, the patient banner and the desktop app's offline copy each show the state.
- **History of allergies, medications and diagnoses**, above the lists, shows every entry added and every change, newest first, with who and when.

**Vital signs** (the expansion plan's step 9, by Tahseen Arnan). The chart's **Vital signs** section lists the newest value of each sign, with High or Low where it is outside its reference range, and links to the patient's vital signs page, `/patients/{id}/vitals`:

- **Flowsheet:** the latest six sets side by side, newest first, each sign with its unit; a value outside its reference range is tagged High or Low.
- **Trends:** a small chart for each sign with two or more values. The shaded band is the reference range, a value outside it is a diamond, and the sentence under the chart says what it shows. **The values as a table** opens every number behind it. The charts are drawn by the server as SVG: no script.
- **Record vital signs**, first on the page: blood pressure, heart rate, temperature, respiratory rate, oxygen saturation, weight and height. Enter what was taken and leave the rest empty. Body mass index is worked out from the weight and height. Only a clinician with the patient on their panel records; off-site the set is forwarded to the clinic's server, and recording is not offered while that server can't be reached.
- A set taken during a visit belongs to that visit: the database ties it to the patient's visit that began in the twelve hours before it was taken, and the chart's latest visit then shows its blood pressure and heart rate. A set sent twice is recorded once.
- Each sign has bounds no person is outside of (a temperature from 20 to 45 °C, for example); the form and the database both refuse a value beyond them.
- Times are typed and shown in UTC, as elsewhere in the clinician app.
- **A wrong reading** (step 9b) is marked **entered in error**, never deleted or edited. Under each set on the flowsheet, **Mark in error** opens a page that lists the set's readings: tick the wrong ones, give the reason, and choose **Mark entered in error**. The readings stay in the record with your name, the time and the reason, and are left out of the flowsheet, the trends, the chart, the offline copy and the portal. **Show entries in error** lists them on the flowsheet struck through, with the reason. It can't be undone. The body mass index isn't a reading to tick: marking a weight or height also marks the body mass index worked out from it. The flowsheet lists the six latest sets; **Show earlier sets** lists up to 24, as many as a trend draws, so any of them can be corrected.
- **The correct value** is a new reading: type the set again for the same time. A value that already stands for that moment is left as it is, so only what was wrong is recorded again; a weight or height corrected by itself gets a new body mass index.
- **Ranges by age.** Heart rate, respiratory rate and blood pressure are flagged against the range for the patient's age on the day they were taken: infant, young child, older child, adolescent or adult (`docs/expansion/reference-data.md` has the numbers and their sources). Temperature and oxygen saturation have one range. A trend marks each point against the range it was recorded with, and says so when a child has since moved into another band.
- **On the chart**, each sign's newest value has a small mark, and the words for a screen reader, when it is up or down from the reading before it.
- A vital sign is an observation with its LOINC code and UCUM unit, like a lab result. It can't be edited or deleted. The patient sees their vital signs in the portal at once (UC 6).
- A visit's blood pressure and heart rate, which used to be the visit's own columns, are now observations linked to the visit. The chart's latest visit and the desktop app's offline copy show them as before. The offline copy does not carry the full flowsheet.

| List | Fields |
|---|---|
| Allergy | Substance, reaction, severity of the reaction, risk from another exposure, status |
| Medication | Medication, how it is taken, started, stopped, status, sensitive |
| Diagnosis | Diagnosis, onset, status, sensitive |

### Sensitive items

Mental-health, substance-use and some other diagnoses are sensitive by their code (`docs/expansion/reference-data.md` has the ranges), and you can mark any diagnosis or medication sensitive. The chart leaves sensitive items out and says how many it left out. **Show sensitive items** reloads the chart with them, each tagged "Sensitive", and that view is recorded in the audit log as `read_sensitive`. The history page follows the same rule, and so does opening a sensitive entry's form. Sensitive items are never in the patient banner, and the desktop app doesn't keep them offline.

## Decisions

- **Who.** Clinicians only, and only for a patient on their panel, as for orders and notes. Under emergency access the chart and the history can be read, and nothing can be changed.
- **Data.** The existing tables, `ehr.allergy_intolerances`, `ehr.medication_statements` and `ehr.conditions`, gain `sensitive` (the last two), and `severity` and `code_system` (allergies). `ehr_app` gets INSERT and UPDATE on them, and never DELETE. Row-level security: a clinician, for a patient on their active panel. A trigger refuses moving an entry to another patient or ID.
- **Codes.** `ehr.clinical_codes`, reference data that nothing at runtime changes, loaded from `db/reference`. A new entry stores the code, its system (for allergies) and the source's own name.
- **History.** `ehr.chart_history`, written only by a trigger that runs as its owner, so it can't be skipped or edited. Clinicians read it. The write-audit trigger records the same changes for admins.
- **Sensitive.** The database sets the flag for a diagnosis whose code is in a sensitive range, whatever the form sent, so it can't be cleared. The reader leaves sensitive rows out unless asked, and records the asking as its own audit action, on-site and off-site.
- **"No known allergies".** An entry of its own, with the app's own marker; the SNOMED CT code it used before is gone. The database keeps the list to one state: it refuses "No known allergies" for a patient with an active allergy, and makes it inactive when an active allergy is added, in the same transaction, by the same clinician, with the history and the write audit recording both.
- **Admins and sensitive values.** The audit page shows that a sensitive item changed and who changed it, never its values, and the same for the events written before the item was marked. The runtime roles can't read the audit table's values column at all; the page asks `ehr.shown_audit_details`, which answers only admins and leaves sensitive items out. The full record stays in the table. See `docs/UC12.md`.
- **Banner.** The patient banner now shows allergies on the note, dictation and review pages too, read with the patient in the one read those pages already record.
- **Search.** `/codes` returns up to 20 names that start with what was typed, then names with a word that starts with it, then diagnoses by code. It reads nothing about a patient. With today's subset it is a small scan; the full lists in step 2a need an index for it.
- **Off-site.** Reads come from the cloud copy; a save is forwarded to the clinic's server. While that can't be reached the chart offers no Add or Change and says "Changes to diagnoses need the clinic's server."
- **Desktop.** The offline chart already held the three lists. It doesn't hold sensitive items, and says how many aren't kept.

## Test cases

One file each, in `tests/Ehr.Web.Tests/TestCases/UC4/`, continuing UC 4's numbering.

| Test case | What it checks | File |
|---|---|---|
| TC-UC4-07, successful flow | A coded diagnosis, medication and allergy are added; the chart, history and audit show them | `TC_UC4_07_AddEntrySuccessfulFlow.cs` |
| TC-UC4-08, alternative flow | An entry is changed or stopped, never deleted; an uncoded entry keeps its name | `TC_UC4_08_ChangeEntryAlternativeFlow.cs` |
| TC-UC4-09, invalid input | A name that isn't a code, a bad date or status is highlighted and nothing is saved | `TC_UC4_09_EntryInvalidInput.cs` |
| TC-UC4-10, exceptional condition | Off the panel, or off-site with the server down, nothing is changed | `TC_UC4_10_EntryExceptionalCondition.cs` |
| TC-UC4-11, NFR-01 | The entry form, code search, history and chart with sensitive items: p99 under 15 ms at 50,000 patients | `TC_UC4_11_Nfr01_ListPagesOnSite.cs` |
| TC-UC4-12, NFR-12 | In a real browser, adding a coded diagnosis with the keyboard only | `TC_UC4_12_Nfr12_EntryKeyboardFirst.cs` |
| TC-UC4-13, alternative flow | The allergy list's three states on the chart, the banner and the offline copy; "No known allergies" recorded, and cleared by adding an allergy | `TC_UC4_13_AllergyStatus.cs` |
| TC-UC4-14, successful flow (vital signs) | A set is recorded from the chart; the flowsheet flags what is out of range and shows the body mass index; a second set gives each sign a trend with its table; the chart's latest visit and the offline copy show the set taken during the visit | `TC_UC4_14_VitalSignsSuccessfulFlow.cs` |
| TC-UC4-15, invalid input and alternative flow (vital signs) | Half a blood pressure, a word, an impossible value, nothing, or a future time is not saved; a clinician off the panel records nothing; an impossible body mass index refuses the whole set; a set sent twice is recorded once | `TC_UC4_15_VitalSignsInvalidAndRefused.cs` |
| TC-UC4-16, exceptional condition (vital signs) | Off-site a set is forwarded; with the server down the flowsheet and trends are read-only | `TC_UC4_16_VitalSignsOffSite.cs` |
| TC-UC4-17, NFR-01 (vital signs) | The vital signs page: p99 under 15 ms at 50,000 patients | `TC_UC4_17_Nfr01_VitalSignsOnSite.cs` |
| TC-UC4-18, NFR-12 (vital signs) | In a real browser, recording a set and opening a trend's table with the keyboard only | `TC_UC4_18_Nfr12_VitalSignsKeyboardFirst.cs` |
| TC-UC4-19, alternative flow (correction) | A reading marked entered in error stays in the record, struck through on request, and leaves the flowsheet, trends, chart, offline copy and portal; the correct value is a new reading | `TC_UC4_19_VitalSignEnteredInError.cs` |
| TC-UC4-20, invalid input and exceptional condition (correction) | Marking needs a reading, a reason and the panel; off-site it is forwarded, and not offered during an outage | `TC_UC4_20_VitalSignErrorRefusedAndOffSite.cs` |
| TC-UC4-21, alternative flow (ranges by age) | A three-year-old's heart rate, breathing and blood pressure are flagged against a child's ranges, an adult's against an adult's | `TC_UC4_21_VitalSignRangesByAge.cs` |
| TC-UC4-22, NFR-12 (correction) | In a real browser, a reading marked entered in error with the keyboard only | `TC_UC4_22_Nfr12_VitalSignErrorKeyboardFirst.cs` |

`tests/Ehr.Security.Tests/VitalSignSecurityTests.cs` asks the database directly: a recorded vital sign gets the database's own name, unit and range and is audited; only a clinician with the patient on their panel records one; without an order only a sound vital sign is accepted; the body mass index is the database's arithmetic; a set is tied to the visit it was taken at; values no person has, and the same sign twice, are refused; a result of an order can't pass as a vital sign; nothing is edited or deleted. `VitalSignCorrectionSecurityTests.cs` asks it about corrections and ranges: a vital sign is marked entered in error once, with a reason, by a clinician with the patient on their panel, and the database records who and when; no other column of an observation moves and nothing unmarks one; a result of an order is not corrected this way; the body mass index follows its weight and height; each sign gets the range for the patient's age on the day it was taken; the reference file and the ranges in code are the same. `VitalSignMigrationTests.cs` runs the migration that moved the visits' vital signs, down and up, on a database with its policies in place.

Extra coverage in `tests/Ehr.Web.Tests/ChartListTests.cs`: the code search and who may use it; sensitive items left out until asked for, with the audit action, on-site and off-site; allergies in the banner on the note and dictation pages; a save forwarded from off-site.

In `tests/Ehr.Security.Tests/ChartListSecurityTests.cs`, against the database directly: who may add and change entries; nothing deletes or moves one; the history and the audit record each change; the sensitive ranges; a sensitive code can't be unmarked; who reads the history; the reference codes are loaded.

## Human review

These need line-by-line review (AGENTS.md):

- `db/policies/75-results.sql`: `ehr.file_result` now lets a vital sign be recorded with no order (step 9), and works out the body mass index.
- `db/policies/65-chart-lists.sql`: the sensitive ranges, the guard trigger, and the `SECURITY DEFINER` history trigger.
- `db/policies/10-grants.sql`, `30-rls.sql` and `40-audit.sql`: the grants and policies for the three lists, `chart_history` and `clinical_codes`.
- The panel rule on the entry page (`Areas/Clinical/Pages/Patients/Lists/Entry.cshtml.cs`), and `CloudAuditEvents`, which now accepts `read_sensitive`.
- The new `PhiReader` methods.

## Limits

- A note is marked sensitive when it is signed, and an addendum when it is written (`docs/UC14.md`); a signed note can't be marked afterwards.
- "No known allergies" is cleared by adding an allergy; there is no button to take it back on its own.
- A medication isn't sensitive by rule; that waits for drug-class data.
- The reference codes are a subset until step 2a.
- The suggestions are the browser's own list, which differs a little between browsers.
