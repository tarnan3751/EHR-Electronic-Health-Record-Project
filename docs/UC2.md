# UC 2 — Read records during an on-site outage

SwiftChart EHR · Tahseen Arnan · CS 3354 Software Engineering, Fall 2026

## What it does

A clinician already signed in, off-site, keeps reading charts, search results and their panel while the clinic's on-site server can't be reached. The cloud app serves those pages from its replica as `ehr_read`, records each read in its own audit queue (a file flushed to disk before the page is sent), and every page says the system is read-only. A write is refused with an explanation, and what the clinician typed is kept. When the on-site server is back, the queued reads ship to it within a couple of seconds and writing works again. If the clinic's data has to be recovered, Barman restores the primary to a point in time.

Most of this was built with the cloud milestone (forwarding, replica reads, the read-only notice, the "Not saved" page, the audit queue and recovery). This change measures off-site render time (NFR-03), keeps typed text on the "Not saved" page, stops pages offering changes that can't be saved, and adds tests for the notes, draft sync and dictation that landed since.

## Course of events, code and tests

The tests are in `tests/Ehr.Web.Tests` unless named otherwise. `CloudCutOff` is the cloud app with its link to the on-site server down; `CloudPrimaryDrill` is the on-site server up but its database down (`WebApps.cs`).

