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
                                     "sources": [{"start": checked.spans[0][0], "end": checked.spans[0][1]}]}


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


def test_a_negation_counts_even_when_another_source_affirms_the_same_words():
    text = transcript("No fever yesterday. Fever today.")

    assert "negation_dropped" in field("Fever today.", "No fever yesterday", "Fever today", text=text).reasons


def test_a_negation_may_not_be_reworded():
    assert "unsupported_content" in own("Denies chest pain.", "No chest pain.").reasons
    assert "unsupported_content" in own("Denies chest pain.", "Chest pain denied.").reasons


def test_too_long_fails():
    assert "too_long" in field("x " * 1001, "He takes lisinopril").reasons
    assert "too_long" in field("Lisinopril.", *(["He takes lisinopril"] * 13)).reasons


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


def test_an_order_sentence_is_cut_and_the_rest_of_the_plan_kept():
    text = transcript("Start metformin 500 mg daily. I'm ordering an A1c.")

    checked = field("Start metformin 500 mg daily. I'm ordering an A1c.", "Start metformin 500 mg daily",
                    "I'm ordering an A1c", name="plan", text=text)

    assert checked.pending and checked.value == "Start metformin 500 mg daily."
    assert [text.text[s:e] for s, e in checked.spans] == ["Start metformin 500 mg daily"]


def test_an_instruction_after_and_is_cut_where_it_starts():
    checked = own("Start lisinopril 10 mg daily and order a chest x-ray.",
                  "Start lisinopril 10 mg daily and order a chest x-ray.", "start lisinopril 10 mg daily")

    assert checked.pending and checked.value == "Start lisinopril 10 mg daily."


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
