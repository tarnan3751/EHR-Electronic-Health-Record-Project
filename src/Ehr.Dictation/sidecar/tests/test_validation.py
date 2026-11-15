"""validation.py: each check, with its reason code; what a pending field carries; strict grounding; and the boundary
between a note that documents care and an instruction to order something or change the allergy list (FR-12, FR-14).
The review's and the owner's cases are here verbatim."""

from __future__ import annotations

import pytest

from ehr_dictation.transcript import Segment, assemble
from ehr_dictation.validation import FREE_WORDS, REASONS, check, directive_spans, directives, not_carried_over

DICTATION = (
    "Fifty-eight-year-old man here for blood pressure follow up. He takes lisinopril 10 mg daily. "
    "Denies chest pain or shortness of breath. Blood pressure today 150 over 90, sorry, 140 over 90. "
    "Seen in clinic on March 3. Assessment: hypertension, improving. "
    "Plan: continue lisinopril, recheck in 3 months. Go ahead and order a basic metabolic panel."
)


def transcript(*passages: str | tuple[str, str]):
    segments = [
        Segment(0, 16000, p if isinstance(p, str) else p[0], 0.9, None if isinstance(p, str) else p[1], True)
        for p in passages
    ]
    return assemble(segments, final=True)


def field(value: str, *sources: str, name: str = "history", text=None):
    fields = {n: {"value": "", "sources": []} for n in ("history", "assessment", "plan")}
    fields[name] = {"value": value, "sources": list(sources)}
    return check(fields, [], text or transcript(DICTATION)).fields[name]


def own(text: str, value: str, *sources: str):
    """A value checked against a transcript that is just `text`, quoting all of it unless told otherwise."""
    return field(value, *(sources or (text,)), name="plan", text=transcript(text))


# ---- What goes out ---------------------------------------------------------------------------------------------------


def test_a_grounded_value_is_pending_with_where_it_came_from():
    text = transcript(DICTATION)

    checked = field("Hypertension, improving.", "hypertension, improving", name="assessment", text=text)

    assert checked.pending
    assert [text.text[start:end] for start, end in checked.spans] == ["hypertension, improving"]
    assert checked.to_json(text) == {"status": "pending", "value": "Hypertension, improving.",
                                     "sources": [{"start": checked.spans[0][0], "end": checked.spans[0][1]}],
                                     "warnings": []}


def test_a_failed_value_goes_out_as_reason_codes_only():
    text = transcript(DICTATION)

    checked = field("Hypertension, worsening.", "hypertension, worsening", name="assessment", text=text)

    assert checked.to_json(text) == {"status": "empty", "reasons": ["source_not_in_transcript", "unsupported_content"]}


def test_keys_outside_the_schema_are_reported_and_never_a_field():
    fields = {n: {"value": "", "sources": []} for n in ("history", "assessment", "plan")}

    checks = check(fields, ["orders"], transcript(DICTATION))

    assert checks.unknown == ["orders"] and list(checks.fields) == ["history", "assessment", "plan"]


def test_an_empty_value_was_not_dictated():
    assert field("").reasons == ["not_dictated"]


def test_every_failure_is_recorded_in_a_fixed_order():
    checked = field("<i>Takes losartan 20 mg</i> since April.", "nothing like this was said")

    assert checked.reasons == [r for r in REASONS if r in set(checked.reasons)]
    assert {"bad_format", "source_not_in_transcript", "number_not_in_sources", "date_not_in_sources",
            "drug_not_in_sources", "unsupported_content"} <= set(checked.reasons)


# ---- Sources ---------------------------------------------------------------------------------------------------------


def test_a_value_without_sources_fails():
    assert "no_sources" in field("Hypertension.").reasons


def test_a_one_word_source_is_too_short_unless_it_is_a_number():
    assert "source_too_short" in field("Hypertension.", "hypertension").reasons
    assert "source_too_short" not in field("Recheck in 3 months.", "3", "recheck in 3 months").reasons


def test_sources_match_word_for_word_ignoring_case_spacing_and_punctuation():
    assert field("He takes lisinopril 10 mg daily.", "HE   TAKES lisinopril, 10 mg daily").pending
    assert field("He takes lisinopril 10 mg.", "he takes lisinopril 20 mg").reasons[:1] == ["source_not_in_transcript"]


def test_a_source_in_an_unclear_passage_fails():
    text = transcript("Cough for three days.", ("Fever since Tuesday.", "too_noisy"))

    assert "source_in_unclear_passage" in field("Fever since Tuesday.", "Fever since Tuesday", text=text).reasons
    assert "source_in_unclear_passage" in field("Fever.", "[unclear] since", text=text).reasons


def test_a_passage_said_twice_is_highlighted_where_each_source_was_said():
    text = transcript("Cough for three days. Better now. Cough for three days again at night.")

    checked = field("Cough for three days. Cough for three days.", "Cough for three days", "Cough for three days",
                    text=text)

    assert checked.pending
    assert checked.spans == [(0, 20), (34, 54)]


# ---- Corrections -----------------------------------------------------------------------------------------------------


def test_a_source_the_clinician_corrected_fails_and_the_correction_passes():
    assert "source_superseded" in field("Blood pressure 150/90.", "Blood pressure today 150 over 90").reasons
    assert field("140/90.", "140 over 90").pending
    assert field("Blood pressure today 140 over 90.", "Blood pressure today 150 over 90, sorry, 140 over 90").pending
    assert "source_superseded" in field(
        "Blood pressure 150/90.", "Blood pressure today 150 over 90, sorry, 140 over 90").reasons


