# Reference data

The standard code lists SwiftChart loads, where each comes from, and what its publisher asks of anyone who uses it. Nothing is fetched while the apps run: the codes are in the repository as SQL, generated from pinned sources except for one short hand-written file of interactions taken from FDA labels, so the clinic's server works with no internet (NFR-08).

## How it is loaded

1. Download the pinned source files into a folder outside the repository. The addresses, file names and SHA-256 checksums are at the top of `tools/reference/import.cs` and `tools/reference/import-catalogs.cs`.
2. Run both scripts: `dotnet run tools/reference/import.cs -- --sources <folder>` and `dotnet run tools/reference/import-catalogs.cs -- --sources <folder>`. Each checks every file against its checksum before reading it, and writes SQL into `db/reference`.
3. Commit the SQL. The migration job (`infra/migrate`), the tests' database and the benchmark apply `db/reference/*.sql` and then `db/reference/ddinter/*.sql` after `db/policies`, as `ehr_owner`. Every file is safe to run again.

To move to a newer release of a source, change its address, the file's name inside the archive and its checksum in the script, download it, and run the script again. DailyMed and DDInter publish without a version in the address: the date below is when the file was downloaded, and the checksum is what pins it.

None of these tables is patient data. Both apps read them and nothing at runtime changes them.

## What is loaded

| Table | Used for | Source and release | Rows |
| --- | --- | --- | --- |
| `clinical_codes` | Diagnoses | ICD-10-CM, fiscal year 2027 (CDC, National Center for Health Statistics): every code valid on a claim | 74,879 |
| `clinical_codes` | Medications | RxNorm Current Prescribable Content, 2 November 2026 (U.S. National Library of Medicine): clinical and branded drugs (SCD, SBD) | 20,061 |
| `clinical_codes` | Drug allergens | The same RxNorm release: ingredients (IN) | 5,886 |
| `clinical_codes` | Other allergens | FDA Unique Ingredient Identifiers (UNII), 4 August 2026: 21 substances chosen by this project, each given a FHIR allergy category | 21 |
| `order_catalog_items` | Lab orders | LOINC 2.83, Universal Lab Orders Value Set | 1,519 |
| `order_catalog_items` | Medication orders | The same RxNorm release: clinical drugs (SCD) | 11,971 |
| `observation_codes` | Results, vital signs, survey scores | LOINC 2.83: 33 terms chosen by this project, with UCUM units | 33 |
| `order_result_codes` | Which results a common lab order produces | Curated by this project | 47 |
| `note_templates` | The visit types a note is written for, and what to cover in each of its sections | Written for this project: hand-written in `db/reference/37-note-templates.sql` (below, "Note templates") | 5 |
| `questionnaires` | PHQ-9 and GAD-7, for pre-visit forms | The authors' English forms and instruction manual (phqscreeners.com), with LOINC 2.83's codes: hand-written in `db/reference/36-questionnaires.sql` (below, "Pre-visit questionnaires") | 2 |
| `drug_ingredients` | What each drug product is made of | The same RxNorm release (RXNREL) | 25,490 |
| `drug_classes` | The class an ingredient belongs to | FDA pharmacologic class indexing files, and DailyMed's mapping files, downloaded 18 November 2026 | 2,105 |
| `drug_class_cross_reactions` | Which classes cross-react in an allergic patient | Curated by this project, each row citing its source | 2 |
| `drug_interactions`, source `ddinter` | Interactions between two ingredients, with DDInter's level | DDInter 2.0, downloaded 18 November 2026 | 81,610 |
| `drug_interactions`, source `onc` | The ONC high-priority interactions, each marked "high-priority" | Wikidata's "significant drug interaction" statements that cite the ONC list, queried 19 November 2026 (`db/reference/wikidata`) | 717 |
| `drug_interactions`, source `fda-label` | Important pairs neither holds, with what the label says | Hand-written from FDA labels on DailyMed (`db/reference/60-label-interactions.sql`) | 23 |

The clinic's 29 common order items (`SYN-...`) come from a migration, not from here; where a standard code applies they name it.

Names are the sources' own, with two changes of form: an ICD-10-CM code is written with its dot (`E11.9`), and a UNII or RxNorm ingredient name is written in sentence case. 136 RxNorm product names longer than 300 characters are left out.

### Reference ranges

