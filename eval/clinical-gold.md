# The clinical gold against the dictation checks

Written by `uv run --project eval python -m ehr_eval.goldreport` (`ehr_eval/goldreport.py`); don't edit it. Each script's display text is read as its transcript, and its clinical gold (`[gold.*]`), what a careful clinician would accept, is checked by the dictation service's own checks (`validation.py` in the sidecar). These are the values they would discard, with why, and the rule-following gold (`[gold_rules.*]`) that the eval measures the pipeline against instead. See README.md, Two gold sets.

63 of 161 clinical gold values would be discarded. By reason (a value can have more than one):

| Reason | Values |
|---|---|
| `words_out_of_order` | 36 |
| `unsupported_content` | 26 |
| `negation_dropped` | 4 |
| `drug_not_in_sources` | 1 |
| `experiencer_dropped` | 1 |

## d01-htn-aprocitentan: subjective

Reasons: `unsupported_content`

- Clinical gold: 63-year-old woman with resistant hypertension, started on aprocitentan 12.5 mg daily 6 weeks ago in addition to amlodipine 10 mg, chlorthalidone 25 mg and losartan 100 mg. Mild swelling in both ankles by the end of the day. Denies chest pain, shortness of breath, headaches or dizziness. Home readings in the 130s over 80s. Family history of stroke in her father at 68.
- Rule-following gold: This is a follow-up visit for resistant hypertension. She is a 63-year-old woman who was started on aprocitentan 12.5 mg daily 6 weeks ago in addition to amlodipine 10 mg, chlorthalidone 25 mg and losartan 100 mg. She reports mild swelling in both ankles by the end of the day. She denies chest pain, shortness of breath, headaches or dizziness. Home readings have been running in the 130s over 80s. Family history of stroke in her father at 68.

## d01-htn-aprocitentan: objective

Reasons: `words_out_of_order`

- Clinical gold: Blood pressure 142/88, heart rate 72; blood pressure better than at the last visit, heart rate about the same. Trace pitting edema at both ankles. Hemoglobin 12.9, down from 13.6.
- Rule-following gold: Blood pressure today is 142/88, heart rate 72. Her blood pressure is better than at the last visit but her heart rate is about the same. There is trace pitting edema at both ankles. Hemoglobin is 12.9, down from 13.6.

## d02-t2dm-bexagliflozin: objective

Reasons: `words_out_of_order`

- Clinical gold: A1c 8.4, down from 9.1 in the spring. Weight 96 kg, down 2 kg.
- Rule-following gold: His A1c today is 8.4, down from 9.1 in the spring. Weight is 96 kg, down 2 kg.

## d02-t2dm-bexagliflozin: plan

Reasons: `drug_not_in_sources`, `unsupported_content`

- Clinical gold: Start semaglutide 0.25 mg weekly for the first month, then increase as tolerated. Continue metformin and bexagliflozin. Reviewed hygiene to prevent yeast infections. Follow up in 3 months with a repeat A1c.
- Rule-following gold: Add semaglutide, start at 0.25 mg weekly for the first month and then increase as tolerated. Continue metformin and bexagliflozin. Reviewed hygiene to prevent yeast infections. Follow up in 3 months with a repeat A1c.

## d03-copd-ensifentrine: subjective

Reasons: `words_out_of_order`

- Clinical gold: 71-year-old retired joiner admitted on October 28 with an infective exacerbation of COPD, discharged after 4 days on a 5-day course of prednisolone 30 mg and doxycycline. Feels about back to his usual self; his wife says he is coping well at home. Breathlessness MRC grade 3: stops for breath after about 100 meters on the flat. Morning cough with white sputum; no green sputum, no fever, no chest pain. Has not smoked for 6 years. Inhalers: tiotropium and olodaterol once daily. 3 exacerbations in the last year needing steroids.
- Rule-following gold: Follow-up after an exacerbation of COPD. This 71-year-old retired joiner was admitted on October 28 with an infective exacerbation and discharged after 4 days on a 5-day course of prednisolone 30 mg and doxycycline. He feels about back to his usual self. His wife says he is coping well at home. His breathlessness is MRC grade 3, so he stops for breath after about 100 meters on the flat. He still has a morning cough with white sputum but no green sputum, no fever and no chest pain. He has not smoked for 6 years. His inhalers are tiotropium and olodaterol once daily. He had 3 exacerbations in the last year needing steroids.

## d03-copd-ensifentrine: objective

Reasons: `words_out_of_order`

- Clinical gold: Good technique. Oxygen saturation 94% on room air, respiratory rate 18, pulse 84, temperature 36.8 degrees. Scattered wheezes at both bases, no crackles. Spirometry from the summer: FEV1 1.4 liters, 48% predicted.
- Rule-following gold: His technique was good when I watched him today. On examination his oxygen saturation is 94% on room air, respiratory rate 18, pulse 84 and temperature 36.8 degrees. There are scattered wheezes at both bases, no crackles. Spirometry from the summer showed an FEV1 of 1.4 liters, 48% predicted.

## d03-copd-ensifentrine: plan

Reasons: `words_out_of_order`

- Clinical gold: Add ensifentrine 3 mg nebulized twice daily alongside his current inhalers; explained he may notice back pain or a mild headache at first. Refer to pulmonary rehabilitation. Rescue pack of prednisolone and doxycycline to keep at home with a written action plan. Review in clinic in 3 months, sooner if he has another flare.
- Rule-following gold: Add ensifentrine 3 mg nebulized twice daily, alongside his current inhalers. I have explained it is a new kind of inhaled treatment and he may notice back pain or a mild headache at first. Refer to pulmonary rehabilitation. Rescue pack of prednisolone and doxycycline to keep at home with a written action plan. Review in clinic in 3 months, sooner if he has another flare.

## d04-bronchiolitis-infant: plan

