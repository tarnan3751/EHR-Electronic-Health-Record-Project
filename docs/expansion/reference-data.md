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
| `questionnaires` | PHQ-9 and GAD-7 | LOINC 2.83 panels 44249-1 and 69737-5, with their answer lists | 2 |
| `drug_ingredients` | What each drug product is made of | The same RxNorm release (RXNREL) | 25,490 |
| `drug_classes` | The class an ingredient belongs to | FDA pharmacologic class indexing files, and DailyMed's mapping files, downloaded 18 November 2026 | 2,105 |
| `drug_class_cross_reactions` | Which classes cross-react in an allergic patient | Curated by this project, each row citing its source | 2 |
| `drug_interactions`, source `ddinter` | Interactions between two ingredients, with DDInter's level | DDInter 2.0, downloaded 18 November 2026 | 81,610 |
| `drug_interactions`, source `onc` | The ONC high-priority interactions, each marked "high-priority" | Wikidata's "significant drug interaction" statements that cite the ONC list, queried 19 November 2026 (`db/reference/wikidata`) | 717 |
| `drug_interactions`, source `fda-label` | Important pairs neither holds, with what the label says | Hand-written from FDA labels on DailyMed (`db/reference/60-label-interactions.sql`) | 23 |

The clinic's 29 common order items (`SYN-...`) come from a migration, not from here; where a standard code applies they name it.

Names are the sources' own, with two changes of form: an ICD-10-CM code is written with its dot (`E11.9`), and a UNII or RxNorm ingredient name is written in sentence case. 136 RxNorm product names longer than 300 characters are left out.

### Reference ranges

The ranges in `observation_codes` are **this clinic's own**, set for synthetic data from the adult intervals that textbooks and laboratories commonly publish. They are not LOINC's, they don't vary by age or sex, and they are not validated for clinical use. Each row says so in `range_source`.

### Vital-sign ranges by age

Added in the plan's step 9b (Tahseen's decision of 19 November 2026). Heart rate, respiratory rate and blood pressure get a range for each of five age bands: infant (under 1 year), young child (1 to 5), older child (6 to 11), adolescent (12 to 17) and adult. Temperature and oxygen saturation keep one range. The rows are hand-written in `db/reference/35-vital-ranges.sql`, and the same rows are `Ehr.Data.Reference.VitalRanges`, which the seed uses; a test fails if the two differ. The patient's age on the day a sign was taken picks the band.

They flag synthetic values on a flowsheet. **They are not validated for clinical use**, and two of the sources were read second-hand, as said below.

| Sign | Infant | Young child | Older child | Adolescent | Adult |
|---|---|---|---|---|---|
| Heart rate, per minute | 91 to 186 | 66 to 173 | 56 to 155 | 48 to 138 | 60 to 100 |
| Respiratory rate, per minute | 19 to 76 | 14 to 60 | 13 to 46 | 11 to 35 | 12 to 20 |
| Systolic pressure, mmHg | 70 and up | 72 to 103 | 82 to 110 | 90 to 119 | 90 to 129 |
| Diastolic pressure, mmHg | none | up to 63 | up to 73 | up to 79 | 60 to 79 |

**Where each comes from, and how it was fitted to five bands:**

- **Children's heart rate and respiratory rate:** Bonafide CP, Brady PW, Keren R, Conway PH, Marsolo K, Daymont C. Development of heart and respiratory rate percentile curves for hospitalized children. *Pediatrics* 2013;131(4):e1150–e1157 (doi:10.1542/peds.2012-2443), Tables 3 and 4, read on PubMed Central (<https://pmc.ncbi.nlm.nih.gov/articles/PMC4074640/>).
  - The 1st and 99th percentile cut points. The source prints them for thirteen age groups; each band here takes the lowest 1st and the highest 99th among its groups, so a band is wider than any one age in it.
  - The children measured were on hospital wards, so this is a range for flagging, not a healthy child's range.
