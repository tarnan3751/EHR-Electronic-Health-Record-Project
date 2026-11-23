"""facts_es.py, the Spanish scripts' fact lists and the reading of the English that the scoring counts changes with
(the expansion plan's step 17): it shares no code with the checks it measures; each script's facts are where they say;
every gold statement is unchanged by them; and every change the review found the checks' own reading let through
(F13), and one of each kind the scoring counts, is changed."""

from __future__ import annotations

import ast
import sys

import pytest

from dictation_scripts_es import HOLDOUT_DIR, load_scripts
from ehr_eval import facts_es, scoring_es
from ehr_eval.scripts import EVAL_DIR

SCRIPTS = {script.id[:3]: script for script in load_scripts()}
HOLDOUT = load_scripts(HOLDOUT_DIR)


def test_the_reading_imports_nothing_but_the_standard_library():
    # Not translation.py, spanish.py or validation.py, nor anything of the sidecar's: a mistake in the checks' own
    # reading would otherwise be measured as right.
    tree = ast.parse((EVAL_DIR / "ehr_eval" / "facts_es.py").read_text(encoding="utf-8"))
    imported = {alias.name.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names}
    imported |= {(node.module or "").split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.ImportFrom)}
    assert imported - {"__future__"} <= set(sys.stdlib_module_names), imported


def test_the_scoring_of_changes_reads_neither_the_spanish_checks_nor_the_english_ones():
    tree = ast.parse((EVAL_DIR / "ehr_eval" / "scoring_es.py").read_text(encoding="utf-8"))
    names = {alias.name for node in ast.walk(tree) if isinstance(node, ast.ImportFrom) for alias in node.names}
    assert not names & {"translation", "validation"}


def test_every_script_has_a_fact_list_and_the_held_out_ones_none():
    assert all(script.facts for script in SCRIPTS.values())
    assert not any(script.facts for script in HOLDOUT)


@pytest.mark.parametrize("script", SCRIPTS.values(), ids=lambda s: s.id)
def test_each_fact_is_said_once_in_the_text_and_follows_the_format(script):
    for entry in facts_es.entries(script.facts):
        assert script.display.count(entry.said) == 1, entry.said


@pytest.mark.parametrize(("fact", "says"), [
    ({"said": "x"}, "says nothing"),
    ({"said": "x", "items": [{"of": "fever", "colour": "red"}]}, "an item needs of"),
    ({"said": "x", "items": [{"of": "metformin", "frequency": "sometimes"}]}, "isn't one the reading knows"),
    ({"said": "x", "items": [{"of": "metformin", "action": "prescribe"}]}, "isn't one the reading knows"),
])
def test_a_fact_list_that_doesnt_follow_the_format_is_refused(fact, says):
    with pytest.raises(facts_es.FactsError, match=says):
        facts_es.entries([fact])


def changes(key: str, said: str, english: str, hypothesis: str | None = None) -> set[tuple[str, str]]:
    script = SCRIPTS[key]
    heard = hypothesis or script.display
    at = facts_es.places([said], script.display, heard)[0]
    changed, _ = scoring_es._statement_items(english, [at], heard, "", script)
    return {(kind, what) for kind, what, _ in changed}


# The review's F13: what the checks' own reading scored as nothing changed.
@pytest.mark.parametrize(("key", "said", "english", "changed"), [
    ("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 78/152, HR 94, weight 88 kg.",
     {("number", "78/152 (BP)"), ("number", "94 (HR)")}),
    ("e01", "Control en 4 semanas.", "Follow-up in 4 months.", {("duration", "4 months (follow-up)")}),
    ("e01", "Antecedente de tabaquismo, lo dejó hace 5 años.", "History of smoking, still smokes 5 packs.",
     {("unit", "5 packs (smoking)"), ("action", "stop, past (smoking) left out")}),
    ("e01", "Hombre de 62 años con hipertensión arterial desde hace 10 años.",
     "62-year-old man with hypotension for 10 years.", {("polarity", "hypotension (age)"), ("duration", "10 years (age)")}),
])
def test_what_the_checks_own_reading_let_through_is_changed(key, said, english, changed):
    assert changes(key, said, english) == changed


