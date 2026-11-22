"""translation.py: the checks on English proposed from a transcript dictated in Spanish (the expansion plan's step 17,
proposed FR-61). Each English statement must rest on Spanish passages of the transcript, word for word, and say no
number, unit, how often, date, drug, side, route, severity or abbreviation they don't, the same medication changes,
negations, uncertainty, past and relatives, and not much more; any failure discards the field."""

from __future__ import annotations

import pytest

from ehr_dictation import prompt, spanish, translation
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
    ("Suspender metformina.", "Suspender metformina.", "Stop metoprolol.", ["drug_not_in_sources", "drug_dropped"]),
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


def test_a_statement_citing_more_than_400_characters_of_spanish_is_too_long():
    # The schema allows two quotes of 200; the on-prem app refuses a sentence citing more (TrustedResult).
    def cited(first: int, second: int) -> list[str]:
        text = "Refiere cansancio por las tardes. " * first + "Niega dolor torácico al caminar. " * second
        statement = {"spanish": [("Refiere cansancio por las tardes. " * first).strip(" ."),
                                 ("Niega dolor torácico al caminar. " * second).strip(" .")],
                     "english": "Tired in the afternoons, denies chest pain walking."}
        assert sum(len(quote) for quote in statement["spanish"]) == 34 * first + 33 * second - 4
        return translation.check({"plan": [statement]}, [], transcript(text)).fields["plan"].reasons

    assert "too_long" not in cited(6, 6)  # 398 characters
    assert "too_long" in cited(6, 7)  # 431


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
    # "Al" is "a el": the father told is not the father having it, as "a la madre" isn't.
    ("Se informa al padre del plan.", "The father is informed of the plan.", []),
    ("Se explica al abuelo la dieta.", "The diet is explained to the grandfather.", []),
    ("El padre tiene diabetes.", "Diabetes is reported to the father.", ["experiencer_dropped"]),
])
def test_a_relative_told_something_has_nothing(text, english, expected):
    assert reasons(text, [text], english) == expected


# ---- Drugs, failing closed both ways --------------------------------------------------------------------------------


@pytest.mark.parametrize(("text", "english", "expected"), [
    # A drug the lists know only as a word of a name given in several, swapped in for the one said, and the right ones.
    ("Continuar levetiracetam 500 mg cada 12 horas.", "Continue valproic acid 500 mg every 12 hours.",
     ["drug_not_in_sources", "drug_dropped"]),
    ("Ácido valproico 500 mg cada 12 horas.", "Valproic acid 500 mg every 12 hours.", []),
    ("Insulina glargina 10 unidades por la noche.", "Insulin glargine 10 units at night.", []),
    # A drug no list knows at all, swapped in: the one said is gone from the English.
    ("Continuar levetiracetam.", "Continue zorbitex.", ["drug_dropped", "action_changed"]),
    # Every drug the Spanish names must be in the English, not just one of them.
    ("Continuar metformina y losartán.", "Continue metformin.", ["drug_dropped"]),
    # A word just before a dose is a drug's name, whether a list knows it or not.
    ("Continuar levetiracetam 500 mg.", "Continue zorbitex 500 mg.", ["drug_not_in_sources", "drug_dropped"]),
    ("Usa Plixor 2 inhalaciones al día.", "Uses kraxin 2 puffs daily.", ["drug_not_in_sources"]),
    # Brand names: one no list knows swapped for another before a dose, and a listed one swapped for another.
    ("Toma Plixor 20 mg al día.", "Takes Vortanex 20 mg daily.", ["drug_not_in_sources"]),
    ("Toma Zyrtec 10 mg al día.", "Takes Claritin 10 mg daily.", ["drug_not_in_sources", "drug_dropped"]),
    ("Toma Plixor 20 mg al día.", "Takes Plixor 20 mg daily.", []),
    ("Toma Zyrtec 10 mg al día.", "Takes Zyrtec 10 mg daily.", []),
    # What comes before a dose and isn't a drug.
    ("Tomar 2 comprimidos de paracetamol.", "Take 2 tablets of acetaminophen.", []),
    ("Dieta con sodio 2 g al día.", "Diet with sodium 2 g a day.", []),
    ("Salbutamol inhalado 2 inhalaciones cada 6 horas.", "Albuterol inhaled 2 puffs every 6 hours.", []),
])
def test_drugs_fail_closed_both_ways(text, english, expected):
    assert reasons(text, [text], english) == expected


