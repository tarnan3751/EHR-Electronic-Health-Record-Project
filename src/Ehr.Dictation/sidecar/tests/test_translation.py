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


# A point or a comma before exactly three digits (sentences outside the test set): a thousand times apart.
@pytest.mark.parametrize(("text", "english"), [
    ("Heparina 5.000 unidades.", "Heparin 5 units."),
    ("Heparina 5.000 unidades.", "Heparin 5000 units."),
    ("Heparina 5.000 unidades.", "Heparin."),
    ("Metformina 1.000 mg al día.", "Metformin 1000 mg daily."),
    ("Metformina 1.000 mg al día.", "Metformin 1 mg daily."),
    ("Warfarina 2,500 mg.", "Warfarin 2.5 mg."),
])
def test_a_number_that_reads_two_ways_is_refused(text, english):
    field = one(text, [text], english)
    assert not field.pending and translation.AMBIGUOUS_NUMBER in field.reasons


@pytest.mark.parametrize(("text", "english"), [
    ("Metformina 1000 mg al día.", "Metformin 1000 mg daily."),
    ("Metformina mil miligramos al día.", "Metformin 1000 mg daily."),
    ("Temperatura 37,8.", "Temperature 37.8."),
    ("Warfarina 2,5 mg.", "Warfarin 2.5 mg."),
    ("Heparina 5.000.000 unidades.", "Heparin 5000000 units."),
])
def test_a_number_that_reads_one_way_is_offered(text, english):
    assert one(text, [text], english).pending


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
    # The schema allows two quotes of 200; the on-prem app refuses a sentence citing more (TrustedResult). A quote is
    # found ignoring punctuation, so the transcript's words it cites can be longer than the quote.
    def cited(mark: str) -> list[str]:
        text = ("Refiere cansancio por las tardes" + mark) * 5 + "fin. " + ("Niega dolor torácico al caminar" + mark) * 5
        statement = {"spanish": [(("Refiere cansancio por las tardes ") * 5).strip(),
                                 (("Niega dolor torácico al caminar ") * 5).strip()],
                     "english": "Tired in the afternoons, denies chest pain walking."}
        assert all(len(quote) <= translation.MAX_QUOTE for quote in statement["spanish"])
        return translation.check({"plan": [statement]}, [], transcript(text)).fields["plan"].reasons

    assert "too_long" not in cited(" ")  # 323 characters cited
    assert "too_long" in cited(" ... ... ... ")  # 413


@pytest.mark.parametrize(("quotes", "english"), [
    (["Tos productiva", "desde ayer", "sin fiebre"], "Productive cough since yesterday without fever."),  # three quotes
    (["Tos productiva " + "con flemas " * 17 + "desde ayer"], "Productive cough since yesterday."),  # 201 characters
    (["Tos productiva desde ayer"], "Productive cough since yesterday" + " with phlegm" * 18 + "."),  # 247 characters
    # 240 characters with no full stop: the grammar cut it there, so it isn't a whole sentence.
    (["Tos productiva desde ayer"], ("Productive cough since yesterday" + " with phlegm" * 18)[:240]),
])
def test_a_statement_past_the_schemas_limits_or_cut_at_them_is_too_long(quotes, english):
    text = "Tos productiva " + "con flemas " * 17 + "desde ayer y sin fiebre."
    assert "too_long" in reasons(text, quotes, english)


def test_the_checks_limits_are_the_spanish_schemas():
    assert (translation.MAX_QUOTES, translation.MAX_QUOTE, translation.MAX_ENGLISH) == (2, 200, 240)


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
    assert reasons(LONG, [LONG[:QUOTE_LIMIT].rsplit(" ", 1)[0], LONG[QUOTE_LIMIT - 9 :]], LONG_ENGLISH) == []


# ---- A doubt, said before what it doubts or after it (sentences outside the test set) -------------------------------