Reasons: `unsupported_content`

- Clinical gold: Admit for observation, with nasogastric feeds if intake drops below half. Nasal suction before feeds. Saline drops. Target saturation above 92%. Paracetamol for the fever if he is uncomfortable. Explained the expected course to mum.
- Rule-following gold: Admit for observation and nasogastric feeds if intake drops below half. Nasal suction before feeds. Saline drops. Target saturation above 92. Paracetamol for the fever if he is uncomfortable. Explained the expected course to mum.

## d05-tka-suzetrigine: subjective

Reasons: `unsupported_content`

- Clinical gold: 66-year-old woman 12 days after a right total knee replacement on November 3, 2026. Pain much better. Discharged on suzetrigine 50 mg every 12 hours, now taking it about once a day, with paracetamol 1 g four times a day. No opioid needed since discharge. No calf pain, no fever, no wound discharge. Walking with one stick. Right knee much better; left knee now aches more because she leans on it.
- Rule-following gold: 66-year-old woman seen 12 days after a right total knee replacement done on November 3, 2026. Her pain is much better. She was discharged on suzetrigine 50 mg every 12 hours and now takes it about once a day, with paracetamol 1 g four times a day. She has not needed any opioid since discharge. No calf pain, no fever and no wound discharge. She is walking with one stick. The right knee is much better, but the left knee now aches more because she leans on it.

## d05-tka-suzetrigine: objective

Reasons: `words_out_of_order`

- Clinical gold: Wound clean and dry; clips removed today. Range of motion 5 to 90 degrees. Calf soft and non-tender.
- Rule-following gold: The wound is clean and dry and the clips were removed today. Range of motion is 5 to 90 degrees. Calf is soft and non-tender.

## d06-eczema-lebrikizumab: subjective

Reasons: `unsupported_content`

- Clinical gold: 29-year-old woman with lifelong eczema, worse over the last year despite twice daily triamcinolone ointment and tacrolimus on the face. Itches most nights and wakes 2 or 3 times. No recent skin infections, no eye symptoms. Tried dupilumab last year, stopped after 4 months because of conjunctivitis. Reports hives from cephalexin last month.
- Rule-following gold: 29-year-old woman with lifelong eczema, worse over the last year despite twice daily triamcinolone ointment and tacrolimus on the face. She itches most nights and wakes 2 or 3 times. No skin infections recently and no eye symptoms. She tried dupilumab last year and stopped after 4 months because of conjunctivitis. She told the nurse she got hives from cephalexin last month.

## d06-eczema-lebrikizumab: objective

Reasons: `words_out_of_order`

- Clinical gold: Lichenified plaques in both elbow creases and behind the knees, with excoriations. EASI score 18.
- Rule-following gold: Exam shows lichenified plaques in both elbow creases and behind the knees, with excoriations. EASI score is 18.

## d07-ppd-zuranolone: objective

Reasons: `words_out_of_order`

- Clinical gold: EPDS score 19. Mental status: tearful, speech normal, no suicidal ideation, insight good.
- Rule-following gold: Her EPDS score today is 19. Mental status: tearful, speech normal, thought content without suicidal ideation, insight good.

## d08-cystitis-gepotidacin: plan

Reasons: `words_out_of_order`

- Clinical gold: Start gepotidacin 1500 mg twice daily for 5 days. Fluids. Come back if she gets a fever or back pain, or if not better in 48 hours.
- Rule-following gold: Start gepotidacin 1500 mg twice daily for 5 days. Fluids and come back if she gets a fever or back pain or if it is not better in 48 hours.

## d09-mash-resmetirom: subjective

Reasons: `unsupported_content`

- Clinical gold: 58-year-old man referred with metabolic dysfunction associated steatohepatitis. Type 2 diabetes, high blood pressure and obesity. Drinks alcohol rarely, about one beer a month. No jaundice, no confusion, no swelling of the abdomen, no bleeding. Medications: metformin, semaglutide 1 mg weekly, lisinopril, rosuvastatin 40 mg. Lost 7 kg since starting semaglutide. No gallbladder problems.
- Rule-following gold: This is a 58-year-old man referred with metabolic dysfunction associated steatohepatitis. He has type 2 diabetes, high blood pressure and obesity. He drinks alcohol rarely, maybe one beer a month. No jaundice, no confusion, no swelling of the abdomen and no bleeding. His medications are metformin, semaglutide 1 mg weekly, lisinopril and rosuvastatin 40 mg. He has lost 7 kg since starting semaglutide. No gallbladder problems.

## d09-mash-resmetirom: objective

Reasons: `unsupported_content`

- Clinical gold: Liver biopsy in the summer: steatohepatitis with stage F2 fibrosis. Thyroid function normal in the summer. Weight 104 kg, BMI 34, blood pressure 128/78. No stigmata of chronic liver disease; liver edge not palpable. ALT 64, AST 48, platelets 212, albumin 4.2. Liver stiffness 9.6 kPa on elastography.
- Rule-following gold: A liver biopsy in the summer showed steatohepatitis with stage F2 fibrosis. His thyroid function was normal in the summer. Weight 104 kg, BMI 34, blood pressure 128/78. No stigmata of chronic liver disease. Liver edge not palpable. Labs from last week show ALT 64, AST 48, platelets 212 and albumin 4.2. Elastography gave a liver stiffness of 9.6 kPa.

## d10-pah-sotatercept: subjective

Reasons: `words_out_of_order`