The ranges in `observation_codes` are **this clinic's own**, set for synthetic data from the adult intervals that textbooks and laboratories commonly publish. They are not LOINC's, they don't vary by age or sex, and they are not validated for clinical use. Each row says so in `range_source`. The two questionnaire totals, the PHQ-9's (44261-6) and the GAD-7's (70274-6), have no range: a score is banded and flagged by its questionnaire's own definition (step 13e), and the made-up range of up to 4 that step 2a gave them is gone.

### Vital-sign ranges by age

Added in the plan's step 9b (Tahseen's decision of 19 November 2026); the children's ranges replaced in step 9d with the two primary sources Tahseen supplied (decision of 19 November 2026). Heart rate, respiratory rate and blood pressure get a range for the patient's age, and a child's blood pressure for their sex too. Temperature and oxygen saturation keep one range. The rows are hand-written in `db/reference/35-vital-ranges.sql`, each naming its source, table and page, and the same rows are `Ehr.Data.Reference.VitalRanges`, which the seed uses; a test fails if the two differ, and another holds both to a copy of the published tables typed from the PDFs. The patient's age in whole months on the day a sign was taken picks the row, so a range changes on the birthday and not before.

They flag synthetic values on a flowsheet. **They are not validated for clinical use.**

| Sign | Birth to 18 years | From 18 |
|---|---|---|
| Heart rate, per minute | Fleming's 1st to 99th centile, for 13 age groups | 60 to 100 |
| Respiratory rate, per minute | Fleming's 1st to 99th centile, for 13 age groups | 12 to 20 |
| Systolic and diastolic pressure, mmHg | From 1 year: Table 6's screening value for the year of age (13 and over as one) and sex; none under 1 year | 90 to 129 and 60 to 79 |

**Where each comes from:**

- **Children's heart rate and respiratory rate:** Fleming S, Thompson M, Stevens R, et al. Normal ranges of heart rate and respiratory rate in children from birth to 18 years: a systematic review of observational studies. *Lancet* 2011;377:1011–1018 (author manuscript PMC3789232). Read in its Web Appendix (the author manuscript's supplement, `NIHMS54714-supplement-Web_Appendix.pdf`), page 12: Web Table 4, "Proposed respiratory rate cut-offs (breaths/minute) based on centile charts", and Web Table 5, "Proposed heart rate cut-offs (beats/minute) based on centile charts". Every heart and respiratory rate value comes from that page.
  - **The 1st and 99th centiles** are the flag limits, Tahseen's choice: a value below the 1st or above the 99th is flagged Low or High. The tables also print the 10th, 25th, 50th, 75th and 90th, which are not used.
  - **The 13 age groups as printed:** 0–3, 3–6, 6–9, 9–12, 12–18 and 18–24 months, then 2–3, 3–4, 4–6, 6–8, 8–12, 12–15 and 15–18 years. A group runs from its first month up to, not including, the next group's, so a child moves on the birthday that ends the group.
  - **Web Table 5's "Birth" row is not used.** The table prints it above the 0–3 months group for "the immediate neonatal period"; it is not one of the 13 groups, and a newborn is held to the 0–3 months group from the day of birth.
