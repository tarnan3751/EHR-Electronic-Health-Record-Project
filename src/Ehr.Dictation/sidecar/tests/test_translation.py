"""translation.py: the checks on English proposed from a transcript dictated in Spanish (the expansion plan's step 17,
proposed FR-61). Each English statement must rest on Spanish passages of the transcript, word for word, and say no
number, unit, how often, date, drug, side, route, severity or abbreviation they don't, the same medication changes,
negations, uncertainty, past and relatives, and not much more; any failure discards the field."""

from __future__ import annotations

import pytest

from ehr_dictation import spanish, translation
from ehr_dictation.transcript import Placed, Segment, Transcript, utf16_offset


def transcript(text: str, unclear: tuple[int, int] | None = None) -> Transcript:
    if unclear is None:
        return Transcript(text, (Placed(Segment(0, 0, text, 1.0, None, True), 0, len(text)),), True)
    start, end = unclear
    placed = [Placed(Segment(0, 0, text[:start], 1.0, None, True), 0, start),
              Placed(Segment(0, 0, text[start:end], 0.2, "low_confidence", True), start, end),
              Placed(Segment(0, 0, text[end:], 1.0, None, True), end, len(text))]
    return Transcript(text, tuple(p for p in placed if p.end > p.start), True)


def one(text: str, spanish: list[str], english: str, field: str = "plan", **kwargs) -> translation.Translated:
    checks = translation.check({field: [{"spanish": spanish, "english": english}]}, [], transcript(text, **kwargs))
    return checks.fields[field]


def reasons(text: str, spanish: list[str], english: str, **kwargs) -> list[str]:
    return one(text, spanish, english, **kwargs).reasons


# ---- The plan's examples (§4) ---------------------------------------------------------------------------------------


@pytest.mark.parametrize(("text", "quote", "english", "expected"), [
    ("Niega dolor torácico.", "Niega dolor torácico.", "Denies chest pain.", []),
    ("Niega dolor torácico.", "Niega dolor torácico.", "Chest pain.", ["negation_dropped"]),
    ("Metformina 850 mg dos veces al día.", "Metformina 850 mg dos veces al día", "Metformin 500 mg twice a day.",
     ["number_not_in_sources"]),
    ("Levotiroxina 50 microgramos al día.", "Levotiroxina 50 microgramos al día", "Levothyroxine 50 mg daily.",
     ["unit_not_in_sources"]),
    ("Suspender metformina.", "Suspender metformina.", "Stop metoprolol.", ["drug_not_in_sources"]),
    ("Suspender metformina.", "Suspender metformina.", "Continue metformin.", ["action_changed"]),
    ("Su padre tuvo un infarto a los 50 años.", "Su padre tuvo un infarto a los 50 años.",
     "Her mother had a heart attack at age 50.", ["experiencer_changed"]),
    ("Continuar metformina.", "Continuar metformina.", "Continue metformin and check kidney function.",
     ["translation_adds_words"]),
    ("Amoxicilina 500, no, perdón, 875 mg cada 12 horas.", "Amoxicilina 500, no, perdón, 875 mg cada 12 horas.",
     "Amoxicillin 500 mg every 12 hours.", ["source_superseded"]),
    # Example 12, the stated limit: a wrong word outside the classes checked passes. The review must catch it.
    ("Dolor abdominal leve.", "Dolor abdominal leve.", "Mild chest pain.", []),
])
def test_the_plans_examples(text, quote, english, expected):
    assert reasons(text, [quote], english) == expected


def test_an_instruction_cited_as_a_statements_source_is_dropped_and_reported():
    text = "Continuar losartán 50 mg al día y pido una radiografía de tórax."
    checks = translation.check({"plan": [
        {"spanish": ["Continuar losartán 50 mg al día"], "english": "Continue losartan 50 mg daily."},
        {"spanish": ["pido una radiografía de tórax"], "english": "Chest X-ray."},
    ]}, [], transcript(text))

    assert checks.fields["plan"].value == "Continue losartan 50 mg daily."
    assert translation.not_carried_over(transcript(text)) == [
        {"reason": "order", "start": text.index("y pido"), "end": len(text) - 1}]


