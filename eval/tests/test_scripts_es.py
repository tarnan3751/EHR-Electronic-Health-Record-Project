"""The Spanish test set's scripts (eval/scripts-es, the expansion plan's step 17) and the five held out
(eval/scripts-es-holdout) against the rules in dictation_scripts_es.py: each parses, says every number in words and
writes it in digits, quotes its gold word for word, lists what it names, and the set covers what the plan asks of it."""

from __future__ import annotations

import re
from collections import Counter

import pytest

from dictation_scripts_es import FIELDS, HOLDOUT_DIR, load_scripts, runs
from ehr_dictation import spanish, translation, validation
from ehr_dictation.textnorm import tokens
from ehr_eval import scoring_es

SCRIPTS = load_scripts()
HOLDOUT = load_scripts(HOLDOUT_DIR)
ALL = SCRIPTS + HOLDOUT
TAGS = frozenset(
    """numbers vitals dates decimal-comma units frequency routes sides severity actions drug-name-differs negation
    certainty history experiencer self-correction recombination order-attempt allergy-change mixed-plan injection
    code-switching english-passage missing-field spoken-punctuation ambiguous short long max-length""".split()
)
VOICES = {"ef_dora": "es", "em_alex": "es", "em_santa": "es", "af_heart": "en-voice", "am_michael": "en-voice"}
NOISE = {"hvac", "babble", "monitor"}
WITH_NOISE = {"e01", "e03", "e06", "e08", "e12", "e13"}
# The plan's coverage (§9): at least this many scripts with each tag.
COVERAGE = {
    "negation": 3, "certainty": 3, "history": 3, "experiencer": 3, "self-correction": 3, "order-attempt": 3,
    "mixed-plan": 3, "frequency": 3, "sides": 3, "units": 3, "actions": 3, "code-switching": 3, "missing-field": 3,
    "decimal-comma": 2, "short": 2, "max-length": 1, "injection": 1, "spoken-punctuation": 1,
}


def test_there_are_sixteen_scripts_e01_to_e16():
    assert [s.id[:3] for s in SCRIPTS] == [f"e{n:02}" for n in range(1, 17)]


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_the_script_follows_the_format(script):
    assert script.path.stem == script.id and script.language == "es"
    assert set(script.tags) <= TAGS, set(script.tags) - TAGS
    assert script.voice in VOICES and set(script.noise) <= NOISE
    assert bool(script.noise) == (script.id[:3] in WITH_NOISE)
    assert not re.search(r"\d", script.spoken), "every number is said in words: write it [[digits|words]]"
    assert not re.search(r"\d", re.sub(r"\[\[[^\]]*\]\]", "", script.text)), "digits only inside [[display|spoken]]"


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_the_spoken_length_fits_the_tags(script):
    words = script.words
    if "max-length" in script.tags:
        assert 420 <= words <= 540
    else:
        assert 75 <= words <= 330
    # Most of the set is shorter than the English set's two minutes; "short" marks the shortest, "long" the longest.
    if "short" in script.tags:
        assert words <= 110
    if "long" in script.tags:
        assert 290 <= words <= 330


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_the_gold_quotes_the_display_text_and_writes_no_markup(script):
    for name in FIELDS:
        for statement in script.statements[name]:
            for quote in statement.spanish:
                assert quote in script.display, f"{name}: {quote!r} isn't in the display text"
            assert not re.search(r"[\[\]{}<>|]", statement.english)
            assert validation._without_instructions(statement.english)[1] == 0, "an English instruction"


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_what_the_script_names_is_in_it(script):
    display = script.display
    for drug, english in zip(script.drugs, script.drugs_en, strict=True):
        assert drug.lower() in display.lower()
        assert english.lower() in spanish.drug_candidates(drug.split()[-1]) | {drug.lower()}, (drug, english)
    for retracted in script.retracted:
        at = display.find(retracted)
        assert at >= 0, retracted
        assert spanish.correction_cues(display[at:], spanish.tokens(display[at:])), "a correction follows it"
    gold = " ".join(g.value for g in script.gold.values())
    for retracted in script.retracted:
        assert retracted.replace(",", ".") not in gold
    for phrase in script.forbidden_en:
        assert not re.search(rf"(?<!\w){re.escape(phrase.lower())}(?!\w)", gold.lower()), phrase
    for finding, relative in script.family:
        assert finding in gold.lower() and relative in gold.lower()
    for iso in script.dates:
        month = int(iso[5:7])
        assert list(spanish.MONTHS)[month - 1] in spanish.fold(display)


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_each_negated_or_present_finding_is_said_in_the_gold_and_one_way_only(script):
    # The scoring counts a finding listed as negated that a statement shows without its negation, and one listed as
    # present that it shows negated, wherever the statement says it (scoring_es.negated()). A phrase the gold says both
    # ways would count a right answer as changed ("effusion" in "mild effusion ... without effusion"), and one it never
    # says would check nothing.
    gold = " ".join(g.value for g in script.gold.values())
    assert not set(script.negated) & set(script.affirmed), "listed both negated and present"
    for finding in script.negated:
        places = scoring_es.negated(gold, finding)
        assert places and all(places), f"{finding!r} isn't negated everywhere the gold says it: {places}"
    for finding in script.affirmed:
        places = scoring_es.negated(gold, finding)
        assert places and not any(places), f"{finding!r} isn't present everywhere the gold says it: {places}"