# One of each kind the scoring counts, each from a script's own passage; and the right translation of it unchanged.
@pytest.mark.parametrize(("key", "said", "right", "wrong", "kind"), [
    ("e01", "Toma losartán 50 mg una vez al día.", "Takes losartan 50 mg once a day.",
     "Takes losartan 50 mg twice a day.", "frequency"),
    ("e01", "Toma losartán 50 mg una vez al día.", "Takes losartan 50 mg once a day.",
     "Takes losartan 50 mcg once a day.", "unit"),
    ("e01", "Toma losartán 50 mg una vez al día.", "Takes losartan 50 mg once a day.",
     "Takes lisinopril 50 mg once a day.", "drug"),
    ("e01", "Toma losartán 50 mg una vez al día.", "Takes losartan 50 mg once a day.",
     "Stopped losartan 50 mg once a day.", "action"),
    ("e07", "Antecedente de tiroidectomía total en 2019 por bocio.", "History of total thyroidectomy in 2019 for goiter.",
     "History of total thyroidectomy in 2018 for goiter.", "date"),
    ("e07", "Antecedente de tiroidectomía total en 2019 por bocio.", "History of total thyroidectomy in 2019 for goiter.",
     "Acute thyroidectomy in 2019 for goiter.", "history"),
    ("e03", "amoxicilina 500 mg, no, perdón, 1 g por vía oral cada 8 horas durante 7 días.",
     "Amoxicillin 1 g by mouth every 8 hours for 7 days.", "Amoxicillin 1 g intravenous every 8 hours for 7 days.",
     "route"),
    ("e03", "amoxicilina 500 mg, no, perdón, 1 g por vía oral cada 8 horas durante 7 días.",
     "Amoxicillin 1 g by mouth every 8 hours for 7 days.", "Amoxicillin 500 mg by mouth every 8 hours for 7 days.",
     "number"),
    ("e03", "Crepitantes en la base derecha.", "Crackles at the right base.", "Crackles at the left base.", "side"),
    ("e03", "posible neumonía adquirida en la comunidad.", "Possible community-acquired pneumonia.",
     "Community-acquired pneumonia.", "certainty"),
    ("e02", "Refiere poliuria leve.", "Reports mild polyuria.", "Reports severe polyuria.", "severity"),
    ("e02", "Su madre tiene diabetes tipo 2.", "Her mother has type 2 diabetes.", "Her father has type 2 diabetes.",
     "relative"),
    ("e02", "Su madre tiene diabetes tipo 2.", "Her mother has type 2 diabetes.", "Has type 2 diabetes.", "experiencer"),
    ("e02", "diabetes tipo 2 mal controlada.", "Type 2 diabetes poorly controlled.", "Type 2 diabetes well controlled.",
     "polarity"),
    ("e09", "No tiene ideas suicidas.", "No suicidal ideation.", "Suicidal ideation.", "negation"),
    ("e16", "Evitar antiinflamatorios.", "Avoid NSAIDs.", "Avoid NSAIDs, hiv positive.", "abbreviation"),
    ("e12", "El INR de hoy es de 3,6.", "INR today is 3.6.", "INR yesterday was 3.6.", "date"),
    # A value moved to another vital sign or result: "glucose 140" as "blood pressure 140".
    ("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 152/94, HR 78, weight 88 kg.", "BP 152/94, pulse 88, weight 78 kg.",
     "number"),
])
def test_each_kind_of_change_is_counted_and_the_right_translation_isnt(key, said, right, wrong, kind):
    assert changes(key, said, right) == set()
    assert kind in {changed for changed, _ in changes(key, said, wrong)}


def test_a_number_the_spanish_says_without_a_unit_may_be_given_a_unit_of_measurement_only():
    assert changes("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 152/94 mmHg, HR 78 bpm, weight 88 kg.") == set()
    assert changes("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 152/94, HR 78, weight 88 lb.") == {
        ("unit", "88 lb (weight)")}


def test_a_passage_misheard_in_part_is_still_found_in_the_transcript():
    script = SCRIPTS["e01"]
    heard = script.display.replace("Toma losartán", "Toma los artán").replace("TA 152/94", "te a 152/94")
    said = "TA 152/94, FC 78, peso 88 kg."
    start, end = facts_es.places([said], script.display, heard)[0]
    assert heard[start:end] == "te a 152/94, FC 78, peso 88 kg"
    assert changes("e01", said, "BP 78/152, HR 78, weight 88 kg.", heard) == {("number", "78/152 (BP)")}


def test_a_passage_whose_number_reads_two_ways_is_changed_by_any_statement_shown_from_it():
    entry = facts_es.Entry("Heparina 5.000 unidades.", (), "ambiguous_number")
    changed, _ = facts_es.compare("Heparin 5000 units.", [entry], [entry], facts_es.Lexicon())
    assert [kind for kind, _, _ in changed] == ["number"]


@pytest.mark.parametrize(("english", "phrase", "negated"), [
    ("Denies fever, cough, dyspnea, chest pain and syncope.", "syncope", [True]),
    ("No edema; cyanosis present.", "cyanosis", [False]),
    ("Fracture was ruled out.", "fracture", [True]),
    ("Rule out fracture.", "fracture", [False]),
    ("Type 2 diabetes not controlled with probable neuropathy.", "neuropathy", [False]),
    ("Left knee with mild effusion and crepitus, right knee without effusion.", "effusion", [False, True]),
])
def test_a_negation_reaches_a_list_and_stops_at_a_semicolon_but_or_with(english, phrase, negated):
    assert facts_es.negated(english, phrase) == negated