def test_the_owners_self_correction_example():
    assert "source_superseded" in own("10 mg, sorry, 20 mg", "10 mg").reasons
    assert own("10 mg, sorry, 20 mg", "20 mg").pending


@pytest.mark.parametrize("cue", ["actually", "no,", "make it", "rather", "I mean", "scratch that"])
def test_each_correction_cue_supersedes_what_it_replaces(cue):
    said = f"Lisinopril 10 mg {cue} 20 mg daily."

    assert "source_superseded" in own(said, "Lisinopril 10 mg daily.").reasons
    assert own(said, "Lisinopril 20 mg daily.").pending


def test_a_correction_reaches_a_source_that_does_not_quote_it():
    said = "Start lisinopril 10 mg daily for blood pressure, sorry, 20 mg daily."

    assert "source_superseded" in own(said, "Start lisinopril 10 mg.", "Start lisinopril 10 mg").reasons


def test_a_correction_at_the_start_of_the_next_sentence_counts():
    said = "Start lisinopril 10 mg daily. Sorry, 20 mg daily."

    assert "source_superseded" in own(said, "Start lisinopril 10 mg daily.", "Start lisinopril 10 mg daily").reasons
    assert own("Start lisinopril 10 mg daily. She is sorry, 20 mg daily.", "Start lisinopril 10 mg daily.",
               "Start lisinopril 10 mg daily").pending


def test_a_correction_that_starts_a_sentence_finds_the_same_unit_further_back():
    said = "Start lisinopril 10 mg daily. Recheck in 2 weeks. Actually, make that 20 mg."

    assert "source_superseded" in own(said, "Start lisinopril 10 mg daily.", "Start lisinopril 10 mg daily").reasons
    assert own(said, "Recheck in 2 weeks.", "Recheck in 2 weeks").pending
    assert "source_superseded" in own("Lisinopril 10 mg daily. Recheck in 2 weeks. Sorry, 3 weeks.",
                                      "Recheck in 2 weeks.", "Recheck in 2 weeks").reasons


def test_a_correction_supersedes_only_the_measurement_it_corrects():
    said = "Blood pressure 98/60, heart rate 78, no sorry 72."

    assert own(said, "Blood pressure 98/60.", "Blood pressure 98/60").pending
    assert "source_superseded" in own(said, "Heart rate 78.", "heart rate 78").reasons
    assert own(said, "Heart rate 72.", "heart rate 78, no sorry 72").pending


def test_no_correction_is_a_correction_not_a_negation():
    said = "ECG shows sinus rhythm with a QTc of 468 no correction 448 milliseconds."

    assert own(said, "QTc of 448 milliseconds.", "QTc of 468 no correction 448 milliseconds").pending
    assert "source_superseded" in own(said, "QTc of 468.", "QTc of 468").reasons


def test_no_sorry_is_a_correction_not_a_negation():
    said = "Psoriasis improving, PASI 12 no sorry PASI 10."

    assert own(said, "PASI 10.", "PASI 10").pending
    assert own(said, "Psoriasis improving, PASI 10.").pending
    assert "source_superseded" in own(said, "PASI 12.", "PASI 12").reasons


def test_what_a_correction_says_again_is_not_superseded():
    said = "Start lisinopril 10 mg, sorry, lisinopril 20 mg daily."

    assert own(said, "Start lisinopril.", "Start lisinopril 10 mg").pending
    assert "source_superseded" in own(said, "Start lisinopril 10 mg.", "Start lisinopril 10 mg").reasons


def test_a_term_superseded_in_one_source_but_said_in_another_is_fine():
    text = transcript(DICTATION)

    assert field("140/90.", "Blood pressure today 150 over 90", "140 over 90", text=text).pending
    assert own("Fever 3 days, sorry, 2 days. Cough 3 days.", "Cough 3 days.", "Fever 3 days", "Cough 3 days").pending


def test_a_correction_takes_back_the_unit_said_with_what_it_corrects():
    said = "Range of motion is 5 to 95 degrees no sorry 5 to 90 degrees."

    assert own(said, "Range of motion is 5 to 90 degrees.").pending
    assert "source_superseded" in own(said, "Range of motion is 5 to 95 degrees.").reasons


@pytest.mark.parametrize(("wrong", "right"), [("metoprolol", "metformin"), ("warfarin", "paracetamol"),
                                             ("prednisone", "tacrolimus"), ("lamotrigine", "levetiracetam")])
def test_a_common_drug_no_stem_covers_is_still_a_drug(wrong, right):
    said = f"Type 2 diabetes on {wrong} I mean {right}."

    assert own(said, f"Type 2 diabetes on {right}.").pending
    assert "source_superseded" in own(said, f"Type 2 diabetes on {wrong}.", f"Type 2 diabetes on {wrong}").reasons
    assert "drug_not_in_sources" in own("Type 2 diabetes.", f"Type 2 diabetes on {right}.", "Type 2 diabetes").reasons


def test_a_side_the_clinician_corrected_is_superseded():
    said = "Pain in the left knee, sorry, right knee, for two weeks."

    assert "source_superseded" in own(said, "Pain in the left knee.", "Pain in the left knee").reasons
    assert own(said, "Pain in the right knee, for two weeks.").pending