| Step | What happens | Code | Tests |
|---|---|---|---|
| 1. Off-site, signed in, the clinician opens search, the panel or a chart | Served from the replica as `ehr_read`; reads aren't forwarded | `Program.cs` (cloud registrations), `Forwarding/CloudForwardingMiddleware.cs`, `Ehr.Domain/Phi/PhiReader.cs` | `PatientSearchTests.Off_site_during_an_outage_search_and_as_you_type_come_from_the_replica_and_their_reads_are_queued_then_shipped`, `TestCases/UC2/TC_UC2_01_SuccessfulFlow.cs` (TC-UC2-01), `OffSiteOutageTests.The_panel_is_read_from_the_replica_and_each_patient_listed_is_audited`, `CloudForwardingTests.With_the_on_prem_app_unreachable_reads_come_from_the_replica`, `CloudForwardingTests.During_an_outage_reads_after_a_save_come_from_the_replica` |
| 2. Each read is audited | One event per patient read, written to the cloud app's queue before the page is sent: a request's events together, with one flush to disk (the plan's step 10c) | `Ehr.Domain/Audit/AuditRecorder.cs`, `CloudAudit/RequestAuditEvents.cs`, `CloudAudit/CloudAuditQueue.cs`, `CloudAudit/CloudAuditEvents.cs` | `CloudAuditTests.With_the_primary_database_down_off_site_pages_say_read_only_and_reads_stay_queued`, `CloudAuditTests.An_off_site_search_queues_one_read_per_patient_listed_and_ships_them_from_the_cloud`, `CloudAuditQueueTests` |
| 3. The site says it is read-only | The shipper's check every 2 s finds the on-site server can't save; every page shows the notice | `Forwarding/OnPremStatus.cs`, `CloudAudit/CloudAuditShipper.cs`, `Pages/Shared/_Layout.cshtml` | `TestCases/UC2/TC_UC2_04_ExceptionalCondition.cs` (TC-UC2-04), `CloudAuditTests.A_save_answered_with_a_server_error_leaves_the_read_only_notice_as_it_was` |
| 4. A write is refused with an explanation, and what was typed is kept | 503 and the "Not saved" page, which shows the typed text again; htmx and the page scripts keep the form; read-only pages offer no change | `CloudForwardingMiddleware.cs`, `Forwarding/ShownIfNotSavedAttribute.cs`, `Ehr.Design/wwwroot/js/request-errors.js`, `note-editor.js`, `dictation.js`; the pages in the table below | `CloudForwardingTests.With_the_on_prem_app_unreachable_a_save_fails_clearly`, `TestCases/UC2/TC_UC2_03_InvalidInput.cs` (TC-UC2-03), `CloudForwardingTests.An_htmx_post_that_cannot_be_forwarded_still_gets_the_short_message`, `OffSiteOutageTests` (the first five tests), `PatientPanelTests.With_the_on_prem_app_unreachable_the_chart_offers_no_changes`, `BreakGlassTests.With_the_on_prem_app_unreachable_emergency_access_is_not_offered`, `DictationPageTests.Off_site_with_the_clinics_server_down_no_dictation_is_offered` |
| 5. Signing out still works | The cloud app signs out itself and queues the event | `Forwarding/HandledOffSiteAttribute.cs`, `Areas/Clinical/Pages/Account/SignOut.cshtml.cs` | `StaffSignInTests.Off_site_signing_out_works_while_the_on_prem_app_cant_save`, `CloudForwardingTests.Signing_out_off_site_is_done_by_the_cloud_app_and_recorded_from_there` |
| 6. The on-site server is back | The next check ships the queued reads (as source `cloud`), the notice goes, and saves work again | `CloudAuditShipper.cs`, `Api/Audit/CloudAuditApi.cs` | `CloudAuditTests.The_read_only_notice_goes_once_the_on_prem_app_is_back_and_the_reads_arrive`, `TestCases/UC2/TC_UC2_02_AlternativeFlow.cs` (TC-UC2-02) |
| Alternative: restore | Barman restores the primary to a point in time | `infra/barman`, `infra/compose` ("Backup and point-in-time restore") | The drill below |
| NFR-03: off-site render time | p99 server time under 20 ms, excluding the commit, at 50,000 patients | `bench/Ehr.Bench/RenderBenchmark.cs` (`OffSiteApp`, `TimedAuditQueue`), CI's benchmark job | `BenchmarkTests.Off_site_the_audit_queues_append_counts_as_the_commit`; the run below |
| UC 14, UC 9 during an outage | Notes and the dictation page are read from the replica and audited; signing, new notes and dictation are refused | `Notes/*.cshtml`, `Dictate.cshtml`, `Api/Dictation` | `OffSiteOutageTests.Notes_are_read_from_the_replica_and_audited_and_note_writes_are_refused`, `OffSiteOutageTests.The_dictation_page_read_is_audited_and_dictation_is_refused`, `ClinicalNoteTests.Primary_outage_refuses_signature_and_keeps_the_server_draft_available`, `DictationApiTests.Off_site_with_the_link_down_dictation_says_the_server_cannot_be_reached` |
| UC 1 during an outage | An order is refused, and what was entered is kept | `Orders/New.cshtml.cs` (`ShownIfNotSaved`), `Ehr.Design/wwwroot/js/orders.js` | `OffSiteOutageTests.An_order_placed_during_an_outage_is_refused_and_its_entries_are_kept`, `TestCases/UC1/TC_UC1_04_ExceptionalCondition.cs` (TC-UC1-04) |
| UC 8 (desktop) during an outage | Draft push and pull, and the offline panel, are refused (503); quick text sync still reads the replica | `Api/Desktop/DraftSyncApi.cs`, `Api/Desktop/DesktopApi.cs` | `OffSiteOutageTests.The_desktop_apps_draft_sync_and_offline_panel_are_refused_and_quick_text_sync_still_reads` |
| Signing in during an outage | Not possible; see Limits | `CloudForwardingMiddleware.cs` | `OffSiteOutageTests.During_an_outage_nobody_can_sign_in_off_site` |

## Assignment 4 test cases

One file each, in `tests/Ehr.Web.Tests/TestCases/UC2/`, with the test case's fields in the file's header.

| Test case | What it checks | File |
|---|---|---|
| TC-UC2-01, successful flow | Off-site during an outage, a chart is read from the replica with the read-only notice, and the read is queued, then shipped | `TC_UC2_01_SuccessfulFlow.cs` |
| TC-UC2-02, alternative flow | Once the on-site server is back, the notice goes, the queued reads arrive and saves work again | `TC_UC2_02_AlternativeFlow.cs` |
| TC-UC2-03, invalid input | A change submitted during the outage is refused with the "Not saved" page, and what was typed is shown again | `TC_UC2_03_InvalidInput.cs` |
| TC-UC2-04, exceptional condition | The on-site app answers but its database is down; the site stays read-only | `TC_UC2_04_ExceptionalCondition.cs` |
| TC-UC2-05, NFR-03 | Off-site, every page: p99 under 20 ms of server time excluding the commit, at 50,000 patients with durability on | `TC_UC2_05_Nfr03_OffSiteNavigation.cs` |
| TC-UC2-06, NFR-09 | One authoritative copy: the read role can write no table; a refused write changes nothing; after recovery the save lands once, on the primary. The restore drill below is supporting evidence | `TC_UC2_06_Nfr09_OneAuthoritativeCopy.cs` |
| TC-UC2-07, exceptional condition (audit before the page) | Off-site, a page's audit events are written together, once, before it is sent; when they can't be written, the page is not sent | `TC_UC2_07_AuditBeforeThePage.cs` |

TC-UC2-05 runs only against a run of the render benchmark; `tests/Ehr.Web.Tests/TestCases/PerformanceResults.cs` says how. In the normal test run it is skipped.

## Every write form, when the on-site server goes down between loading the page and sending it

| Form | With JavaScript | Without JavaScript | Loaded while read-only |
|---|---|---|---|
| Chart: Add to / Remove from my patients | "Not saved" page; nothing was typed | Same | Not offered |
| Chart: Issue an enrollment code | "Not saved" page; nothing was typed | Same | Not offered |
| Chart: New note | "Not saved" page; nothing was typed | Same | Not offered |
| Emergency access reason | The page uses a plain form either way: "Not saved" page with the reason shown again | Same | Not offered (says why) |
| Note editor: Save draft, autosave, Sign note… | The text stays on screen with "could not confirm this save" | "Not saved" page with History, Assessment and Plan shown again | **New:** the draft as last saved, no editor |
| Note confirmation: Confirm and sign | The page stays, "Signature not confirmed" | "Not saved" page; only a checkbox was set | **New:** no signature offered |
| Dictation | Recording carries on; the audio waits in the page and is sent again when the connection returns | Needs JavaScript; not offered | Not offered |
| Order form (UC 1): Sign order | The form stays filled and says nothing was saved (`orders.js`) | "Not saved" page with the item, the details and the duplicate reason shown again | The chart doesn't offer "New order" |
| Quick text: Add, Save, Keep mine | htmx leaves the form as typed and says "Not saved" | "Not saved" page with Shortcut and Body shown again | **New:** no add form or Edit buttons |
| Sign-in steps (password, code, enrollment, new password, setup) | "Not saved" page; nothing typed is shown back | Same | Still offered (sign-in unchanged) |
| Sign out | Works: the cloud app does it and queues the event | Same | Offered |

"Shown again" means a read-only box to copy from, on the "Not saved" page, only for a form the browser sent from this site (`Sec-Fetch-Site: same-origin`), never a password or code, and sent with `no-store`. The text is held in memory while the form is forwarded, never on the cloud server's disk, so a form over 256 KB isn't shown again.

## Off-site render time (NFR-03)

The benchmark (`bench/Ehr.Bench`, see "Synthetic data and the benchmark" in the README) now makes the same requests off-site after the on-site ones: to the cloud app reading as `ehr_read`, with its link to the on-site server down, as in this use case. The cloud app records reads in its audit queue rather than the database, so in the off-site rows the audit insert is 0 and the queue's write and flush to disk count as the commit: that is where the page waits for the disk, as an on-site page does at its commit. `--assert` gates the off-site rows as it does the on-site ones: p99 excluding the commit under 20 ms.

A run at 50,000 patients with `--assert`, durability on, nothing else running, on an Apple Silicon Mac (macOS 26.5.1, PostgreSQL 18.6), passed (exit 0). Milliseconds:

| Kind of request | On-site p50 / p99 excl. commit | On-site p99 in full | Off-site p50 / p99 excl. commit | Off-site p99 in full |
|---|---:|---:|---:|---:|
| Chart, a patient on the panel | 3.7 / 4.7 | 5.2 | 5.2 / 14.5 | 21.2 |
| Panel, 100 patients | 4.7 / 7.2 | 9.0 | 5.7 / 11.8 | 20.8 |
| Search, first 3 letters of a name | 4.7 / 9.1 | 9.6 | 6.2 / 11.5 | 16.7 |
| Search, a whole family name | 1.3 / 1.9 | 2.3 | 5.0 / 8.3 | 13.5 |
| Search, date of birth | 1.2 / 1.8 | 2.1 | 4.4 / 7.4 | 12.7 |
| Search, MRN | 1.2 / 4.3 | 4.9 | 4.2 / 7.5 | 12.1 |
| Search, name and date of birth | 1.4 / 4.3 | 5.1 | 5.0 / 9.2 | 14.1 |
| As you type, first 3 letters | 4.8 / 11.2 | 11.7 | 5.5 / 10.5 | 15.2 |

Off-site, the queue's write and flush took 4.0 to 4.4 ms at p50. The off-site chart and panel get the full-time warning (p99 21.2 and 20.8 ms, the flush included), which doesn't fail the run; their 20 ms target is for the cloud server. An earlier run, made while other builds, tests and benchmarks were running on the same machine, failed six off-site rows in their tails only.

After UC 1 was merged in, one quiet run of two failed the first off-site row, the chart (p99 32.4 ms excluding the commit, with a median of 5.5 ms). The benchmark uses one PostgreSQL for both sites, and the off-site rows began the moment the on-site rows had written their 200,000 or so audit events, while PostgreSQL was still vacuuming, analyzing and checkpointing after them. A replica on its own server wouldn't be doing that for the on-site rows. The benchmark now lets the database finish that work before each set of rows (`SettleAsync`: `VACUUM (ANALYZE)`, `CHECKPOINT`, and a collected .NET heap), and never with `--stack`. The gate, its limit and the sampling are unchanged. Three runs in a row then passed, with the off-site chart at 8.9, 10.8 and 12.6 ms and no gated row above 13.0 ms.

### What the audit queue costs, and what step 10c changed

Measured for the expansion plan's step 10c (Tahseen's diagnosis, 19 November 2026), on the development Mac, 50,000 patients:

