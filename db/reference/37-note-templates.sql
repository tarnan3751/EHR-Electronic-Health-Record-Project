-- Hand-written, not generated: the visit types a clinician can write a note for (the expansion plan's step 14), each
-- with what to cover in its Subjective, Objective, Assessment and Plan. The note editor shows a template's prompts under
-- the sections' own definitions when the clinician picks its visit type; nothing of a template is ever put into a note,
-- and nothing here is about a patient. A template is data, not code: a clinic adds or changes one here, as a reviewed
-- change, in the same shape.
--
--   code          short and stable (lower-case letters, digits and hyphens): a draft names the template it was written
--                 with, so a template a draft uses can't be taken out of this file (the database refuses it)
--   title         what the editor's "Visit type" list says
--   position      where it comes in that list, from 1, each once (reordering means moving one out of the way first)
--   *_prompt      what to cover in that section, up to 500 characters
--
-- Written for this project, from no published source. Not validated for clinical use: every row needs a clinician's
-- reading before a clinic relies on it.
--
-- Applied as ehr_owner after db/policies, whose grants name the table; safe to run again: it makes the table hold
-- exactly these rows.
BEGIN;
CREATE TEMPORARY TABLE incoming_note_templates (
    code text, title text, position integer, subjective_prompt text, objective_prompt text, assessment_prompt text, plan_prompt text
) ON COMMIT DROP;
INSERT INTO incoming_note_templates (code, title, position, subjective_prompt, objective_prompt, assessment_prompt, plan_prompt) VALUES
    -- Not validated for clinical use.
    ('follow-up', 'Follow-up visit', 1,
     'How each problem has been since the last visit: symptoms, side effects, how medicines are being taken, and readings taken at home.',
     'Today''s vital signs, the examination each problem needs, and results reviewed since the last visit.',
     'How each problem is doing since the last visit: better, the same or worse, and why.',
     'Changes to medicines, tests, referrals and advice, and when to come back.'),
    -- Not validated for clinical use.
    ('new-patient', 'New patient visit', 2,
     'The reason for the visit and its history; past medical and surgical history; medicines and allergies; family and social history; a review of systems.',
     'Vital signs with height and weight, a complete examination, and results from elsewhere that were reviewed.',
     'Each problem found, with the diagnoses still being considered.',
     'For each problem: treatment, tests and referrals; screening and immunizations due; and the next visit.'),
    -- Not validated for clinical use.
    ('acute', 'Acute visit', 3,
     'The main symptom: when it started, how it has changed, what makes it better or worse, what came with it, and what has been tried.',
     'Vital signs, the focused examination, and any tests done at the visit.',
     'The working diagnosis, and what else was considered.',
     'Treatment and advice, the warning signs to come back for, and follow-up.'),
    -- Not validated for clinical use.
    ('well-child', 'Well-child visit', 4,
     'Concerns of the child and their parent or carer: feeding, sleep, development, school or day care, and safety at home.',
     'Vital signs, growth measurements with their centiles, and the examination.',
     'Growth and development for the child''s age, and any problem found.',
     'Immunizations given or due, screening, guidance for the coming months, and the next visit.'),
    -- Not validated for clinical use.
    ('telephone', 'Telephone visit', 5,
     'What the patient or their carer reports, including readings they took at home.',
     'No examination is done on a call: say so, and note what was observed during the call.',
     'The impression, and whether the patient needs to be seen.',
     'Advice, changes to medicines, tests, and when and how to be seen if things change.');
DELETE FROM ehr.note_templates t
WHERE NOT EXISTS (SELECT 1 FROM incoming_note_templates i WHERE i.code = t.code);
INSERT INTO ehr.note_templates (code, title, position, subjective_prompt, objective_prompt, assessment_prompt, plan_prompt)
SELECT code, title, position, subjective_prompt, objective_prompt, assessment_prompt, plan_prompt FROM incoming_note_templates
ON CONFLICT (code) DO UPDATE
    SET title = excluded.title, position = excluded.position, subjective_prompt = excluded.subjective_prompt,
        objective_prompt = excluded.objective_prompt, assessment_prompt = excluded.assessment_prompt, plan_prompt = excluded.plan_prompt
    WHERE (note_templates.title, note_templates.position, note_templates.subjective_prompt, note_templates.objective_prompt,
           note_templates.assessment_prompt, note_templates.plan_prompt)
        IS DISTINCT FROM (excluded.title, excluded.position, excluded.subjective_prompt, excluded.objective_prompt,
                          excluded.assessment_prompt, excluded.plan_prompt);
COMMIT;
