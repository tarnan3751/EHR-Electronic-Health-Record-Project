# UC 4 — View a patient's chart: editing its lists

SwiftChart EHR · UC 4 is Diego Valenzuela's · this part (the expansion plan's step 2) by Tahseen Arnan

This page covers what the expansion added to the chart: editing allergies, medications and diagnoses, their standard codes, their history, and sensitive items. The chart itself is described in the README.

## Clinician workflow

Open a patient on your panel. Each of **Allergies**, **Medications** and **Diagnoses** has an **Add** button and, after each entry, a **Change** link, while the clinic's server can be reached.

- **Add** opens a form with the patient's banner. Type two letters or more of the name and pick from the list: diagnoses are ICD-10-CM (a code can be typed too, as "I10" or "e119", and saving it saves that diagnosis), medications are RxNorm clinical drugs, and allergens are RxNorm ingredients or FDA substances. The form refuses a name that isn't one of them. Under the field, the search says "No matches." when nothing matches, and "Signed out: …" if your session has ended.
- **Change** opens the same form filled in. Nothing is deleted: give the entry another status: an allergy inactive or resolved, a medication on hold, stopped, completed or entered in error, a diagnosis in remission, inactive or resolved. The chart lists what the patient has now.
- An entry recorded before there were codes keeps its name until you change it.
- **After saving,** the chart says so: "Saved: Essential (primary) hypertension is listed under Diagnoses.", or, for an entry given a status that takes it off the chart, "… is now stopped, so it is no longer listed." A sensitive entry the chart is leaving out isn't named: "Saved: the diagnosis is sensitive, so it is kept with the sensitive items."
- **Posting a form twice saves one entry.** An "Add" form carries its new entry's ID from when it opened, so a double click, or going back and saving again, changes that entry rather than adding a second. (Two people changing the same entry at once still isn't warned about: the later save wins.)
- **The keyboard follows what happened:** after a save the chart's notice has the focus, and a form that comes back with mistakes has the cursor in the first field to fix.
- **When a save can't happen,** nothing is lost. If the database refuses it, the form comes back with what you entered and "Not saved: the database refused the change …". If the patient left your panel after you opened the form, a page says "Not saved: this patient isn't on your panel, so nothing was changed." and shows what you typed, to copy, without anything about the patient.
- **Allergies are in one of three states.** "No allergy information recorded" means nobody has asked. **Record no known allergies** records that someone asked and there are none, shown as "No known allergies (recorded <date> by <name>)", and the chart confirms it ("Saved: no known allergies recorded for this patient."). If a colleague recorded an allergy since you opened the chart, it isn't recorded, and the chart says why. Adding an allergy clears "No known allergies" by itself. The chart, the patient banner and the desktop app's offline copy each show the state.
- **History of allergies, medications and diagnoses**, above the lists, shows every entry added and every change, newest first, with who and when, in the form's words ("Status: Active → On hold", "Risk: not recorded → High risk").

| List | Fields |
|---|---|
| Allergy | Substance, reaction, severity of the reaction, risk from another exposure, status |
| Medication | Medication, how it is taken, started, stopped, status, sensitive |
| Diagnosis | Diagnosis, onset, status, sensitive |

### Sensitive items

Mental-health, substance-use and some other diagnoses are sensitive by their code (`docs/expansion/reference-data.md` has the ranges), and you can mark any diagnosis or medication sensitive. On the form, a diagnosis sensitive by its code has its **Sensitive** box checked and fixed, with the hint "Sensitive by its code". (A new diagnosis is checked once it's saved or the form comes back, since the code isn't known while you type; the database marks it either way.) The chart leaves sensitive items out and says how many it left out. **Show sensitive items** reloads the chart with them, each tagged "Sensitive", and that view is recorded in the audit log as `read_sensitive`. The history page follows the same rule, and so does opening a sensitive entry's form. While sensitive items are shown, going to the history or a form and back keeps them shown. Sensitive items are never in the patient banner, and the desktop app doesn't keep them offline.

## Decisions

