"""facts_es.py, the Spanish scripts' fact lists and the reading of the English that the scoring counts changes with
(the expansion plan's step 17): it shares no code with the checks it measures; each script's facts are where they say;
every gold statement is unchanged by them; and every change the review found the checks' own reading let through
(F13), and one of each kind the scoring counts, is changed."""

from __future__ import annotations

import ast
import hashlib
import sys

import pytest

from dictation_scripts_es import HOLDOUT_DIR, SEEN, load_scripts
from ehr_eval import facts_es, run_es, scoring_es
from ehr_eval.scripts import EVAL_DIR

HOLDOUT = load_scripts(HOLDOUT_DIR)
SCRIPTS = {script.id[:3]: script for script in load_scripts() + HOLDOUT}  # the held-out ones too
# The five seen scripts' files (h01 to h05), as sealed after run A2 (the decision log's step 17), and the five held out
# (h06 to h10), as their author delivered them (2026-11-24), each sealed apart, so the seen five's seal stays the one
# run A2 recorded: their fact lists are kept apart.
SEALED = "7180abd6707bf9f62b19c3b94ee21a3879fc33698f15c866752e7aef032005a9"
SEALED_HELD_OUT = "39bb769c8ba101dfc4a346ef23aeb86c3f229683faed58ec88cab791b72e4fcb"
# Their fact lists (eval/scripts-es-holdout-facts) are sealed the same way, each five apart, as they stand after the
# rulings round (the review of 2026-11-24): a fact list changed to suit a reading changes its seal with it.
SEALED_FACTS = "7210f3bbd0ba938dd7a466ea4d677b5f45889a19639418fe9137fad89c4bcf87"
SEALED_FACTS_HELD_OUT = "0c288696317af65d396456d022dc85f7f03970f517047ca28896f675641417e6"


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


def test_every_script_has_a_fact_list_the_run_only_when_asked_ones_kept_apart_so_their_files_stay_as_sealed():
    assert all(scoring_es.facts_of(script) for script in SCRIPTS.values())
    assert not any(script.facts for script in HOLDOUT)
    kept_apart = {path.name for path in scoring_es.facts_dir(HOLDOUT_DIR).glob("*.toml")}
    assert kept_apart == {s.path.name for s in HOLDOUT}
    facts_dir = scoring_es.facts_dir(HOLDOUT_DIR)
    for scripts, seal, facts_seal in (([s for s in HOLDOUT if s.id[:3] in SEEN], SEALED, SEALED_FACTS),
                                      ([s for s in HOLDOUT if s.id[:3] not in SEEN], SEALED_HELD_OUT,
                                       SEALED_FACTS_HELD_OUT)):
        ordered = sorted(scripts, key=lambda s: s.path.name)
        assert hashlib.sha256(b"".join(s.path.read_bytes() for s in ordered)).hexdigest() == seal
        facts = b"".join((facts_dir / s.path.name).read_bytes() for s in ordered)
        assert hashlib.sha256(facts).hexdigest() == facts_seal


def test_a_script_with_no_fact_list_anywhere_is_refused(tmp_path):
    lost = tmp_path / "scripts-es"
    lost.mkdir()
    content = SCRIPTS["e14"].path.read_text(encoding="utf-8")
    without = content[: content.index("\n[[facts]]")] + content[content.index("\n[[gold.") :]
    (lost / "e14-nino-sano.toml").write_text(without, encoding="utf-8")
    (script,) = load_scripts(lost)
    with pytest.raises(ValueError, match="has no fact list"):
        scoring_es.facts_of(script)


@pytest.mark.parametrize("script", SCRIPTS.values(), ids=lambda s: s.id)
def test_each_fact_is_said_once_in_the_text_and_follows_the_format(script):
    for entry in facts_es.entries(scoring_es.facts_of(script)):
        assert script.display.count(entry.said) == 1, entry.said