def test_a_drug_is_superseded_only_when_the_correction_names_another():
    assert own("Lisinopril 10 mg sorry 20 mg daily.", "Lisinopril 20 mg daily.").pending
    assert "source_superseded" in own("Start lisinopril, sorry, losartan 50 mg daily.",
                                      "Start lisinopril 50 mg daily.").reasons
    assert own("Start lisinopril, sorry, losartan 50 mg daily.", "Start losartan 50 mg daily.").pending


# ---- Grounding (FR-12) -----------------------------------------------------------------------------------------------


@pytest.mark.parametrize(("said", "value"), [
    ("Decrease metformin to 500 mg.", "Increase metformin to 500 mg."),
    ("Left knee pain for two weeks.", "Right knee pain for two weeks."),
    ("Lisinopril 10 mg daily.", "Lisinopril 10 mg twice daily."),
    ("Continue glipizide 5 mg daily.", "Continue metformin 5 mg daily."),
    ("Continue warfarin 5 mg daily.", "Continue aspirin 5 mg daily."),
    ("Fever for 2 days.", "Fever for 2 weeks."),
])
def test_a_word_the_sources_dont_have_is_discarded(said, value):
    assert "unsupported_content" in own(said, value).reasons


@pytest.mark.parametrize(("said", "value"), [
    ("Admitted to rule out pneumonia.", "Ruled out pneumonia."),
    ("Rash resolving.", "Rash resolved."),
    ("Blood pressure increasing.", "Blood pressure increased."),
])
def test_a_word_with_another_ending_is_another_word(said, value):
    assert "unsupported_content" in own(said, value).reasons


def test_a_plural_meets_its_singular():
    assert own("Blood pressures stable. Allergies reviewed.", "Blood pressure stable. Allergy reviewed.").pending


def test_june_for_may_is_a_date_not_in_the_sources():
    checked = own("Symptoms started in May.", "Symptoms started in June.")

    assert {"date_not_in_sources", "unsupported_content"} <= set(checked.reasons)
    assert own("Symptoms started in May.", "Symptoms started in May.").pending
    assert own("She may need a referral.", "She may need a referral.").pending


def test_number_words_match_digits_and_unit_spellings_match():
    assert own("Started on lisinopril ten milligrams two weeks ago.", "Started lisinopril 10 mg 2 weeks ago.").pending


@pytest.mark.parametrize("said", ["Continue aprocitentan 12.5 mg daily.", "Temperature 38.9, heart rate 112.",
                                  "Potassium 5.5, recheck in 1 week.", "Start semaglutide 0.25 mg weekly."])
def test_a_decimal_point_does_not_split_a_value(said):
    assert own(said, said).pending


def test_an_instruction_reaches_past_a_decimal_point():
    text = transcript("Prescribe semaglutide 0.25 mg weekly. Recheck in 3 months.")

    assert [text.text[r["start"] : r["end"]] for r in not_carried_over(text)] == ["Prescribe semaglutide 0.25 mg weekly"]


def test_only_an_article_may_be_added():
    assert own("Cough for three days.", "A cough for the three days.").pending
    assert "unsupported_content" in own("Cough for three days.", "She has a cough for three days.").reasons
    assert "unsupported_content" in own("He takes lisinopril 10 mg daily.",
                                        "Patient reports he takes lisinopril daily.").reasons


# Words that can change what a value says, by class; none may be free (the owner's list).
CAN_CHANGE_MEANING = {
    "time": "before after until since during then later earlier prior",
    "place or side": "left right up down over under above below bilateral",
    "quantity": "more less all some few each both twice half once most",
    "negation": "no not without never none nor",
    "with": "with",
    "joining": "and or but",
}


@pytest.mark.parametrize("kind", CAN_CHANGE_MEANING)
def test_no_word_that_can_change_meaning_is_free(kind):
    assert not FREE_WORDS & set(CAN_CHANGE_MEANING[kind].split())


@pytest.mark.parametrize(("said", "value"), [
    ("Take with food after meals.", "Take with food before meals."),
    ("Keep systolic under 160.", "Keep systolic over 160."),
    ("Knee pain for two weeks.", "Left knee pain for two weeks."),
    ("Cough for three days.", "Cough for more than three days."),
    ("Cough with fever.", "Cough without fever."),
    ("No cough. Fever since Monday.", "No cough and fever since Monday."),
    ("Ibuprofen or acetaminophen for pain.", "Ibuprofen and acetaminophen for pain."),
])
def test_a_word_that_changes_meaning_can_not_be_added(said, value):
    assert "unsupported_content" in own(said, value).reasons


def test_and_or_or_must_be_where_the_clinician_said_it():
    said = "Ibuprofen or acetaminophen for fever and pain."

    assert own(said, "Ibuprofen or acetaminophen for fever and pain.").pending
    assert own(said, "Ibuprofen and acetaminophen for fever or pain.").reasons == ["words_out_of_order"]


@pytest.mark.parametrize(("said", "value"), [
    ("Continue lisinopril 10 mg and metformin 500 mg daily.", "Continue lisinopril 500 mg daily."),
    ("Continue lisinopril 10 mg, metformin 500 mg, atorvastatin 40 mg.", "Continue metformin 40 mg."),
    ("Continue lisinopril and aspirin 81 mg daily.", "Continue lisinopril 81 mg daily."),  # aspirin has no drug stem
    ("Blood pressure 98/60, heart rate 78.", "Blood pressure 78."),
    ("Weight 82 kg, 180 cm tall.", "Weight 180 cm tall."),
    ("Systolic over 160 at home.", "Systolic 160 at home."),  # "over" is a word unless it joins two numbers
])
def test_a_number_stays_with_the_word_it_follows(said, value):
    assert own(said, value).reasons == ["words_out_of_order"]