- Clinical gold: 47-year-old woman with idiopathic pulmonary arterial hypertension diagnosed 4 years ago, on macitentan 10 mg daily and tadalafil 40 mg daily. Started sotatercept 6 months ago, injection under the skin every 3 weeks; last dose on November 10. Feels much better; can now climb two flights of stairs without stopping, which she could not do in the spring. No chest pain, no fainting, no ankle swelling. A couple of nosebleeds, short and stopping with pressure, and some small red spots on her cheeks. Back at work full time.
- Rule-following gold: 47-year-old woman with idiopathic pulmonary arterial hypertension diagnosed 4 years ago, on macitentan 10 mg daily and tadalafil 40 mg daily. She started sotatercept 6 months ago, given as an injection under the skin every 3 weeks, and her last dose was on November 10. She feels much better. She can now climb two flights of stairs without stopping, which she could not do in the spring. No chest pain, no fainting, no ankle swelling. She has had a couple of nosebleeds and some small red spots on her cheeks. The nosebleeds are short and stop with pressure. She is back at work full time.

## d10-pah-sotatercept: objective

Reasons: `words_out_of_order`

- Clinical gold: Blood pressure 118/72, heart rate 76, saturation 96% at rest. A few telangiectasias on the cheeks. Heart sounds normal with a loud P2. No peripheral edema. 6-minute walk distance 452 meters, up from 371 before sotatercept. NT-proBNP 310, down from 1240. Hemoglobin 15.1, platelets 168.
- Rule-following gold: On examination blood pressure 118/72, heart rate 76, saturation 96% at rest. A few telangiectasias on the cheeks. Heart sounds normal with a loud P2. No peripheral edema. Her 6-minute walk distance today was 452 meters, up from 371 before sotatercept. NT-proBNP is 310, down from 1240. Hemoglobin 15.1 and platelets 168.

## d11-afib-dronedarone: subjective

Reasons: `unsupported_content`

- Clinical gold: 68-year-old man with paroxysmal atrial fibrillation on apixaban 5 mg twice daily and dronedarone 400 mg twice daily with meals. 2 short episodes of palpitations last month, each lasting less than an hour. No bleeding, no syncope. Amiodarone listed as an allergy, but the reaction was nausea years ago.
- Rule-following gold: Cardiology follow-up for paroxysmal atrial fibrillation. 68-year-old man on apixaban 5 mg twice daily and dronedarone 400 mg twice daily with meals. He had 2 short episodes of palpitations last month, each lasting less than an hour. No bleeding, no syncope. His chart lists amiodarone as an allergy but that was just nausea years ago.

## d11-afib-dronedarone: objective

Reasons: `words_out_of_order`

- Clinical gold: Blood pressure 134/82, heart rate 64 and regular. ECG: sinus rhythm, QTc 448 milliseconds. Creatinine 1.1.
- Rule-following gold: Blood pressure 134/82, heart rate 64 and regular. ECG today shows sinus rhythm with a QTc of 448 milliseconds. Creatinine 1.1.

## d12-hfref-ivabradine: subjective

Reasons: `words_out_of_order`

- Clinical gold: 61-year-old man with heart failure with reduced ejection fraction after an anterior myocardial infarction 2 years ago, ejection fraction 30% on his last echo. NYHA class 2: breathless on hills but fine on the flat. Sleeps on two pillows. No paroxysmal nocturnal dyspnea, no ankle swelling, no chest pain, no dizziness. Weighs himself every morning; weight steady. Had his flu and pneumonia vaccines this autumn. Medications: sacubitril valsartan 49/51 mg twice daily, bisoprolol 10 mg daily, eplerenone 25 mg daily, dapagliflozin 10 mg daily, furosemide 40 mg in the morning.
- Rule-following gold: This is a 61-year-old man with heart failure with reduced ejection fraction after an anterior myocardial infarction 2 years ago, ejection fraction 30% on his last echo. He is NYHA class 2, breathless on hills but fine on the flat. He sleeps on two pillows, no paroxysmal nocturnal dyspnea, no ankle swelling, no chest pain and no dizziness. He weighs himself every morning and his weight has been steady. He had his flu and pneumonia vaccines this autumn. Current medications: sacubitril valsartan 49/51 mg twice daily, bisoprolol 10 mg daily, eplerenone 25 mg daily, dapagliflozin 10 mg daily and furosemide 40 mg in the morning.

## d13-hypothyroid-phone: objective

Reasons: `words_out_of_order`

- Clinical gold: TSH 7.8.
- Rule-following gold: Her TSH came back at 7.8.

## d13-hypothyroid-phone: plan

Reasons: `unsupported_content`

- Clinical gold: Take levothyroxine on an empty stomach an hour before breakfast and away from calcium. Increase levothyroxine to 112 mcg daily and repeat the TSH in 6 weeks. Liothyronine to be revisited only if she still feels unwell once her TSH is normal.
- Rule-following gold: We talked about taking it on an empty stomach an hour before breakfast and away from calcium. Increase levothyroxine to 112 mcg daily and repeat the TSH in 6 weeks. She asked about liothyronine and I said we would revisit that only if she still feels unwell once her TSH is normal.

## d14-asthma-child: subjective

Reasons: `unsupported_content`

- Clinical gold: 9-year-old boy with moderate persistent asthma, seen with his father. Using albuterol about twice a week, mostly with soccer; woke at night with cough once in the last month. No emergency visits and no oral steroids this year. Takes budesonide formoterol 80/4.5 two puffs twice daily with a spacer.
- Rule-following gold: 9-year-old boy with moderate persistent asthma, seen with his father. Since the last visit he has used his albuterol inhaler about twice a week, mostly with soccer, and woke at night with cough once in the last month. No emergency visits and no oral steroids this year. He takes budesonide formoterol 80/4.5 two puffs twice daily with a spacer.

## d15-acne-isotretinoin: objective

Reasons: `words_out_of_order`

- Clinical gold: Pregnancy test today negative. A few small papules on the chin, healing scars on the back, cheilitis. Fasting triglycerides last month 1.9; liver tests normal.
- Rule-following gold: Her pregnancy test today is negative. On examination there are a few small papules on the chin and healing scars on the back, with cheilitis. Her fasting triglycerides last month were 1.9 and liver tests were normal.