@pytest.mark.parametrize("script", [s for s in SCRIPTS.values() if s not in HOLDOUT], ids=lambda s: s.id)
def test_every_gold_statement_changes_nothing_and_misfiles_nothing(script):
    # Read straight against the facts, whether or not the checks would offer it (test_run_a.py goes through them). The
    # scripts run only when asked are held to their structure only (test_scripts_es.py): where the scorer reads their
    # gold as changed is reported (refusals.scorer_on_gold()), so a changed item an answer shows there may be the
    # scorer's.
    for name, statements in script.statements.items():
        for statement in statements:
            spans = [(script.display.index(q), script.display.index(q) + len(q)) for q in statement.spanish]
            others = tuple(script.gold[other].value for other in script.gold if other != name)
            changed, misfiled, no_gold = scoring_es._statement_items(statement.english, spans, script.display,
                                                                     script.gold[name].value, script, others)
            assert (changed, misfiled, no_gold) == ([], [], []), statement.english


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
    changed, *_ = scoring_es._statement_items(english, [at], heard, "", script)
    return {(kind, what) for kind, what, _ in changed}


# The review's F13: what the checks' own reading scored as nothing changed.
@pytest.mark.parametrize(("key", "said", "english", "changed"), [
    ("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 78/152, HR 94, weight 88 kg.",
     {("number", "78/152 (BP)"), ("number", "94 (HR)")}),
    ("e01", "Control en 4 semanas.", "Follow-up in 4 months.", {("duration", "4 months (follow-up)")}),
    ("e01", "Antecedente de tabaquismo, lo dejó hace 5 años.", "History of smoking, still smokes 5 packs.",
     {("unit", "5 packs (smoking)"), ("action", "stop, past (smoking) left out")}),
    ("e01", "Hombre de 62 años con hipertensión arterial desde hace 10 años.",
     "62-year-old man with hypotension for 10 years.", {("polarity", "hypotension")}),
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
    # The held-out scripts are read against their own facts too.
    ("h05", "Suspender enalapril e iniciar losartán 50 mg al día.", "Stop enalapril and start losartan 50 mg daily.",
     "Start enalapril and stop losartan 50 mg daily.", "action"),
    ("h03", "Antecedentes familiares de cáncer de colon en su padre a los 60 años.",
     "Family history of colon cancer in her father at age 60.", "Family history of colon cancer in her sister at age 60.",
     "relative"),
    # A value moved to another vital sign or result: "glucose 140" as "blood pressure 140".
    ("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 152/94, HR 78, weight 88 kg.", "BP 152/94, pulse 88, weight 78 kg.",
     "number"),
])
def test_each_kind_of_change_is_counted_and_the_right_translation_isnt(key, said, right, wrong, kind):
    assert changes(key, said, right) == set()
    assert kind in {changed for changed, _ in changes(key, said, wrong)}


# The scorer's own misreadings (step 17, For Tahseen first 8), each gone, and what each resembles still counted: an age
# said in words read as a duration and other values tied to the age; a weight lifted read as the vital sign; a doubt of
# a cause cut off at its comma (e04's facts give the doubt to the finding, e08's and h01's to the cause).
@pytest.mark.parametrize(("key", "said", "right", "wrong", "kind"), [
    ("h02", "Mujer de 34 años con ardor al orinar y ganas de orinar con frecuencia desde hace 2 días.",
     "34 years old woman with burning on urination and urinary frequency for 2 days.",
     "35 years old woman with burning on urination and urinary frequency for 2 days.", "number"),
    ("h02", "Mujer de 34 años con ardor al orinar y ganas de orinar con frecuencia desde hace 2 días.",
     "34 years old woman with burning on urination and urinary frequency for 2 days.",
     "34 years old woman with burning on urination and urinary frequency for 5 days.", "duration"),
    ("e04", "Mujer de 33 años que empezó con ronchas en los brazos hace 2 días, después de tomar amoxicilina.",
     "33-year-old woman who started with spots on her arms 2 days ago, after taking amoxicillin.",
     "33-year-old woman who started with spots on her arms 5 days ago, after taking amoxicillin.", "duration"),
    ("h01", "Evitar cargar peso durante 2 semanas.", "Avoid lifting weight for 2 weeks.",
     "Avoid lifting weight for 2 months.", "duration"),
    ("e04", "urticaria probablemente por amoxicilina.", "Urticaria, probably due to amoxicillin.",
     "Urticaria, due to amoxicillin.", "certainty"),
    ("h01", "lumbociática derecha aguda, probablemente por una hernia discal.",
     "Acute right sciatica probably due to a herniated disc.", "Acute right sciatica due to a herniated disc.",
     "certainty"),
    ("e01", "TA 152/94, FC 78, peso 88 kg.", "BP 152/94, HR 78, weight 88 kg.", "BP 152/94, HR 78, weight 89 kg.",
     "number"),
])
def test_the_scorers_misreadings_are_gone_and_what_they_resemble_is_still_counted(key, said, right, wrong, kind):
    assert changes(key, said, right) == set()
    assert kind in {changed for changed, _ in changes(key, said, wrong)}


