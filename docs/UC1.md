# UC 1 — Place an order

SwiftChart EHR · Tahseen Arnan · CS 3354 Software Engineering, Fall 2026

## Clinician workflow

Open a patient on your panel and choose **New order** on the chart, or press Alt+Shift+O. The order form shows the patient's name and allergies at the top. Pick a type: lab, imaging, medication or referral. Type the item; matching items from that type's catalog are suggested as you type. Fill in the type's details; required ones are marked.

**Sign order** checks for missing fields and for an active order of the same item, saves the order on the on-site primary, and takes you back to the chart, which confirms the order and lists it under **Active orders**. **Cancel** goes back to the chart and saves nothing.

| Type | Required | Optional |
|---|---|---|
| Lab | Reason for the test | Notes for the lab |
| Imaging | Reason for the exam | Notes for imaging |
| Medication | Dose, route, frequency | Duration |
| Referral | Reason for referral | Notes for the specialist |

### Changing and discontinuing an order

A signed order is never edited in place. Each active order on the chart has two links, for a patient on your panel while the clinic's server can be reached:

- **Discontinue…** opens a page with the patient's banner and the order, and asks for the reason, which is required. **Discontinue order** records the order as discontinued, with the reason, your name and the time.
- **Change** opens the order form filled with the order's item and details. Change what you need, add a reason if you want one, and choose **Sign changed order**: that signs a new order and discontinues the old one as replaced, together. The type stays the same; for another type, discontinue the order and place a new one.

**Discontinued orders**, under the active orders, lists every discontinued order of the patient, newest first, with what it was, who discontinued it, when and why, and a "Changed" tag when a changed order replaced it. It is open to whoever the chart is open to, emergency access included.

### Results and their acknowledgment

Added in the expansion plan's step 7 (Tahseen Arnan).

- **Enter results.** Each active lab order on the chart has **Enter results**, for a patient on your panel. The page lists the tests the order reports, each with its unit and reference range. Type the values the lab reported and the time the sample was collected, and choose **File results**. Leave a test empty if its result hasn't come; it can be added later. Until the lab sends results itself (step 12), this is how they arrive.
- **Resulted.** With its first result the order becomes completed and leaves the active orders. The chart's **Results** section lists resulted orders: those still to acknowledge first, abnormal ones before the rest.
- **Abnormal.** A value outside its reference range is flagged "High" or "Low" and listed first, on the results page, the chart and the inbox.
- **Acknowledge.** The clinician who signed the order gets an inbox item, "Results are in", with the patient's name and no values. **Open the results**, then **Acknowledge results**, records who acknowledged and when, and clears the item. It covers the results that were on the page: if another arrived meanwhile, the page comes back with it and nothing is recorded yet. A result's inbox item can't be ticked off any other way. Any clinician with the patient on their panel can acknowledge. Once acknowledged, nothing more is added to that order's results.
- **The patient** sees a filed result in the portal at once, as results have always shown there, acknowledged or not.

### Safety checks on a medication order

Added in the expansion plan's step 8 (Tahseen Arnan).

- **What is checked.** When a medication order is signed, the database checks it against the patient's coded allergies (the drug itself, another drug of the same FDA class, and classes known to cross-react) and against what the patient takes (active medications on the chart and other active medication orders) for known interactions.
- **What the form shows.** Each warning names the problem, with its severity, one general line of advice, and its source: the chart, DDInter 2.0, the ONC high-priority list (by way of Wikidata), or an FDA label, with what the label says and where to read it.
- **What stops the order.** An allergy; a drug of a class known to cross-react with one the patient is allergic to (the penicillins, cephalosporins and penems, from a curated table); or a major or high-priority interaction: the form comes back headed **Check before signing**, with the cursor in **Reason for ordering despite the warnings**. Give a reason and sign, or cancel. The reason and what was found are kept on the order and in the write audit.
- **What doesn't.** A moderate or minor interaction, and a drug that only shares an FDA class with an allergy (those classes group drugs by how they act, which is not how most allergies run), are shown under **Be aware** and kept on the order, without asking for a reason. The first **Sign order** brings the notes up with the cursor on the button; pressing it again signs.
- **What it is not.** The form says so on every medication order: decision support for this demonstration system, not a complete clinical reference. Only coded allergies and medications are checked, and no warning doesn't mean a combination is safe. `docs/expansion/reference-data.md` says what the interaction data covers and what it doesn't.

## Decisions