@pytest.mark.parametrize(("said", "value"), [
    ("Continue lisinopril 10 mg and metformin 500 mg daily.", "Metformin 500 mg daily."),
    ("Takes lisinopril 10 mg daily.", "Takes lisinopril daily."),
    ("Blood pressure 140 over 90.", "Blood pressure 140/90."),
    ("Takes the 20 mg tablet.", "Takes 20 mg tablet."),
])
def test_a_number_may_lose_an_article_and_a_dose_may_be_left_out(said, value):
    assert own(said, value).pending


def test_a_value_may_leave_out_another_item_of_a_list_whole():
    said = "Continue lisinopril 10 mg and metformin 500 mg daily."

    assert own(said, "Continue metformin 500 mg daily.").pending
    assert own(said, "Continue lisinopril 500 mg daily.").reasons == ["words_out_of_order"]
    assert own("Stop lisinopril and start metformin.", "Stop metformin.").reasons == ["words_out_of_order"]
    assert own("Continue lisinopril 10 mg daily and metformin.", "Continue metformin.").reasons == ["words_out_of_order"]


def test_the_owners_recombination_example():
    assert own("Left knee pain, right knee normal.", "Right knee pain.").reasons == ["words_out_of_order"]


def test_words_may_skip_one_of_the_sources_words_but_not_two():
    assert own("Denies any chest pain.", "Denies chest pain.").pending
    assert own("Right knee normal, pain.", "Right pain.").reasons == ["words_out_of_order"]
    assert own("Right knee normal pain.", "Right normal pain.").pending
    assert own("Swelling of the right ankle.", "Swelling right ankle.").pending  # an article never counts


def test_a_value_drawing_on_two_sources_takes_each_part_from_one():
    text = transcript("Cough for three days. Fever since Monday.")

    assert field("Cough for three days. Fever since Monday.", "Cough for three days", "Fever since Monday",
                 text=text).pending
    assert "words_out_of_order" in field("Cough since Monday.", "Cough for three days", "Fever since Monday",
                                         text=text).reasons


def test_the_owners_negation_example():
    assert own("No chest pain.", "Chest pain.").reasons == ["negation_dropped"]


@pytest.mark.parametrize("negated", ["No chest pain.", "Denies chest pain.", "Without chest pain.",
                                     "Negative for chest pain.", "Not having chest pain."])
def test_a_negation_a_source_applies_must_be_kept(negated):
    assert "negation_dropped" in own(negated, "Chest pain.").reasons
    assert own(negated, negated).pending


@pytest.mark.parametrize(("said", "quote", "reason"), [
    ("No chest pain.", "chest pain", "negation_dropped"),
    ("Chest pain denied.", "Chest pain", "negation_dropped"),
    ("Mother had a stroke.", "had a stroke", "experiencer_dropped"),
    ("Possible community acquired pneumonia.", "community acquired pneumonia", "certainty_dropped"),
])
def test_a_source_can_not_leave_its_qualifier_out(said, quote, reason):
    assert own(said, quote.capitalize() + ".", quote).reasons == [reason]


def test_a_negation_counts_even_when_another_source_affirms_the_same_words():
    text = transcript("No fever yesterday. Fever today.")

    assert "negation_dropped" in field("Fever today.", "No fever yesterday", "Fever today", text=text).reasons
    assert field("No fever yesterday. Fever today.", "No fever yesterday", "Fever today", text=text).pending


def test_a_negation_may_not_be_reworded():
    assert "unsupported_content" in own("Denies chest pain.", "No chest pain.").reasons
    assert "unsupported_content" in own("Denies chest pain.", "Chest pain denied.").reasons


@pytest.mark.parametrize(("said", "value", "reason"), [
    ("Mother had a stroke.", "Stroke.", "experiencer_dropped"),
    ("Family history of breast cancer.", "Breast cancer.", "experiencer_dropped"),
    ("Type 2 diabetes in her father.", "Type 2 diabetes.", "experiencer_dropped"),
    ("Possible community acquired pneumonia.", "Community acquired pneumonia.", "certainty_dropped"),
    ("Admitted to rule out appendicitis.", "Appendicitis.", "certainty_dropped"),
    ("Community acquired pneumonia suspected.", "Community acquired pneumonia.", "certainty_dropped"),
    ("She has a history of MI in 2019.", "MI in 2019.", "history_dropped"),
    ("Status post appendectomy.", "Appendectomy.", "history_dropped"),
])
def test_who_has_it_how_sure_and_when_must_be_kept(said, value, reason):
    assert own(said, value).reasons == [reason]
    assert own(said, said).pending


def test_the_owners_experiencer_examples():
    assert own("Mother had a stroke.", "Stroke.").reasons == ["experiencer_dropped"]
    assert own("Mother had a stroke.", "Mother had a stroke.").pending
    said = "Father has diabetes, patient has hypertension."
    assert own(said, "Diabetes.", "Father has diabetes").reasons == ["experiencer_dropped"]
    assert own(said, "Hypertension.", "patient has hypertension").pending