# A doubt of a cause the Spanish doesn't say is still added; a doubt of a finding after a comma is still that finding's.
def test_a_doubt_of_a_cause_added_is_counted_and_a_doubt_after_a_comma_is_still_its_findings():
    entries = facts_es.entries([{"said": "Exantema por amoxicilina.", "items": [{"of": "rash"}, {"of": "amoxicillin"}]}])
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items), frozenset({"amoxicillin"}))
    for english in ("Rash probably due to amoxicillin.", "Rash, probably due to amoxicillin."):
        changed, _ = facts_es.compare(english, entries, entries, lexicon)
        assert ("certainty", "shown with doubt", False) in changed, english
    assert facts_es.compare("Rash due to amoxicillin.", entries, entries, lexicon)[0] == []
    items = (facts_es.Item("pneumonia", ("pneumonia",), {}, None, False, False, None),
             facts_es.Item("asthma", ("asthma",), {}, None, False, False, None))
    said = facts_es.claims("Pneumonia, possible asthma.", facts_es.Lexicon(items))
    assert [(c.kind, c.label) for c in said if c.kind == "doubt"] == [("doubt", "asthma")]


# "unlikely", "may be" and "could be" are doubt (the owner's ruling of 2026-11-24): each said of a doubted finding is
# what the Spanish says ("unlikely" where it leans against it, the modal hedges where it says "posible"), and the
# finding without it is a doubt dropped; each said of a finding the Spanish states is a doubt added. "May" as a month
# or leave isn't doubt.
@pytest.mark.parametrize(("hedged", "doubted"), [
    ("A fracture is unlikely.", "Es poco probable una fractura."), ("Fracture unlikely.", "Es poco probable una fractura."),
    ("It may be a fracture.", "Posible fractura."), ("It could be a fracture.", "Posible fractura."),
    ("A fracture may be present.", "Posible fractura."),
])
@pytest.mark.parametrize("stated", [True, False])
def test_unlikely_may_be_and_could_be_are_doubt_both_ways(hedged, doubted, stated):
    entries = facts_es.entries([{"said": "Hay una fractura." if stated else doubted,
                                 "items": [{"of": "fracture", "doubt": not stated}]}])
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items))
    shown = {kind for kind, _, _ in facts_es.compare(hedged, entries, entries, lexicon)[0]}
    plain = {kind for kind, _, _ in facts_es.compare("A fracture.", entries, entries, lexicon)[0]}
    if stated:
        assert (shown, plain) == ({"certainty"}, set())
    else:
        assert shown == set() and "certainty" in plain


def facts_changes(said: str, item: dict, english: str, drugs: frozenset[str] = frozenset()) -> set[str]:
    entries = facts_es.entries([{"said": said, "items": [item]}])
    lexicon = facts_es.Lexicon(tuple(i for entry in entries for i in entry.items), drugs)
    return {kind for kind, _, _ in facts_es.compare(english, entries, entries, lexicon)[0]}