def test_a_field_that_was_only_an_instruction_is_empty_and_says_why():
    text = "Agrega la penicilina a sus alergias."
    assert reasons(text, ["Agrega la penicilina a sus alergias"], "Penicillin allergy.") == ["order_or_allergy_change"]


def test_an_english_instruction_is_cut_from_the_translation_as_from_an_english_value():
    text = "Continuar metformina. Pido una A1c."
    field = one(text, ["Continuar metformina."], "Continue metformin and order an A1c.")
    assert field.pending and field.value == "Continue metformin."


def test_a_number_written_two_ways_is_offered_with_a_warning_pointing_at_it():
    text = "Metformina 1.000 mg al día."
    field = one(text, ["Metformina 1.000 mg al día"], "Metformin 1000 mg daily.")
    at = text.index("1.000")
    assert field.pending and (translation.AMBIGUOUS_NUMBER, at, at + 5) in field.warnings


def test_every_third_day_is_offered_with_a_warning():
    text = "Warfarina 2,5 mg cada tercer día."
    field = one(text, ["Warfarina 2,5 mg cada tercer día"], "Warfarin 2.5 mg every other day.")
    assert field.pending and field.warnings[0][0] == translation.AMBIGUOUS_FREQUENCY


# ---- Each reason --------------------------------------------------------------------------------------------------


@pytest.mark.parametrize(("text", "quote", "english", "expected"), [
    ("Control en 4 semanas.", "Control en 6 semanas.", "Follow-up in 6 weeks.", "source_not_in_transcript"),
    ("Tos productiva.", "tos", "Cough.", "source_too_short"),
    ("Tos productiva.", "[unclear]", "Cough.", "source_in_unclear_passage"),
    ("Paracetamol 1 g cada 8 horas.", "Paracetamol 1 g cada 8 horas.", "Acetaminophen 1 g twice a day.",
     "frequency_not_in_sources"),
    ("Control el lunes.", "Control el lunes.", "Follow-up on Tuesday.", "date_not_in_sources"),
    ("Dolor en la rodilla izquierda.", "Dolor en la rodilla izquierda.", "Right knee pain.", "side_not_in_sources"),
    ("Salbutamol inhalado.", "Salbutamol inhalado.", "Albuterol by mouth.", "route_not_in_sources"),
    ("Dolor leve.", "Dolor leve.", "Severe pain.", "severity_not_in_sources"),
    ("Presión arterial alta.", "Presión arterial alta.", "High BP and MI.", "abbreviation_not_in_sources"),
    ("Posible neumonía.", "Posible neumonía.", "Pneumonia.", "certainty_dropped"),
    ("Neumonía.", "Neumonía basal.", "Possible pneumonia.", "source_not_in_transcript"),
    ("Neumonía basal.", "Neumonía basal.", "Possible basal pneumonia.", "certainty_added"),
    ("Tos y fiebre.", "Tos y fiebre.", "No cough or fever.", "negation_added"),
    ("Antecedente de infarto.", "Antecedente de infarto.", "Heart attack.", "history_dropped"),
    ("Infarto agudo.", "Infarto agudo.", "History of acute MI.", "history_added"),
    ("Diabetes en su madre.", "Diabetes en su madre.", "Diabetes.", "experiencer_dropped"),
    ("Tiene diabetes tipo 2.", "Tiene diabetes tipo 2.", "Her mother has type 2 diabetes.", "experiencer_added"),
    ("Sin fiebre <b>.", "Sin fiebre <b>.", "No fever <b>.", "bad_format"),
])
def test_each_reason(text, quote, english, expected):
    assert expected in reasons(text, [quote], english)


def test_no_sources():
    assert reasons("Tos productiva.", [], "Productive cough.") == ["no_sources"]