def test_a_family_member_who_tells_is_not_who_has_it():
    assert own("Mom says the rash started Monday.", "The rash started Monday.").pending
    assert own("Maternal grandmother and mother had breast cancer.", "Mother had breast cancer.").pending


@pytest.mark.parametrize(("said", "value"), [
    ("Lives with her husband and has had a cough for 3 days.", "Has had a cough for 3 days."),
    ("Seen with her mother who wonders if the rash is spreading.", "The rash is spreading."),
    ("Seen today with her father, fever for 2 days.", "Fever for 2 days."),
    ("History of present illness 45-year-old man with a cough for 3 days.", "45-year-old man with a cough for 3 days."),
])
def test_ordinary_words_are_not_qualifiers(said, value):
    assert own(said, value, value.rstrip(".")).pending


@pytest.mark.parametrize(("said", "value"), [
    ("Seen with her mother who has diabetes.", "Has diabetes."),
    ("Her father who had a stroke is well.", "Had a stroke."),
    ("Lives with her sister that is diabetic.", "Is diabetic."),
    ("Seen with his brother who was treated for gout.", "Was treated for gout."),
])
def test_a_family_members_relative_clause_is_about_them(said, value):
    assert own(said, value).reasons == ["experiencer_dropped"]
    assert own(said, said).pending


def test_a_family_member_who_is_the_subject_still_has_it():
    assert own("Mother had a stroke and has diabetes.", "Has diabetes.", "has diabetes").reasons == [
        "experiencer_dropped"]
    assert own("She has a history of MI in 2019.", "MI in 2019.").reasons == ["history_dropped"]


def test_a_qualifier_stops_at_a_new_subject():
    assert own("No fever she is eating well.", "She is eating well.").pending
    assert own("Mother had a stroke patient has hypertension.", "Patient has hypertension.").pending
    assert own("No fever she is eating well.", "Fever.", "No fever").reasons == ["negation_dropped"]


def test_without_punctuation_a_qualifier_stops_at_the_next_step_or_heading():
    said = "no further pancreatitis continue monthly olezarsen no other injury on exam swelling below the malleolus"

    assert own(said, "Continue monthly olezarsen.", "continue monthly olezarsen").pending
    assert own(said, "Swelling below the malleolus.", "swelling below the malleolus").pending
    assert own("Does not take aspirin.", "Takes aspirin.", "take aspirin").reasons == ["negation_dropped"]
    assert own("Not able to take aspirin.", "Takes aspirin.", "take aspirin").reasons == ["negation_dropped"]


def test_a_certainty_word_reaches_back_only_from_the_end_of_its_clause():
    said = "Sore throat likely viral pharyngitis."

    assert own(said, "Sore throat.", "Sore throat likely").pending
    assert own(said, "Viral pharyngitis.").reasons == ["certainty_dropped"]


def test_too_long_fails():
    assert "too_long" in field("x " * 1501, "He takes lisinopril").reasons
    assert "too_long" in field("Lisinopril.", *(["He takes lisinopril"] * 31)).reasons


@pytest.mark.parametrize("value", ["<b>Hypertension</b>", "See https://example.com", "{\"plan\": \"x\"}",
                                   "Hypertension [unclear]"])
def test_markup_links_and_json_are_bad_format(value):
    assert "bad_format" in field(value, "hypertension, improving", name="assessment").reasons


def test_an_instruction_hidden_by_an_invisible_character_is_still_recognized():
    # The source doesn't quote an instruction, so only cleaning the value reveals the order hidden in it.
    checked = own("Chest x-ray today.", "Or\u200bder a chest x-ray today.", "Chest x-ray today")

    assert {"bad_format", "order_or_allergy_change"} <= set(checked.reasons)


def test_may_for_june_is_a_date_not_in_the_sources_too():
    assert "date_not_in_sources" in own("Symptoms started in June.", "Symptoms started in May.").reasons


def test_an_ascii_letter_the_transcript_happens_not_to_use_is_fine():
    assert own("Cough, fever.", "A cough, fever.").pending


def test_look_alike_and_invisible_characters_are_bad_format():
    assert "bad_format" in own("Hypertension, improving.", "Hypеrtension, improving.").reasons  # Cyrillic e
    assert "bad_format" in own("Hypertension, improving.", "Hyper​tension, improving.").reasons
    assert "bad_format" in own("Hypertension, improving.", "Hypertension, improving﹒").reasons  # NFKC changes it


# ---- Orders and allergy-list changes (FR-14) -----------------------------------------------------------------------