- **Who.** Clinicians only, and only for a patient on their panel, as for "New note" and dictation. Under emergency access the chart says to add the patient to the panel first, and the order page sends anyone off the panel back to the chart before anything is read.
- **Data.** One table, `ehr.orders`: the patient, type, catalog item, the type's details (a JSON object), status, signer, signing time and duplicate reason. The database sets the signer (the signed-in user, which row-level security holds it to) and the signing time (the clock's, in the signing trigger), as for signed notes.
- **Never edited.** There is no DELETE grant on orders and no table-wide UPDATE. An order is signed `active`. The one change afterwards is discontinuing it; completing an order is not built.
- **Discontinuing.** The order's status becomes `revoked`, FHIR's request status for a stopped order, which the screens call "Discontinued". New columns hold the reason (required), who and when. `ehr_app` may update three columns only: `status`, `discontinue_reason` and `replaced_by_order_id`. Row-level security lets a clinician update only orders of a patient on their active panel, the rule for signing one. The `ehr.discontinue_order()` trigger (`db/policies/60-orders.sql`) holds the update to one change: active to revoked, once, with a reason, no other column touched, and it sets who and when from the session and the clock. So nothing reactivates an order, discontinues it twice or rewrites its reason. Any clinician with the patient on their panel may discontinue a colleague's order, and is the one recorded.
- **Changing.** A change is a new order with `replaces_order_id`, and the old order discontinued with `replaced_by_order_id`, in one savepoint of the page's transaction. The signing trigger accepts a replacement only for an active order of the same patient and type. A deferred constraint trigger (`ehr.check_order_replacement()`) refuses, at commit, a replacement whose old order isn't discontinued and naming it, and the discontinue trigger accepts "replaced by" only from the order that names this one, signed by the same clinician. So the links always point both ways and both writes happen or neither.
- **Duplicates and a change.** The order being replaced isn't counted as a duplicate of its replacement. Any other active order for the item still is, and still needs a reason.
- **Someone got there first.** If the order was discontinued or changed while the page was open, nothing is saved: the page answers 409 and says the order is no longer active, with what was typed still on screen. A link to an order that is no longer active goes back to the chart.
- **Catalog.** `ehr.order_catalog_items`: 29 synthetic items across the four types, seeded by the `Orders` migration. The codes are plain synthetic ones (`SYN-LAB-001`, …), because the synthetic clinic records its medications without codes. No licensed code set is used. The catalog is not PHI.
- **Suggestions.** A `datalist` of the type's items. The order page turns htmx off, since its address carries the patient.
- **Duplicates.** An active order for the same patient and catalog item. The page warns and asks for a reason. The database enforces it: the signing trigger (`db/policies/60-orders.sql`) locks the patient's row, then refuses a second active order for the item unless a reason is given, so two submissions at once can't both get through. The trigger refuses any transaction that isn't READ COMMITTED, because at a stricter level the waiting transaction would check against an old snapshot.
- **Database access.** Row-level security is enabled and forced. A clinician inserts only as themselves and only for a patient on their active panel. Reads follow the `clinical_notes` policy: any clinician. Grants: `ehr_app` SELECT and INSERT; `ehr_read` SELECT. The standard write-audit trigger records each order with the clinician and the time.
- **Change log.** No change-log trigger: the desktop app's chart cache doesn't hold orders, so nothing needs their changes today.
- **Reads of the new pages.** The discontinue page and the change form are each one recorded read of the patient (`PhiReader.OrderFormAsync`), and so is the discontinued orders page (`PhiReader.DiscontinuedOrdersAsync`), which joins the staff account for the name. The chart doesn't count or load discontinued orders: it only links to them, so its render time is unchanged.
- **Reads.** Through `PhiReader`. The order form's banner and allergies are one recorded read. The chart's active-orders list loads only the fields it shows and is part of the chart's one recorded read, with no audit row per order. `CloudAuditEvents` did not need widening: both reads are recorded against `patients`, as the chart's already was.
- **Off-site.** The cloud app forwards the post to the on-site server. `js/orders.js` posts the form with `fetch`; on a 503 or a network failure the form stays on screen with its entries and says nothing was saved. Without JavaScript the form posts normally. While the site is read-only the chart offers no "New order", as for "New note", and no "Change" or "Discontinue…": it says "Changing or discontinuing an order needs the clinic's server." The discontinue page posts the same way as the order form, and its reason is shown again on the cloud app's "Not saved" page.
- **Shortcut.** Alt+Shift+O, set in `aria-keyshortcuts`. It is clear of "/", Ctrl/Cmd+S and dictation's Alt+Shift+D.
- **FR-14.** `DictationOrdersTests` scans the dictation code and fails if any of it names a type from the order code.

