# Reference data

The standard code lists SwiftChart loads, where each comes from, and what its publisher asks of anyone who uses it. Nothing is fetched while the apps run: the codes are in the repository as generated SQL, so the clinic's server works with no internet (NFR-08).

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
| `drug_interactions` | Interactions between two ingredients | DDInter 2.0, downloaded 18 November 2026 | 81,610 |

The clinic's 29 common order items (`SYN-...`) come from a migration, not from here; where a standard code applies they name it.

Names are the sources' own, with two changes of form: an ICD-10-CM code is written with its dot (`E11.9`), and a UNII or RxNorm ingredient name is written in sentence case. 136 RxNorm product names longer than 300 characters are left out.

### Reference ranges

The ranges in `observation_codes` are **this clinic's own**, set for synthetic data from the adult intervals that textbooks and laboratories commonly publish. They are not LOINC's, they don't vary by age or sex, and they are not validated for clinical use. Each row says so in `range_source`.

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
| R45.88 | Nonsuicidal self-harm |
| Z91.51, Z91.52 | Personal history of suicidal behavior, and of nonsuicidal self-harm |
| T36–T65 with intent 2 | Poisoning by drugs, and toxic effects of other substances, by intentional self-harm |
| T71.1x2, T71.2x2 | Asphyxiation by intentional self-harm (smothering, plastic bag, bed linens, furniture, hanging, other causes, car trunk, refrigerator) |

A code matches by its start, so every code under a category listed is covered (T14.91XA under T14.91, F41.1 under F01–F99). Case, the dot and spaces round the code don't matter.

In T36–T65 the intent is one character of the code, and "2" means intentional self-harm. In most of these codes it is the sixth character (T39.012A, T43.212D). In the codes of a four-character subcategory it is the fifth, with an X filling the sixth (T36.92XA). The rule takes both forms. Checked against the fiscal year 2027 code file, they are exactly its 954 "intentional self-harm" codes in T36–T65, and no other code in that range.

In T71 (asphyxiation) the intent is the sixth character of the T71.1 and T71.2 codes, and "2" again means intentional self-harm (T71.162A, hanging; T71.222A, trapped in a car trunk). The rule takes those, and no other T71 code: checked against the fiscal year 2027 code file, they are exactly its 24 "intentional self-harm" codes in T71 (eight kinds, each with A, D and S). R45.88 and these T71 codes were added after step 2b's review; step 2b's subset of the codes holds R45.88 but none of the T71 codes, which can be picked once step 2a loads the full list.

When the rule widens, the migration job marks the diagnoses already recorded that it now covers, on its next run, and the audit page stops showing their values.

The rule is `ehr.icd10cm_is_sensitive` in `db/policies/65-chart-lists.sql`; changing it is a reviewed change to that file. A medication is sensitive when a clinician marks it.