## d16-gout-febuxostat: objective

Reasons: `words_out_of_order`

- Clinical gold: Small tophus on the right elbow, no joint swelling today. Uric acid 7.4, down from 9.2. Creatinine 1.0.
- Rule-following gold: Exam shows a small tophus on the right elbow, no joint swelling today. Uric acid is 7.4, down from 9.2. Creatinine 1.0.

## d16-gout-febuxostat: plan

Reasons: `unsupported_content`

- Clinical gold: Increase febuxostat to 80 mg daily. Continue colchicine for another 3 months. Recheck uric acid in 4 weeks. Cut down on beer and sugary drinks.
- Rule-following gold: Increase febuxostat to 80 mg daily. Continue colchicine for another 3 months. Recheck uric acid in 4 weeks. Cut down on beer and sugary drinks.

## d17-schizophrenia-xanomeline: subjective

Reasons: `words_out_of_order`

- Clinical gold: 26-year-old man with schizophrenia, seen with his mother. Stopped risperidone in the summer because of weight gain and loss of libido. Started xanomeline and trospium 4 weeks ago, 50 mg and 20 mg twice daily, increased after a week to 100 mg and 20 mg twice daily. Takes it an hour before breakfast and before his evening meal. Some nausea and constipation, no vomiting. Voices quieter, sleeping better. Denies thoughts of harming himself or anyone else. No urinary problems.
- Rule-following gold: 26-year-old man with schizophrenia, seen with his mother. He stopped risperidone in the summer because of weight gain and loss of libido. 4 weeks ago we started xanomeline and trospium, 50 mg and 20 mg twice daily, and increased it after a week to 100 mg and 20 mg twice daily. He takes it an hour before breakfast and before his evening meal. He has had some nausea and constipation but no vomiting. The voices are quieter and he is sleeping better. He denies thoughts of harming himself or anyone else. No urinary problems.

## d17-schizophrenia-xanomeline: plan

Reasons: `unsupported_content`

- Clinical gold: Continue the current dose. Increase fiber and fluids for the constipation. Review in 4 weeks, when going up to 125 mg and 30 mg twice daily can be considered.
- Rule-following gold: Continue the current dose, increase fiber and fluids for the constipation, and review in 4 weeks, when we can consider going up to 125 mg and 30 mg twice daily.

## d18-alzheimers-donanemab: subjective

Reasons: `unsupported_content`

- Clinical gold: 74-year-old woman with early Alzheimer's disease confirmed on amyloid PET, here with her husband. 3 infusions of donanemab, the last on October 21, tolerated without any reaction. About the same per her husband; still managing at home with prompts for appointments and medicines. No headaches, no confusion after the infusions, no falls, no change in vision.
- Rule-following gold: 74-year-old woman with early Alzheimer's disease confirmed on amyloid PET, here with her husband. She has had 3 infusions of donanemab, the last on October 21. She tolerated them without any reaction. Her husband says she is about the same, still managing at home with prompts for appointments and medicines. No headaches, no confusion after the infusions, no falls and no change in vision.

## d18-alzheimers-donanemab: objective

Reasons: `words_out_of_order`

- Clinical gold: APOE4 heterozygous. MoCA 21 out of 30, the same as in the summer. Neurological examination unchanged. MRI last week showed no ARIA.
- Rule-following gold: She is APOE4 heterozygous. MoCA today 21 out of 30, the same as in the summer. Neurological examination unchanged. Her MRI last week showed no ARIA.

## d19-migraine-zavegepant: subjective

Reasons: `negation_dropped`, `words_out_of_order`

- Clinical gold: 36-year-old woman with migraine without aura, currently about 6 migraine days a month. Chest tightness with sumatriptan; does not want triptans again. Using zavegepant nasal spray 10 mg, one spray at onset; works within about an hour for most attacks. Bad taste afterwards, no other side effects. Takes atogepant 60 mg daily for prevention. Her mother and her sister both have migraine. No new neurological symptoms, no visual loss, no weakness. Headaches worse with bright light and noise, but not worse lying down or in the morning.
- Rule-following gold: 36-year-old woman with migraine without aura, currently about 6 migraine days a month. Triptans helped but she had chest tightness with sumatriptan and does not want to take them again. She has been using zavegepant nasal spray 10 mg, one spray at onset, and it works within about an hour for most attacks. She notices a bad taste afterwards but no other side effects. She takes atogepant 60 mg daily for prevention. Her mother and her sister both have migraine. No new neurological symptoms, no visual loss and no weakness. Headaches are worse with bright light and noise but not worse lying down or in the morning.

## d19-migraine-zavegepant: assessment

Reasons: `unsupported_content`

- Clinical gold: Episodic migraine without aura, improving, with good response to zavegepant for acute attacks.
- Rule-following gold: Episodic migraine without aura, improving, good response to zavegepant for acute attacks.

## d21-iga-sparsentan: subjective

Reasons: `negation_dropped`

- Clinical gold: 41-year-old man with biopsy proven IgA nephropathy diagnosed 18 months ago after he noticed cola colored urine during a chest infection. On losartan 100 mg daily and dapagliflozin 10 mg daily. Feels well. No visible blood in the urine since the first episode, no swelling, no rashes, no joint pains. Does not smoke, drinks very little. Father needed dialysis in his sixties; mother has high blood pressure. Works as a teacher and wants to keep working full time.
- Rule-following gold: 41-year-old man with biopsy proven IgA nephropathy diagnosed 18 months ago after he noticed cola colored urine during a chest infection. Since then he has been on losartan 100 mg daily and dapagliflozin 10 mg daily. He feels well. No visible blood in the urine since the first episode, no swelling, no rashes and no joint pains. He does not smoke and drinks very little. His father needed dialysis in his sixties and his mother has high blood pressure. He works as a teacher and wants to keep working full time.