def test_a_statement_too_long_or_too_many_is_too_long():
    text = "Tos productiva. " * 40
    many = [{"spanish": ["Tos productiva."], "english": "Productive cough."}] * 31
    assert "too_long" in translation.check({"plan": many}, [], transcript(text)).fields["plan"].reasons


def test_one_failing_statement_discards_the_whole_field():
    text = "Niega dolor torácico. Suspender metformina."
    checks = translation.check({"plan": [
        {"spanish": ["Niega dolor torácico."], "english": "Denies chest pain."},
        {"spanish": ["Suspender metformina."], "english": "Continue metformin."},
    ]}, [], transcript(text))
    assert checks.fields["plan"].reasons == ["action_changed"] and not checks.fields["plan"].pending


def test_nothing_proposed_is_not_dictated():
    checks = translation.check({}, ["orders"], transcript("Tos."))
    assert all(field.reasons == ["not_dictated"] for field in checks.fields.values()) and checks.unknown == ["orders"]


# ---- Warnings -------------------------------------------------------------------------------------------------------


def test_a_correction_the_checks_didnt_hear_is_a_possible_correction():
    # "no weight" for "no wait": the cue misheard, a correction-like word ("no") between two doses.
    text = "Metformina 500 mg no weight 850 mg al día."
    field = one(text, ["Metformina 500 mg"], "Metformin 500 mg.")
    at = text.index("850")
    assert field.pending and ("possible_correction", at, at + 3) in field.warnings


def test_a_source_beside_an_unclear_passage_is_warned_of():
    text = "Continuar metformina mmm algo dos veces al día."
    at = text.index("mmm")
    field = one(text, ["Continuar metformina"], "Continue metformin.", unclear=(at, at + 8))
    assert field.pending and field.warnings == [("near_unclear_passage", at, at + 8)]


def test_the_only_field_holding_the_whole_dictation_is_warned_of():
    text = "Suspender metformina."
    field = one(text, ["Suspender metformina."], "Stop metformin.")
    assert field.warnings == [("one_field_holds_dictation", 0, len(text) - 1)]  # word to word, not the full stop


# ---- What goes out --------------------------------------------------------------------------------------------------


def test_each_statements_place_in_the_value_and_its_sources_go_out_as_utf16_offsets():
    text = "𝔁 Niega dolor torácico. Suspender metformina."
    checks = translation.check({"subjective": [
        {"spanish": ["Niega dolor torácico."], "english": "Denies chest pain"},
    ], "plan": [{"spanish": ["Suspender metformina."], "english": "Stop metformin."}]}, [], transcript(text))
    sent = checks.to_json(transcript(text))

    start, end = text.index("Niega"), text.index("torácico") + len("torácico")
    source = {"start": utf16_offset(text, start), "end": utf16_offset(text, end)}
    assert source == {"start": 3, "end": 23}  # the 𝔁 before them is two UTF-16 code units
    assert sent["subjective"] == {
        "status": "pending",
        "value": "Denies chest pain.",
        "sources": [source],
        "statements": [{"start": 0, "end": 18, "sources": [source]}],
        "warnings": [],
    }
    assert sent["objective"] == {"status": "empty", "reasons": ["not_dictated"]}


def test_statements_are_joined_into_the_value_in_order():
    text = "Niega fiebre. Niega disnea."
    checks = translation.check({"subjective": [
        {"spanish": ["Niega fiebre."], "english": "Denies fever."},
        {"spanish": ["Niega disnea."], "english": "Denies shortness of breath"},
    ]}, [], transcript(text))
    field = checks.fields["subjective"]
    assert field.value == "Denies fever. Denies shortness of breath."
    assert [(start, end) for start, end, _ in field.statements] == [(0, 13), (14, 41)]


# ---- Words read as said (run A's discards) --------------------------------------------------------------------------
# Each rule loosened for a right translation, with the wrong one it must still refuse beside it. None of these sentences
# is in the Spanish test set (eval/scripts-es), so the lists aren't fitted to it.