## Test cases, courses of events and their tests

The Assignment 4 test cases are one file each, in `tests/Ehr.Web.Tests/TestCases/UC1/`. Each file's header holds the test case's fields: ID, purpose, type, requirements, preconditions, inputs, test data, steps, expected result and creator.

| Test case | What it checks | File |
|---|---|---|
| TC-UC1-01, successful flow | A clinician signs a lab order; the chart confirms and lists it; the audit row names the clinician and the time | `TC_UC1_01_SuccessfulFlow.cs` |
| TC-UC1-02, alternative flow | A match with an active order warns; saved only once a reason is given | `TC_UC1_02_AlternativeFlow.cs` |
| TC-UC1-03, invalid input | An empty required field is highlighted, nothing is saved, the entries stay | `TC_UC1_03_InvalidInput.cs` |
| TC-UC1-04, exceptional condition | Off-site with the on-site server unreachable, the order isn't saved and the form keeps its entries | `TC_UC1_04_ExceptionalCondition.cs` |
| TC-UC1-05, NFR-01 | On-site, the chart with active orders and the order form: p99 under 15 ms, the commit included, at 50,000 patients with durability on | `TC_UC1_05_Nfr01_OnSiteNavigation.cs` |
| TC-UC1-06, NFR-12 | Keyboard-first: the shortcut, autofocus, labelled fields and real buttons, signing without a mouse | `TC_UC1_06_Nfr12_KeyboardFirst.cs`, and its manual browser walkthrough, `TC_UC1_06_Nfr12_KeyboardFirst.md` |

| TC-UC1-07, successful flow | A clinician discontinues an order with a reason; the chart keeps it under discontinued orders with who, when and why; the audit row names the clinician | `TC_UC1_07_DiscontinueSuccessfulFlow.cs` |
| TC-UC1-08, alternative flow | A change signs a new order and discontinues the old one as replaced, linked both ways; the replaced order isn't a duplicate, another active one still is | `TC_UC1_08_ChangeAlternativeFlow.cs` |
| TC-UC1-09, invalid input | No reason, or an empty required field in a change, is highlighted and nothing is saved | `TC_UC1_09_ChangeInvalidInput.cs` |
| TC-UC1-10, exceptional condition | An order someone else discontinued first isn't changed again; off-site with the server down, the actions are off the chart and a post isn't saved | `TC_UC1_10_ChangeExceptionalCondition.cs` |
| TC-UC1-11, NFR-01 | On-site, the discontinued orders, the change form and the discontinue form: p99 under 15 ms at 50,000 patients | `TC_UC1_11_Nfr01_ChangePagesOnSite.cs` |
| TC-UC1-12, NFR-12 | In a real browser, discontinuing and changing an order with the keyboard only | `TC_UC1_12_Nfr12_ChangeKeyboardFirst.cs` |

| TC-UC1-13, successful flow (results) | Results are filed against their order, the abnormal one flagged and first, the order resulted; acknowledged from the inbox, with who and when | `TC_UC1_13_ResultsSuccessfulFlow.cs` |
| TC-UC1-14, alternative flow and invalid input (results) | Results in parts; values that aren't numbers; a colleague on the care team files and acknowledges; emergency access reads only; nothing is added after acknowledgment | `TC_UC1_14_ResultsAlternativeAndInvalid.cs` |
| TC-UC1-15, exceptional condition (results) | Off-site, filing and acknowledging are forwarded; with the server down the results are read-only | `TC_UC1_15_ResultsExceptionalCondition.cs` |
| TC-UC1-16, NFR-01 (results) | An order's results page, and the inbox holding results: p99 under 15 ms at 50,000 patients | `TC_UC1_16_Nfr01_ResultPagesOnSite.cs` |
| TC-UC1-17, NFR-12 (results) | In a real browser, entering and acknowledging results with the keyboard only | `TC_UC1_17_Nfr12_ResultsKeyboardFirst.cs` |

`tests/Ehr.Security.Tests/ResultSecurityTests.cs` asks the database directly: a filed result gets the database's own name, unit and range; only a panel clinician files, only for a test of an open lab order of that patient; an order is completed only by its results and then never changes; results are acknowledged once, by a panel clinician as themselves, which ends the filing. TC-UC6-12 is the portal's side and TC-UC15-07 the inbox's.

