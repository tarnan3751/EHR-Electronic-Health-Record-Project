# TC-UC12-06 — Emergency access uses only synthetic data (manual part)

The manual part of TC-UC12-06: what needs the running stack's database. The automated part (the setup's emergency
access and reason, and the synthetic clinic) is `TC_UC12_06_Nfr13_SyntheticData.cs` in this folder.

| Field | Value |
|---|---|
| Test Case ID | TC-UC12-06 (manual part) |
| Purpose / scenario | Every chart opened through emergency access in the local stack is a synthetic patient's, and every reason recorded holds no real patient's details, so no real person's information is used in testing UC 12. |
| Type | Non-functional (data: synthetic only) |
| Requirements | NFR-13, with FR-06 |
| Use case | UC 12, Open a chart through emergency access |
| Preconditions | The local stack is running (`infra/compose/README.md`), the synthetic clinic is seeded ("Synthetic data and the benchmark" in `docs/development.md`), the example accounts exist (`dotnet run infra/compose/test-accounts.cs -- setup`), and the manual parts of TC-UC12-01 to TC-UC12-05 have been run. |
| Inputs and system conditions | `psql` as the database superuser through `docker compose exec`, run from `infra/compose`, which sees every row whatever row-level security allows. |
| Test data | The reasons from these test cases and from `test-accounts.cs` ("Setting up a test portal account on the local stack"). |
| Expected result | Step 1 lists only patients whose MRN starts `SYN`, and step 2 finds every reason among the test texts. Steps 3 and 4 each return the count 0. |
| Creator | Luke Garcia |

## Steps

1. Every emergency access, with its patient and reason:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select p.mrn, p.given_name, p.family_name, b.reason from ehr.break_glass_accesses b join ehr.patients p on p.id = b.patient_id order by b.granted_at"`
2. Read every reason from step 1: each should be one of the test texts above, with no names, MRNs, dates or contact
   details of a real person.
3. Emergency accesses to a patient who isn't synthetic:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select count(*) from ehr.break_glass_accesses b join ehr.patients p on p.id = b.patient_id where p.mrn not like 'SYN%'"`
4. Patients who aren't synthetic at all:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select count(*) from ehr.patients where mrn not like 'SYN%'"`

## Results

Not run yet.

| Step | Date run | Result |
|---|---|---|
| 1. Emergency accesses | | |
| 2. Reasons | | |
| 3. Non-synthetic patients opened | | |
| 4. Non-synthetic patients | | |
