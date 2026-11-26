-- Hand-written, not generated: interactions between two ingredients that neither DDInter nor the ONC list holds, each
-- taken from the Drug Interactions section (or the contraindications, or a boxed warning) of one drug's FDA label on
-- DailyMed. Labels are in the public domain. Tahseen's decision of 19 November 2026 (docs/expansion/PLAN.md); the
-- pairs and how they were chosen are in docs/expansion/reference-data.md.
--
-- Each row: the two ingredients' RxNorm codes, a level, what the label says in this project's words, and the label it
-- was read from, with its DailyMed set ID. The words are a paraphrase kept close to the label; nothing is added that
-- the label doesn't say. The level is this project's reading of the label, not the label's own word: "major" where the
-- label contraindicates the pair, carries a boxed warning about it, reports a serious outcome (toxicity, serotonin
-- syndrome, renal failure), or says one drug's concentration rises by more than half; "moderate" where it says to
-- monitor or adjust a dose; "minor" otherwise.
--
-- To add a pair: read the label, add a row, and say which label and section. Applied as ehr_owner after db/policies;
-- safe to run again: it makes the table hold exactly these rows for the source 'fda-label'.
BEGIN;
CREATE TEMPORARY TABLE incoming_label_interactions (one text, other text, level text, detail text, citation text) ON COMMIT DROP;
INSERT INTO incoming_label_interactions (one, other, level, detail, citation) VALUES
    -- tramadol + sertraline
    ('10689', '36437', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- tramadol + fluoxetine
    ('10689', '4493', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome. It also names this drug as a CYP2D6 inhibitor: together it may raise tramadol''s plasma level and lower that of its active metabolite, and the label says to follow the patient closely for opioid withdrawal, seizures and serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- tramadol + paroxetine
    ('10689', '32937', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome. It also names this drug as a CYP2D6 inhibitor: together it may raise tramadol''s plasma level and lower that of its active metabolite, and the label says to follow the patient closely for opioid withdrawal, seizures and serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- tramadol + citalopram
    ('10689', '2556', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- tramadol + escitalopram
    ('10689', '321988', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- tramadol + fluvoxamine
    ('10689', '42355', 'major',
     'The tramadol label lists SSRIs among the serotonergic drugs whose use with an opioid has resulted in serotonin syndrome.',
     'FDA label: TRAMADOL HYDROCHLORIDE tablet, coated (Bryant Ranch Prepack), section 7, Drug Interactions. DailyMed set ID 2bf66203-8b45-475d-9a70-4d7c8bdfe6b3: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=2bf66203-8b45-475d-9a70-4d7c8bdfe6b3 (read 19 November 2026).'),
    -- oxycodone + alprazolam
    ('7804', '596', 'major',
     'The oxycodone label carries a boxed warning: use of an opioid with a benzodiazepine may result in profound sedation, respiratory depression, coma and death. It says to reserve prescribing them together for patients for whom the alternatives are inadequate.',
     'FDA label: OXYCODONE HYDROCHLORIDE tablet, coated (Ohemo Life Sciences), Boxed Warning and section 7, Drug Interactions. DailyMed set ID 1bcc266d-cb6a-8c4a-e063-6394a90a9c5e: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1bcc266d-cb6a-8c4a-e063-6394a90a9c5e (read 19 November 2026).'),
    -- oxycodone + lorazepam
    ('7804', '6470', 'major',
     'The oxycodone label carries a boxed warning: use of an opioid with a benzodiazepine may result in profound sedation, respiratory depression, coma and death. It says to reserve prescribing them together for patients for whom the alternatives are inadequate.',
     'FDA label: OXYCODONE HYDROCHLORIDE tablet, coated (Ohemo Life Sciences), Boxed Warning and section 7, Drug Interactions. DailyMed set ID 1bcc266d-cb6a-8c4a-e063-6394a90a9c5e: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1bcc266d-cb6a-8c4a-e063-6394a90a9c5e (read 19 November 2026).'),
    -- oxycodone + diazepam
    ('7804', '3322', 'major',
     'The oxycodone label carries a boxed warning: use of an opioid with a benzodiazepine may result in profound sedation, respiratory depression, coma and death. It says to reserve prescribing them together for patients for whom the alternatives are inadequate.',
     'FDA label: OXYCODONE HYDROCHLORIDE tablet, coated (Ohemo Life Sciences), Boxed Warning and section 7, Drug Interactions. DailyMed set ID 1bcc266d-cb6a-8c4a-e063-6394a90a9c5e: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1bcc266d-cb6a-8c4a-e063-6394a90a9c5e (read 19 November 2026).'),
    -- oxycodone + clonazepam
    ('7804', '2598', 'major',
     'The oxycodone label carries a boxed warning: use of an opioid with a benzodiazepine may result in profound sedation, respiratory depression, coma and death. It says to reserve prescribing them together for patients for whom the alternatives are inadequate.',
     'FDA label: OXYCODONE HYDROCHLORIDE tablet, coated (Ohemo Life Sciences), Boxed Warning and section 7, Drug Interactions. DailyMed set ID 1bcc266d-cb6a-8c4a-e063-6394a90a9c5e: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1bcc266d-cb6a-8c4a-e063-6394a90a9c5e (read 19 November 2026).'),
    -- lisinopril + losartan
    ('29046', '52175', 'major',
     'The lisinopril label says blocking the renin-angiotensin system twice, with an ACE inhibitor and an angiotensin receptor blocker, is associated with increased risks of hypotension, hyperkalemia and changes in renal function, including acute renal failure, compared with one drug alone.',
     'FDA label: LISINOPRIL tablet (Quallent Pharmaceuticals Health), section 7, Drug Interactions. DailyMed set ID f2e09150-22f2-4028-b214-917d686200ad: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=f2e09150-22f2-4028-b214-917d686200ad (read 19 November 2026).'),
    -- lisinopril + spironolactone
    ('29046', '9997', 'moderate',
     'The lisinopril label says potassium-sparing diuretics, spironolactone among them, can increase the risk of hyperkalemia, and that if they are used together the patient''s serum potassium should be monitored frequently.',
     'FDA label: LISINOPRIL tablet (Quallent Pharmaceuticals Health), section 7, Drug Interactions. DailyMed set ID f2e09150-22f2-4028-b214-917d686200ad: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=f2e09150-22f2-4028-b214-917d686200ad (read 19 November 2026).'),
    -- lisinopril + lithium
    ('29046', '6448', 'major',
     'The lisinopril label says lithium toxicity has been reported in patients taking lithium with drugs that cause sodium to be eliminated, including ACE inhibitors, that it was usually reversible when both were stopped, and to monitor serum lithium levels during concurrent use.',
     'FDA label: LISINOPRIL tablet (Quallent Pharmaceuticals Health), section 7, Drug Interactions. DailyMed set ID f2e09150-22f2-4028-b214-917d686200ad: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=f2e09150-22f2-4028-b214-917d686200ad (read 19 November 2026).'),
    -- metoprolol + verapamil
    ('6918', '11170', 'moderate',
     'The metoprolol label says verapamil and diltiazem slow atrioventricular conduction and decrease heart rate, and that using them with a beta-blocker can increase the risk of bradycardia.',
     'FDA label: METOPROLOL TARTRATE tablet (NuCare Pharmaceuticals), section 7, Drug Interactions. DailyMed set ID 485f1fdb-6543-4f47-e063-6394a90a2459: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=485f1fdb-6543-4f47-e063-6394a90a2459 (read 19 November 2026).'),
    -- metoprolol + diltiazem
    ('6918', '3443', 'moderate',
     'The metoprolol label says verapamil and diltiazem slow atrioventricular conduction and decrease heart rate, and that using them with a beta-blocker can increase the risk of bradycardia.',
     'FDA label: METOPROLOL TARTRATE tablet (NuCare Pharmaceuticals), section 7, Drug Interactions. DailyMed set ID 485f1fdb-6543-4f47-e063-6394a90a2459: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=485f1fdb-6543-4f47-e063-6394a90a2459 (read 19 November 2026).'),
    -- digoxin + amiodarone
    ('3407', '703', 'major',
     'The digoxin label lists amiodarone among the drugs that raise digoxin concentrations by more than 50% (serum concentration up 70%), and says to measure serum digoxin concentrations before starting the other drug.',
     'FDA label: DIGOXIN tablet (Proficient Rx), section 7.2, Pharmacokinetic Drug Interactions. DailyMed set ID 42d079a7-def1-4220-b71f-db2651966ffe: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=42d079a7-def1-4220-b71f-db2651966ffe (read 19 November 2026).'),
    -- digoxin + verapamil
    ('3407', '11170', 'major',
     'The digoxin label lists verapamil among the drugs that raise digoxin concentrations by more than 50% (serum concentration up 50 to 75%), and says to measure serum digoxin concentrations before starting the other drug.',
     'FDA label: DIGOXIN tablet (Proficient Rx), section 7.2, Pharmacokinetic Drug Interactions. DailyMed set ID 42d079a7-def1-4220-b71f-db2651966ffe: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=42d079a7-def1-4220-b71f-db2651966ffe (read 19 November 2026).'),
    -- fluoxetine + amitriptyline
    ('4493', '704', 'moderate',
     'The fluoxetine label says plasma levels of tricyclic antidepressants that had been stable rose 2- to 10-fold when fluoxetine was added (shown with imipramine and desipramine), and that the tricyclic''s dose may need to be reduced and its plasma concentration monitored. Fluoxetine strongly inhibits CYP2D6.',
     'FDA label: FLUOXETINE capsule (Time Cap Laboratories), section 7, Drug Interactions. DailyMed set ID d7a4db7f-cdac-2b36-e053-2995a90a8d64: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=d7a4db7f-cdac-2b36-e053-2995a90a8d64 (read 19 November 2026).'),
    -- sertraline + linezolid
    ('36437', '190376', 'major',
     'The sertraline label contraindicates sertraline in patients taking a monoamine oxidase inhibitor, and names linezolid as one, because of an increased risk of serotonin syndrome.',
     'FDA label: SERTRALINE HYDROCHLORIDE tablet, film coated (Major Pharmaceuticals), section 4, Contraindications, and section 7.1. DailyMed set ID 4cf8d5c8-842b-4e7c-9a99-3f41ef8abbdb: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=4cf8d5c8-842b-4e7c-9a99-3f41ef8abbdb (read 19 November 2026).'),
    -- sildenafil + nitroglycerin
    ('136411', '4917', 'major',
     'The sildenafil label contraindicates sildenafil in patients using organic nitrates in any form: sildenafil potentiates their blood-pressure-lowering effect.',
     'FDA label: SILDENAFIL CITRATE tablet (Proficient Rx), section 4, Contraindications, and section 7.1, Nitrates. DailyMed set ID 1add0cab-8873-4cd1-ab53-e05c4ac458b1: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1add0cab-8873-4cd1-ab53-e05c4ac458b1 (read 19 November 2026).'),
    -- sildenafil + isosorbide dinitrate
    ('136411', '6058', 'major',
     'The sildenafil label contraindicates sildenafil in patients using organic nitrates in any form: sildenafil potentiates their blood-pressure-lowering effect.',
     'FDA label: SILDENAFIL CITRATE tablet (Proficient Rx), section 4, Contraindications, and section 7.1, Nitrates. DailyMed set ID 1add0cab-8873-4cd1-ab53-e05c4ac458b1: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=1add0cab-8873-4cd1-ab53-e05c4ac458b1 (read 19 November 2026).'),
    -- lithium + hydrochlorothiazide
    ('6448', '5487', 'moderate',
     'The lithium label says diuretics may increase serum lithium concentrations: the sodium loss a diuretic causes may reduce lithium''s clearance.',
     'FDA label: LITHIUM CARBONATE tablet and capsule (Hikma Pharmaceuticals), section 7, Drug Interactions. DailyMed set ID b839ff4b-f62d-41ab-a823-550a756d58ec: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=b839ff4b-f62d-41ab-a823-550a756d58ec (read 19 November 2026).'),
    -- doxycycline + ferrous sulfate
    ('3640', '24947', 'minor',
     'The doxycycline label says the absorption of tetracyclines is impaired by iron-containing preparations.',
     'FDA label: DOXYCYCLINE HYCLATE tablet, coated (Hikma Pharmaceuticals USA), Drug Interactions. DailyMed set ID 9b52e0a7-f024-4d8a-a59e-374946e60b44: https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=9b52e0a7-f024-4d8a-a59e-374946e60b44 (read 19 November 2026).');
INSERT INTO ehr.drug_interactions (ingredient_a, ingredient_b, source, level, detail, citation)
SELECT least(one, other), greatest(one, other), 'fda-label', level, detail, citation FROM incoming_label_interactions
ON CONFLICT (ingredient_a, ingredient_b, source) DO UPDATE
    SET level = EXCLUDED.level, detail = EXCLUDED.detail, citation = EXCLUDED.citation
    WHERE (drug_interactions.level, drug_interactions.detail, drug_interactions.citation)
        IS DISTINCT FROM (EXCLUDED.level, EXCLUDED.detail, EXCLUDED.citation);
DELETE FROM ehr.drug_interactions t
WHERE t.source = 'fda-label'
  AND NOT EXISTS (SELECT 1 FROM incoming_label_interactions i WHERE least(i.one, i.other) = t.ingredient_a AND greatest(i.one, i.other) = t.ingredient_b);
COMMIT;