| TC-UC1-18, alternative flow (safety) | An allergy, and a high-priority and a major interaction, each stop the order until a reason is given; each warning names its severity, advice and source | `TC_UC1_18_SafetyWarningsOverride.cs` |
| TC-UC1-19, alternative flow and exceptional condition (safety) | A moderate interaction is shown and kept without a reason; an uncoded allergy and a lab order raise nothing; off-site the warning shows from the cloud copy | `TC_UC1_19_SafetyInformationOnly.cs` |
| TC-UC1-20, NFR-12 (safety) | In a real browser, a stopped order is answered and signed with the keyboard only | `TC_UC1_20_Nfr12_SafetyKeyboardFirst.cs` |

`tests/Ehr.Security.Tests/OrderSafetySecurityTests.cs` asks the database directly: an order for a drug the patient is allergic to needs a reason, kept with what the database found (not what the client claimed); same-class and cross-reactive classes; interactions from each source, with only serious ones needing a reason; and only a clinician is told anything.

TC-UC1-05, TC-UC1-11 and TC-UC1-16 run only against a run of the render benchmark; `tests/Ehr.Web.Tests/TestCases/PerformanceResults.cs` says how. In the normal test run they are skipped.

Extra coverage, without test case IDs, in `tests/Ehr.Web.Tests/OrderTests.cs`:

| Course of events | Test |
|---|---|
| A role that can't order sees no "New order" and is refused | `A_role_that_cannot_order_sees_no_new_order_and_is_refused` |
| Off-site, the order is forwarded and saved on-site | `Off_site_the_order_is_forwarded_and_saved_on_site` |
| Cancel saves nothing | `Cancel_saves_nothing` |
| A clinician off the panel is offered no change or discontinue, is refused both, and can still read the discontinued orders under emergency access | `A_clinician_off_the_panel_is_offered_no_change_or_discontinue_and_is_refused` |
| Off-site, a discontinuation and a change are forwarded and saved on-site | `Off_site_a_discontinuation_and_a_change_are_forwarded_and_saved_on_site` |
| A link to an order that is no longer active goes back to the chart | `A_link_to_an_order_that_is_no_longer_active_goes_back_to_the_chart` |

In `tests/Ehr.Security.Tests/OrderSecurityTests.cs`, against the database directly:

| Rule | Test |
|---|---|
| The database sets the time; the audit row names the clinician and the time | `The_database_sets_the_signing_time_and_the_audit_row_names_the_clinician_and_the_time` |
| An admin, a patient, a disabled clinician, nobody and the read role can't insert | `Inserts_by_an_admin_a_patient_a_disabled_clinician_nobody_or_the_read_role_are_refused` |
| Signing as someone else | `Signing_as_someone_else_is_refused` |
| A patient off the panel | `An_order_for_a_patient_off_the_clinicians_panel_is_refused` |
| No deletes, and no edits outside the three columns discontinuing writes | `Orders_cannot_be_deleted_or_edited` |
| A duplicate needs a reason | `A_duplicate_active_order_without_a_reason_is_refused_and_one_with_a_reason_is_kept` |
| Two duplicates at once | `Two_duplicates_at_once_cannot_both_get_through` |
| Only READ COMMITTED | `An_order_outside_a_read_committed_transaction_is_refused` |
| Only active orders | `An_order_that_is_not_active_is_refused` |
| The item is of the order's type | `An_order_names_a_catalog_item_of_its_own_type` |
| Who reads orders | `Clinicians_read_every_order_and_nobody_else_reads_any` |

In `tests/Ehr.Security.Tests/OrderChangeSecurityTests.cs`, against the database directly:

| Rule | Test |
|---|---|
| Discontinuing records the reason; the database sets who and when; the audit row has the old and new status | `Discontinuing_records_the_reason_and_the_database_sets_who_and_when_and_audits_it` |
| A colleague with the patient on their panel discontinues as themselves | `A_colleague_with_the_patient_on_their_panel_discontinues_as_themselves` |
| A clinician off the panel or whose assignment ended, an admin, a patient, a disabled clinician and nobody change nothing | `Nobody_but_a_clinician_with_the_patient_on_their_panel_discontinues_an_order` |
| A reason is required | `Discontinuing_needs_a_reason` |
| Active to discontinued, once; no other status; no reactivating | `An_order_changes_only_once_from_active_to_discontinued` |
| Two discontinuations at once | `Two_discontinuations_at_once_cannot_both_get_through` |
| A change links both orders | `A_change_signs_a_new_order_and_discontinues_the_old_one_linked_both_ways` |
| A replacement is still checked against other active orders | `A_replacement_that_duplicates_another_active_order_still_needs_a_reason` |
| A replacement that leaves the old order active, or unlinked, isn't kept | `A_replacement_that_leaves_the_old_order_active_is_not_kept` |
| Only an active order of the same patient and type is replaced | `An_order_replaces_only_an_active_order_of_the_same_patient_and_type` |
| "Replaced by" only names the order that names this one | `An_order_is_marked_replaced_only_by_the_order_that_names_it` |
| The column grant | `The_app_role_may_update_only_the_columns_discontinuing_writes_and_the_read_role_none` |