- **The flush to disk is the largest single cost of an off-site page:** about 3.7 ms each time, where a database statement takes 0.3 to 0.5 ms. A scratch program doing the same append measured 4.0 ms with the flush and 0.003 ms without. On macOS a .NET flush to disk waits for the drive itself; a Linux server's will differ, so these are this machine's numbers.
- **It also slows what follows it.** With the flush switched off for one run, the off-site chart's ten statements took 2.7 ms in all, the same as on-site; with it on, 3.8 to 4.9 ms. The benchmark's "excluding the commit" figure takes out the flush itself, not that after-effect, which is why off-site pages look slower per statement than on-site ones.
- **Before step 10c each audit event was its own write and flush.** A page that recorded one event paid once; a chart with a signed note, or a note's page, recorded two and paid twice (7.2 ms).
- **Now a request's events are held as they are recorded and written together, once, before any of the page is sent** (`CloudAudit/RequestAuditEvents.cs`). The page is kept in memory while the handler makes it, the events are written and flushed, and then the page goes out. If the write fails, the error page is sent and the page that was made is thrown away: a read is never shown without its durable record, the rule the on-site app keeps by committing the event in the request's transaction.

| Off-site page | Events | Before: mean; p99 whole request; p99 excluding the commit | After |
|---|---|---|---|
| Chart with a signed note | 2 | 15.2 ms; 19.2 ms; 10.5 ms | 10.1 ms; 17.0 ms; 12.5 ms |
| Signed note with addenda | 2 | 15.6 ms; 22.2 ms; 13.7 ms | 9.0 ms; 11.1 ms; 7.4 ms |
| Chart (no notes) | 1 | 12.8 ms; 19.6 ms; 14.7 ms | 10.1 ms; 16.1 ms; 10.5 ms |

