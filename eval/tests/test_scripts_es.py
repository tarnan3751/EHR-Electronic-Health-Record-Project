"""The Spanish test set's scripts (eval/scripts-es, the expansion plan's step 17) and the ones run only when asked
(eval/scripts-es-holdout) against the rules in dictation_scripts_es.py: each parses, says every number in words and
writes it in digits, quotes its gold word for word, lists what it names, and the set covers what the plan asks of it.

The ones run only when asked are checked for their structure only (the owner's ruling of 2026-11-24): their fields,
sources and lists of what each passage says, never whether their words are on the checks' lists (spanish.py,
translation.py, validation.py), so a held-out script tests the checks rather than being written to pass them. A gold
statement of theirs the checks refuse is a false refusal, reported (ehr_eval/refusals.py), not a failure here."""

from __future__ import annotations

import re
import tomllib
from collections import Counter

import pytest

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import FIELDS, HOLDOUT_DIR, SEEN, load_scripts, runs
from ehr_dictation import prompt, spanish, translation, validation
from ehr_dictation.textnorm import tokens
from ehr_eval import scoring_es
from ehr_eval.scripts import EVAL_DIR

SCRIPTS = load_scripts()
HOLDOUT = load_scripts(HOLDOUT_DIR)
ALL = SCRIPTS + HOLDOUT


def held_out(script) -> bool:  # noqa: ANN001
    """Whether the script is one run only when asked: the loader's folder for them (HOLDOUT_DIR)."""
    return script.path.parent.name == HOLDOUT_DIR.name
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


# A Spanish dictation is up to 2 minutes (the decision log's step 17, decision 20), about 300 words: "max-length" is
# close to it, "long" the plan's long items, well under it, and "short" the shortest.
MAX_LENGTH_WORDS, LONG_WORDS, MAX_WORDS, SHORT_WORDS = (270, 300), (200, 240), 240, 110


@pytest.mark.parametrize("script", SCRIPTS, ids=lambda s: s.id)
def test_the_spoken_length_fits_the_tags(script):
    words = script.words
    if "max-length" in script.tags:
        assert MAX_LENGTH_WORDS[0] <= words <= MAX_LENGTH_WORDS[1]
    else:
        assert 75 <= words <= MAX_WORDS
    if "short" in script.tags:
        assert words <= SHORT_WORDS
    if "long" in script.tags:
        assert LONG_WORDS[0] <= words <= LONG_WORDS[1]


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_the_gold_quotes_the_display_text_and_writes_no_markup(script):
    for name in FIELDS:
        for statement in script.statements[name]:
            for quote in statement.spanish:
                assert quote in script.display, f"{name}: {quote!r} isn't in the display text"
                # Whole words: a source the checks would place word for word (translation._locate()), never one cut
                # inside a word ("Niega dis"); structure, so every script is held to it.
                assert re.search(rf"(?<!\w){re.escape(quote)}(?!\w)", script.display), f"{name}: {quote!r} cuts a word"
            assert not re.search(r"[\[\]{}<>|]", statement.english)
            if not held_out(script):  # the English checks' reading of an instruction: words, not structure
                assert validation._without_instructions(statement.english, "", [])[1] == 0, "an English instruction"


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_what_the_script_names_is_in_it(script):
    display = script.display
    for drug, english in zip(script.drugs, script.drugs_en, strict=True):
        assert drug.lower() in display.lower()
        if not held_out(script):  # the checks' reading of a Spanish drug name: words, not structure
            assert english.lower() in spanish.drug_candidates(drug.split()[-1]) | {drug.lower()}, (drug, english)
    for retracted in script.retracted:
        at = display.find(retracted)
        assert at >= 0, retracted
        if not held_out(script):  # the checks' list of the words that take something back
            assert spanish.correction_cues(display[at:], spanish.tokens(display[at:])), "a correction follows it"
    gold = " ".join(g.value for g in script.gold.values())
    for retracted in script.retracted:
        assert retracted.replace(",", ".") not in gold
    for phrase in script.forbidden_en:
        assert not re.search(rf"(?<!\w){re.escape(phrase.lower())}(?!\w)", gold.lower()), phrase
    for finding, relative in script.family:
        assert finding in gold.lower() and relative in gold.lower()
    for iso in script.dates:
        assert said_month(iso, display), iso