def test_what_the_drug_checks_still_miss():
    # A name no list knows, with no dose just after it, for one no list knows either (translation.py's header).
    assert reasons("Continuar plixor.", ["Continuar plixor."], "Continue vortanex.") == []


# ---- A passage cited once -------------------------------------------------------------------------------------------


def test_a_passage_cited_again_in_another_field_discards_that_field_and_keeps_the_first():
    text = "Mujer de 40 años con migraña. Control en 2 semanas."
    statement = {"spanish": ["Mujer de 40 años con migraña."], "english": "40-year-old woman with migraine."}
    checks = translation.check({"subjective": [statement], "assessment": [statement]}, [], transcript(text))
    assert checks.fields["subjective"].pending
    assert checks.fields["assessment"].reasons == ["source_repeated"]


def test_the_answers_order_decides_which_citation_is_the_repeat():
    # The flat answer said Assessment first: Subjective, later in the answer, is the one discarded.
    text = "Mujer de 40 años con migraña. Control en 2 semanas."
    quote = ["Mujer de 40 años con migraña."]
    checks = translation.check({
        "subjective": [{"spanish": quote, "english": "40-year-old woman with migraine.", "order": 1}],
        "assessment": [{"spanish": quote, "english": "40-year-old woman with migraine.", "order": 0}],
    }, [], transcript(text))
    assert checks.fields["assessment"].pending and checks.fields["subjective"].reasons == ["source_repeated"]


def test_a_statement_citing_part_of_an_earlier_ones_passage_is_a_repeat_in_one_field_too():
    text = "Cefalea desde hace 3 días, sin fiebre."
    checks = translation.check({"subjective": [
        {"spanish": ["Cefalea desde hace 3 días, sin fiebre."], "english": "Headache for 3 days, no fever."},
        {"spanish": ["sin fiebre"], "english": "No fever."},
    ]}, [], transcript(text))
    assert checks.fields["subjective"].reasons == ["source_repeated"]


def test_the_same_words_said_twice_may_each_be_cited_once():
    text = "Paracetamol 1 g cada 8 horas. Ibuprofeno 400 mg cada 8 horas."
    checks = translation.check({"subjective": [
        {"spanish": ["Paracetamol 1 g", "cada 8 horas"], "english": "Acetaminophen 1 g every 8 hours."},
    ], "plan": [
        {"spanish": ["Ibuprofeno 400 mg", "cada 8 horas"], "english": "Ibuprofen 400 mg every 8 hours."},
    ]}, [], transcript(text))
    assert checks.fields["subjective"].pending and checks.fields["plan"].pending


# ---- What the English is written in, and a quote cut short (sentences outside the test set) --------------------------


@pytest.mark.parametrize(("text", "english"), [
    # Another script, which the transcript never uses (run A2 wrote "咳嗽" for "cough").
    ("Tiene tos seca por las noches.", "Has a dry 咳嗽 at night."),
    # An Italian word, with a letter the transcript never uses.
    ("Es independiente para las actividades de la vida diaria.", "Independent for attività of daily living."),
    # The same word where the transcript uses its letter elsewhere: it still isn't a word the transcript says.
    ("Es independiente para las actividades de la vida diaria, dice à veces.",
     "Independent for attività of daily living."),
])
def test_a_word_written_in_letters_english_doesnt_use_is_bad_format(text, english):
    assert reasons(text, [text], english) == ["bad_format"]


def test_an_eponym_the_transcript_says_keeps_its_accent():
    text = "Signo de Lasègue positivo en la pierna derecha."
    assert reasons(text, [text], "Positive Lasègue sign in the right leg.") == []


def test_the_english_may_write_micrograms_as_the_spanish_does():
    assert reasons("Levotiroxina 50 μg al día.", ["Levotiroxina 50 μg al día."], "Levothyroxine 50 μg daily.") == []


