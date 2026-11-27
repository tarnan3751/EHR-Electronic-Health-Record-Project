# TC-UC11-06 — Sign-in uses only synthetic data (manual part)

The manual part of TC-UC11-06: what needs the running stack's database. The automated part (the example accounts, the
synthetic clinic and the tests' own accounts) is `TC_UC11_06_Nfr13_SyntheticData.cs` in this folder.

| Field | Value |
|---|---|
| Test Case ID | TC-UC11-06 (manual part) |
| Purpose / scenario | Every staff account, every portal account and every patient in the local stack's database is synthetic, so no real person's details are used in testing sign-in. |
| Type | Non-functional (data: synthetic only) |
| Requirements | NFR-13, with FR-27 |
| Use case | UC 11, Sign in with multi-factor authentication |
| Preconditions | The local stack is running (`infra/compose/README.md`), the synthetic clinic is seeded ("Synthetic data and the benchmark" in the root README), and the example accounts exist (`dotnet run infra/compose/test-accounts.cs -- setup`). |
| Inputs and system conditions | `psql` as the database superuser through `docker compose exec`, run from `infra/compose`, which sees every row whatever row-level security allows. |
| Test data | The example accounts (`admin@example.com`, `clinician@example.com`, `patient@example.com`) and the example patient, MRN `SYN0000001`. |
| Expected result | Steps 1 to 3 each return 0 rows (step 3 returns the count 0). Step 4 lists only `@example.com` addresses. |
| Creator | Luke Garcia |

## Steps

1. Staff accounts at a real domain:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select email from ehr.staff_users where email not like '%@example.com'"`
2. Portal accounts at a real domain, or for a patient who isn't synthetic:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select u.email, p.mrn from ehr.patient_users u join ehr.patients p on p.id = u.patient_id where u.email not like '%@example.com' or p.mrn not like 'SYN%'"`
3. Patients who aren't synthetic:
   `docker compose exec -u postgres onprem-db psql -d ehr -c "select count(*) from ehr.patients where mrn not like 'SYN%'"`
4. Open `infra/compose/test-accounts.json` and read the addresses in it.

## Results

Not run yet.

| Step | Date run | Result |
|---|---|---|
| 1. Staff accounts | | |
| 2. Portal accounts and their patients | | |
| 3. Patients | | |
| 4. test-accounts.json | | |