# The Spanish months' names, the test's own, not the checks' table (spanish.MONTHS), so a script's dates are checked as
# structure whatever that table holds; September has two.
MONTHS_ES = (("enero",), ("febrero",), ("marzo",), ("abril",), ("mayo",), ("junio",), ("julio",), ("agosto",),
             ("septiembre", "setiembre"), ("octubre",), ("noviembre",), ("diciembre",))


def said_month(iso: str, display: str) -> bool:
    """Whether the display text names the date's month, by any of its Spanish names ("septiembre" or "setiembre")."""
    text = display.lower()
    return any(re.search(rf"\b{name}\b", text) for name in MONTHS_ES[int(iso[5:7]) - 1])


# September has two names, so the month a date names isn't its place among a list of names: November was read as
# "octubre" when the checks' table was used (the held-out scripts' author's finding, 2026-11-24).
@pytest.mark.parametrize(("iso", "display", "said"), [
    ("2026-11-03", "Vista el 3 de noviembre.", True), ("2026-11-03", "Vista el 3 de octubre.", False),
    ("2026-10-03", "Vista el 3 de octubre.", True), ("2026-10-03", "Vista el 3 de setiembre.", False),
    ("2026-09-03", "Vista el 3 de setiembre.", True), ("2026-09-03", "Vista el 3 de septiembre.", True),
    ("2026-12-03", "Vista el 3 de diciembre.", True), ("2026-01-03", "Vista el 3 de enero.", True),
])
def test_a_scripts_date_is_found_by_its_months_spanish_name(iso, display, said):
    assert said_month(iso, display) is said


@pytest.mark.parametrize("script", ALL, ids=lambda s: s.id)
def test_each_negated_or_present_finding_is_said_in_the_gold_and_one_way_only(script):
    # The scoring counts a finding listed as negated that a statement shows without its negation, and one listed as
    # present that it shows negated, wherever the statement says it (scoring_es.negated()). A phrase the gold says both
    # ways would count a right answer as changed ("effusion" in "mild effusion ... without effusion"), and one it never
    # says would check nothing. A script run only when asked is checked for its structure only: each finding it lists
    # is in its gold, as the scorer's reading of its negations is reported, not refused (ehr_eval/refusals.py).
    gold = " ".join(g.value for g in script.gold.values())
    assert not set(script.negated) & set(script.affirmed), "listed both negated and present"
    if held_out(script):
        assert all(_contains(gold, finding) for finding in script.negated + script.affirmed)
        return
    for finding in script.negated:
        places = scoring_es.negated(gold, finding)
        assert places and all(places), f"{finding!r} isn't negated everywhere the gold says it: {places}"
    for finding in script.affirmed:
        places = scoring_es.negated(gold, finding)
        assert places and not any(places), f"{finding!r} isn't present everywhere the gold says it: {places}"


@pytest.mark.parametrize("script", [s for s in ALL if s.forbidden], ids=lambda s: s.id)
def test_every_forbidden_passage_is_inside_an_instruction_the_spanish_checks_recognize(script):
    # For a script run only when asked, only that it is in the text: whether the checks recognize it is what the run
    # finds out.
    text = script.display
    spans = spanish.directives(text)
    for forbidden in script.forbidden:
        at = text.find(forbidden)
        assert at >= 0, f"{forbidden!r} isn't in the display text"
        assert held_out(script) or any(start <= at and at + len(forbidden) <= end for start, end, _ in spans), forbidden


def _contains(text: str, phrase: str) -> bool:
    return bool(re.search(rf"(?<!\w){re.escape(phrase.lower())}(?!\w)", text.lower()))


# The format tests each script is held to, the ones run only when asked for their structure only.
FORMAT_TESTS = (
    test_the_script_follows_the_format, test_the_gold_quotes_the_display_text_and_writes_no_markup,
    test_what_the_script_names_is_in_it, test_each_negated_or_present_finding_is_said_in_the_gold_and_one_way_only,
    test_every_forbidden_passage_is_inside_an_instruction_the_spanish_checks_recognize,
)