def test_what_the_letter_check_still_misses():
    # A word the transcript says passes, accent or not: a Spanish word left untranslated. So does a word written in
    # English's letters whatever its language: an Italian one without its accent, or no word at all. The review shows
    # the Spanish beside it.
    assert reasons("Dolor en la rodilla.", ["Dolor en la rodilla."], "Pain in the rodilla.") == []
    assert reasons("Dolor en la región lumbar.", ["Dolor en la región lumbar."], "Pain in the región lumbar.") == []
    text = "Es independiente para las actividades de la vida diaria."
    assert reasons(text, [text], "Independent for attivita of daily living.") == []
    assert reasons(text, [text], "Independent for activitudes of daily living.") == []


# The most the Spanish schema lets one quote hold: a longer passage reaches the checks cut at that many characters.
QUOTE_LIMIT = prompt.load(language="es").schema["properties"]["statements"]["items"]["properties"]["spanish"]["items"][
    "maxLength"]
LONG = ("Refiere que se cansa bastante más al caminar por las tardes, le cuesta mucho subir las escaleras de su casa y "
        "tiene que detenerse a descansar a mitad de camino, aunque niega dolor torácico y niega disnea en reposo.")
LONG_ENGLISH = ("Gets much more tired walking in the afternoon, finds it very hard to climb the stairs at home and has to "
                "stop to rest halfway, though denies chest pain and denies dyspnea at rest.")


def test_a_quote_cut_inside_a_word_at_the_schemas_limit_is_not_found():
    cut = LONG[:QUOTE_LIMIT]
    assert cut.endswith(" niega dis")  # "disnea" cut short: never matched as the start of a longer word
    assert reasons(LONG, [cut], LONG_ENGLISH) == ["source_not_in_transcript"]
    assert reasons("Niega disnea.", ["Niega dis"], "Denies dyspnea.") == ["source_not_in_transcript"]


def test_a_quote_ending_between_words_is_found():
    assert reasons(LONG, [LONG[:QUOTE_LIMIT].rsplit(" ", 1)[0]], LONG_ENGLISH) == []
    assert reasons(LONG, [LONG], LONG_ENGLISH) == []


# ---- A doubt, said before what it doubts or after it (sentences outside the test set) -------------------------------


DOUBTS = [
    ("No creo que tenga neumonía.", "I don't think she has pneumonia.", ["negation_dropped"]),
    ("Creo que tiene neumonía.", "I think she has pneumonia.", ["certainty_dropped"]),
    ("Quizá tiene neumonía.", "Maybe she has pneumonia.", ["certainty_dropped"]),
    ("Quizás tiene neumonía.", "Perhaps she has pneumonia.", ["certainty_dropped"]),
    ("Tal vez tiene neumonía.", "Maybe she has pneumonia.", ["certainty_dropped"]),
    ("A lo mejor tiene neumonía.", "Maybe she has pneumonia.", ["certainty_dropped"]),
    ("Puede que tenga neumonía.", "She may have pneumonia.", ["certainty_dropped"]),
    ("Podría tener neumonía.", "She might have pneumonia.", ["certainty_dropped"]),
    ("Tiene neumonía, creo.", "She has pneumonia, I think.", ["certainty_dropped"]),
    ("Tiene neumonía, probablemente.", "She has pneumonia, probably.", ["certainty_dropped"]),
    ("Tiene neumonía, probablemente.", "She probably has pneumonia.", ["certainty_dropped"]),
]


@pytest.mark.parametrize(("text", "english", "dropped"), DOUBTS)
def test_a_doubt_said_in_spanish_is_kept_and_never_dropped(text, english, dropped):
    assert reasons(text, [text], english) == []
    assert reasons(text, [text], "She has pneumonia.") == dropped


@pytest.mark.parametrize(("text", "english", "dropped"), DOUBTS)
def test_a_doubt_the_spanish_doesnt_say_is_never_added(text, english, dropped):
    assert reasons("Tiene neumonía.", ["Tiene neumonía."], english) == [r.replace("dropped", "added") for r in dropped]


def test_a_word_of_doubt_inside_a_clause_doesnt_close_it():
    # "creo" before more of its clause is no closing doubt, and "podría" before something to do is no doubt about
    # what the patient has.
    assert reasons("Tiene neumonía, creo que es leve.", ["Tiene neumonía, creo que es leve."],
                   "She has pneumonia, I think it is mild.") == []
    assert reasons("Podría iniciar amoxicilina.", ["Podría iniciar amoxicilina."], "Could start amoxicillin.") == []
