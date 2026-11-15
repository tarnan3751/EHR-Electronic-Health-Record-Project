"""validation.py: each check, with its reason code; what a pending field carries; and the boundary between a note that
documents care and an instruction to order something or change the allergy list (FR-12, FR-14)."""

from __future__ import annotations

import pytest

from ehr_dictation.transcript import Segment, assemble
from ehr_dictation.validation import REASONS, check, directive_spans

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

    assert checked.to_json(text) == {"status": "empty", "reasons": ["source_not_in_transcript"]}


def test_keys_outside_the_schema_are_reported_and_never_a_field():
    fields = {n: {"value": "", "sources": []} for n in ("history", "assessment", "plan")}

    checks = check(fields, ["orders"], transcript(DICTATION))

    assert checks.unknown == ["orders"] and list(checks.fields) == ["history", "assessment", "plan"]


def test_an_empty_value_was_not_dictated():
    assert field("").reasons == ["not_dictated"]


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


def test_a_source_the_clinician_corrected_fails_and_the_correction_passes():
    assert "source_superseded" in field("BP 150/90.", "Blood pressure today 150 over 90").reasons
    assert field("BP 140/90.", "140 over 90").pending
    assert field("Blood pressure 140/90.", "Blood pressure today 150 over 90, sorry, 140 over 90").pending
    assert "source_superseded" in field("Blood pressure 150/90.", "Blood pressure today 150 over 90, sorry, 140 over 90").reasons


def test_too_long_fails():
    assert "too_long" in field("x " * 1001, "He takes lisinopril").reasons
    assert "too_long" in field("Lisinopril.", *(["He takes lisinopril"] * 13)).reasons


@pytest.mark.parametrize("value", ["<b>Hypertension</b>", "See https://example.com", "{\"plan\": \"x\"}", "Hypertension [unclear]"])
def test_markup_links_and_json_are_bad_format(value):
    assert "bad_format" in field(value, "hypertension, improving", name="assessment").reasons


def test_a_number_not_in_the_sources_fails_and_number_words_match_digits():
    assert "number_not_in_sources" in field("He takes lisinopril 20 mg daily.", "He takes lisinopril 10 mg daily").reasons
    text = transcript("Started on lisinopril ten milligrams two weeks ago.")
    assert field("Started lisinopril 10 milligrams 2 weeks ago.",
                 "Started on lisinopril ten milligrams two weeks ago", text=text).pending


def test_a_date_not_in_the_sources_fails():
    assert "date_not_in_sources" in field("Seen in clinic on April 3.", "Seen in clinic on March 3").reasons
    assert "date_not_in_sources" in field("Seen yesterday in clinic.", "Seen in clinic on March 3").reasons
    assert field("Seen in clinic on March 3.", "Seen in clinic on March 3").pending


def test_a_drug_not_in_the_sources_fails():
    assert "drug_not_in_sources" in field("He takes losartan daily.", "He takes lisinopril 10 mg daily").reasons


def test_a_dropped_negation_fails():
    assert "negation_dropped" in field("Chest pain and shortness of breath.",
                                       "Denies chest pain or shortness of breath").reasons
    assert field("Denies chest pain or shortness of breath.", "Denies chest pain or shortness of breath").pending
    assert field("No chest pain or shortness of breath.", "Denies chest pain or shortness of breath").pending
    assert field("Chest pain denied.", "Denies chest pain").pending


def test_content_the_sources_dont_support_fails():
    assert "unsupported_content" in field("He takes lisinopril for diabetic kidney disease.",
                                          "He takes lisinopril 10 mg daily").reasons
    assert field("Patient reports taking lisinopril daily.", "He takes lisinopril 10 mg daily").pending


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
]

DOCUMENTATION = [
    "Start lisinopril 10 mg daily.",
    "Continue metformin and increase atorvastatin to 40 mg.",
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
]


@pytest.mark.parametrize("value", DIRECTIVES)
def test_an_order_or_allergy_list_change_is_discarded(value):
    assert directive_spans(value)
    text = transcript(value)
    assert "order_or_allergy_change" in field(value, value, name="plan", text=text).reasons


@pytest.mark.parametrize("value", DOCUMENTATION)
def test_documentation_that_mentions_orders_or_allergies_passes(value):
    assert directive_spans(value) == []
    text = transcript(value)
    assert field(value, value, name="plan", text=text).pending


def test_an_instruction_can_not_reach_the_note_through_another_field():
    text = transcript(DICTATION)

    # The model put the panel in History, quoting the instruction: discarded there too.
    history = field("Basic metabolic panel.", "order a basic metabolic panel", text=text)
    plan = field("Continue lisinopril, recheck in 3 months.", "continue lisinopril, recheck in 3 months", name="plan", text=text)

    assert "order_or_allergy_change" in history.reasons
    assert plan.pending


def test_an_instruction_without_punctuation_reaches_only_its_own_words():
    text = transcript("start lisinopril 10 mg daily and go ahead and order a basic metabolic panel")

    assert field("Start lisinopril 10 mg daily.", "start lisinopril 10 mg daily", name="plan", text=text).pending
    assert "order_or_allergy_change" in field("Basic metabolic panel.", "order a basic metabolic panel", name="plan",
                                              text=text).reasons


def test_every_failure_is_recorded_in_a_fixed_order():
    checked = field("<i>Takes losartan 20 mg</i> since April.", "nothing like this was said")

    assert checked.reasons == [r for r in REASONS if r in set(checked.reasons)]
    assert {"bad_format", "source_not_in_transcript", "number_not_in_sources", "date_not_in_sources",
            "drug_not_in_sources", "unsupported_content"} <= set(checked.reasons)