@pytest.mark.parametrize("script", [s for s in ALL if s.forbidden], ids=lambda s: s.id)
def test_every_forbidden_passage_is_inside_an_instruction_the_spanish_checks_recognize(script):
    text = script.display
    spans = spanish.directives(text)
    for forbidden in script.forbidden:
        at = text.find(forbidden)
        assert at >= 0, f"{forbidden!r} isn't in the display text"
        assert any(start <= at and at + len(forbidden) <= end for start, end, _ in spans), forbidden


def test_tags_agree_with_the_content():
    for script in ALL:
        assert bool(script.forbidden) == bool({"order-attempt", "allergy-change", "injection"} & set(script.tags))
        assert bool(script.retracted) == ("self-correction" in script.tags), script.id
        assert bool(script.family) == ("experiencer" in script.tags), script.id
        assert any(language == "en" for language, _ in runs(script.text)) == ("english-passage" in script.tags)
        assert ("{punto y aparte}" in script.text) == ("spoken-punctuation" in script.tags)
        assert any(not script.statements[name] for name in FIELDS) == ("missing-field" in script.tags), script.id


def test_the_set_covers_what_the_plan_asks():
    counts = Counter(tag for script in SCRIPTS for tag in script.tags)
    assert {tag: counts[tag] for tag in COVERAGE if counts[tag] < COVERAGE[tag]} == {}
    voices = Counter(script.voice for script in SCRIPTS)
    assert all(voices[voice] >= 3 for voice, kind in VOICES.items() if kind == "es")
    assert {script.id[:3] for script in SCRIPTS if VOICES[script.voice] == "en-voice"} == {"e05", "e14"}


def test_drugs_the_english_gold_names_are_ones_the_script_lists():
    for script in ALL:
        named = {name.lower() for name in script.drugs_en}
        for gold in script.gold.values():
            assert {t.text for t in tokens(gold.value) if translation.is_drug(t.text)} <= named, script.id


def test_five_scripts_are_held_out_and_run_only_when_asked():
    assert [s.id[:3] for s in HOLDOUT] == ["h01", "h02", "h03", "h04", "h05"]
    assert not {s.id for s in SCRIPTS} & {s.id for s in HOLDOUT}
    long, corrected, family, switching, follow_up = HOLDOUT
    assert "long" in long.tags and 290 <= long.words <= 330
    assert len(corrected.retracted) == 2 and "self-correction" in corrected.tags
    assert family.family and "allergy-change" in family.tags and family.forbidden
    assert "code-switching" in switching.tags
    assert len(follow_up.drugs) >= 3 and "actions" in follow_up.tags