One run each, before and after, on one machine. "Excluding the commit" is the figure the NFR-03 gate checks; it takes the flush's own time out, so for the pages with two events it shows little of this change, whose saving is a flush.

- A page that records one event pays the same one flush as before. Its flush now comes after all its statements, where it used to come part-way, and by the after-effect above that could itself help; the chart's difference between the two runs can't be told apart from run-to-run variation.
- Pages that record more gain a flush for each event beyond the first.
- If the events are written and sending the page then fails, because the browser has gone, the read is recorded for a page nobody received.
- **On Linux the flush is much cheaper.** CI's Linux runner measured about 0.7 ms for it (the off-site chart's commit time in the benchmark job), against 3.7 ms on the development Mac, where .NET's flush waits for the drive. The clinic's cloud server is Linux: the saving there will be smaller in milliseconds, and the off-site pages were never as slow there as they look on a Mac.

Tests: `RequestAuditEventsTests` (the middleware and holder in a small app: order, failure, a response with no body, a failing handler, a large page, a refused event) and TC-UC2-07 (the real cloud app).

## Restore drill

Run on 2026-11-15 (US Central; 2026-11-16 UTC) on the local Compose stack, with the web and migration images rebuilt from `main` at 86dc093 (12 migrations applied, `ClinicalNotes` among them), following "Backup and point-in-time restore" in `infra/compose/README.md`. `barman check onprem` passed every check first.