- **Children's blood pressure:** Flynn JT, Kaelber DC, Baker-Smith CM, et al. Clinical Practice Guideline for Screening and Management of High Blood Pressure in Children and Adolescents. *Pediatrics* 2017;140(3):e20171904, Table 6, "Screening BP Values Requiring Further Evaluation", page 15 (doi:10.1542/peds.2017-1904). Read in the journal's PDF of the article (`peds_20171904.pdf`). Its content carries the words "ROUGH GALLEY PROOF" on 66 of its 72 pages, page 15 among them, in a running line placed just below the bottom edge of each page, so they neither show nor print; page 15 as rendered and its text agree on every value. The table gives a systolic and diastolic value for boys and for girls at each year of age from 1 to 12, and one for 13 and over. Every child's blood pressure value comes from that page, as printed.
  - **A screening value, not a range.** Section 3.2a, page 8, says the table "is designed as a screening tool only" and "should not be used to diagnose elevated BP or HTN by itself". So a reading at or above the value is flagged **"Needs follow-up"**, never High or abnormal, and nothing below it is flagged: there is no lower value for a child's blood pressure.
  - **None under 1 year:** the table starts at age 1.
  - **By sex:** a boy gets the boys' values and a girl the girls'. A patient whose sex is other or unknown is held to the lower of the two, so flagged sooner (the coordinator's answer of 20 November 2026, for Tahseen to confirm).
  - **13 and over** runs to the 18th birthday, when the adults' range takes over and the flag goes back to High and Low.
  - **Checked against the original:** the girls' diastolic value at age 1 is 54 on page 15. The Merck Manual's reproduction, from which step 9b took its numbers, gives 57.
- **Adults:** heart rate 60 to 100, and blood pressure from 90/60, from MedlinePlus (US National Library of Medicine), "Vital signs", reviewed 1 January 2025 (<https://medlineplus.gov/ency/article/002341.htm>). Respiratory rate 12 to 20 from StatPearls, "Vital Sign Assessment" (<https://www.ncbi.nlm.nih.gov/sites/books/NBK553213/>). The upper blood pressure is one below the American Heart Association and American College of Cardiology's stage 1 (130 systolic or 80 diastolic).
  - The adult diastolic upper value was 84 before step 9b, which no source supported; it is 79 now, in `observation_codes` too (the range used if this table is ever empty). Readings already recorded were given the new ranges by the `VitalCorrections` migration.
  - **Kept at 60 to 79 (Tahseen, 19 November 2026).** The 2017 ACC/AHA guideline defines normal blood pressure as under 120 systolic and under 80 diastolic, elevated as 120 to 129 and under 80, and stage 1 hypertension as 130 to 139 or 80 to 89: Whelton PK, Carey RM, Aronow WS, et al. 2017 ACC/AHA/AAPA/ABC/ACPM/AGS/APhA/ASH/ASPC/NMA/PCNA Guideline for the Prevention, Detection, Evaluation, and Management of High Blood Pressure in Adults. *Hypertension* 2018;71(6):e13–e115 (<https://doi.org/10.1161/HYP.0000000000000065>). The journal's page refused the request; the categories were read on the American Heart Association's own page, "Understanding Blood Pressure Readings" (<https://www.heart.org/en/health-topics/high-blood-pressure/understanding-blood-pressure-readings>).
  - **A diastolic of 80 to 84 is "normal" only on the European scale:** optimal is under 120 and under 80, normal is 120 to 129 and/or 80 to 84, and high normal is 130 to 139 and/or 85 to 89. Williams B, Mancia G, Spiering W, et al. 2018 ESC/ESH Guidelines for the management of arterial hypertension. *European Heart Journal* 2018;39(33):3021–3104, Table 3 (<https://doi.org/10.1093/eurheartj/ehy339>), read in the guideline. This project uses the American one.
- **Replaced in step 9d:** step 9b's children's ranges, fitted to five age bands from Bonafide et al. (*Pediatrics* 2013, hospitalized children's heart and respiratory rate centiles), the Merck Manual's reproduction of Table 6, and the Pediatric Advanced Life Support rule for a low systolic pressure read on WikEM. Readings already recorded were given the new ranges by the `ChildrenRanges` migration, which reports how many flags changed. The five bands remain only to shape the seed's synthetic children.

**Rights.** MedlinePlus is a US government work. The *Pediatrics* article is copyright American Academy of Pediatrics, the *Lancet* article and its web appendix are their publisher's, and StatPearls is CC BY-NC-ND 4.0. Nothing of theirs is copied here but the numbers, which are facts, each with its citation; no table is reproduced.

### Pre-visit questionnaires

Added in the plan's step 13e. The PHQ-9 and GAD-7 are hand-written in `db/reference/36-questionnaires.sql`, each line naming its source and page, and a test (`tests/Ehr.Security.Tests/QuestionnaireDefinitionTests.cs`) holds the database's rows to a copy typed from the forms. Step 2a had loaded LOINC's names for the questions ("…in last 2 weeks" added to each, the GAD-7's sixth question "in last 4 weeks", its second worded "Not able to…") and gave the GAD-7 the PHQ-9's prompt; those are replaced. LOINC's codes are kept for each question, answer and total.

**The forms and the manual**, read on 21 November 2026 on phqscreeners.com, Pfizer's site for the measures, which links each file from its "Select a Screener" page (<https://www.phqscreeners.com/select-screener>):

| File | Address | Bytes | SHA-256 |
| --- | --- | --- | --- |
| The PHQ-9, English, one page | <https://www.phqscreeners.com/images/sites/g/files/g10060481/f/201412/PHQ-9_English.pdf> | 40,899 | `e3fb5b8853e3530c9025c822b99fd4f4bc17da49eefda5ed689c49238c132a46` |
| The GAD-7, English, one page | <https://www.phqscreeners.com/images/sites/g/files/g10060481/f/201412/GAD-7_English.pdf> | 32,952 | `180538a8e3f2f3cb012f9991d1b307d3d14720e82bf0593f8853f4885740ce48` |
| "Instructions for Patient Health Questionnaire (PHQ) and GAD-7 Measures", nine pages | <https://www.phqscreeners.com/images/sites/g/files/g10016261/f/201412/instructions.pdf> | 134,493 | `6ed2f00f97e438c1168cd702d38d31fc923031445c720653005f138aa424228a` |

Each form was read as text and as its page rendered, and the two agree. The prompts, questions, answers, scores and attribution lines are the forms' words exactly, with two changes of form: a printed line break or doubled space is one space, and what belongs to paper (the tick instruction, the office-coding line, the underlining) is left out. The PHQ-9's difficulty question is kept, optional and not scored, as the form prints it and the manual says. The GAD-7's form has no such question, so none is asked.

**Scores, bands and follow-up, and where each comes from:**

| | PHQ-9 | GAD-7 |
| --- | --- | --- |
| Each question scores | 0, 1, 2 or 3: "Not at all", "Several days", "More than half the days", "Nearly every day" (the form) | The same (the form) |
| Total | 0 to 27 (manual, page 3, Table 1) | 0 to 21 (manual, page 3, Table 1) |
| Bands | 0–4 Minimal, 5–9 Mild, 10–14 Moderate, 15–19 Moderately severe, 20–27 Severe: Kroenke K, Spitzer RL, Williams JBW. The PHQ-9: validity of a brief depression severity measure. *J Gen Intern Med* 2001;16:606–613, Table 2, "Level of Depression Severity, PHQ-9 Score", read in the article (<https://pmc.ncbi.nlm.nih.gov/articles/PMC1495268/>). The manual (pages 5 and 6) gives the same cutpoints, 5, 10, 15 and 20; its Table 4 (page 7) calls 0–4 "None-minimal" | 5–9 Mild, 10–14 Moderate, 15–21 Severe: "Scores of 5, 10, and 15 represent cutpoints for mild, moderate, and severe anxiety, respectively" (manual, page 6). **0–4 "Below mild" is this clinic's wording**: the manual names no band below 5, and the paper that may (Spitzer RL, Kroenke K, Williams JBW, Löwe B. A brief measure for assessing generalized anxiety disorder: the GAD-7. *Arch Intern Med* 2006;166:1092–1097) could not be read: its page, <https://jamanetwork.com/journals/jamainternalmedicine/fullarticle/410326>, answered HTTP 403 with a browser check, Europe PMC lists its full text as by subscription only, and its abstract names no bands. "Minimal" is not used for the GAD-7 |
| Needs follow-up at | 10 or more: "a cutpoint of 10 or greater is considered a 'yellow flag' on all 3 measures (i.e., drawing attention to a possible clinically significant condition)" (manual, page 2) | 10 or more: "When screening for anxiety disorders, a recommended cutpoint for further evaluation is a score of 10 or greater" (manual, page 6) |
| Put in the care team's inbox | Any answer to question 9 but "Not at all": it "counts if present at all, regardless of duration" (Kroenke 2001, Methods), and "a final decision about the actual risk of self-harm requires a clinical interview" (manual, page 2). The question names the portal's notice for it, `help: self-harm`, which the web app words (`FormNotices`) | None |

**What each source says about using them**, verbatim, as read on 21 November 2026. They disagree on whether the measures are in the public domain or Pfizer's copyright; every one of them allows reproducing, displaying and distributing them without asking. The plan's list of sources names them as "free to use" (Tahseen's plan, "Reference data and synthetic data"), and the conflict is listed for Tahseen in phase 4's pull request draft.

- The PHQ-9 form and the GAD-7 form, foot of the page (a doubled space after "Inc." made single): "Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute."
- The instruction manual, page 8: "All of the measures included in Table 1 are in the public domain. No permission is required to reproduce, translate, display or distribute." Table 1 (page 3) lists the PHQ-9 and the GAD-7.
- The "Select a Screener" page: "All PHQ, GAD-7 screeners and translations are downloadable from this website and no permission is required to reproduce, translate, display or distribute them."
- The site's terms, <https://www.phqscreeners.com/terms>: "Content found at the PHQ Screeners site is expressly exempted from Pfizer’s general copyright restrictions; content found on the PHQ Screeners site is free for download and use as stated within the PHQ Screeners site."
- LOINC 2.83's notice for both panels, their questions and totals (`Loinc.csv`, `EXTERNAL_COPYRIGHT_NOTICE`): "Copyright © Pfizer Inc. All rights reserved. Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute."
- Kroenke et al. 2001, Acknowledgments: "PRIME-MD is a trademark of Pfizer Copyright held by Pfizer." Its appendix, the PHQ-9 as printed then: "PRIME-MD is a trademark of Pfizer Inc. Copyright 1999 Pfizer Inc. All rights reserved. Reproduced with permission".

**A request keeps its wording.** Each request for a form stores the definition it was asked with, and the patient answers, the database scores and every page shows that copy, so a later change here never rewrites what someone answered. A questionnaire that has been asked for can't be removed.

They are for a demonstration system's synthetic patients and **are not validated for clinical use**.

### Note templates

Added in the plan's step 14. The visit types a clinician can write a note for, each with a prompt for what to cover in Subjective, Objective, Assessment and Plan: guidance for writing a note of that kind, which the note editor shows under each section's own definition when the clinician picks the visit type (`docs/UC14.md`). They are hand-written in `db/reference/37-note-templates.sql`, for this project and from no published source: follow-up, new patient, acute, well-child and telephone visits. Nothing of a template is put into a note. **Not validated for clinical use:** each needs a clinician's reading before a clinic relies on it. A template a draft was written with can't be taken out of the file: the database refuses it.

### Units

Every unit in `observation_codes` is UCUM's, and the synthetic seed records each result and vital sign in its code's own unit, so the FHIR API can send every seeded value with UCUM's system and code. `tests/Ehr.Web.Tests/UcumTests.cs` checks both with Fhir.Metrics, Firely's UCUM implementation. Until the plan's step 11 the seed wrote thyrotropin (3016-3) in "mIU/L", which isn't UCUM; it now writes "m[IU]/L", as the reference data always had (Tahseen's decision, 19 November 2026). A database seeded before that keeps "mIU/L" on those seeded results: nothing rewrites them, and the FHIR API sends that unit as text.

### How drug classes are joined to RxNorm

The FDA's indexing files name a substance and its Established Pharmacologic Classes. DailyMed's two mapping files say which class files and which RxNorm products belong to each drug label. A label whose products have exactly one ingredient between them tells us that ingredient's classes; a class is kept when at least four in five of the ingredient's labels carry it. That gives classes for 1,413 ingredients. For an ingredient with no label of its own, such as one sold only in combinations, the class file's substance is matched through the FDA substance list's RxNorm column, which brings the total to 1,945 of RxNorm's 5,886 ingredients. The rest have no FDA class and get no class check. Classes are kept by name; the class files' own codes are MED-RT's and are not stored.

### Which classes cross-react

Two rows, both from Khan DA, Banerji A, Blumenthal KG, et al. Drug allergy: a 2022 practice parameter update. *J Allergy Clin Immunol.* 2022;150(6):1333-1393: penicillins with cephalosporins, and penicillins with carbapenems. A class always cross-reacts with itself. Adding a row needs a published source.

## Systems the FHIR API names

The FHIR API (the plan's step 11, `docs/UC19.md`) sends each code with its system's canonical FHIR address:

| Codes | System |
| --- | --- |
| RxNorm (medications, drug allergens) | `http://www.nlm.nih.gov/research/umls/rxnorm` |
| FDA UNII (other allergens) | `http://fdasis.nlm.nih.gov` |
| ICD-10-CM (diagnoses) | `http://hl7.org/fhir/sid/icd-10-cm` |
| LOINC (results, vital signs) | `http://loinc.org` |
| UCUM (units) | `http://unitsofmeasure.org` |
| The clinic's medical record numbers | `http://swiftchart.example/fhir/sid/mrn`, the clinic's own (Tahseen's decision, 19 November 2026), with the identifier type MR from `http://terminology.hl7.org/CodeSystem/v2-0203` |

"No known allergy" has the app's own marker for a code, which has no system, so it is sent as text alone.

## Licenses and attribution

- **RxNorm.** The Current Prescribable Content needs no license. This product uses publicly available data courtesy of the U.S. National Library of Medicine (NLM), National Institutes of Health, Department of Health and Human Services; NLM is not responsible for the product and does not endorse or recommend this or any other product. The data here is the release named above and may not be current. The full RxNorm release, which needs a UMLS license, is not used.
- **ICD-10-CM.** Published by the U.S. government (CDC/NCHS) for public use.
- **UNII.** Published by the U.S. Food and Drug Administration. No license is stated on its download page; as a work of the U.S. government it is taken to be in the public domain.
- **FDA pharmacologic class indexing files and DailyMed mapping files.** Published by the FDA and the National Library of Medicine. MED-RT and RxClass are not used.
- **LOINC.** This material contains content from LOINC (http://loinc.org). LOINC is copyright © Regenstrief Institute, Inc. and the Logical Observation Identifiers Names and Codes (LOINC) Committee and is available at no cost under the license at http://loinc.org/license. LOINC® is a registered United States trademark of Regenstrief Institute, Inc. The version used is 2.83. Where LOINC records a third party's rights in a term, its notice is carried in the `copyright` column.
- **PHQ-9 and GAD-7.** The publisher's instruction manual calls them public domain, and every form says "No permission required to reproduce, translate, display or distribute." LOINC's notice for them, stored with each questionnaire as LOINC's license asks, begins "Copyright © Pfizer Inc. All rights reserved." Every statement found, with where it was read, is under "Pre-visit questionnaires" above. Each severity band and cutpoint comes from the source named there for it: the PHQ-9's bands from Kroenke et al. 2001, Table 2; the GAD-7's cutpoints from Pfizer's instruction manual, page 6; the GAD-7's band below 5 is this clinic's wording, since no source read names it.
- **The dictation sidecar's copy.** `src/Ehr.Dictation/sidecar/ehr_dictation/catalog.py` is a derived copy of names from these sources: the order catalog's LOINC lab orders and RxNorm clinical drugs, RxNorm's medications, and the allergens (RxNorm and UNII), with the clinic's common catalog items, which `src/Ehr.Dictation/sidecar/generate_catalog.py` writes from `db/reference` for the dictation's proposals. Its header carries the RxNorm, UNII and LOINC notices above, as the SQL files point here for theirs, and it ships in the sidecar's image.
- **UCUM.** Units are written in UCUM, © Regenstrief Institute, Inc., used under the UCUM license (https://ucum.org/license) without change of meaning.
- **FHIR value sets.** The status, category, criticality and severity codes are HL7 FHIR R4's, published under CC0.
- **DDInter 2.0. An exception to the project's free-software rule (NFR-16), accepted by Tahseen on 18 November 2026.** Used under CC BY-NC-SA 4.0, which DDInter states on its terms page, https://ddinter.scbdd.com/terms/: free, but not for commercial use, and share-alike. Its table is kept apart in `db/reference/ddinter/`, with `LICENSE.md` there holding the license, the citation and what was changed. If the project ever goes commercial, this table must be replaced. Tian Y, Yi J, Wang N, et al. DDInter 2.0: an enhanced drug interaction resource with expanded data coverage, new interaction types, and improved user interface. *Nucleic Acids Research.* 2025;53(D1):D1356.

### What DDInter's files do and don't hold

Each row is two drug names and a level (major, moderate, minor or unknown). There is no management advice in the files; that text is only on DDInter's web pages. Of 222,383 rows, 47,182 have an unknown level and are left out, and 56,526 name a drug that isn't a RxNorm ingredient (446 of DDInter's 1,939 drugs, many not sold in the United States). 81,610 distinct pairs are loaded. The checks built on this are decision support for a demonstration system, not a complete clinical reference.

### Why DDInter is supplemented, and with what

DDInter's eight download files hold only pairs where at least one drug has an ATC code in groups A, B, D, H, L, P, R or V. Pairs where neither does are not in the files: an SSRI with tramadol, an ACE inhibitor with an angiotensin receptor blocker, a beta-blocker with verapamil. Tahseen decided on 19 November 2026 to supplement it rather than ship around the gaps. A pair can have a row from each source, and every warning names the source it came from.

- **The ONC high-priority list**, by way of Wikidata. Phansalkar S, Desai AA, Bell D, et al. High-priority drug-drug interactions for use in electronic health records. *J Am Med Inform Assoc.* 2012;19(5):735-743: the interactions an electronic record should always warn about. Wikidata records them as "significant drug interaction" (P769) statements that cite the paper, with each drug's RxNorm code; Wikidata is CC0. The query and its dated result are committed in `db/reference/wikidata`, so building the data reads the repository, never the network. Of 1,798 statements, 717 pairs are between RxNorm ingredients (a drug whose Wikidata code is another RxNorm concept is found by its name; twelve drugs, mostly withdrawn ones, are not ingredients at all and their pairs are left out). DDInter also has some of them. Each is loaded with the level "high-priority".
- **FDA labels**, by hand. `db/reference/60-label-interactions.sql` lists 23 pairs, each with what the label's Drug Interactions section (or its contraindications or boxed warning) says, paraphrased closely, and the label it was read from with its DailyMed set ID. Labels are in the public domain. The level on these rows is this project's reading of the label: major where the label contraindicates the pair, carries a boxed warning or reports a serious outcome; moderate where it says to monitor or adjust; minor otherwise.

Coverage of the order catalog's 2,581 medication ingredients, pairs with a warning: 78,166 from DDInter alone, 78,587 with both supplements (398 more from ONC, 23 from labels). The numbers are small beside DDInter's because the supplements are aimed at what it lacks, not at volume.

**What is still not covered.** Labels mostly speak of classes ("angiotensin receptor blockers", "benzodiazepines", "SSRIs"), and the hand-written table lists pairs, so it covers the pairs written and not their classmates: lisinopril with losartan is there, enalapril with valsartan is not. By class, among catalog ingredients: ACE inhibitor with angiotensin receptor blocker 1 of 88 pairs, beta-blocker with calcium channel blocker 2 of 160, SSRI with SNRI 0 of 40, antipsychotic with antiarrhythmic 18 of 228. No warning never means a pair is safe, and the order form says so.

DDInter's website is not read by any tool here: only its download files. A warning from DDInter links to DDInter's interaction checker, not to a page for the pair, because the files don't give the addresses of pair pages.

## Not loaded, and why

- **SNOMED CT and CPT**: both need licenses. Neither is used anywhere.
- **MED-RT and RxClass**: not used, by Tahseen's decision.
- **CVX** (vaccine codes): optional in the plan, and nothing uses it yet.

## Which codes are sensitive

A diagnosis is sensitive when its ICD-10-CM code is in one of these ranges, or when a clinician marks it:

| Codes | What they cover |
| --- | --- |
| F01–F99 | Mental, behavioral and neurodevelopmental disorders, including substance use (F10–F19) |
| A50–A64 | Infections with a predominantly sexual mode of transmission |
| B20, Z21 | HIV disease, and asymptomatic HIV infection status |
| O04, Z33.2 | Termination of pregnancy |
| T74, T76 | Abuse, neglect and other maltreatment, confirmed or suspected |
| X71–X83 | Intentional self-harm |
| T14.91 | Suicide attempt |
| R45.851 | Suicidal ideations |
| Z91.51, Z91.52 | Personal history of suicidal behavior, and of nonsuicidal self-harm |
| T36–T65 with intent 2 | Poisoning by drugs, and toxic effects of other substances, by intentional self-harm |

A code matches by its start, so every code under a category listed is covered (T14.91XA under T14.91, F41.1 under F01–F99). Case, the dot and spaces round the code don't matter.

In T36–T65 the intent is one character of the code, and "2" means intentional self-harm. In most of these codes it is the sixth character (T39.012A, T43.212D). In the codes of a four-character subcategory it is the fifth, with an X filling the sixth (T36.92XA). The rule takes both forms. Checked against the fiscal year 2027 code file, they are exactly its 954 "intentional self-harm" codes in T36–T65, and no other code in that range.

When the rule widens, the migration job marks the diagnoses already recorded that it now covers, on its next run, and the audit page stops showing their values.

The rule is `ehr.icd10cm_is_sensitive` in `db/policies/65-chart-lists.sql`; changing it is a reviewed change to that file. A medication is sensitive when a clinician marks it.