DIRECTIVES = [
    "Order a chest X-ray.",
    "Go ahead and order a CBC and a BMP.",
    "We will order an MRI of the lumbar spine.",
    "Please place an order for an echocardiogram.",
    "Send a prescription for amoxicillin to her pharmacy.",
    "Call in a new prescription for albuterol.",
    "Refill her metformin.",
    "E-prescribe azithromycin.",
    "Prescribe amoxicillin 500 mg three times a day.",
    "Add penicillin to her allergy list.",
    "Please add an allergy to codeine.",
    "Remove sulfa from her allergies.",
    "Take amoxicillin off her allergy list.",
    "Update her allergies to include latex.",
    "List shellfish as an allergy.",
    "Document a new allergy to ibuprofen.",
    "Allergy list: add peanuts.",
    # The review's bypasses.
    "Оrder chest x-ray today.",  # Cyrillic O
    "Or​der chest x-ray today.",  # zero-width space
    "Re-fill lisinopril 10 mg daily.",
    "Renew lisinopril 10 mg daily.",
    "I'll put an order in for a chest x-ray.",
    "I'm ordering a CBC and a basic metabolic panel today.",
    "Call in amoxicillin 500 mg three times a day to her pharmacy.",
    "Penicillin should be added to her allergies.",
    "Make sure sulfa is on her allergy list.",
    "Prescribing amoxicillin 500 mg today.",
    # The second review's misses.
    "Write a prescription for amoxicillin 500 mg three times a day.",
    "Putting in an order for a CT of the head.",
    "Allergies: add sulfa.",
    "Penicillin allergy can come off her list.",
    "I'm sending amoxicillin to her pharmacy.",
    "Give her a script for prednisone 40 mg daily.",
    "Give him a new prescription for amoxicillin.",
    "Let's get an order in for a chest x-ray.",
    "eRx amoxicillin 500 mg three times a day.",
    "Prescription for amoxicillin sent to her pharmacy.",
    "Record a new sulfa allergy.",
    "Note the new codeine allergy.",
    "Discontinue the penicillin allergy.",
    "Call that in to his usual pharmacy.",
    "Call that into his usual pharmacy.",  # as MedASR writes "in to"
    "Please call in amoxicillin 500 mg to her pharmacy.",
    "Also note the new codeine allergy.",
    "Clear her allergy list.",
    "Add an orders section with prednisolone 20 mg for 5 days.",
    # The final review's: "call in" with nothing to order named, "note" or "record" after asking for it.
    "Please call in an antibiotic for her sinusitis.",
    "Can you call in something for her nausea.",
    "Call in amoxicillin, she asked about the cost.",
    "We need to record the new codeine allergy.",
    "Make sure to note her latex allergy.",
    "Don't forget to note her latex allergy.",
    "Remember to record the new peanut allergy.",
    "Be sure to note the new latex allergy.",
    "Please do not forget to record the new sulfa allergy.",
    "I did call in amoxicillin.",  # only someone else calling in, in the past, is documentation
]

DOCUMENTATION = [
    "Start lisinopril 10 mg daily.",
    "Continue metformin and increase atorvastatin to 40 mg.",
    "Stop ibuprofen.",
    "No known drug allergies.",
    "Allergic to penicillin, causes hives.",
    "Denies drug allergies.",
    "Allergies reviewed and updated.",
    "Her cardiologist ordered an echo last year.",
    "She needs a refill of her inhaler soon.",
    "Her prescription for metformin ran out.",
    "In order to rule out pneumonia, chest X-ray was reviewed.",
    "We'll add loratadine for her seasonal allergies.",
    "She was put on amoxicillin and had an allergic reaction.",
    "No change in her allergies.",
    "The ordering physician is her cardiologist.",
    "Of note allergies reviewed with her today.",
    "Lungs clear, no known drug allergies.",
    "Chest clear no allergies to report.",
    "Allergies: penicillin, causes hives.",
    "Call her in 2 weeks with the results.",
    "Her daughter sent photos of the rash.",
    "Mom called in yesterday about the rash.",
    "She will call in sick to work tomorrow.",
    "Her prescriptions were sent to the pharmacy last week.",
    "Her records note a sulfa allergy.",
    "Mom will call in tomorrow about the rash.",
    "Please call in to say how she is doing.",
    "She can call in for an appointment next week.",
    "Her husband did call in to check on her.",
    "Calling in yesterday, mom said the rash was worse.",
    # Instructions not to.
    "Start metformin and don't order an A1c.",
    "Do not order an MRI.",
    "No need to order labs today.",
    "Hold off on ordering an MRI.",
    "We will not be ordering a CT.",
    "Sulfa should not be added to her allergies.",
]


@pytest.mark.parametrize("value", DIRECTIVES)
def test_an_order_or_allergy_list_change_is_never_carried(value):
    checked = own(value.replace("О", "O").replace("​", ""), value)

    assert not checked.pending
    assert {"order_or_allergy_change", "bad_format"} & set(checked.reasons)


@pytest.mark.parametrize("value", DOCUMENTATION)
def test_documentation_that_mentions_orders_or_allergies_passes(value):
    assert directive_spans(value) == []
    assert own(value, value).pending


def test_please_update_that_is_the_instruction_and_what_it_refers_to_is_documentation():
    said = "She is not allergic to sulfa after all, please update that."

    assert said_instructions(said) == ["please update that"]
    assert own(said, said).value == "She is not allergic to sulfa after all."


@pytest.mark.parametrize(("said", "instruction"), [
    ("There's no reason not to order an MRI of the knee.", "order an MRI of the knee"),
    ("I can't not order an MRI.", "order an MRI"),
    ("There's no good reason not to order an MRI.", "order an MRI"),
])
def test_two_negations_make_an_instruction_again(said, instruction):
    assert said_instructions(said) == [instruction]
    assert "MRI" not in own(said, said).value


def test_a_negation_of_something_else_does_not_undo_an_instruction_not_to():
    said = "She has no chest pain so no need to order a troponin."

    assert said_instructions(said) == []
    assert own(said, said).value == said


def test_an_instruction_not_to_must_keep_its_negation():
    said = "Start naproxen and don't order an MRI."

    assert not_carried_over(transcript(said)) == []
    assert own(said, "An MRI.", "don't order an MRI").reasons == ["negation_dropped"]