## d21-iga-sparsentan: objective

Reasons: `words_out_of_order`

- Clinical gold: Protein to creatinine ratio still 1.4 g per gram; eGFR drifted from 68 to 59 over the year. Blood pressure 136/84, pulse 70, weight 88 kg. No edema. Potassium 4.8; ALT and AST normal.
- Rule-following gold: Despite that his protein to creatinine ratio is still 1.4 g per gram, and his eGFR has drifted from 68 to 59 over the year. Blood pressure today 136/84, pulse 70, weight 88 kg. No edema. Potassium 4.8, ALT and AST normal.

## d21-iga-sparsentan: plan

Reasons: `words_out_of_order`

- Clinical gold: Stop losartan tomorrow and start sparsentan 200 mg once daily for 2 weeks, then 400 mg once daily. Continue dapagliflozin. Liver tests every month for the first year as required by the safety program; potassium and creatinine in 2 weeks. Home blood pressure readings, target below 125/75. Low salt diet, under 2 g of sodium a day. Stop it and call if dizzy or lightheaded. Clinic review in 6 weeks.
- Rule-following gold: Stop losartan tomorrow and start sparsentan 200 mg once daily for 2 weeks, then 400 mg once daily. Continue dapagliflozin. Liver tests every month for the first year as required by the safety program, and potassium and creatinine in 2 weeks. Home blood pressure readings with a target below 125/75. Low salt diet, under 2 g of sodium a day. He knows to stop it and call if he is dizzy or lightheaded. Clinic review in 6 weeks.

## d22-cellulitis-ed: subjective

Reasons: `negation_dropped`, `words_out_of_order`

- Clinical gold: 49-year-old man with 3 days of a red, hot and painful left lower leg after a scratch from gardening. Felt shivery, temperature not measured. No calf pain before this, no recent travel, no history of clots. Type 2 diabetes on metformin. Reaction to penicillin as a child was only a mild rash, not hives or swelling.
- Rule-following gold: 49-year-old man with 3 days of a red, hot and painful left lower leg after a scratch from gardening. He has felt shivery but has not measured a temperature. No calf pain before this, no recent travel, no history of clots. He has type 2 diabetes on metformin. He told me his reaction to penicillin as a child was only a mild rash, not hives or swelling.

## d22-cellulitis-ed: objective

Reasons: `words_out_of_order`

- Clinical gold: Temperature 38.1, heart rate 102, blood pressure 132/80, respiratory rate 18, saturations 97% on air. Spreading erythema from the ankle to mid shin, warm and tender, no fluctuance, no crepitus. Calf itself not tender. Edge marked with a pen. Glucose 11.2.
- Rule-following gold: Temperature 38.1, heart rate 102, blood pressure 132/80, respiratory rate 18, saturations 97% on air. Spreading erythema from the ankle to mid shin, warm and tender, no fluctuance, no crepitus. The calf itself is not tender. I have marked the edge with a pen. Glucose 11.2.

## d24-psoriasis-apremilast: plan

Reasons: `unsupported_content`

- Clinical gold: Start apremilast with the starter pack, going up over five days to 30 mg twice daily. Warned about loose stools, nausea, headache and low mood, and to report if his mood drops. Continue the scalp gel. Review in 16 weeks. Refer to rheumatology if the finger stiffness gets worse.
- Rule-following gold: Agreed to start apremilast with the starter pack going up over five days to 30 mg twice daily. Warned him about loose stools nausea headache and low mood and to tell us if his mood drops. Continue the scalp gel. Review in 16 weeks and refer to rheumatology if the finger stiffness gets worse.

## d26-depression-vortioxetine: subjective

Reasons: `words_out_of_order`

- Clinical gold: 44-year-old woman with recurrent depression. Overdose of paracetamol 3 years ago during a previous episode. Sertraline 150 mg helped her mood but caused sexual side effects and emotional blunting; switched to vortioxetine 10 mg daily 6 weeks ago. Mood a little better. Sleep and appetite improving. Mild nausea in the mornings. Passive thoughts that life is not worth living on bad days, but no plans or intent; has not harmed herself.
- Rule-following gold: 44-year-old woman with recurrent depression. She took an overdose of paracetamol 3 years ago during a previous episode. Sertraline 150 mg helped her mood but caused sexual side effects and emotional blunting, so 6 weeks ago we switched to vortioxetine 10 mg daily. Her mood is a little better. Sleep and appetite are improving. She has mild nausea in the mornings. She has passive thoughts that life is not worth living on bad days but no plans or intent, and she has not harmed herself.

## d27-osteoporosis-denosumab: subjective

Reasons: `words_out_of_order`

- Clinical gold: 72-year-old woman with osteoporosis and a wrist fracture 2 years ago. Denosumab 60 mg under the skin every 6 months, the last dose on May 6, 2026, so due now. Takes calcium and vitamin D every day. Her mother broke her hip at 78. No new fractures, no jaw pain, no thigh pain. Has seen her dentist this year.
- Rule-following gold: 72-year-old woman with osteoporosis and a wrist fracture 2 years ago. She has had denosumab 60 mg under the skin every 6 months, the last dose on May 6, 2026, so she is due now. She takes calcium and vitamin D every day. Her mother broke her hip at 78. No new fractures, no jaw pain and no thigh pain. She has seen her dentist this year.

## d27-osteoporosis-denosumab: objective

Reasons: `words_out_of_order`

- Clinical gold: DEXA in the spring: T-score -2.8 at the lumbar spine and -2.4 at the hip. Calcium 9.4, vitamin D 38.
- Rule-following gold: Her DEXA in the spring showed a T-score of -2.8 at the lumbar spine and -2.4 at the hip. Calcium 9.4, vitamin D 38.