# A hedge of leave ("may", "might" or "could" with "be" and a past participle) is no doubt, and a modal hedge said of
# nothing named is no doubt of what is named: the review's five, each a doubt dropped without the finding's own hedge and
# right with it (the checks refuse the same, translation.LEAVE).
@pytest.mark.parametrize(("said", "of", "dropped", "kept"), [
    ("Posible varicela. Volver al colegio en una semana.", "chickenpox", "Chickenpox. School may be resumed in 1 week.",
     "Possible chickenpox. School may be resumed in 1 week."),
    ("Probable ictus. Se puede trasladar.", "stroke", "Stroke. Transfer could be arranged.",
     "Probable stroke. Transfer could be arranged."),
    ("Posible esguince de tobillo, se puede vendar.", "ankle sprain", "Ankle sprain; bandage may be applied.",
     "Possible ankle sprain; bandage may be applied."),
    ("Sospecha de gripe. Puede volver al trabajo en tres días.", "influenza",
     "Influenza. Return to work may be allowed in 3 days.", "Suspected influenza. Return to work may be allowed in 3 days."),
    ("Posible culebrilla. Se puede aplicar crema.", "shingles", "Shingles. Cream may be applied.",
     "Possible shingles. Cream may be applied."),
    ("Probable ictus.", "stroke", "Stroke. It may be.", "It may be a stroke."),
])
def test_a_hedge_of_leave_or_of_nothing_named_is_no_doubt_of_the_finding(said, of, dropped, kept):
    item = {"of": of, "doubt": True}
    assert "certainty" in facts_changes(said, item, dropped)
    assert facts_changes(said, item, kept) <= {"number", "duration"}  # the days and weeks aren't in these facts


def test_a_hedge_of_leave_said_of_a_drug_is_no_doubt_added():
    assert facts_changes("Puede tomar ibuprofeno.", {"of": "ibuprofen", "action": "take"}, "Ibuprofen may be taken.",
                         frozenset({"ibuprofen"})) == set()
    assert facts_changes("Tiene neumonía.", {"of": "pneumonia", "negated": False}, "Pneumonia may be present.") == {
        "certainty"}


# "unlikely" is a negation too, read against what the Spanish says (the owner's ruling of 2026-11-24): "poco probable"
# and "improbable" as "unlikely" pass; "probable" or "posible" as "unlikely", and "poco probable" as "likely", are caught.
@pytest.mark.parametrize(("said", "english", "caught"), [
    ("Neumonía poco probable.", "Pneumonia unlikely.", False),
    ("Es poco probable una neumonía.", "Pneumonia is unlikely.", False),
    ("Es improbable una neumonía.", "Pneumonia is unlikely.", False),
    ("Es improbable una neumonía.", "Pneumonia is improbable.", False),
    ("Probable neumonía.", "Pneumonia unlikely.", True),
    ("Posible neumonía.", "Pneumonia is unlikely.", True),
    ("Neumonía poco probable.", "Likely pneumonia.", True),
    ("Neumonía poco probable.", "Probable pneumonia.", True),
    ("Probable neumonía.", "Probable pneumonia.", False),
])
def test_unlikely_is_a_negation_read_against_how_the_spanish_leans(said, english, caught):
    assert ("negation" in facts_changes(said, {"of": "pneumonia", "doubt": True}, english)) is caught


@pytest.mark.parametrize("english", ["Fracture follow-up on May 3.", "You may take ibuprofen for the fracture.",
                                     "The fracture was seen in May."])
def test_may_as_a_month_or_leave_is_no_doubt(english):
    items = (facts_es.Item("fracture", ("fracture",), {}, None, False, False, None),)
    assert not [c for c in facts_es.claims(english, facts_es.Lexicon(items, frozenset({"ibuprofen"}))) if c.kind == "doubt"]


# An increase or decrease of something named, said as a trend ("the swelling has decreased"), is no medication change;
# one said of a drug or a dose still is.
@pytest.mark.parametrize(("english", "action"), [
    ("The swelling has decreased.", None),
    ("Pain has increased since Monday.", None),
    ("Ibuprofen was decreased.", "decrease, past"),
    ("The dose was decreased.", "decrease, past"),
    ("Decrease ibuprofen.", "decrease"),
    ("Metformin has been increased to 1000 mg.", "increase, past"),
])
def test_a_trend_is_no_medication_change_and_a_drugs_change_still_is(english, action):
    lexicon = facts_es.Lexicon(drugs=frozenset({"ibuprofen", "metformin"}))
    assert next((c.value for c in facts_es.claims(english, lexicon) if c.kind == "action"), None) == action