def test_an_order_sentence_is_cut_and_the_rest_of_the_plan_kept():
    text = transcript("Start metformin 500 mg daily. I'm ordering an A1c.")

    checked = field("Start metformin 500 mg daily. I'm ordering an A1c.", "Start metformin 500 mg daily",
                    "I'm ordering an A1c", name="plan", text=text)

    assert checked.pending and checked.value == "Start metformin 500 mg daily."
    assert [text.text[s:e] for s, e in checked.spans] == ["Start metformin 500 mg daily"]


def test_a_source_quoting_an_instruction_counts_for_the_rest_of_its_words():
    text = transcript("Start metformin 500 mg twice daily and order an A1c. Recheck in 3 months.")
    quote = "Start metformin 500 mg twice daily and order an A1c"

    checked = field("Start metformin 500 mg twice daily.", quote, name="plan", text=text)

    assert checked.pending
    assert [text.text[s:e] for s, e in checked.spans] == ["Start metformin 500 mg twice daily"]
    assert "unsupported_content" in field("Start metformin 500 mg twice daily. A1c.", quote, name="plan",
                                          text=text).reasons



def test_an_instruction_inside_a_source_splits_it_in_two():
    said = "Start naproxen, order an MRI, recheck in 3 months."

    assert own(said, "Start naproxen. Recheck in 3 months.").pending
    assert own(said, "MRI, recheck in 3 months.").reasons == ["unsupported_content"]


@pytest.mark.parametrize("said", ["Do not start metformin until we order an A1c.",
                                  "There's no reason not to order an MRI of the knee."])
def test_a_sentence_whose_rest_needs_the_instruction_goes_whole(said):
    assert own(said, said).reasons == ["order_or_allergy_change"]


def test_an_instruction_after_and_is_cut_where_it_starts():
    checked = own("Start lisinopril 10 mg daily and order a chest x-ray.",
                  "Start lisinopril 10 mg daily and order a chest x-ray.", "start lisinopril 10 mg daily")

    assert checked.pending and checked.value == "Start lisinopril 10 mg daily."


def test_calling_it_into_the_pharmacy_is_cut_and_reported():
    said = "Add ivabradine 5 mg twice daily, and call that into his usual pharmacy. Recheck in 2 weeks."

    assert said_instructions(said) == ["and call that into his usual pharmacy"]
    assert own(said, said).value == "Add ivabradine 5 mg twice daily. Recheck in 2 weeks."


def test_an_instruction_can_not_reach_the_note_through_another_field():
    text = transcript(DICTATION)

    # The model put the panel in History, quoting the instruction: that source is set aside, so nothing grounds it.
    history = field("Basic metabolic panel.", "order a basic metabolic panel", text=text)
    plan = field("Continue lisinopril, recheck in 3 months.", "continue lisinopril, recheck in 3 months", name="plan",
                 text=text)

    assert history.reasons == ["order_or_allergy_change", "unsupported_content"]
    assert plan.pending


def test_every_instruction_in_the_transcript_is_reported_as_not_carried_over():
    text = transcript("Start metformin. Go ahead and order an A1c. Add sulfa to her allergy list. Recheck in 3 months.")

    reported = not_carried_over(text)

    assert [(r["reason"], text.text[r["start"] : r["end"]]) for r in reported] == [
        ("order", "Go ahead and order an A1c"),
        ("allergy_change", "Add sulfa to her allergy list"),
    ]


def test_an_instruction_without_punctuation_reaches_only_its_own_words():
    text = transcript("start lisinopril 10 mg daily and go ahead and order a basic metabolic panel")

    assert field("Start lisinopril 10 mg daily.", "start lisinopril 10 mg daily", name="plan", text=text).pending
    assert [kind for _, _, kind in directives(text.text)] == ["order"]


def said_instructions(text):
    t = transcript(text)
    return [t.text[r["start"] : r["end"]] for r in not_carried_over(t)]


def test_an_instruction_naming_the_allergy_list_ends_there_without_punctuation():
    text = transcript("the allergist cleared her last month so take amoxicillin off her allergy list strep pharyngitis "
                      "amoxicillin 50 mg/kg once daily for 10 days")

    assert said_instructions(text.text) == ["take amoxicillin off her allergy list"]
    assert field("Strep pharyngitis.", "strep pharyngitis", name="assessment", text=text).pending
    assert field("Amoxicillin 50 mg/kg once daily for 10 days.", "amoxicillin 50 mg/kg once daily for 10 days",
                 name="plan", text=text).pending


@pytest.mark.parametrize(("said", "instruction"), [
    ("Please add an allergy to codeine today.", "Please add an allergy to codeine today"),
    ("Update her allergies to include latex, recheck in 2 weeks.", "Update her allergies to include latex"),
    ("Send a prescription for amoxicillin to her pharmacy.", "Send a prescription for amoxicillin to her pharmacy"),
    ("Prescribe the patient amoxicillin 500 mg.", "Prescribe the patient amoxicillin 500 mg"),  # no "and" or comma
    ("So update his allergy record to say low risk penicillin rash only.",
     "update his allergy record to say low risk penicillin rash only"),
])
def test_an_instruction_goes_on_where_a_word_carries_it(said, instruction):
    assert said_instructions(said) == [instruction]