@pytest.mark.parametrize(("text", "english", "expected"), [
    # "No" and an adjective, written as one English word that negates itself.
    ("Diabetes mellitus no controlada.", "Uncontrolled diabetes mellitus.", []),
    ("Tos no productiva.", "Nonproductive cough.", []),
    ("Abdomen no doloroso a la palpación.", "Nontender abdomen on palpation.", []),
    ("Edema sin fóvea en ambos tobillos.", "Nonpitting edema in both ankles.", []),
    ("Diabetes mellitus no controlada.", "Controlled diabetes mellitus.", ["negation_dropped"]),
    ("Diabetes mellitus controlada.", "Uncontrolled diabetes mellitus.", ["negation_added"]),
])
def test_an_english_word_that_negates_itself_is_a_negation(text, english, expected):
    assert reasons(text, [text], english) == expected


@pytest.mark.parametrize(("english", "expected"), [
    ("Suspend lisinopril.", []),
    ("Suspended lisinopril.", []),
    ("Continue lisinopril.", ["action_changed"]),
])
def test_suspend_is_a_stop(english, expected):
    assert reasons("Suspender lisinopril.", ["Suspender lisinopril."], english) == expected


@pytest.mark.parametrize(("text", "english", "expected"), [
    ("Mareo desde hace una semana.", "Dizziness for the past week.", []),
    ("Mareo desde hace una semana.", "Dizziness for 1 week.", []),
    ("Mareo desde hace tres meses.", "Dizziness for the past 3 months.", []),
    ("Mareo desde hace unos meses.", "Dizziness for the past months.", []),
    ("Caída hace 3 días.", "Fall 3 days ago.", []),
    ("Mareo desde hace una semana.", "Dizziness since last week.", ["date_not_in_sources"]),
    ("Caída hace 3 días.", "Fall 5 days ago.", ["number_not_in_sources"]),
    ("Caída hace 3 días.", "Fall in the past 3 days, since yesterday.", ["date_not_in_sources"]),
])
def test_hace_is_a_time_up_to_now(text, english, expected):
    assert reasons(text, [text], english) == expected


def test_last_week_is_still_last_week_only():
    assert spanish_dates("Empezó la semana pasada.") == {"last week"}
    assert spanish_dates("Empezó hace una semana.") == {"ago", "past week", "past weeks"}


def spanish_dates(text: str) -> set[str]:
    return spanish.date_terms(spanish.tokens(text))


@pytest.mark.parametrize(("text", "english", "expected"), [
    ("Disnea al caminar una cuadra.", "Dyspnea walking one block.", []),
    ("Disnea al caminar una cuadra.", "Dyspnea walking 1 block.", []),
    ("Tomar un comprimido de ibuprofeno.", "Take 1 tablet of ibuprofen.", []),
    ("Tomar un comprimido de ibuprofeno.", "Take 2 tablets of ibuprofen.", ["number_not_in_sources"]),
    ("Disnea al caminar una cuadra.", "Dyspnea walking 2 blocks.", ["number_not_in_sources"]),
])
def test_un_una_and_uno_are_one(text, english, expected):
    assert reasons(text, [text], english) == expected


@pytest.mark.parametrize(("text", "english", "expected"), [
    ("Se le explica a la madre la dieta.", "Mother is explained the diet.", []),
    ("Se explica a la madre la dieta.", "The diet is explained to the mother.", []),
    ("Se informa a la hija del diagnóstico.", "The daughter is informed of the diagnosis.", []),
    ("Se aconseja a su esposo la vacuna.", "Her husband was advised about the vaccine.", []),
    ("La madre tiene asma.", "Asthma is explained to the mother.", ["experiencer_dropped"]),
    ("La madre tiene asma.", "Mother has asthma.", []),
])
def test_a_relative_told_something_has_nothing(text, english, expected):
    assert reasons(text, [text], english) == expected
