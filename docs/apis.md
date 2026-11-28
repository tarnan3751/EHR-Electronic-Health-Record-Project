# FHIR API and lab interface

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

## Lab interface

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
- **Orders signed, nothing handed over:** the hand-over stops short of the oldest transaction still running on the database server that has written something, so one session left open in a transaction holds back every lab order signed after its first write, with no error anywhere, until it ends; then the lab is handed them all. A session of the apps' roles, `ehr_app` or `ehr_read`, left idle in a transaction is ended after a minute (`idle_in_transaction_session_timeout`, `db/policies/00-roles.sql`), its transaction rolled back, so it holds the hand-over back for a minute at most; one of another role (the superuser's, `ehr_owner`'s), or a long statement still running, holds it back until it ends. On the primary, `select pid, usename, application_name, state, xact_start from pg_stat_activity where backend_xid is not null order by age(backend_xid) desc` shows it first.

## Simulated lab

The simulated lab (`src/Ehr.Lab.Simulator`) is a lab's system for development and the local stack: it asks every few seconds, keeps each order until its results are due, 5 to 30 seconds after it was signed, and posts values drawn from the clinic's own ranges, as the hand-over gives them: above or below for the diagnoses that move a test (type 1 or 2 diabetes, lipid disorders, hypothyroidism, vitamin D deficiency, chronic kidney disease, anemias: the table is in `docs/UC20.md`), and out of range now and then otherwise. The same order gets the same values every time, so two labs at once, or one started again, file each result once. The local stack runs it as its `lab` service when `EHR_LAB_KEY` is set in `infra/compose/.env`. To run it yourself against the stack, put the stack's key in the environment without typing it on the command line, where the shell's history would keep it (PowerShell 7: `$env:EHR_LAB_KEY = Read-Host -MaskInput "Lab key"`; zsh or bash: `read -rs EHR_LAB_KEY && export EHR_LAB_KEY`), then:

```
dotnet run --project src/Ehr.Lab.Simulator -- --base-url https://ehr.example.com:8443 --certificate infra/compose/certs/example.com.pem
```

`--certificate` trusts the stack's certificate as it is, for a machine that doesn't trust mkcert's root. The address must be `https://`: `--allow-http` lets an `http://` one through, for testing on one machine only. `--poll-seconds`, `--min-delay-seconds`, `--max-delay-seconds` and `--abnormal-rate` change its pace and its odds (`src/Ehr.Lab.Simulator/Program.cs`). Without `EHR_LAB_KEY` it says so and stops. It never prints the key.