def test_an_instruction_whose_subject_comes_first_starts_with_its_subject():
    said = "Continue the ointments as needed, and cephalexin should be added to her allergies."
    text = transcript(said)

    assert said_instructions(said) == ["cephalexin should be added to her allergies"]
    assert field("Continue the ointments as needed.", "Continue the ointments as needed", name="plan",
                 text=text).pending


@pytest.mark.parametrize(("said", "instruction"), [
    ("Order a CBC and a BMP and start metformin 500 mg daily.", "Order a CBC and a BMP"),
    ("order a chest x-ray then recheck in 2 weeks", "order a chest x-ray"),
    ("Call in amoxicillin 500 mg, recheck in 10 days.", "Call in amoxicillin 500 mg"),
    ("Order a CBC, BMP and lipid panel, she will come fasting.", "Order a CBC, BMP and lipid panel"),
    ("Order an MRI of the lumbar spine; continue naproxen.", "Order an MRI of the lumbar spine"),
])
def test_an_instruction_ends_with_its_clause(said, instruction):
    assert said_instructions(said) == [instruction]



# ---- Possible corrections ----------------------------------------------------------------------------------------


def warned(said, value, *sources):
    """The transcript's text at each place the value is warned about."""
    text = transcript(said)
    checked = field(value, *(sources or (said,)), name="plan", text=text)
    assert checked.pending, checked.reasons
    return [text.text[start:end] for code, start, end in checked.warnings if code == "possible_correction"]


@pytest.mark.parametrize(("said", "value", "later"), [
    ("Lisinopril ten milligrams no weight twenty milligrams daily.", "Lisinopril ten milligrams.", "twenty"),
    ("Add semaglutide 0.5 mg weekly no weight start at 0.25 mg weekly.", "Add semaglutide 0.5 mg weekly.", "0.25"),
    ("After 10 minutes standing heart rate 124 weight no 118, blood pressure 104/72.", "Heart rate 124.", "118"),
    ("Increase febuxostat to 120 mg scratch the 80 mg daily.", "Increase febuxostat to 120 mg.", "80"),
])
def test_a_correction_the_checks_did_not_hear_is_flagged_not_discarded(said, value, later):
    assert warned(said, value, value.rstrip(".")) == [later]


@pytest.mark.parametrize("said", [
    "Type 2 diabetes on metoprolol and metformin.",  # "I mean" heard as "and": not caught, a known limitation
    "Continue metoprolol 25 mg and lisinopril 10 mg daily.",
    "Increase lisinopril from 10 mg to 20 mg daily.",
    "Blood pressure 142 over 88, heart rate 72.",
    "Off work from March 3 to March 10.",
    "Temperature 38.1, heart rate 102, respiratory rate 18.",
    "Started aprocitentan 12.5 mg daily 6 weeks ago.",
])
def test_ordinary_lists_ranges_and_titrations_are_not_flagged(said):
    assert warned(said, said) == []


@pytest.mark.parametrize("said", ["Blood pressure 142 over 88.", "Blood pressure 142/88.", "Lisinopril 10 mg daily.",
                                  "Cough for 3 days, no fever.", "Take 10 mg now and 10 mg at night.",
                                  "Lisinopril 10 mg from Monday."])
def test_nothing_else_of_its_kind_after_it_no_warning(said):
    assert warned(said, said) == []


def test_only_where_the_value_took_its_words_counts():
    said = "Fever for 3 days and she has been resting at home since then. Score 3 then 5."

    assert warned(said, "Fever for 3 days.", "Fever for 3 days") == []


def test_a_correction_the_checks_hear_still_discards_and_its_correction_is_not_flagged():
    said = "Lisinopril 10 mg, sorry, 20 mg daily."

    assert "source_superseded" in own(said, "Lisinopril 10 mg.", "Lisinopril 10 mg").reasons
    assert warned(said, "Lisinopril 20 mg daily.") == []
    assert warned("Range of motion is 5 to 95 degrees no sorry 5 to 90 degrees.", "Range of motion is 5 to 90 degrees.") == []


def test_a_warning_goes_out_with_the_pending_value_as_where_it_points():
    text = transcript("Heart rate 124 weight no 118.")

    checked = field("Heart rate 124.", "Heart rate 124", text=text)

    assert checked.to_json(text)["warnings"] == [{"code": "possible_correction", "start": 25, "end": 28}]
    assert "warnings" not in field("Heart rate 99.", "Heart rate 99", text=text).to_json(text)


def test_a_value_next_to_an_unclear_passage_is_flagged():
    text = transcript("Cough for three days.", ("Fever since Tuesday.", "too_noisy"), "Recheck in a week.")

    checked = field("Cough for three days.", "Cough for three days", text=text)

    assert checked.pending
    assert [(code, text.text[start:end]) for code, start, end in checked.warnings] == [
        ("near_unclear_passage", "Fever since Tuesday.")]
    assert checked.to_json(text)["warnings"][0]["code"] == "near_unclear_passage"


def test_a_value_far_from_an_unclear_passage_is_not_flagged():
    text = transcript("Cough for three days, worse at night when lying flat.", ("Fever since Tuesday.", "too_noisy"))

    assert field("Cough for three days.", "Cough for three days", text=text).warnings == []
    earlier = transcript(("Fever since Tuesday.", "too_noisy"), "She says it is worse at night and lying flat. Cough for "
                                                                 "three days.")
    assert field("Cough for three days.", "Cough for three days", text=earlier).warnings == []