- **Target time:** 2026-11-16 03:28:39.439106+00.
- **The change to undo:** a scratch table, `public.restore_drill`, held 5 rows at the target time; 5 more were inserted after it, so the primary had 10.
- **Restore:** the WAL in progress was shipped with `barman switch-wal --force --archive onprem`; `barman recover onprem latest /var/lib/barman/restore --target-time "<target>"` completed; `restore-db` started in recovery, paused, with its last replayed transaction at 03:28:37.158222+00.
- **Cleanup:** as the README says (`restore-db` removed, the restore folder deleted), and the scratch table dropped.

| Table | Primary at the target time | Restored copy |
|---|---:|---:|
| `patients` | 50,000 | 50,000 |
| `audit_events` | 92 | 92 |
| `clinical_notes` | 0 | 0 |
| `clinical_drafts` | 0 | 0 |
| `staff_users` | 3 | 3 |
| `patient_users` | 2 | 2 |
| `patient_assignments` | 2 | 2 |
| `__ef_migrations_history` | 12 | 12 |
| `restore_drill` | 5 (10 after the change) | 5 |

Every count matched. The drill's limit: `clinical_notes` and `clinical_drafts`, added since the cloud milestone, were empty on this stack, so it shows that their schema restores, not their rows; orders (UC 1) weren't on `main` when it ran.

## Limits

- **Signing in during an outage isn't possible.** A clinician who isn't signed in gets the sign-in page from the replica, but the sign-in is a write (it checks and records against the primary), so it is forwarded and refused with the "Not saved" page. UC 2 assumes the clinician signed in before the outage. Sign-in is unchanged.
- **The desktop app's draft pull and offline panel are refused during an outage**, not read from the replica: they are POSTs, which the cloud app forwards (UC 8's design). The desktop app keeps showing its encrypted local copy. Whether they should read the replica is an open question for the owner.
- The typed text is shown again only by browsers that send `Sec-Fetch-Site` (all current ones); an older browser gets the "Not saved" page with the Back link, as before.
- The browser behaviour (Back, the scripts keeping text) was checked through the web tests' HTTP requests and by reading the scripts, not in a real browser.
- The off-site benchmark rows run the cloud app against the same PostgreSQL, which stands in for the replica as in the web tests, with no network; `--stack` measures on-site only, since it has no `ehr_read` login.
- The patient portal and the admin pages were not reviewed for UC 2, which is a clinician's use case.
- **Who reads the queued reads.** Admins read the audit log at **Audit**, and a patient, or someone they share their record with, reads who viewed that record in the portal (step 13c, `docs/UC6.md`), each on the site they are using. Off-site that is the replica's copy of the log, so a read the cloud app has queued and not yet shipped, or that the replica hasn't received yet, isn't listed for a few seconds; the portal's page says a view from the last minute may not be listed yet.
- **Each queued read says which part of the record it was for** (step 13c2, `AuditParts`): the cloud app's allow-list takes, for each kind of read, only a part the PHI reader records that kind with, or none. None is what an app older than the step's migration writes, and a queue it left on disk is still taken: so a deploy migrates first, then starts the apps, and an older app still running for a moment writes reads without a part, which the portal's page words as it did before the step.