## d28-pots-midodrine: subjective

Reasons: `unsupported_content`

- Clinical gold: 24-year-old woman with postural orthostatic tachycardia syndrome since a viral illness last year. Lightheaded with her heart racing within a few minutes of standing, worse in the heat and after hot showers. Nearly fainted twice but has not fainted. Drinking about 2.5 liters a day and taking salt tablets. Fludrocortisone 0.1 mg helped a little but gave her headaches.
- Rule-following gold: 24-year-old woman with postural orthostatic tachycardia syndrome since a viral illness last year. She gets lightheaded and her heart races within a few minutes of standing, worse in the heat and after hot showers. She has nearly fainted twice but has not fainted. She is drinking about 2.5 liters a day and taking salt tablets. Fludrocortisone 0.1 mg helped a little but gave her headaches.

## d29-attr-acoramidis: subjective

Reasons: `unsupported_content`

- Clinical gold: 79-year-old man with wild type transthyretin amyloid cardiomyopathy diagnosed last year on a technetium PYP scan, after presenting with heart failure and bilateral carpal tunnel syndrome. On tafamidis, which his insurance will no longer cover. Short of breath walking about one block; has to rest on the stairs. Sleeps flat. No chest pain, no fainting, no palpitations. Ankles swell by the evening. Furosemide 40 mg twice daily and apixaban 5 mg twice daily for atrial fibrillation.
- Rule-following gold: 79-year-old man with wild type transthyretin amyloid cardiomyopathy diagnosed last year on a technetium PYP scan, after he presented with heart failure and bilateral carpal tunnel syndrome. He has been on tafamidis, but his insurance will no longer cover it. He is short of breath walking about one block and has to rest on the stairs. He sleeps flat. No chest pain, no fainting, no palpitations. His ankles swell by the evening. He takes furosemide 40 mg twice daily and apixaban 5 mg twice daily for atrial fibrillation.

## d29-attr-acoramidis: objective

Reasons: `words_out_of_order`

- Clinical gold: Blood pressure 106/64, heart rate 72 and irregular, weight 74 kg, up 2 kg from last visit. JVP up to the angle of the jaw. Bibasal crackles. Pitting edema to the mid shins. NT-proBNP 3400, up from 2100; creatinine 1.5. Echo: ejection fraction 45% with thick walls.
- Rule-following gold: Blood pressure 106/64, heart rate 72 and irregular, weight 74 kg, up 2 kg from last visit. JVP up to the angle of the jaw. Bibasal crackles. Pitting edema to the mid shins. NT-proBNP is 3400, up from 2100, and creatinine 1.5. His echo showed an ejection fraction of 45% with thick walls.

## d29-attr-acoramidis: plan

Reasons: `unsupported_content`

- Clinical gold: Stop tafamidis and start acoramidis 712 mg twice daily, since insurance will no longer cover tafamidis. Increase furosemide to 80 mg in the morning and 40 mg in the afternoon for 5 days, then back to 40 mg twice daily. Kidney function and potassium on Friday. Daily weights; call the heart failure nurse if up more than 1 kg in a day. Continue apixaban. Clinic review in 4 weeks.
- Rule-following gold: He has been on tafamidis, but his insurance will no longer cover it, so we are switching to acoramidis. Stop tafamidis and start acoramidis 712 mg twice daily. Increase furosemide to 80 mg in the morning and 40 mg in the afternoon for 5 days, then back to 40 mg twice daily. Kidney function and potassium on Friday. Daily weights, call the heart failure nurse if up more than 1 kg in a day. Continue apixaban. Clinic review in 4 weeks.

## d30-fcs-olezarsen: subjective

Reasons: `negation_dropped`, `unsupported_content`

- Clinical gold: 34-year-old woman with familial chylomicronemia syndrome confirmed on genetic testing. Four episodes of acute pancreatitis, the last on March 2, 2026, needing 5 days in hospital. Very low fat diet, under 20 g of fat a day, with the dietitian; does not drink alcohol. Olezarsen 80 mg by monthly injection started on June 15, 2026. No abdominal pain since starting. No injection site problems apart from some redness for a day.
- Rule-following gold: 34-year-old woman with familial chylomicronemia syndrome confirmed on genetic testing. She has had four episodes of acute pancreatitis the last one on March 2, 2026 which needed 5 days in hospital. She follows a very low fat diet under 20 g of fat a day with the dietitian and does not drink alcohol olezarsen 80 mg by monthly injection was started on June 15, 2026. No abdominal pain since starting. No injection site problems apart from some redness for a day.

## d30-fcs-olezarsen: objective

Reasons: `words_out_of_order`

- Clinical gold: Fasting triglycerides down from about 2400 to 880 mg/dL.
- Rule-following gold: Her fasting triglycerides have come down from about 2400 to 880 mg/dL.

## d31-cah-crinecerfont: subjective

Reasons: `words_out_of_order`

- Clinical gold: 12-year-old boy with classic congenital adrenal hyperplasia due to 21-hydroxylase deficiency, seen with his mum. Hydrocortisone 20 mg a day in three doses and fludrocortisone 100 mcg daily. Androgens hard to control without high steroid doses; crinecerfont 50 mg twice daily with meals added 3 months ago, since he weighs under 55 kg. Feels well; no headaches, no tiredness, no salt craving. No illnesses needing stress doses since the last visit.
- Rule-following gold: 12-year-old boy with classic congenital adrenal hyperplasia due to 21-hydroxylase deficiency, seen with his mum. He has been on hydrocortisone 20 mg a day in three doses and fludrocortisone 100 mcg daily. His androgens have been hard to control without high steroid doses, so 3 months ago we added crinecerfont twice daily with meals, 50 mg twice daily, since he weighs under 55 kg. He feels well, no headaches, no tiredness and no salt craving. No illnesses needing stress doses since the last visit.

