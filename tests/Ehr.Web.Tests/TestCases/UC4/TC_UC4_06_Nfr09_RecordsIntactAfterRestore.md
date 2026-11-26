# TC-UC4-06 — Records stay intact after an outage and a restore

A manual test case, as in the SE document (section 7.3). It needs the running server stack with Barman's continuous
backups and a point-in-time restore, which the test suite doesn't have, so the suite has no part of it. Its run is the
restore drill recorded in `docs/UC2.md` ("Restore drill"), automated since the expansion plan's step 21 as
`infra/compose/restore-drill.cs`, which CI's stack job runs on every push. What an outage does to a write is checked automatically by
`TC_UC2_06_Nfr09_OneAuthoritativeCopy.cs`.

| Field | Value |
|---|---|
| Test Case ID | TC-UC4-06 |
| Purpose / scenario | An unexpected server outage does not corrupt existing patient records or compromise database integrity: the primary, restored to a point in time, holds the data as it was at that time. |
| Type | Non-functional (NFR-09) |
| Requirements | NFR-09, FR-35, FR-36 |
| Use case | UC 4, View a patient's chart (and UC 2, system-wide) |
| Preconditions | The local server stack with continuous backups (Barman), set up as "Backup and point-in-time restore" in `infra/compose/README.md` describes. `barman check onprem` passes every check. |
| Inputs and system conditions | A recorded target time, rows added after it, and a point-in-time restore of the primary to that time. The local Docker Compose stack, rebuilt from `main` at 86dc093, with 12 migrations applied. The restore goes to a separate `restore-db`, so the primary is untouched. |
| Test data | The synthetic clinic (50,000 patients). A scratch table, `public.restore_drill`, with 5 rows at the target time and 10 after it. Everything is synthetic. |
| Expected result | The restore completes, and the restored copy holds the data as it was at the target time: every table's row count matches, and the rows added afterwards are not there. No table is corrupted or duplicated. |
| Creator | Diego Valenzuela |

## Steps

1. Record the target time and the row count of each table.
2. Add 5 more rows to the scratch table.
3. Ship the WAL in progress and restore the primary to the target time into `restore-db`, as `infra/compose/README.md` says: `barman switch-wal --force --archive onprem`, then `barman recover onprem latest /var/lib/barman/restore --target-time "<target>"`.
4. Compare the restored copy's row counts with the primary's at the target time.

## Results

| Date | Stack | Result |
|---|---|---|
| 2026-11-15 | Local Compose stack, `main` at 86dc093 | **Pass.** The restore completed and every count matched: 50,000 patients, 92 audit events, 3 staff users, 2 patient users, 2 panel assignments, and the scratch table at 5 rows, without the 5 added later. The full table of counts is in `docs/UC2.md` ("Restore drill"). |
| 2026-11-25 | Local Compose stack, the expansion plan's step 21 (no patients seeded) | **Pass,** by `infra/compose/restore-drill.cs` in 45 seconds: the restore completed to T, the row written before T was there and the one written after wasn't, and the count and checksum of the migrations (49), the codes (100,847), patients and audit events matched the primary's at T. CI's stack job runs it on every push. |

**Status: Pass.**