# A trend is read only where its clause names a subject, and for its direction against the Spanish: given the other way
# round it is changed, the right way it isn't. A change said of something named before ("which was increased", "it was
# decreased") is a medication change, counted where the Spanish says none (the review of 2026-11-24).
@pytest.mark.parametrize(("said", "of", "right", "reversed_"), [
    ("La hinchazón ha disminuido.", "swelling", "The swelling has decreased.", "The swelling has increased."),
    ("El dolor ha disminuido.", "pain", "Pain has decreased.", "Pain has increased."),
    ("El dolor ha aumentado desde el lunes.", "pain", "Pain has increased since Monday.", "Pain has decreased since Monday."),
])
def test_a_trend_given_the_other_way_round_is_changed_and_the_right_way_is_not(said, of, right, reversed_):
    assert facts_changes(said, {"of": of}, right) == set()
    assert facts_changes(said, {"of": of}, reversed_) == {"polarity"}


@pytest.mark.parametrize("english", ["Lisinopril 10 mg daily, which was increased.", "Lisinopril 10 mg daily. It was decreased."])
def test_a_change_said_of_a_drug_named_before_is_a_medication_change(english):
    item = {"of": "lisinopril", "number": "10 mg", "frequency": "daily"}
    assert facts_changes("Lisinopril 10 mg al día.", item, english, frozenset({"lisinopril"})) == {"action"}
    assert facts_changes("Lisinopril 10 mg al día.", item, "Lisinopril 10 mg daily.", frozenset({"lisinopril"})) == set()


# A value said of nothing the passage names is backed by the one cited item with a value of its kind, or by those the
# statement names; with two or more and none named, it is changed, not a value no gold has (the review of 2026-11-24:
# "Febrile for 2 weeks" for "Fiebre de 3 días y tos de 2 semanas" had moved the cough's two weeks unseen).
@pytest.mark.parametrize(("english", "changed"), [
    ("Febrile for 2 weeks.", {("duration", "2 weeks")}),
    ("Coughing for 3 days.", {("duration", "3 days")}),
    ("Fever for 2 weeks.", {("duration", "2 weeks (fever)")}),
    ("Fever for 3 days and cough for 2 weeks.", set()),
    ("Fever and cough, for 3 days and 2 weeks respectively.", set()),
])
def test_a_value_said_of_nothing_named_is_changed_where_it_could_be_either_items(english, changed):
    entries = facts_es.entries([{"said": "Fiebre de 3 días y tos de 2 semanas.",
                                 "items": [{"of": "fever", "duration": "3 days"}, {"of": "cough", "duration": "2 weeks"}]}])
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items))
    assert {(kind, what) for kind, what, _ in facts_es.compare(english, entries, entries, lexicon)[0]} == changed


def test_a_value_said_of_nothing_named_is_backed_where_one_item_has_its_kind():
    entries = facts_es.entries([{"said": "Fiebre de 3 días.", "items": [{"of": "fever", "duration": "3 days"}]}])
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items))
    changed, backed = facts_es.compare("Febrile for 3 days.", entries, entries, lexicon)
    assert changed == [] and [(c.kind, c.value) for c in backed] == [("duration", "3 days")]


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


HEPARIN = "Recibe heparina 5.000 unidades subcutáneas cada 12 horas desde la cirugía de cadera."


# The review's F8: "5.000" is five where a decimal point is written so and five thousand where a thousands one is, so
# while the transcript writes it so the checks leave its field for the clinician. What was said is "cinco mil
# unidades": a statement shown from it is compared with that, however the transcript writes it.
@pytest.mark.parametrize("written", ["5.000", "5000"])
def test_the_dose_said_as_five_thousand_is_compared_as_said_however_it_is_written(written):
    heard = SCRIPTS["e16"].display.replace("5.000", written)
    said = "Receives heparin {} units subcutaneous every 12 hours since hip surgery."
    assert changes("e16", HEPARIN, said.format(5000), heard) == set()
    assert changes("e16", HEPARIN, said.format(5), heard) == {("number", "5 units (heparin)")}


def offered(written: str, english: str, quote: str) -> dict:
    """e16 with one statement shown in Subjective, citing `quote`, from a transcript that writes the dose `written`."""
    script = SCRIPTS["e16"]
    heard = script.display.replace("5.000", written)
    at = heard.index(quote.replace("5.000", written))
    shown = {"english": english, "sources": [[at, at + len(quote.replace("5.000", written))]]}
    item = scoring_es.SpanishItem(variant="e16-anadido-corto--clean", script=script.id, tags=[], hypothesis=heard,
                                  sorting="done", fields={"subjective": scoring_es.SpanishOutcome(
                                      status="pending", value=english, shown_statements=[shown])})
    return scoring_es.score([item], {script.id: script})["ambiguous_offered"]