def format_failures(script) -> list[str]:  # noqa: ANN001
    failures = []
    for test in FORMAT_TESTS:
        try:
            test(script)
        except AssertionError as error:
            failures.append(f"{test.__name__}: {error}")
    return failures


def written(tmp_path, folder: str, replacements: list[tuple[str, str]]):  # noqa: ANN001, ANN201
    """h02, the seen script, written into a folder of that name with the replacements made, and loaded from it."""
    source = next(s for s in HOLDOUT if s.id.startswith("h02")).path
    text = source.read_text(encoding="utf-8")
    for old, new in replacements:
        assert old in text, old
        text = text.replace(old, new)
    (tmp_path / folder).mkdir()
    (tmp_path / folder / source.name).write_text(text, encoding="utf-8")
    return load_scripts(tmp_path / folder)[0]


# A drug the checks don't read from its Spanish, well formed: what a held-out script may say, and the test set's may
# not. Made up ("zentarimicina", read by the checks as "zentarimycin"), so no list the tightening round widens takes it.
OFF_LIST = [("nitrofurantoína", "zentarimicina"), ("nitrofurantoin", "zentarithromycin"),
            ("Nitrofurantoin", "Zentarithromycin")]


def test_a_held_out_script_with_a_word_off_the_checks_lists_passes_the_format_tests(tmp_path):
    script = written(tmp_path, HOLDOUT_DIR.name, OFF_LIST)
    assert held_out(script) and "zentarithromycin" not in spanish.drug_candidates("zentarimicina")
    assert format_failures(script) == []
    # The same file in the test set's folder is held to the checks' reading too.
    other = written(tmp_path, "scripts-es", OFF_LIST)
    assert [f.split(":")[0] for f in format_failures(other)] == ["test_what_the_script_names_is_in_it"]


@pytest.mark.parametrize("broken", [
    [("spanish = [\"No está embarazada.\"]", "spanish = [\"No está embarazada ahora.\"]")],  # a source not in the text
    [("spanish = [\"No está embarazada.\"]", "spanish = [\"No está embaraz\"]")],  # a source cut inside a word
    [("drugs = [\"zentarimicina\"]", "drugs = [\"fosfomicina\"]")],  # a drug listed that the text doesn't say
    [("negated = [\"fever\", \"low back pain\"]", "negated = [\"fever\", \"chest pain\"]")],  # a finding not in the gold
    [("retracted = [\"50 mg\", \"3 días\"]", "retracted = [\"50 mg\", \"4 días\"]")],  # a retraction not in the text
    [("voice = \"ef_dora\"", "voice = \"af_bella\"")],  # a voice the set doesn't have
])
def test_a_held_out_script_broken_in_its_structure_still_fails_them(tmp_path, broken):
    assert format_failures(written(tmp_path, HOLDOUT_DIR.name, OFF_LIST + broken))


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
    # The test set's only: which words are drugs is the checks' list (translation.is_drug()), so a script run only when
    # asked isn't held to it ("folic" is a drug word there, "folic acid" the script's drug).
    for script in SCRIPTS:
        named = {name.lower() for name in script.drugs_en}
        for gold in script.gold.values():
            assert {t.text for t in tokens(gold.value) if translation.is_drug(t.text)} <= named, script.id


def test_the_spanish_sets_voices_and_recordings_are_the_ones_voices_toml_lists():
    voices = tomllib.loads((EVAL_DIR / "voices.toml").read_text(encoding="utf-8"))
    assert {name: voice["accent"] for name, voice in voices["voices_es"].items()} == VOICES
    variants = voices["variants_es"]
    assert {name[:3] for name in variants["unusable"]} == {"e01", "e08"}
    assert set(variants["unusable"]) <= {s.id for s in SCRIPTS if s.noise}
    # The two in the wrong language each hold an instruction to keep out of the note (FR-14), and fit in 2 minutes.
    english = next(s for s in load_english() if s.id == variants["english_as_spanish"])
    spanish = next(s for s in SCRIPTS if s.id == variants["spanish_as_english"])
    assert english.forbidden and english.words <= MAX_LENGTH_WORDS[1] and spanish.forbidden