## Human review

These need line-by-line review by someone other than Tahseen (AGENTS.md):

- `db/policies/60-orders.sql`: the signing trigger, with its isolation check, the patient row lock, the status check, the duplicate check and the signing time.
- `db/policies/10-grants.sql`, `30-rls.sql` and `40-audit.sql`: the grants, the two policies and the audit trigger for orders.
- For changing and discontinuing: in `db/policies/60-orders.sql`, the signing trigger's replacement check and its exception to the duplicate check, `ehr.discontinue_order()` and `ehr.check_order_replacement()` with its deferred constraint trigger; in `10-grants.sql`, the UPDATE grant on three columns; in `30-rls.sql`, the `orders_panel_update` policy; the `OrderChanges` migration's check constraints; and the panel rule on the discontinue page and on the chart's two links.
- The order page's panel rule (`Areas/Clinical/Pages/Patients/Orders/New.cshtml.cs`) and the chart's condition for offering "New order".
- The new `PhiReader` methods. `CloudAuditEvents` is unchanged.
- For safety checks (step 8): the new `db/policies/61-order-safety.sql` (`ehr.order_safety_warnings`, which runs as the caller); in `60-orders.sql`, the part of `ehr.sign_order()` that finds the warnings, keeps them and requires a reason for serious ones.
- For results (step 7): the new `db/policies/75-results.sql` (`ehr.deliver_result` and `ehr.clear_result_items` run as their owner); in `60-orders.sql`, the first branch of `ehr.discontinue_order()`, which lets an active order become completed when a final result exists; in `30-rls.sql`, `observations_panel_insert`, the policies on `result_acknowledgments`, and the check of `orders_panel_update`; `INSERT` on `observations` in `10-grants.sql`; and the gate of the results page (`MessagePage.GateAsync`).

## Limits

- A discontinuation can't be undone: place the order again instead. A resulted order can't be discontinued or changed.
- Results are numbers typed in for lab orders whose catalog item has result fields set up (12 items today, including the common panels). Other lab tests, imaging and referrals have no results page content yet. Nothing imports results; the simulated lab is step 12.
- A filed result can't be corrected or withdrawn. A wrong value needs a new order.
- The reference ranges are one range for each test, for adults, not by age or sex.
- The inbox item goes to the clinician who signed the order, even if they have since left the patient's panel; any panel clinician can acknowledge from the chart. If the signer's account is disabled or no longer a clinician's, it goes to each clinician on the patient's panel instead.
- The chart lists a patient's eight most relevant resulted orders (unacknowledged and abnormal first); there is no page of older ones yet.
- The inbox looks at the oldest 500 waiting results when deciding which are abnormal.
- Results are released to the portal as soon as they are filed, before anyone acknowledges them.
- Collection time is entered and shown in UTC.
- A change keeps the order's type.
- The chart's confirmation after discontinuing doesn't name the order; the discontinued orders list does.
- An order isn't sent anywhere: no lab, pharmacy or referral interface.
- The catalog is small and synthetic, and changing it takes a migration.
- Safety checks see only coded data: an allergy or medication typed without a code is not checked. Food and other non-drug allergies are not checked against drugs.
- An interaction is known only if one of three sources has the pair. Whole classes are still thin (an ACE inhibitor with an angiotensin receptor blocker beyond lisinopril with losartan, for example): see `docs/expansion/reference-data.md`.
- A DDInter warning links to DDInter's interaction checker, not to a page for the pair.
- The reason for overriding is kept on the order and in the audit, but the chart doesn't show it beside the order yet.
- A warning names a medication the patient takes even when it is marked sensitive: the clinician signing the order needs to know. What the order and its audit keep says only "a medication marked sensitive", since administrators can read an order's audit details.
- An item with no drug code (the albuterol inhaler among the common items) can't be checked, and the form says only coded items are.
- A warning that appears between opening the form and signing is caught by the database and shown; adding a medication at the same instant as an order is signed can slip past.
- Doses, kidney function, age and pregnancy are not considered.
- The form's behaviour with `fetch` in a browser is checked by hand; the tests cover the server side.
- With `--stack`, the render benchmark leaves its 600 orders on the primary, as it leaves its audit events.