# Its gold is empty, so a statement shown from it is reported, with the passage as heard, whatever it says; one shown
# from another passage is not.
@pytest.mark.parametrize(("written", "two_ways"), [("5.000", True), ("5000", False)])
def test_a_statement_offered_from_the_dose_that_reads_two_ways_is_reported_with_its_passage(written, two_ways):
    found = offered(written, "Receives heparin 5000 units subcutaneous every 12 hours.", HEPARIN)

    assert found == {"statements": 1, "listed": [{
        "variant": "e16-anadido-corto--clean", "field": "subjective",
        "statement": "Receives heparin 5000 units subcutaneous every 12 hours.",
        # Heard otherwise than written, the passage is found word by word, to its last word.
        "passage": HEPARIN if two_ways else HEPARIN.replace("5.000", written).rstrip("."), "written_two_ways": two_ways}]}
    assert offered(written, "Labs show a GFR of 28.", "un filtrado glomerular de 28") == {"statements": 0, "listed": []}
    printed = run_es.ambiguous_offered({"ambiguous_offered": found, "text": {"ambiguous_offered": {"listed": []}}})
    assert "Receives heparin 5000 units" in printed and "Recibe heparina" in printed


# The review's F15: what the test set now says that the checks of 9db0821 let through translated wrongly, each seen by
# the eval as changed (the scripts' gold, the right translation, is unchanged: test_run_a.py).
@pytest.mark.parametrize(("key", "said", "wrong", "kind"), [
    ("e12", "Niega mareo, palpitaciones, dolor torácico y síncope.",
     "Denies dizziness; palpitations, chest pain and syncope.", "negation"),
    ("e12", "Ibuprofeno: se suspende por el riesgo de sangrado.", "Ibuprofen is continued because of the risk of bleeding.",
     "action"),
    ("e12", "Metoprolol: continuar igual.", "Metoprolol: stop.", "action"),
    ("e02", "Dejó de tomar la glimepirida hace un mes por hipoglucemias.",
     "Takes glimepiride, started a month ago because of hypoglycemia.", "action"),
    ("e08", "El cardiólogo le suspendió la espironolactona el mes pasado por el potasio alto.",
     "The cardiologist stopped his spironolactone last month because of low potassium.", "polarity"),
    ("e09", "Lorazepam 1 mg, media tableta por la noche si no puede dormir, durante 2 semanas.",
     "Lorazepam 1 mg, 2 tablets at night if she cannot sleep, for 2 weeks.", "unit"),
    ("e12", "Toma también metoprolol de 100 mg, un cuarto de tableta en la mañana.",
     "Also takes metoprolol 100 mg, 4 tablets in the morning.", "number"),
    ("e13", "Después, un litro y medio de solución salina en 24 horas.", "Then 1 liter of normal saline over 24 hours.",
     "number"),
    ("e04", "En urgencias se descartó anafilaxia.", "Anaphylaxis in the emergency department.", "negation"),
    ("e03", "Disminución del murmullo vesicular en la base derecha y sibilancias en el campo superior izquierdo.",
     "Decreased breath sounds at the left base and wheezes in the right upper field.", "side"),
    ("e11", "Toma también aspirina 100 mg en la mañana y omeprazol 20 mg en la noche.",
     "Also takes aspirin 20 mg in the morning and omeprazole 100 mg at night.", "number"),
    ("e11", "Toma también aspirina 100 mg en la mañana y omeprazol 20 mg en la noche.",
     "Also takes aspirin 100 mg at night and omeprazole 20 mg in the morning.", "frequency"),
    ("e02", "Glucosa en ayunas de 162 mg/dl.", "Glucose after breakfast 162 mg/dL.", "frequency"),
    ("e02", "Glucosa en ayunas de 162 mg/dl.", "Fasting glucose 162 mmol/L.", "unit"),
    ("e08", "La radiografía de tórax de hoy es compatible con congestión pulmonar.",
     "Today's chest X-ray shows pulmonary congestion.", "certainty"),
])
def test_what_the_new_content_says_translated_wrongly_is_changed(key, said, wrong, kind):
    assert kind in {changed for changed, _ in changes(key, said, wrong)}


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
