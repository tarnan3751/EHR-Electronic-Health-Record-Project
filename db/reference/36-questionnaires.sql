-- Hand-written, not generated: the questionnaires a clinician can ask a patient to fill in before a visit (the expansion
-- plan's step 13e), worded exactly as their authors' published forms are, with LOINC's codes for each question and
-- answer. A questionnaire is data, not code: a clinic adds one here, as a reviewed change, in the same shape.
--
-- The shape of a definition (ehr.questionnaire_score in db/policies/85-pre-visit-forms.sql scores it, and the pages show
-- it):
--   prompt        the sentence above the questions
--   items         the questions in order: code (LOINC), text, answers (the name of a list in answerLists), and
--                 scored (its answer's score counts in the total), optional (it may be left unanswered) and alert
--                 (any answer that scores more than 0 is put in the care team's inbox), each false when left out
--   answerLists   the lists of answers: code (LOINC), text and, for a scored list, score
--   total         the total's code (LOINC) and the most it can be
--   bands         the severity band each total falls in: from and to, both included, and its words
--   followUpAt    the total at or above which a score needs follow-up
--   attribution   the line the authors print on the form
--
-- Sources, read 21 November 2026 (docs/expansion/reference-data.md has each statement quoted, with its address and the
-- SHA-256 of each file):
--   PHQ-9_English.pdf and GAD-7_English.pdf, the English forms on phqscreeners.com (Pfizer's site for the measures),
--     one page each: every prompt, question, answer and score below, and the attribution line. Line breaks and doubled
--     spaces of the printed form are single spaces; its tick instruction, office-coding line and underlining are paper's.
--   Pfizer's "Instructions for Patient Health Questionnaire (PHQ) and GAD-7 Measures" (instructions.pdf, same site):
--     the score ranges (page 3, Table 1), the GAD-7's bands (page 6), the follow-up value of 10 (pages 2 and 6), and
--     that the PHQ-9's difficulty question is not scored (page 2).
--   Kroenke K, Spitzer RL, Williams JBW. The PHQ-9: validity of a brief depression severity measure. J Gen Intern Med
--     2001;16:606-613, Table 2 (PMC1495268): the PHQ-9's bands and their words.
--   LOINC 2.83: the panels 44249-1 and 69737-5 (PanelsAndForms.csv) for the questions' codes and order, the answer
--     lists LL358-3 and LL359-1 (AnswerList.csv) for the answers' codes, and the copyright notice LOINC carries for
--     these terms (Loinc.csv, EXTERNAL_COPYRIGHT_NOTICE), stored in the copyright column as LOINC's license asks.
--
-- A request for a form keeps a copy of the definition it was asked with (ehr.questionnaire_requests.definition), and
-- the patient answers, the database scores and the pages show that copy: a change here reaches only the forms asked
-- for after it. A questionnaire that has been asked for can't be removed (its requests refer to it).
--
-- Applied as ehr_owner after db/policies and after 30-observations.sql, whose total codes these name; safe to run
-- again: it makes the table hold exactly these rows. Not validated for clinical use.
BEGIN;
CREATE TEMPORARY TABLE incoming_questionnaires (code text, title text, definition jsonb, copyright text) ON COMMIT DROP;
INSERT INTO incoming_questionnaires (code, title, definition, copyright) VALUES
    ('44249-1', 'Patient Health Questionnaire-9 (PHQ-9)', jsonb_build_object(
        -- PHQ-9_English.pdf, page 1, above the questions.
        'prompt', 'Over the last 2 weeks, how often have you been bothered by any of the following problems?',
        'items', jsonb_build_array(
            -- Questions 1 to 9, as printed and in LOINC panel 44249-1's order.
            jsonb_build_object('code', '44250-9', 'text', 'Little interest or pleasure in doing things', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44255-8', 'text', 'Feeling down, depressed, or hopeless', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44259-0', 'text', 'Trouble falling or staying asleep, or sleeping too much', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44254-1', 'text', 'Feeling tired or having little energy', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44251-7', 'text', 'Poor appetite or overeating', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44258-2', 'text', 'Feeling bad about yourself — or that you are a failure or have let yourself or your family down',
                'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44252-5', 'text', 'Trouble concentrating on things, such as reading the newspaper or watching television',
                'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '44253-3',
                'text', 'Moving or speaking so slowly that other people could have noticed? Or the opposite — being so fidgety or restless that you have been moving around a lot more than usual',
                'answers', 'frequency', 'scored', true),
            -- Question 9 counts "if present at all" (Kroenke 2001, Methods), and "a final decision about the actual risk
            -- of self-harm requires a clinical interview" (instructions.pdf, page 2): any answer but "Not at all" alerts.
            jsonb_build_object('code', '44260-8', 'text', 'Thoughts that you would be better off dead or of hurting yourself in some way',
                'answers', 'frequency', 'scored', true, 'alert', true),
            -- Under the questions on the form; "not used in calculating any PHQ score" (instructions.pdf, page 2).
            jsonb_build_object('code', '69722-7',
                'text', 'If you checked off any problems, how difficult have these problems made it for you to do your work, take care of things at home, or get along with other people?',
                'answers', 'difficulty', 'optional', true)),
        'answerLists', jsonb_build_object(
            -- The column heads and their scores, PHQ-9_English.pdf, page 1 (LOINC LL358-3).
            'frequency', jsonb_build_array(
                jsonb_build_object('code', 'LA6568-5', 'text', 'Not at all', 'score', 0),
                jsonb_build_object('code', 'LA6569-3', 'text', 'Several days', 'score', 1),
                jsonb_build_object('code', 'LA6570-1', 'text', 'More than half the days', 'score', 2),
                jsonb_build_object('code', 'LA6571-9', 'text', 'Nearly every day', 'score', 3)),
            -- The difficulty question's boxes, PHQ-9_English.pdf, page 1 (LOINC LL359-1). Not scored.
            'difficulty', jsonb_build_array(
                jsonb_build_object('code', 'LA6572-7', 'text', 'Not difficult at all'),
                jsonb_build_object('code', 'LA6573-5', 'text', 'Somewhat difficult'),
                jsonb_build_object('code', 'LA6575-0', 'text', 'Very difficult'),
                jsonb_build_object('code', 'LA6574-3', 'text', 'Extremely difficult'))),
        -- "Nine items, each of which is scored 0 to 3, providing a 0 to 27 severity score" (instructions.pdf, page 3).
        'total', jsonb_build_object('code', '44261-6', 'max', 27),
        -- Kroenke 2001, Table 2, "Level of Depression Severity, PHQ-9 Score".
        'bands', jsonb_build_array(
            jsonb_build_object('from', 0, 'to', 4, 'band', 'Minimal'),
            jsonb_build_object('from', 5, 'to', 9, 'band', 'Mild'),
            jsonb_build_object('from', 10, 'to', 14, 'band', 'Moderate'),
            jsonb_build_object('from', 15, 'to', 19, 'band', 'Moderately severe'),
            jsonb_build_object('from', 20, 'to', 27, 'band', 'Severe')),
        -- "A cutpoint of 10 or greater is considered a 'yellow flag'" (instructions.pdf, page 2).
        'followUpAt', 10,
        'attribution', 'Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute.'),
     'Copyright © Pfizer Inc. All rights reserved. Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute.'),
    ('69737-5', 'GAD-7', jsonb_build_object(
        -- GAD-7_English.pdf, page 1, above the questions: "the following problems", without the PHQ-9's "any of".
        'prompt', 'Over the last 2 weeks, how often have you been bothered by the following problems?',
        'items', jsonb_build_array(
            -- Questions 1 to 7, as printed and in LOINC panel 69737-5's order. Question 6's code is the one LOINC's
            -- panel uses, 69689-8, whatever that term's own name says of its period.
            jsonb_build_object('code', '69725-0', 'text', 'Feeling nervous, anxious or on edge', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '68509-9', 'text', 'Not being able to stop or control worrying', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '69733-4', 'text', 'Worrying too much about different things', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '69734-2', 'text', 'Trouble relaxing', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '69735-9', 'text', 'Being so restless that it is hard to sit still', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '69689-8', 'text', 'Becoming easily annoyed or irritable', 'answers', 'frequency', 'scored', true),
            jsonb_build_object('code', '69736-7', 'text', 'Feeling afraid as if something awful might happen', 'answers', 'frequency', 'scored', true)),
        'answerLists', jsonb_build_object(
            -- The column heads and their scores, GAD-7_English.pdf, page 1 (LOINC LL358-3).
            'frequency', jsonb_build_array(
                jsonb_build_object('code', 'LA6568-5', 'text', 'Not at all', 'score', 0),
                jsonb_build_object('code', 'LA6569-3', 'text', 'Several days', 'score', 1),
                jsonb_build_object('code', 'LA6570-1', 'text', 'More than half the days', 'score', 2),
                jsonb_build_object('code', 'LA6571-9', 'text', 'Nearly every day', 'score', 3))),
        -- "Seven items, each of which is scored 0 to 3, providing a 0 to 21 severity score" (instructions.pdf, page 3).
        'total', jsonb_build_object('code', '70274-6', 'max', 21),
        -- "Scores of 5, 10, and 15 represent cutpoints for mild, moderate, and severe anxiety, respectively"
        -- (instructions.pdf, page 6). Below 5 the manual names no band, and the paper that may (Spitzer et al., Arch
        -- Intern Med 2006) could not be read: "Below mild" is this clinic's wording, not the authors'.
        'bands', jsonb_build_array(
            jsonb_build_object('from', 0, 'to', 4, 'band', 'Below mild'),
            jsonb_build_object('from', 5, 'to', 9, 'band', 'Mild'),
            jsonb_build_object('from', 10, 'to', 14, 'band', 'Moderate'),
            jsonb_build_object('from', 15, 'to', 21, 'band', 'Severe')),
        -- "A recommended cutpoint for further evaluation is a score of 10 or greater" (instructions.pdf, page 6).
        'followUpAt', 10,
        'attribution', 'Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute.'),
     'Copyright © Pfizer Inc. All rights reserved. Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute.');
DELETE FROM ehr.questionnaires q
WHERE NOT EXISTS (SELECT 1 FROM incoming_questionnaires i WHERE i.code = q.code);
INSERT INTO ehr.questionnaires (code, title, definition, copyright)
SELECT code, title, definition, copyright FROM incoming_questionnaires
ON CONFLICT (code) DO UPDATE
    SET title = excluded.title, definition = excluded.definition, copyright = excluded.copyright
    WHERE (questionnaires.title, questionnaires.definition, questionnaires.copyright)
        IS DISTINCT FROM (excluded.title, excluded.definition, excluded.copyright);
COMMIT;