- **Who.** Clinicians only, and only for a patient on their panel, as for orders and notes. Under emergency access the chart and the history can be read, and nothing can be changed.
- **Data.** The existing tables, `ehr.allergy_intolerances`, `ehr.medication_statements` and `ehr.conditions`, gain `sensitive` (the last two), and `severity` and `code_system` (allergies). `ehr_app` gets INSERT and UPDATE on them, and never DELETE. Row-level security: a clinician, for a patient on their active panel. A trigger refuses moving an entry to another patient or ID.
- **Codes.** `ehr.clinical_codes`, reference data that nothing at runtime changes, loaded from `db/reference`. A new entry stores the code, its system (for allergies) and the source's own name.
- **History.** `ehr.chart_history`, written only by a trigger that runs as its owner, so it can't be skipped or edited. Clinicians read it. The write-audit trigger records the same changes for admins.
- **Sensitive.** The database sets the flag for a diagnosis whose code is in a sensitive range, whatever the form sent, so it can't be cleared. The reader leaves sensitive rows out unless asked, and records the asking as its own audit action, on-site and off-site.
- **"No known allergies".** An entry of its own, with the app's own marker; the SNOMED CT code it used before is gone. The database keeps the list to one state: it refuses "No known allergies" for a patient with an active allergy, and makes it inactive when an active allergy is added, in the same transaction, by the same clinician, with the history and the write audit recording both. A patient has at most one active "No known allergies": the guard trigger refuses a second, and a unique partial index (`ix_allergy_intolerances_one_no_known_allergy`) holds it even with the triggers off. The chart treats a second click as already recorded.
- **Admins and sensitive values.** The audit page shows that a sensitive item changed and who changed it, never its values, and the same for the events written before the item was marked. The runtime roles can't read the audit table's values column at all; the page asks `ehr.shown_audit_details`, which answers only admins and leaves sensitive items out. The full record stays in the table. See `docs/UC12.md`.
- **Banner.** The patient banner now shows allergies on the note, dictation and review pages, the history and the discontinued orders too, read with the patient in the one read each page already records; on the history and the discontinued orders it also carries the emergency-access tag while that access lasts, as on the chart.
- **Search.** `/codes` returns up to 20 names that start with what was typed, then names with a word that starts with it, then diagnoses by code. It reads nothing about a patient. With today's subset it is a small scan; the full lists in step 2a need an index for it.
- **Off-site.** Reads come from the cloud copy; a save is forwarded to the clinic's server. While that can't be reached the chart offers no Add or Change and says "Changes to diagnoses need the clinic's server."
- **Desktop.** The offline chart already held the three lists. It doesn't hold sensitive items, and says how many aren't kept.

## Test cases

One file each, in `tests/Ehr.Web.Tests/TestCases/UC4/`, continuing UC 4's numbering.

| Test case | What it checks | File |
|---|---|---|
| TC-UC4-07, successful flow | A coded diagnosis, medication and allergy are added; the chart, history and audit show them | `TC_UC4_07_AddEntrySuccessfulFlow.cs` |
| TC-UC4-08, alternative flow | An entry is changed or stopped, never deleted; an uncoded entry keeps its name | `TC_UC4_08_ChangeEntryAlternativeFlow.cs` |
| TC-UC4-09, invalid input | A name that isn't a code, a bad date or status, and an allergy's bad severity, risk or over-long reaction are highlighted and nothing is saved | `TC_UC4_09_EntryInvalidInput.cs` |
| TC-UC4-10, exceptional condition | Off the panel, or off-site with the server down, nothing is changed | `TC_UC4_10_EntryExceptionalCondition.cs` |
| TC-UC4-11, NFR-01 | The entry form, code search, history and chart with sensitive items: p99 under 15 ms at 50,000 patients | `TC_UC4_11_Nfr01_ListPagesOnSite.cs` |
| TC-UC4-12, NFR-12 | In a real browser, adding a coded diagnosis with the keyboard only | `TC_UC4_12_Nfr12_EntryKeyboardFirst.cs` |
| TC-UC4-13, alternative flow | The allergy list's three states on the chart, the banner and the offline copy; "No known allergies" recorded, and cleared by adding an allergy | `TC_UC4_13_AllergyStatus.cs` |
| TC-UC4-32, alternative flow | Sensitive items left out of the chart, banner, history and offline copy until asked for; each view that shows them recorded as `read_sensitive` (proposed FR-39). Numbered 32 so it stays unique when merged with the later steps' TC-UC4-14 to 31 | `TC_UC4_32_SensitiveItems.cs` |

Extra coverage in `tests/Ehr.Web.Tests/ChartListTests.cs`: the code search and who may use it; sensitive items left out until asked for, with the audit action, on-site and off-site, and kept shown through the history and the forms; allergies in the banner on the note, dictation, history and discontinued orders pages; a save forwarded from off-site; the "Saved:" and "Not saved:" notices, a patient taken off the panel mid-save, and "No known allergies" after a colleague's allergy; a form posted twice; the fixed "Sensitive" box; a diagnosis saved by its typed code; the first field to fix taking the focus; the search criteria never taking the entry's name; the form never cached.

In `tests/Ehr.Security.Tests/ChartListSecurityTests.cs`, against the database directly: who may add and change entries; nothing deletes or moves one; the history and the audit record each change; the sensitive ranges; a sensitive code can't be unmarked; who reads the history; the reference codes are loaded.

## Human review

These need line-by-line review (AGENTS.md):

- `db/policies/65-chart-lists.sql`: the sensitive ranges, the guard trigger, and the `SECURITY DEFINER` history trigger.
- `db/policies/10-grants.sql`, `30-rls.sql` and `40-audit.sql`: the grants and policies for the three lists, `chart_history` and `clinical_codes`.
- The panel rule on the entry page (`Areas/Clinical/Pages/Patients/Lists/Entry.cshtml.cs`), and `CloudAuditEvents`, which now accepts `read_sensitive`.
- The new `PhiReader` methods.

## Limits

- Notes can't be marked sensitive yet (the plan's step 10).
- "No known allergies" is cleared by adding an allergy; there is no button to take it back on its own.
- Allergies and diagnoses can't be marked entered in error yet; a mistaken one is made inactive. (Medications can.)
- A medication isn't sensitive by rule; that waits for drug-class data.
- The reference codes are a subset until step 2a.
- The suggestions are the browser's own list, which differs a little between browsers.