## d32-alopecia-ritlecitinib: subjective

Reasons: `unsupported_content`

- Clinical gold: 17-year-old girl with patchy hair loss for 2 years, spreading over the last 6 months, now about half of the scalp affected and some loss of the eyebrows. Steroid injections into the patches did not help. No nail changes, no itching, no scalp pain. Mother has hypothyroidism. Doxycycline taken for acne made her very sick to her stomach. Vaccines up to date.
- Rule-following gold: 17-year-old girl with patchy hair loss for 2 years that has spread over the last 6 months, now with about half of the scalp affected and some loss of the eyebrows. Steroid injections into the patches did not help. No nail changes, no itching and no scalp pain. Her mother has hypothyroidism. The patient says the doxycycline she took for acne made her very sick to her stomach. Her vaccines are up to date.

## d33-addendum-potassium: plan

Reasons: `words_out_of_order`

- Clinical gold: Hold the spironolactone for now. Repeat potassium and kidney function on Thursday at the surgery; if the potassium is still above 5.5, stop the spironolactone altogether and let me know. Patient told by phone and understands. Low potassium diet sheet posted out today. Continue the rest of his medicines unchanged, including the ramipril.
- Rule-following gold: Hold the spironolactone for now, repeat potassium and kidney function on Thursday at the surgery, and if the potassium is still above 5.5, stop the spironolactone altogether and let me know. The patient has been told by phone and understands. Low potassium diet sheet posted out today. Continue the rest of his medicines unchanged, including the ramipril.

## d34-lipids-phone: subjective

Reasons: `unsupported_content`

- Clinical gold: 67-year-old man; blood results from November 9. Minor stroke in 2023. Stopped atorvastatin last year because of muscle aches, which went away when he stopped it; not keen to try the same statin again. No chest pain, no new weakness or speech problems. Takes clopidogrel 75 mg daily.
- Rule-following gold: Spoke to the patient, a 67-year-old man, about his blood results from November 9. He had a minor stroke in 2023 and stopped his atorvastatin last year because of muscle aches. The aches went away when he stopped it. He is not keen to try the same statin again. No chest pain and no new weakness or speech problems. He takes clopidogrel 75 mg daily.

## d34-lipids-phone: objective

Reasons: `words_out_of_order`

- Clinical gold: Cholesterol 6.8, LDL 4.4, HbA1c 44.
- Rule-following gold: His cholesterol is 6.8 with LDL 4.4. His HbA1c is 44.

## d35-strep-throat: subjective

Reasons: `words_out_of_order`

- Clinical gold: 8-year-old girl with sore throat and fever for 2 days and a headache. No cough, no runny nose. Stomach ache, eating less. Hives with amoxicillin when she was two were actually from a virus; the allergist cleared her last month.
- Rule-following gold: 8-year-old girl with sore throat and fever for 2 days and a headache. No cough no runny nose. She has had a stomach ache and is eating less. Mom says the hives she had with amoxicillin when she was two were actually from a virus and the allergist cleared her last month.

## d36-back-pain-sciatica: plan

Reasons: `words_out_of_order`

- Clinical gold: Continue physiotherapy and the naproxen for another 2 weeks, then stop. Stay active and return to lighter duties. Safety net advice given about the red flag symptoms. If not better in 6 weeks, consider imaging and a nerve root injection.
- Rule-following gold: Continue physiotherapy and the naproxen for another 2 weeks then stop. Stay active and return to lighter duties. Safety net advice given about the red flag symptoms. If he is not better in 6 weeks we will consider imaging and a nerve root injection.

## d38-epilepsy-lamotrigine: subjective

Reasons: `experiencer_dropped`, `unsupported_content`

- Clinical gold: 31-year-old woman with focal epilepsy on levetiracetam 750 mg twice daily. Focal to bilateral tonic clonic seizures on August 19 and October 4, both after poor sleep. Irritable and low since starting levetiracetam, noticed by her partner too. No thoughts of self-harm. Not driving. Planning a pregnancy in the next year.
- Rule-following gold: 31-year-old woman with focal epilepsy, on levetiracetam 750 mg twice daily. She had a focal to bilateral tonic clonic seizure on August 19 and another on October 4, both after poor sleep. Since the levetiracetam she has felt irritable and low and her partner has noticed it too. No thoughts of self-harm. She is not driving. She is planning a pregnancy in the next year.

## d39-otitis-media: assessment

Reasons: `unsupported_content`

- Clinical gold: Left acute otitis media in a 3-year-old, his third since the summer.
- Rule-following gold: Left acute otitis media his third since the summer.

## d39-otitis-media: plan

Reasons: `words_out_of_order`

- Clinical gold: Start amoxicillin 90 mg/kg a day, split twice daily, for 10 days. Ibuprofen with food for pain and fever. If not better in 48 to 72 hours, mum to bring him back for review. If one more infection before the spring, refer to the ear nose and throat team to talk about grommets. Back to nursery when the fever settles and he is eating normally again.
- Rule-following gold: Start amoxicillin 90 mg/kg a day split twice daily for 10 days. Ibuprofen with food for pain and fever. If he is not better in 48 to 72 hours mum should bring him back for review. If he has one more infection before the spring we will refer him to the ear nose and throat team to talk about grommets. He can go back to nursery when the fever settles and he is eating normally again.

## d40-adhd-guanfacine: subjective

Reasons: `words_out_of_order`