DOUBTS = [
    ("No creo que tenga neumonía.", "I don't think she has pneumonia.", ["negation_dropped", "certainty_dropped"]),
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


# What validation.py reads as doubt in English, the Spanish reads in its words: "I doubt" is a negation as well.
DOUBT_WORDS = [
    ("Dudo que tenga faringitis.", "I doubt she has pharyngitis.", ["negation_dropped", "certainty_dropped"]),
    ("Le preocupa que tenga neumonía.", "She is worried she has pneumonia.", ["certainty_dropped"]),
    ("Me pregunto si tiene neumonía.", "I wonder if she has pneumonia.", ["certainty_dropped"]),
    ("Posibilidad de neumonía.", "A chance of pneumonia.", ["certainty_dropped"]),
    ("Presuntamente tiene neumonía.", "Presumably she has pneumonia.", ["certainty_dropped"]),
]


@pytest.mark.parametrize(("text", "english", "dropped"), DOUBT_WORDS)
def test_a_doubt_word_english_reads_is_read_in_spanish_both_ways(text, english, dropped):
    plain = "Tiene faringitis." if "faringitis" in text else "Tiene neumonía."
    assert reasons(text, [text], english) == []
    assert reasons(text, [text], "She has pharyngitis." if "faringitis" in text else "She has pneumonia.") == dropped
    assert reasons(plain, [plain], english) == [r.replace("dropped", "added") for r in dropped]


def test_a_word_of_doubt_inside_a_clause_doesnt_close_it():
    # "creo" before more of its clause is no closing doubt, and "podría" before something to do is no doubt about
    # what the patient has.
    assert reasons("Tiene neumonía, creo que es leve.", ["Tiene neumonía, creo que es leve."],
                   "She has pneumonia, I think it is mild.") == []
    assert reasons("Podría iniciar amoxicilina.", ["Podría iniciar amoxicilina."], "Could start amoxicillin.") == []


# ---- Halves, quarters and thirds (sentences outside the test set) ---------------------------------------------------


@pytest.mark.parametrize(("text", "english"), [
    ("Paracetamol un gramo y medio.", "Acetaminophen 1.5 g."),
    ("Una tableta y media al día.", "One and a half tablets a day."),
    ("Un cuarto de tableta al día.", "A quarter tablet a day."),
    ("Tres cuartos de tableta al día.", "Three quarters of a tablet a day."),
    ("Tomar 1/2 tableta al día.", "Take 1/2 tablet a day."),
    ("Tomar 1/2 tableta al día.", "Take half a tablet a day."),
    ("Tomar la mitad de la dosis.", "Take half the dose."),
    ("TA 120/80.", "BP 120/80."),
])
def test_a_half_or_a_quarter_is_read_as_its_decimal(text, english):
    assert reasons(text, [text], english) == []


@pytest.mark.parametrize(("text", "english"), [
    ("Paracetamol un gramo y medio.", "Acetaminophen 1 g."),
    ("Una tableta y media al día.", "One tablet a day."),
    ("Un cuarto de tableta al día.", "Four tablets a day."),
    ("Tres cuartos de tableta al día.", "Three tablets a day."),
    ("Tomar 1/2 tableta al día.", "Take 2 tablets a day."),
    ("Tomar 1/2 tableta al día.", "Take 1 tablet a day."),
])
def test_a_half_or_a_quarter_misread_is_refused(text, english):
    assert {"number_not_in_sources", "unit_not_in_sources"} & set(reasons(text, [text], english))


@pytest.mark.parametrize(("text", "english"), [
    ("Un tercio de tableta al día.", "A third of a tablet a day."),
    ("Tomar 1/3 de tableta al día.", "Take 0.33 tablet a day."),
    ("Una tableta al día.", "Two thirds of a tablet a day."),
])
def test_a_fraction_with_no_exact_decimal_is_refused(text, english):
    assert "number_unreadable" in reasons(text, [text], english)


# ---- In order, and paired (sentences outside the test set) ----------------------------------------------------------

SWAPS = [
    ("Losartán 50 mg en la mañana y amlodipino 10 mg en la noche.",
     "Losartan 10 mg in the morning and amlodipine 50 mg at night.",
     "Losartan 50 mg in the morning and amlodipine 10 mg at night."),
    ("TA 142 sobre 88.", "BP 88/142.", "BP 142/88."),
    ("Glucosa 140, TA 120 sobre 80.", "Glucose 120, BP 140/80.", "Glucose 140, BP 120/80."),
    ("Potasio 5,8, sodio 135.", "Potassium 135, sodium 5.8.", "Potassium 5.8, sodium 135."),
    ("Dolor en rodilla derecha y edema en tobillo izquierdo.", "Left knee pain and right ankle edema.",
     "Right knee pain and left ankle edema."),
    ("Amoxicilina oral y ceftriaxona intramuscular.", "Amoxicillin intramuscular and ceftriaxone oral.",
     "Oral amoxicillin and intramuscular ceftriaxone."),
    ("Dolor leve en la rodilla e intenso en la cadera.", "Severe knee pain and mild hip pain.",
     "Mild knee pain and severe hip pain."),
    ("Madre con diabetes y padre con hipertensión.", "Mother with hypertension and father with diabetes.",
     "Mother with diabetes and father with hypertension."),
    ("Posible neumonía, asma conocida.", "Pneumonia, possible asthma.", "Possible pneumonia, known asthma."),
    ("Fiebre de 39 desde hace 2 días.", "Fever of 2 for 39 days.", "Fever of 39 for 2 days."),
]


@pytest.mark.parametrize(("text", "swapped", "kept"), SWAPS)
def test_a_class_swapped_is_refused_and_kept_in_order_is_offered(text, swapped, kept):
    assert "order_changed" in reasons(text, [text], swapped)
    assert reasons(text, [text], kept) == []


@pytest.mark.parametrize(("text", "english"), [
    ("Glucosa de 140.", "Blood pressure 140."),
    ("Glucosa de 140.", "Reading of 140."),  # a word no list knows beside a number the Spanish labels
    ("FC 78, FR 18.", "RR 78, HR 18."),
    ("Losartán 50 mg y amlodipino 10 mg.", "Losartan 10 mg and amlodipine."),  # the dose moved to the other drug
])
def test_a_number_given_another_label_is_refused(text, english):
    assert reasons(text, [text], english) == ["label_changed"]


@pytest.mark.parametrize(("text", "english"), [
    ("Glucosa de 140.", "Glucose 140."),
    ("Glucosa de 140.", "140."),
    ("Glucosa en ayunas de 110.", "Fasting glucose 110."),
    ("La A1c de la última vez fue 6,1.", "The last A1c was 6.1."),
    ("Toma 50 mg de losartán al día.", "Takes losartan 50 mg daily."),
    ("Aumentar lisinopril de 10 a 20 mg.", "Increase lisinopril from 10 to 20 mg."),
    ("Evitar cargar peso durante 2 semanas.", "Avoid lifting loads for 2 weeks."),  # a period measures nothing
    ("Tos y fiebre, sin disnea.", "Cough and fever, no dyspnea."),
    ("Mujer sana con antecedente familiar de cáncer de colon.", "Healthy woman, family history of colon cancer."),
])
def test_a_number_with_its_label_and_a_qualifier_on_its_clause_are_offered(text, english):
    assert reasons(text, [text], english) == []


def test_a_frequency_read_two_ways_may_be_written_both_ways_in_its_place():
    text = "Warfarina 5 mg al día excepto 2,5 mg cada tercer día."
    assert reasons(text, [text], "Warfarin 5 mg daily except 2.5 mg every other day or every third day.") == []
    text = "Losartán en la mañana y amlodipino en la noche."
    assert reasons(text, [text], "Losartan at night and amlodipine in the morning.") == ["order_changed"]


def test_another_order_is_refused_and_what_isnt_spelled_alike_still_passes():
    # Fail closed: a reordering can't be told from a swap.
    text = "Amlodipino y losartán."
    assert reasons(text, [text], "Losartan and amlodipine.") == ["order_changed"]
    # Known gaps: a finding the two languages spell differently, moved to another severity or qualifier.
    for text, english in [("Dolor leve en la rodilla e intenso en la cadera.", "Mild hip pain and severe knee pain."),
                          ("Posible gripe, tos conocida.", "Flu, possible cough.")]:
        assert reasons(text, [text], english) == []


# ---- How long (sentences outside the test set) ----------------------------------------------------------------------


@pytest.mark.parametrize(("text", "english"), [
    ("Amoxicilina por cinco días.", "Amoxicillin for 5 months."),
    ("Control en dos semanas.", "Follow-up in 2 months."),
    ("Tos desde hace tres días.", "Cough for 3 weeks."),
    ("Tos desde hace una semana.", "Cough for a month."),
    ("Ayuno de 8 horas.", "Fast for 8 days."),
])
def test_a_length_of_time_with_another_period_is_refused(text, english):
    assert reasons(text, [text], english) == ["period_not_in_sources"]


@pytest.mark.parametrize(("text", "english"), [
    ("Amoxicilina por cinco días.", "Amoxicillin for 5 days."),
    ("Control en dos semanas.", "Follow-up in 2 weeks."),
    ("Tos desde hace una semana.", "Cough for a week."),
    ("Tos desde hace una semana.", "Cough since a week ago."),
    ("Metformina 850 mg dos veces al día.", "Metformin 850 mg twice a day."),  # "a day" is how often, not how long
    ("Control en 2 semanas y análisis en 3 meses.", "Follow-up in 2 weeks and tests in 3 months."),
])
def test_a_length_of_time_said_as_the_spanish_says_it_is_offered(text, english):
    assert reasons(text, [text], english) == []


def test_lengths_of_time_swapped_are_refused():
    text = "Control en 2 semanas y análisis en 3 meses."
    assert reasons(text, [text], "Follow-up in 3 months and tests in 2 weeks.") == ["order_changed"]


# ---- What is done with each drug, dose and habit (sentences outside the test set) -----------------------------------


@pytest.mark.parametrize(("text", "changed", "kept"), [
    ("Metformina: suspender.", "Metformin: continue.", "Metformin: stop."),
    ("Metformina se suspende.", "Metformin is continued.", "Metformin is stopped."),
    ("Suspendió la metformina hace un mes.", "Takes metformin; started a month ago.",
     "Stopped metformin a month ago."),
    ("Dejó de tomar metformina.", "Takes metformin.", "Stopped taking metformin."),
    ("Se le suspendió la metformina.", "Metformin was started.", "Metformin was stopped."),
    ("Dejó de fumar hace 10 años.", "Smoking for 10 years.", "Quit smoking 10 years ago."),
    ("Fuma desde hace 10 años.", "Quit smoking 10 years ago.", "Has smoked for 10 years."),
    ("Toma metformina.", "Metformin was stopped.", "Takes metformin."),
    ("Suspender metformina e iniciar insulina.", "Start metformin and stop insulin.",
     "Stop metformin and start insulin."),
    ("Suspendió la metformina hace un mes.", "Stop metformin.", "Metformin was stopped a month ago."),
    ("Tomaba metformina.", "Takes metformin.", "Used to take metformin."),
    ("Antecedente de tabaquismo, exfumador desde hace 5 años.", "History of smoking, smoker for 5 years.",
     "History of smoking, former smoker for 5 years."),
])
def test_what_is_done_with_a_drug_or_a_habit_is_kept(text, changed, kept):
    assert reasons(text, [text], changed) == ["action_changed"]
    assert reasons(text, [text], kept) == []


@pytest.mark.parametrize(("text", "english"), [
    ("Suspender metformina y glibenclamida.", "Stop metformin and glyburide."),  # one verb for a list
    ("Aumentar la dosis de losartán.", "Increase the losartan dose."),
    ("Para la tensión toma amlodipino.", "For blood pressure takes amlodipine."),  # "para" is "for" here
    ("Dosis baja de aspirina.", "Low-dose aspirin."),  # "baja" is low here
    ("Bajó la dosis de metformina.", "Lowered the metformin dose."),
    ("Toma metformina.", "On metformin."),
])
def test_a_drug_taken_or_changed_as_the_spanish_says_is_offered(text, english):
    assert reasons(text, [text], english) == []


# ---- A qualifier over a list, and a passage quoting its tail (sentences outside the test set) ------------------------

LIST = "Niega fiebre, tos, disnea, dolor torácico y síncope."
LONG_LIST = "Niega dolor torácico, disnea, palpitaciones, mareo, síncope y edema."


@pytest.mark.parametrize(("text", "quote", "english", "expected"), [
    (LIST, "tos, disnea, dolor torácico y síncope", "Cough, dyspnea, chest pain and syncope.", ["negation_dropped"]),
    (LIST, "tos, disnea, dolor torácico y síncope", "No cough, dyspnea, chest pain or syncope.", []),
    (LIST, LIST, "Denies fever. Cough, dyspnea, chest pain and syncope.", ["order_changed"]),
    (LIST, LIST, "Denies fever, cough, dyspnea, chest pain and syncope.", []),
    (LONG_LIST, "síncope y edema", "Syncope and edema.", ["negation_dropped"]),  # past the six words a cue reaches
    (LONG_LIST, "síncope y edema", "No syncope or edema.", []),
    ("Sin edema, cianosis.", "Sin edema, cianosis.", "No edema; cyanosis present.", ["order_changed"]),
    ("Sin edema, cianosis.", "Sin edema, cianosis.", "No edema or cyanosis.", []),
])
def test_a_negation_carries_over_its_list_and_into_a_quote_of_its_tail(text, quote, english, expected):
    assert reasons(text, [quote], english) == expected


@pytest.mark.parametrize(("text", "quote", "english"), [
    ("Niega fiebre, refiere tos.", "refiere tos", "Reports cough."),  # an item that says something of its own
    ("Niega fiebre, refiere tos.", "Niega fiebre, refiere tos.", "Denies fever, reports cough."),
    ("Sin fiebre, la tos ha mejorado.", "Sin fiebre, la tos ha mejorado.", "No fever, the cough has improved."),
])
def test_a_negation_stops_at_an_item_that_says_something_of_its_own(text, quote, english):
    assert reasons(text, [quote], english) == []


# ---- Cues the Spanish says that were missed, and step 15's English cues read in Spanish (sentences outside the test
# set) -------------------------------------------------------------------------------------------------------------

CUES = [
    ("Se descartó neumonía.", "Pneumonia confirmed.", "Pneumonia was ruled out.", ["negation_dropped"]),
    ("Imagen compatible con neumonía.", "Image showing pneumonia.", "Image consistent with pneumonia.",
     ["certainty_dropped"]),
    ("Hallazgos sugestivos de apendicitis.", "Findings of appendicitis.", "Findings suggestive of appendicitis.",
     ["certainty_dropped"]),
    ("Infarto hace años.", "Acute infarction now.", "Infarction years ago.", ["history_dropped"]),
    ("Tuvo un infarto.", "Is having an infarction.", "Had an infarction.", ["history_dropped"]),
    ("Neumonía el año pasado.", "Pneumonia.", "Pneumonia last year.", ["history_dropped"]),
    ("Infarto antiguo.", "Infarction.", "Old infarction.", ["history_dropped"]),
    ("Hay diabetes en la familia.", "Has diabetes.", "Diabetes in the family.", ["experiencer_dropped"]),
    ("¿Tiene fiebre?", "Has a fever.", "Does she have a fever?", ["certainty_dropped"]),
    ("Tiene neumonía probablemente.", "Has pneumonia.", "Has pneumonia, probably.", ["certainty_dropped"]),
    ("No creo que la paciente tenga neumonía.", "The patient has pneumonia.",
     "I don't think the patient has pneumonia.", ["negation_dropped", "certainty_dropped"]),
    ("No estoy seguro de que tenga neumonía.", "She has pneumonia.", "I'm not sure she has pneumonia.",
     ["negation_dropped", "certainty_dropped"]),
    ("No se puede descartar apendicitis.", "Appendicitis.", "Cannot rule out appendicitis.",
     ["negation_dropped", "certainty_dropped"]),
]


@pytest.mark.parametrize(("text", "plain", "kept", "dropped"), CUES)
def test_a_cue_the_spanish_says_is_kept_and_never_dropped(text, plain, kept, dropped):
    assert reasons(text, [text], plain) == dropped
    assert reasons(text, [text], kept) == []


@pytest.mark.parametrize(("english", "added"), [
    ("Old myocardial infarction.", "history_added"),
    ("Remote myocardial infarction.", "history_added"),
    ("Myocardial infarction in the past.", "history_added"),
    ("Prior myocardial infarction.", "history_added"),
    ("Findings consistent with myocardial infarction.", "certainty_added"),
    ("Myocardial infarction in the family.", "experiencer_added"),
])
def test_an_english_cue_the_spanish_doesnt_say_is_never_added(english, added):
    assert added in reasons("Infarto de miocardio.", ["Infarto de miocardio."], english)


@pytest.mark.parametrize(("text", "english"), [
    ("Caída hace 3 días.", "Fall in the past 3 days."),  # a period, not the past
    ("Dejó de fumar hace 10 años.", "Quit smoking 10 years ago."),  # a date, as "hace 10 años" is
    ("Mujer de 68 años.", "68-year-old woman."),
    ("PHQ-9 de 12, previamente 18.", "PHQ-9 of 12, previously 18."),  # a cue that reaches no finding
    ("Ha tenido tos desde hace 3 días.", "Has had a cough for 3 days."),  # goes on
])
def test_what_only_looks_like_a_cue_isnt_one(text, english):
    assert reasons(text, [text], english) == []


# ---- Instructions: what reaches the note, and what is listed (sentences outside the test set) ----------------------


@pytest.mark.parametrize(("said", "english", "kind"), [
    ("Sácale una placa de tórax.", "Chest X-ray.", "order"),
    ("Quítale la alergia a la penicilina.", "Penicillin allergy.", "allergy_change"),
])
def test_an_everyday_instruction_never_reaches_the_note_and_is_listed(said, english, kind):
    text = "Continuar losartán 50 mg al día. " + said
    checks = translation.check({"plan": [
        {"spanish": ["Continuar losartán 50 mg al día"], "english": "Continue losartan 50 mg daily."},
        {"spanish": [said.rstrip(".")], "english": english},
    ]}, [], transcript(text))
    assert checks.fields["plan"].value == "Continue losartan 50 mg daily."
    assert translation.not_carried_over(transcript(text)) == [
        {"reason": kind, "start": text.index(said), "end": len(text) - 1}]


def test_an_instruction_said_in_english_in_a_spanish_dictation_is_cut_and_listed():
    text = "Continuar metformina. Order a CBC."
    checks = translation.check({"plan": [
        {"spanish": ["Continuar metformina."], "english": "Continue metformin."},
        {"spanish": ["Order a CBC"], "english": "Order a CBC."},
    ]}, [], transcript(text))
    assert checks.fields["plan"].value == "Continue metformin."
    assert translation.not_carried_over(transcript(text)) == [
        {"reason": "order", "start": text.index("Order"), "end": len(text) - 1}]


def test_what_the_english_backstop_cuts_is_listed_with_the_spanish_it_cites():
    # A plan the Spanish detector doesn't read as an instruction, translated as one: the backstop cuts the English, and
    # the Spanish is listed, so the clinician sees it.
    text = "Continuar losartán 50 mg al día y control con perfil de lípidos."
    checks = translation.check({"plan": [
        {"spanish": ["Continuar losartán 50 mg al día y control con perfil de lípidos"],
         "english": "Continue losartan 50 mg daily and order a lipid panel."},
    ]}, [], transcript(text))
    assert checks.fields["plan"].value == "Continue losartan 50 mg daily."
    assert translation.not_carried_over(transcript(text)) == []
    assert translation.not_carried_over(transcript(text), checks) == [
        {"reason": "order", "start": 0, "end": len(text) - 1}]


# ---- What can be ordered, in English (sentences outside the test set) ---------------------------------------------


@pytest.mark.parametrize(("text", "quote", "english"), [
    ("Continuar losartán 50 mg al día y pido una radiografía de tórax.", "Continuar losartán 50 mg al día",
     "Continue losartan 50 mg daily; chest X-ray."),
    ("Control en dos semanas. Pido una biometría hemática.", "Control en dos semanas",
     "Follow-up in 2 weeks with blood count."),
    ("Control en dos semanas.", "Control en dos semanas", "Follow-up in 2 weeks with labs."),
    ("Continuar metformina.", "Continuar metformina.", "Continue metformin; urine culture pending."),
])
def test_something_to_order_the_cited_spanish_doesnt_say_is_refused(text, quote, english):
    assert "orderable_not_in_sources" in reasons(text, [quote], english)


@pytest.mark.parametrize(("text", "english"), [
    ("La radiografía de tórax de hoy es compatible con congestión pulmonar.",
     "Today's chest X-ray is consistent with pulmonary congestion."),
    ("Analítica del 3 de marzo de 2026: TSH 6,2.", "Labs from March 3, 2026: TSH 6.2."),
    ("El último ecocardiograma muestra una FEVI de 35 %.", "The last echocardiogram shows an LVEF of 35%."),
])
def test_something_ordered_the_spanish_says_is_offered(text, english):
    assert reasons(text, [text], english) == []


# ---- What the English side doesn't know fails closed (sentences outside the test set) -----------------------------


@pytest.mark.parametrize(("text", "english", "reason"), [
    ("Pesa 70 kilos.", "Weighs 70 lb.", "unit_not_in_sources"),
    ("Glucosa de 250 mg/dl.", "Glucose 250 mmol/L.", "unit_not_in_sources"),
    ("Temperatura de 38 grados.", "Temperature 38 °F.", "unit_not_in_sources"),
    ("Tomar en ayunas.", "Take after breakfast.", "frequency_not_in_sources"),
    ("Paracetamol cada 8 horas.", "Acetaminophen every 8 minutes.", "frequency_not_in_sources"),
    ("Metformina dos veces al día.", "Metformin twice.", "frequency_not_in_sources"),
    ("Dolor en rodilla izquierda.", "R knee pain.", "side_not_in_sources"),
    ("Dolor en rodilla izquierda.", "Rt knee pain.", "side_not_in_sources"),
    ("Ceftriaxona intramuscular.", "Ceftriaxone intrathecal.", "route_not_in_sources"),
    ("Ceftriaxona intramuscular.", "Ceftriaxone IV push.", "route_not_in_sources"),
    ("Salbutamol inhalado.", "Albuterol tablets.", "dose_form_not_in_sources"),
    ("Prueba negativa.", "hiv negative test.", "abbreviation_not_in_sources"),
    ("Evitar el ibuprofeno.", "Avoid nsaids and ibuprofen.", "abbreviation_not_in_sources"),
])
def test_what_the_english_says_in_a_way_the_checks_cant_compare_is_refused(text, english, reason):
    assert reason in reasons(text, [text], english)


@pytest.mark.parametrize(("text", "english"), [
    ("Pesa 70 kilos.", "Weighs 70 kg."),
    ("Glucosa en ayunas de 162 mg/dl.", "Fasting glucose 162 mg/dL."),
    ("Tomar en ayunas.", "Take fasting."),
    ("Metformina dos veces al día.", "Metformin twice a day."),
    ("Dolor en rodilla izquierda.", "L knee pain."),
    ("Salbutamol inhalado.", "Albuterol inhaler."),
    ("VIH positivo.", "hiv positive."),
    ("Evitar antiinflamatorios.", "Avoid nsaids."),
    ("Suero 2 litros.", "Saline 2 L."),  # a liter, not the left side
])
def test_what_the_english_says_as_the_spanish_does_is_offered(text, english):
    assert reasons(text, [text], english) == []


# ---- Opposites, and findings the allowance mustn't add (sentences outside the test set) -----------------------------


@pytest.mark.parametrize(("text", "english", "reason"), [
    ("Paciente tranquila, sin cambios.", "Calm patient, no changes, suicidal ideation.", "finding_not_in_sources"),
    ("Tos seca de una semana.", "Dry cough for a week, cancer diagnosed.", "finding_not_in_sources"),
    ("Dolor abdominal leve.", "Mild abdominal pain, pregnancy confirmed.", "finding_not_in_sources"),
    ("Disnea en reposo.", "Dyspnea at rest, intubate now.", "finding_not_in_sources"),
    ("Exantema en el tronco.", "Rash on the trunk, sulfa allergy.", "polarity_changed"),
    ("Hipotensión ortostática.", "Orthostatic hypertension.", "polarity_changed"),
    ("Taquicardia sinusal.", "Sinus bradycardia.", "polarity_changed"),
    ("Glucosa baja esta mañana.", "High glucose this morning.", "polarity_changed"),
    ("La tos está peor.", "The cough is better.", "polarity_changed"),
    ("Diabetes mal controlada.", "Controlled diabetes.", "polarity_changed"),
    ("Tolera la penicilina.", "Allergic to penicillin.", "polarity_changed"),
])
def test_an_opposite_or_a_finding_the_spanish_doesnt_say_is_refused(text, english, reason):
    assert reason in reasons(text, [text], english)


@pytest.mark.parametrize(("text", "english"), [
    ("Glucosa alta esta mañana.", "High glucose this morning."),
    ("Hipotensión ortostática.", "Orthostatic hypotension."),
    ("La tos está mejor.", "The cough is better."),
    ("Diabetes mal controlada.", "Poorly controlled diabetes."),
    ("Dolor lumbar.", "Low back pain."),  # anatomy, not a level
    ("Solución salina 500 ml.", "Normal saline 500 ml."),
    ("Disminución de la sensibilidad.", "Decreased sensation."),
    ("Prueba de embarazo hoy.", "Pregnancy test today."),
])
def test_an_opposite_or_a_finding_the_spanish_says_is_offered(text, english):
    assert reasons(text, [text], english) == []


# ---- A negation's list, as step 15 reads it in English (sentences outside the test set) ----------------------------

BUT = "Niega fiebre, escalofríos o sudoración nocturna pero refiere pérdida de peso."
AND = "Niega fiebre, escalofríos y refiere pérdida de peso."


@pytest.mark.parametrize(("text", "quote", "english", "expected"), [
    (BUT, "escalofríos o sudoración nocturna", "Chills or night sweats.", ["negation_dropped"]),
    (BUT, "escalofríos o sudoración nocturna", "No chills or night sweats.", []),
    (BUT, "refiere pérdida de peso", "Reports weight loss.", []),  # the list ends at "pero"
    (AND, "fiebre, escalofríos", "Fever, chills.", ["negation_dropped"]),
    (AND, "refiere pérdida de peso", "Reports weight loss.", []),
    ("Sin fiebre, la tos está mejor.", "la tos está mejor", "The cough is better.", []),  # a clause of its own
    # A new paragraph ends a cue's reach, as it does in English ("I don't think {new paragraph} She has pneumonia.").
    ("No creo que\nla paciente tenga neumonía.", "la paciente tenga neumonía", "The patient has pneumonia.", []),
])
def test_a_negation_carries_its_list_to_the_word_that_ends_it_and_no_further(text, quote, english, expected):
    assert reasons(text, [quote], english) == expected