def test_the_first_five_scripts_run_only_when_asked_are_seen_and_the_next_five_held_out():
    # h01 to h05 are seen (the owner's ruling, 2026-11-24); h06 to h10, written by an agent that had seen no results,
    # are held out, never run before run B's window.
    assert [s.id[:3] for s in HOLDOUT] == [f"h{n:02d}" for n in range(1, 11)]
    assert [s.id[:3] for s in HOLDOUT[:5]] == list(SEEN) and not {s.id[:3] for s in HOLDOUT[5:]} & set(SEEN)
    assert not {s.id for s in SCRIPTS} & {s.id for s in HOLDOUT}
    long, corrected, family, switching, follow_up = HOLDOUT[:5]
    # Sealed before Spanish was limited to 2 minutes (decision 20), they keep the lengths they were written to; they
    # are read as text only (run_a.py), never recorded.
    assert all(75 <= s.words <= 330 for s in HOLDOUT[:5])
    assert "long" in long.tags and 290 <= long.words <= 330
    assert len(corrected.retracted) == 2 and "self-correction" in corrected.tags
    assert family.family and "allergy-change" in family.tags and family.forbidden
    assert "code-switching" in switching.tags
    assert len(follow_up.drugs) >= 3 and "actions" in follow_up.tags


# The Spanish prompt's examples that some script's display text also says, as found when rule 6's example was moved
# out of the test set: kept as they are until the owner decides, and listed so that a new one fails the test below.
SHARED_EXAMPLES = {
    "dos veces al día": {"e02", "e05", "e08", "e10", "e15", "h05"},
    "paracetamol": {"e03", "e06", "h01"},
    "amoxicilina": {"e03", "e04"},
    "TA": {"e01", "e08", "e15", "h01", "h03", "h05"},
    "EPOC": {"e15"},
    "niega dolor torácico": {"e01", "e08", "h05"},
    "sin fiebre": {"e10", "e15"},
    "posible neumonía": {"e03"},
    "suspender": {"e04", "e08", "e16", "h01", "h05"},
    "no, perdón": {"e02", "e03", "e16", "h02"},
    "mejor dicho": {"h02"},
    "en 3 meses": {"e02", "e10", "e15", "h05"},
}


def prompt_examples() -> dict[int, list[str]]:
    """The Spanish each of rules 3 to 7 gives as an example: what an English translation is given for in rules 3 to 5,
    everything quoted in rule 7, and rule 6's correction cues, its example sentence and the passages it quotes."""
    system = prompt.load(language="es").system
    rules = {int(m.group(1)): m.group(2) for m in re.finditer(r"^(\d)\. (.*)$", system, re.M)}
    found = {n: re.findall(r'"([^"]+)" is "', rules[n]) for n in (3, 4, 5)}
    found[7] = re.findall(r'"([^"]+)"', rules[7])
    english = re.search(r'the "english" is "([^"]+)"', rules[6]).group(1)
    found[6] = [q for q in re.findall(r'"([^"]+)"', rules[6]) if q not in ("spanish", "english", english)]
    return found


def test_the_prompts_examples_are_not_the_test_sets_sentences():
    # An example the prompt teaches that a script also says would score the script on what the prompt showed.
    def words(text: str) -> str:
        return " " + " ".join(token.key for token in spanish.tokens(text)) + " "

    examples = prompt_examples()
    shared = {example: {script.id[:3] for script in ALL if words(example) in words(script.display)}
              for rule in examples.values() for example in rule}
    sentence, quoted = re.search(r'for "([^"]+)", the "spanish" is "([^"]+)"', prompt.load(language="es").system).groups()
    assert not shared[sentence] and not shared[quoted], "rule 6's example is a script's own sentence"
    assert {example: scripts for example, scripts in shared.items() if scripts} == SHARED_EXAMPLES