- Clinical gold: 10-year-old girl with ADHD, combined type, seen with her father. Could not tolerate methylphenidate because of poor appetite and weight loss; started guanfacine extended release 1 mg at bedtime 4 weeks ago. Teachers report she is calmer and finishing more work, but her father says afternoons are still hard. A bit sleepy in the mornings. No fainting, no dizziness, no chest pain. Allergic to peanuts; carries an epinephrine pen.
- Rule-following gold: 10-year-old girl with ADHD combined type, seen with her father. She could not tolerate methylphenidate because of poor appetite and weight loss, so 4 weeks ago we started guanfacine extended release 1 mg at bedtime. Her teachers report she is calmer and finishing more work, but her father says afternoons are still hard. She is a bit sleepy in the mornings. No fainting, no dizziness and no chest pain. She is allergic to peanuts and carries an epinephrine pen.

## d41-multiproblem-followup: subjective

Reasons: `unsupported_content`

- Clinical gold: 74-year-old woman with heart failure with preserved ejection fraction, atrial fibrillation, type 2 diabetes and chronic kidney disease stage 3b. Admitted on October 24 with fluid overload; went home on October 30. Started on sotagliflozin 200 mg daily in hospital, and furosemide increased to 40 mg twice a day. Some ankle swelling by the evening, but no swelling in her thighs or abdomen. No orthopnea, no chest pain, no bleeding on apixaban. Two low sugars in the low 60s early in the morning last week. Insulin glargine 16 units at bedtime and metformin 500 mg twice a day. Left knee swollen and painful after walking; right knee fine. Has been taking ibuprofen for the knee. Also apixaban 5 mg twice a day and metoprolol succinate 50 mg daily. Allergic to codeine.
- Rule-following gold: 74-year-old woman with heart failure with preserved ejection fraction, atrial fibrillation, type 2 diabetes and chronic kidney disease stage 3b. She was admitted on October 24 with fluid overload and went home on October 30. In hospital she was started on sotagliflozin 200 mg daily and her furosemide was increased to 40 mg twice a day. She still has some ankle swelling by the evening but no swelling in her thighs or abdomen. No orthopnea, no chest pain and no bleeding on apixaban. She had two low sugars in the low 60s early in the morning last week. She takes insulin glargine 16 units at bedtime and metformin 500 mg twice a day. Her left knee is swollen and painful after walking but the right knee is fine, and she has been taking ibuprofen for it. Her other medicines are apixaban 5 mg twice a day and metoprolol succinate 50 mg daily. She is allergic to codeine.

## d41-multiproblem-followup: objective

Reasons: `words_out_of_order`

- Clinical gold: Weight down from 69.8 to 68.4 kg since discharge. Blood pressure 128/74, heart rate 78 and irregular, oxygen saturation 95% on room air. JVP not raised. A few crackles at the right base and none on the left. Trace pitting edema at both ankles. Small effusion in the left knee. Potassium 4.6, creatinine 1.4 with an eGFR of 38. NT-proBNP 1450, down from 2800 at discharge. A1c 7.6. Urine albumin to creatinine ratio 210 mg/g.
- Rule-following gold: Her weight has come down from 69.8 to 68.4 kg since discharge. Blood pressure 128/74, heart rate 78 and irregular, oxygen saturation 95% on room air. JVP not raised. A few crackles at the right base and none on the left. Trace pitting edema at both ankles. Small effusion in the left knee. Potassium 4.6, creatinine 1.4 with an eGFR of 38. NT-proBNP 1450, down from 2800 at discharge. Her A1c is 7.6. Urine albumin to creatinine ratio 210 mg/g.

## d42-new-patient-visit: subjective

Reasons: `unsupported_content`

- Clinical gold: 58-year-old man who has moved here from out of state and is establishing care. Short of breath walking up hills for about 3 months. Twice felt a tightness across his chest after climbing two flights of stairs, which went away within a few minutes of resting. No chest pain at rest and nothing at night. Heartburn after large meals, but no trouble swallowing and no weight loss. Past history: high blood pressure for 10 years, high cholesterol, prediabetes, gout with two flares in the big toe, and obstructive sleep apnea on CPAP. Colon polyps removed at a colonoscopy on March 3, 2021. Medications: amlodipine 10 mg daily, hydrochlorothiazide 25 mg daily, atorvastatin 20 mg at night, allopurinol 100 mg daily and omeprazole 20 mg before breakfast. Allergies: penicillin gave him hives as a child. Family history: father had a heart attack at 52; mother has type 2 diabetes. Drives a city bus. Smoked a pack a day for 20 years, quit 5 years ago.
- Rule-following gold: 58-year-old man who has moved here from out of state and is establishing care. For about 3 months he has been short of breath walking up hills. Twice he has felt a tightness across his chest after climbing two flights of stairs and it went away within a few minutes of resting. He has no chest pain at rest and nothing at night. He has heartburn after large meals but no trouble swallowing and no weight loss. Past history: high blood pressure for 10 years, high cholesterol, prediabetes, gout with two flares in the big toe and obstructive sleep apnea on CPAP. Colon polyps removed at a colonoscopy on March 3, 2021. Medications: amlodipine 10 mg daily, hydrochlorothiazide 25 mg daily, atorvastatin 20 mg at night, allopurinol 100 mg daily and omeprazole 20 mg before breakfast. Allergies: penicillin gave him hives as a child. Family history: his father had a heart attack at 52 and his mother has type 2 diabetes. He drives a city bus. He smoked a pack a day for 20 years and quit 5 years ago.

## d42-new-patient-visit: objective

Reasons: `unsupported_content`

- Clinical gold: Blood pressure 152/94 and 148/90 on a repeat check, heart rate 84 and regular, BMI 33. Heart sounds normal with no murmur. Chest clear. No edema. Results he brought: LDL 132, A1c 6.4, uric acid 8.9. ECG today: sinus rhythm with no ischemic changes.
- Rule-following gold: Examination: blood pressure 152/94 and 148/90 on a repeat check, heart rate 84 and regular, BMI 33. Heart sounds normal with no murmur. Chest clear. No edema. Results he brought: LDL 132, A1c 6.4, uric acid 8.9. ECG today shows sinus rhythm with no ischemic changes.