- **Children's blood pressure, upper value:** Flynn JT et al. Clinical Practice Guideline for Screening and Management of High Blood Pressure in Children and Adolescents. *Pediatrics* 2017;140(3):e20171904 (doi:10.1542/peds.2017-1904), Table 6, "screening blood pressure values requiring further evaluation".
  - The table gives a value for each year of age from 1 to 12, for boys and girls, and 120/80 from 13. Each band here takes the highest in it (104/64, 111/74, 120/80); a reading at or above that is flagged, so the table holds one less. A 12-year-old is therefore flagged only from 120/80, not from the 113/75 (boys) or 114/75 (girls) the table gives that age.
  - The table starts at age 1, so an infant has no upper value.
  - **Read second-hand:** the journal's page refused the request, and the numbers were taken from the Merck Manual's reproduction of the table (<https://www.msdmanuals.com/professional/multimedia/table/screening-blood-pressure-values-in-children-that-need-additional-evaluation>). They should be checked against the guideline itself.
- **Children's systolic pressure, lower value:** the Pediatric Advanced Life Support definition of hypotension (below 70 under 1 year, below 70 + 2 × age in years to age 10, below 90 above), at the band's youngest age (70, 72, 82, 90).
  - **Read second-hand** (WikEM, <https://wikem.org/wiki/Vital_signs_(peds)>), not in the American Heart Association's text. The rule is said to be lower still in the first month of life; one value, 70, is used for the whole first year here. No source was found for a child's diastolic lower value, so there is none.
- **Adults:** heart rate 60 to 100, and blood pressure from 90/60, from MedlinePlus (US National Library of Medicine), "Vital signs", reviewed 1 January 2025 (<https://medlineplus.gov/ency/article/002341.htm>). Respiratory rate 12 to 20 from StatPearls, "Vital Sign Assessment" (<https://www.ncbi.nlm.nih.gov/sites/books/NBK553213/>). The upper blood pressure is one below the American Heart Association and American College of Cardiology's stage 1 (130 systolic or 80 diastolic).
  - The adult diastolic upper value was 84 before this step, which no source supported; it is 79 now, in `observation_codes` too (the range used if this table is ever empty). Readings already recorded were given the new ranges by the `VitalCorrections` migration.
  - **Kept at 60 to 79 (Tahseen, 19 November 2026).** The 2017 ACC/AHA guideline defines normal blood pressure as under 120 systolic and under 80 diastolic, elevated as 120 to 129 and under 80, and stage 1 hypertension as 130 to 139 or 80 to 89: Whelton PK, Carey RM, Aronow WS, et al. 2017 ACC/AHA/AAPA/ABC/ACPM/AGS/APhA/ASH/ASPC/NMA/PCNA Guideline for the Prevention, Detection, Evaluation, and Management of High Blood Pressure in Adults. *Hypertension* 2018;71(6):e13–e115 (<https://doi.org/10.1161/HYP.0000000000000065>). The journal's page refused the request; the categories were read on the American Heart Association's own page, "Understanding Blood Pressure Readings" (<https://www.heart.org/en/health-topics/high-blood-pressure/understanding-blood-pressure-readings>).
  - **A diastolic of 80 to 84 is "normal" only on the European scale:** optimal is under 120 and under 80, normal is 120 to 129 and/or 80 to 84, and high normal is 130 to 139 and/or 85 to 89. Williams B, Mancia G, Spiering W, et al. 2018 ESC/ESH Guidelines for the management of arterial hypertension. *European Heart Journal* 2018;39(33):3021–3104, Table 3 (<https://doi.org/10.1093/eurheartj/ehy339>), read in the guideline. This project uses the American one.
- **Still to replace (Tahseen, 19 November 2026):** the children's heart and respiratory rate ranges, with the 1st and 99th centiles of Fleming S, Thompson M, Stevens R, et al., *Lancet* 2011;377:1011–1018 (author manuscript PMC3789232); and the children's blood pressure values, with the 2017 AAP guideline's Table 6 read from the guideline. Neither could be read by an automated client: Fleming's centiles are in the web appendix, which PubMed Central serves behind a browser check, and the AAP's page refused the request. Nothing was entered from memory; the rows above stand until the tables are supplied.

**Rights.** MedlinePlus is a US government work. The two *Pediatrics* articles are copyright American Academy of Pediatrics, StatPearls is CC BY-NC-ND 4.0, and the life-support text is the American Heart Association's. Nothing of theirs is copied here but the numbers, which are facts, each with its citation; no table is reproduced.

### How drug classes are joined to RxNorm

The FDA's indexing files name a substance and its Established Pharmacologic Classes. DailyMed's two mapping files say which class files and which RxNorm products belong to each drug label. A label whose products have exactly one ingredient between them tells us that ingredient's classes; a class is kept when at least four in five of the ingredient's labels carry it. That gives classes for 1,413 ingredients. For an ingredient with no label of its own, such as one sold only in combinations, the class file's substance is matched through the FDA substance list's RxNorm column, which brings the total to 1,945 of RxNorm's 5,886 ingredients. The rest have no FDA class and get no class check. Classes are kept by name; the class files' own codes are MED-RT's and are not stored.

### Which classes cross-react

Two rows, both from Khan DA, Banerji A, Blumenthal KG, et al. Drug allergy: a 2022 practice parameter update. *J Allergy Clin Immunol.* 2022;150(6):1333-1393: penicillins with cephalosporins, and penicillins with carbapenems. A class always cross-reacts with itself. Adding a row needs a published source.

## Licenses and attribution

- **RxNorm.** The Current Prescribable Content needs no license. This product uses publicly available data courtesy of the U.S. National Library of Medicine (NLM), National Institutes of Health, Department of Health and Human Services; NLM is not responsible for the product and does not endorse or recommend this or any other product. The data here is the release named above and may not be current. The full RxNorm release, which needs a UMLS license, is not used.
- **ICD-10-CM.** Published by the U.S. government (CDC/NCHS) for public use.
- **UNII.** Published by the U.S. Food and Drug Administration. No license is stated on its download page; as a work of the U.S. government it is taken to be in the public domain.
- **FDA pharmacologic class indexing files and DailyMed mapping files.** Published by the FDA and the National Library of Medicine. MED-RT and RxClass are not used.
- **LOINC.** This material contains content from LOINC (http://loinc.org). LOINC is copyright © Regenstrief Institute, Inc. and the Logical Observation Identifiers Names and Codes (LOINC) Committee and is available at no cost under the license at http://loinc.org/license. LOINC® is a registered United States trademark of Regenstrief Institute, Inc. The version used is 2.83. Where LOINC records a third party's rights in a term, its notice is carried in the `copyright` column.
- **PHQ-9 and GAD-7.** Copyright © Pfizer Inc. All rights reserved. Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute. (LOINC's notice for both, stored with each questionnaire.) The severity bands are the authors' published ones.
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
