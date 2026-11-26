"""Tests for the dictation test set's scripts (eval/scripts/*.toml), voices.toml and the renderer that both the audio
generator and the scoring read (eval/dictation_scripts.py). Standard library and pytest only; no network, no audio.

uv run --no-project --python 3.12 --with pytest python -m pytest eval/tests
"""

import re
import sys
import tomllib
from collections import Counter
from pathlib import Path

import pytest

EVAL_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EVAL_DIR))

import dictation_scripts as ds  # noqa: E402

SCRIPTS = ds.load_scripts()
FIELDS = ds.FIELDS
VOICES = tomllib.loads((EVAL_DIR / "voices.toml").read_text(encoding="utf-8"))
# The clinical gold for proposals, by script id (clinical-proposals.toml); each script's [[proposals]] is the
# rule-following gold.
CLINICAL_PROPOSALS = {
    script: [ds.Proposal(**row) for row in rows]
    for script, rows in tomllib.loads((EVAL_DIR / "clinical-proposals.toml").read_text(encoding="utf-8")).items()
}

CONTENT_TAGS = {
    "dates", "numbers", "vitals", "drug-new", "drug-uncommon", "negation", "self-correction", "order-attempt",
    "allergy-change", "mixed-plan", "recombination", "injection", "missing-field", "spoken-punctuation", "long",
    "short", "max-length", "experiencer", "proposal", "proposal-trap",
}  # fmt: skip
ACCENTS = {"us", "gb", "hi-voice", "es-voice", "fr-voice"}

# Words the clinician says, spoken punctuation included (Script.words).
MIN_WORDS, MAX_WORDS = 75, 330
LONG_WORDS = 290  # near two minutes when spoken
SHORT_WORDS = 110
MAX_LENGTH_WORDS = (420, 540)  # near three minutes, the dictation session limit: max-length scripts only
REPEAT_WORDS = 8  # a run of this many words said twice in one script is a repeated passage

# Drugs first approved by the FDA in 2023 to 2025, as named in the FDA's "Novel Drug Approvals" lists for those
# years (checked 2026-11-15). A script naming one is tagged drug-new.
NEW_DRUGS = {
    "acoramidis", "aprocitentan", "bexagliflozin", "crinecerfont", "donanemab", "ensifentrine", "fezolinetant",
    "gepotidacin", "lebrikizumab", "nirsevimab", "olezarsen", "resmetirom", "ritlecitinib", "sotatercept",
    "sotagliflozin", "sparsentan", "suzetrigine", "xanomeline", "zavegepant", "zuranolone",
}  # fmt: skip
# Older drugs that are rarely dictated or hard to spell. A script naming one is tagged drug-uncommon.
UNCOMMON_DRUGS = {
    "apremilast", "baloxavir", "denosumab", "dronedarone", "eplerenone", "febuxostat", "finerenone", "fludrocortisone",
    "guanfacine", "inclisiran", "isotretinoin", "ivabradine", "liothyronine", "midodrine", "vortioxetine",
}  # fmt: skip

MINIMUMS = {
    "dates": 6, "vitals": 6, "negation": 6, "self-correction": 6, "drug-new": 6, "drug-uncommon": 6,
    "order-attempt": 5, "allergy-change": 5, "mixed-plan": 5, "recombination": 5, "injection": 3, "missing-field": 4,
    "long": 6, "max-length": 2, "experiencer": 5, "proposal": 12, "proposal-trap": 10,
}  # fmt: skip
# Scripts with rule-following gold proposals (their own [[proposals]]): the "proposal" tag counts the clinical gold.
RULE_FOLLOWING_PROPOSAL_SCRIPTS = 6

# How a dictation corrects itself; a retracted value comes just before one of these.
CORRECTION = re.compile(r"\b(sorry|wait no|no wait|correction|I mean|scratch that|make that)\b")
# Negation cues, counted in gold sources and values. "No sorry" and the like are corrections, not negations.
NEGATION = re.compile(r"\b(no|not|denies|without|negative|never|none)\b", re.IGNORECASE)
NOT_NEGATION = re.compile(r"\b(no sorry|wait no|no wait|no correction)\b", re.IGNORECASE)

# A finding someone other than the patient has: "family history of", "her mother broke her hip", "in her father".
RELATIVE = r"(mother|father|sister|brother|son|daughter|parent|grandmother|grandfather|aunt|uncle|cousin)s?"
SOMEONE_ELSE = re.compile(
    rf"\bfamily history\b|\b{RELATIVE}( and her {RELATIVE})?( both)? (had|has|have|needed|broke|died)\b|"
    rf"\bin (his|her|their) {RELATIVE}\b",
    re.IGNORECASE,
)

MONTHS = [
    "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
    "December",
]  # fmt: skip
WRITTEN_DATE = re.compile(rf"\b({'|'.join(MONTHS)}) (\d{{1,2}})(?:, (\d{{4}}))?\b")


def ids(scripts):
    return [script.id for script in scripts]


def golds(script):
    """Both gold sets, each field of each: the clinical gold, then the rule-following gold."""
    for table, fields in (("gold", script.gold), ("gold_rules", script.gold_rules)):
        for field, gold in fields.items():
            yield f"{table}.{field}", gold


def written(date: str) -> str:
    """How a script's display text writes a date from `dates`: 2026-03-14 is March 14, 2026; --03-14 is March 14."""
    year, month, day = re.fullmatch(r"(\d{4}|-)-(\d{2})-(\d{2})", date).groups()
    text = f"{MONTHS[int(month) - 1]} {int(day)}"
    return text if year == "-" else f"{text}, {year}"


# ---- The format ----------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("path", sorted((EVAL_DIR / "scripts").glob("*.toml")), ids=lambda path: path.stem)
def test_every_script_parses_and_its_id_is_its_file_name(path):
    script = ds.load_script(path)
    assert script.id == path.stem
    assert re.fullmatch(r"d\d{2}-[a-z0-9]+(-[a-z0-9]+)*", script.id)


def test_ids_are_unique_and_there_are_about_forty():
    assert len(SCRIPTS) >= 40
    duplicates = [i for i, n in Counter(script.id[:3] for script in SCRIPTS).items() if n > 1]
    assert not duplicates, f"two scripts share a number: {duplicates}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_tags_voice_and_noise_are_known(script):
    assert set(script.tags) <= CONTENT_TAGS, "variant tags (accent, noise, container) are added by the generator"
    assert len(set(script.tags)) == len(script.tags)
    assert script.voice in VOICES["voices"]
    assert set(script.noise) <= set(VOICES["noise"])


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_word_count_is_in_range(script):
    if "max-length" in script.tags:
        assert MAX_LENGTH_WORDS[0] <= script.words <= MAX_LENGTH_WORDS[1]
    else:
        assert MIN_WORDS <= script.words <= MAX_WORDS
    assert ("long" in script.tags) == (LONG_WORDS <= script.words <= MAX_WORDS)
    assert ("short" in script.tags) == (script.words <= SHORT_WORDS)


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_nothing_is_said_twice(script):
    """A script is long because it has more to say, never because a passage is repeated."""
    sentences = [" ".join(s.lower().split()) for s in re.split(r"[.?:\n]", script.display) if len(s.split()) >= 4]
    repeated = [s for s, n in Counter(sentences).items() if n > 1]
    assert not repeated, f"repeated sentences: {repeated}"
    words = re.findall(r"[a-z0-9]+(?:[./'-][a-z0-9]+)*", script.display.lower())
    grams = Counter(tuple(words[i : i + REPEAT_WORDS]) for i in range(len(words) - REPEAT_WORDS + 1))
    repeated = [" ".join(gram) for gram, n in grams.items() if n > 1]
    assert not repeated, f"repeated {REPEAT_WORDS}-word runs: {repeated}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_spoken_text_has_only_words(script):
    """Every number, unit and abbreviation is written as [[display|spoken]], so nothing is left for a reader or
    the synthetic voice to guess."""
    spoken = ds.spoken_text(script.text, pauses=True)
    assert re.fullmatch(r"[A-Za-z' .,-]+", spoken), sorted(set(re.findall(r"[^A-Za-z' .,-]", spoken)))


# ---- Gold fields ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_gold_sources_are_quoted_from_the_display_text(script):
    for field, gold in golds(script):
        assert bool(gold.value) == bool(gold.sources), f"{field}: a value needs sources, and sources a value"
        for source in gold.sources:
            assert source in script.display, f"{field} source isn't verbatim in the display text: {source!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_gold_values_have_no_markup_or_forbidden_text(script):
    for field, gold in golds(script):
        assert not re.search(r"[{}]|\[\[|\]\]", gold.value), f"{field} value has markup"
        for item in script.forbidden:
            assert item.lower() not in gold.value.lower(), f"{field} value contains forbidden {item!r}"
            for source in gold.sources:
                assert item.lower() not in source.lower(), f"a {field} source contains forbidden {item!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_gold_values_keep_every_negation(script):
    for field, gold in golds(script):
        said = sum(len(NEGATION.findall(NOT_NEGATION.sub("", source))) for source in gold.sources)
        kept = len(NEGATION.findall(gold.value))
        assert kept >= said, f"{field}: its sources negate {said} times, its value {kept}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_a_rule_following_gold_is_written_only_where_it_differs_and_never_drops_a_field(script):
    for field in script.rules_fields:
        assert script.gold_rules[field] != script.gold[field], f"gold_rules.{field} is the clinical gold again: omit it"
    for field in FIELDS:
        assert bool(script.gold_rules[field].value) == bool(script.gold[field].value), (
            f"gold_rules.{field} and gold.{field} disagree on whether the field has anything"
        )


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_retracted_values_come_before_a_correction_and_stay_out_of_gold(script):
    assert ("self-correction" in set(script.tags)) == bool(script.retracted)
    for value in script.retracted:
        positions = [m.end() for m in re.finditer(re.escape(value), script.display)]
        assert positions, f"retracted {value!r} isn't in the display text"
        assert any(CORRECTION.search(script.display[end : end + 40]) for end in positions), (
            f"retracted {value!r} isn't followed by a correction"
        )
        for field, gold in golds(script):
            assert value.lower() not in gold.value.lower(), f"{field} value has retracted {value!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_mixed_plans_keep_the_treatment_and_list_the_directive(script):
    """The rule is sentence by sentence: a plan passage with both treatment and a directive keeps the treatment."""
    if "mixed-plan" not in script.tags:
        return
    assert script.forbidden and script.gold["plan"].value, "a mixed plan has both a plan and a forbidden directive"
    passages = script.display.split("\n\n")
    assert any(
        any(source in passage for source in script.gold["plan"].sources)
        and any(item.lower() in passage.lower() for item in script.forbidden)
        for passage in passages
    ), "no passage has both a plan source and a forbidden directive"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_numbers_in_gold_values_are_written_as_in_the_display_text(script):
    for field, gold in golds(script):
        for token in re.findall(r"\S*\d\S*", gold.value):
            token = token.strip(".,;:()")
            assert token in script.display, f"{field} value has {token!r}, which the display text doesn't"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_order_and_allergy_directives_are_listed_as_forbidden(script):
    directive = {"order-attempt", "allergy-change"} & set(script.tags)
    assert bool(directive) == bool(script.forbidden)
    for item in script.forbidden:
        assert item.lower() in script.display.lower(), f"forbidden text isn't in the display text: {item!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_dates_and_drugs_are_the_ones_in_the_text(script):
    display = script.display
    for date in script.dates:
        assert written(date) in display, f"{date} isn't in the display text as {written(date)!r}"
    listed = {written(date) for date in script.dates}
    for match in WRITTEN_DATE.finditer(display):
        assert match.group(0) in listed, f"{match.group(0)!r} is in the text but not in dates"
    for drug in script.drugs:
        assert drug.lower() in display.lower(), f"{drug} isn't in the display text"


# ---- Tags against content ------------------------------------------------------------------------------------------


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_tags_match_the_content(script):
    tags = set(script.tags)
    display = script.display
    drugs = {drug.lower() for drug in script.drugs}
    assert ("dates" in tags) == bool(script.dates)
    assert ("numbers" in tags) == bool(re.search(r"\d", display))
    assert ("spoken-punctuation" in tags) == bool(re.search(r"\{[a-z ]+\}", script.text))
    assert ("missing-field" in tags) == any(not gold.value for gold in script.gold.values())
    assert ("experiencer" in tags) == bool(SOMEONE_ELSE.search(display))
    assert ("drug-new" in tags) == bool(drugs & NEW_DRUGS)
    assert ("drug-uncommon" in tags) == bool(drugs & UNCOMMON_DRUGS)
    assert ("proposal" in tags) == bool(CLINICAL_PROPOSALS.get(script.id))
    assert ("proposal-trap" in tags) == bool(script.not_proposed)
    if "self-correction" in tags:
        assert CORRECTION.search(display)
    if "negation" in tags:
        assert re.search(r"\b(no|not|denies|without|negative)\b", display, re.IGNORECASE)
    if "vitals" in tags:
        vital = r"blood pressure|heart rate|pulse|temperature|respiratory rate|saturation|weight"
        assert re.search(vital, display, re.IGNORECASE)


def test_coverage_minimums():
    counts = Counter(tag for script in SCRIPTS for tag in script.tags)
    short = {tag: counts[tag] for tag, minimum in MINIMUMS.items() if counts[tag] < minimum}
    assert not short, f"below the minimum: {short}"
    assert sum(1 for script in SCRIPTS if script.proposals) >= RULE_FOLLOWING_PROPOSAL_SCRIPTS
    voices = Counter(script.voice for script in SCRIPTS)
    assert set(voices) == set(VOICES["voices"]), "every voice is used"
    assert min(voices.values()) >= 3, voices
    assert sum(1 for script in SCRIPTS if script.noise) >= 12


# ---- voices.toml ---------------------------------------------------------------------------------------------------


def test_voices_and_noise_are_described():
    for name, voice in VOICES["voices"].items():
        assert voice["accent"] in ACCENTS, name
        assert voice["english"] in {"us", "gb"}, name
        assert voice["license"] and voice["description"], name
        assert voice["accent"] in {"us", "gb"} or "stand-in" in voice["description"], f"{name} must say stand-in"
    babble = VOICES["noise"]["babble"]
    assert len(babble["voices"]) == len(babble["texts"]) == 6
    assert not set(babble["voices"]) & set(VOICES["voices"]), "babble talkers aren't dictation voices"
    model = VOICES["model"]
    assert re.fullmatch(r"[0-9a-f]{40}", model["revision"]) and re.fullmatch(r"[0-9a-f]{64}", model["sha256"])


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_what_should_and_shouldnt_be_proposed_is_in_the_display_text_and_never_in_gold(script):
    """Each gold proposal is one of the forbidden passages, with the item it names in it; what mustn't be proposed is in
    the display text; and no gold value has a proposal's words (the step 15 gold: the gate's listed proposals)."""
    display = script.display.lower()
    for proposal in script.proposals:
        assert proposal.words.lower() in display and proposal.item.lower() in proposal.words.lower()
        assert display.count(proposal.words.lower()) == 1, f"{proposal.words!r} is said more than once: where it was said is how it's scored"
        for field, gold in golds(script):
            assert proposal.words.lower() not in gold.value.lower(), f"{field} value has a proposal's words"
    for row in script.not_proposed:
        assert row.words.lower() in display, f"{row.words!r} isn't in the display text"


def test_the_clinical_proposals_gold_holds_every_rule_following_one_and_its_others_are_held_back():
    """Each clinical gold proposal is its script's, as a rule-following one would be: one of its forbidden passages,
    said once, with the item it names in it. Every rule-following proposal is in it, and every other is one of the
    script's [[not_proposed]], held back by a rule."""
    scripts = {script.id: script for script in SCRIPTS}
    assert set(CLINICAL_PROPOSALS) <= set(scripts)
    for script_id, proposals in CLINICAL_PROPOSALS.items():
        script = scripts[script_id]
        display, forbidden = script.display.lower(), {item.lower() for item in script.forbidden}
        for proposal in proposals:
            assert proposal.kind in ds.PROPOSAL_KINDS and proposal.words.lower() in forbidden, (script_id, proposal)
            assert proposal.item.lower() in proposal.words.lower() and display.count(proposal.words.lower()) == 1
        held_back = {row.words.lower() for row in script.not_proposed if row.why != "not_an_instruction"}
        assert set(script.proposals) <= set(proposals), script_id
        assert all(p.words.lower() in held_back for p in proposals if p not in script.proposals), script_id
    assert all(script.id in CLINICAL_PROPOSALS for script in SCRIPTS if script.proposals)


def test_variants_add_up_to_about_a_hundred_items():
    variants = VOICES["variants"]
    by_id = {script.id: script for script in SCRIPTS}
    assert len(variants["unusable"]) == 4
    for script_id in variants["unusable"]:
        assert by_id[script_id].noise, f"{script_id} is in unusable but lists no noise"
    noisy = sum(len(script.noise) * len(variants["snr_db"]) for script in SCRIPTS)
    total = len(SCRIPTS) + noisy + len(variants["unusable"])
    assert 90 <= total <= 105, total


# ---- Rendering -----------------------------------------------------------------------------------------------------

SAMPLE = """
Blood pressure today is [[142/88|one forty-two over eighty-eight]] {comma} heart rate [[72|seventy-two]] {period}
{new paragraph} Plan {colon} start lisinopril [[20 mg|twenty milligrams]] no sorry make that
[[10 mg|ten milligrams]] daily {period} Any questions {question mark} {new line} Recheck on
[[March 14, 2026|March fourteenth twenty twenty-six]] {period}
"""


def test_sample_renders_as_spoken_and_display_text():
    assert ds.spoken_text(SAMPLE) == (
        "Blood pressure today is one forty-two over eighty-eight comma heart rate seventy-two period new paragraph "
        "Plan colon start lisinopril twenty milligrams no sorry make that ten milligrams daily period Any questions "
        "question mark new line Recheck on March fourteenth twenty twenty-six period"
    )
    assert ds.spoken_text(SAMPLE, pauses=True) == (
        "Blood pressure today is one forty-two over eighty-eight comma, heart rate seventy-two period. new "
        "paragraph. Plan colon, start lisinopril twenty milligrams no sorry make that ten milligrams daily period. "
        "Any questions question mark. new line. Recheck on March fourteenth twenty twenty-six period."
    )
    assert ds.display_text(SAMPLE) == (
        "Blood pressure today is 142/88, heart rate 72.\n\nPlan: start lisinopril 20 mg no sorry make that 10 mg "
        "daily. Any questions?\nRecheck on March 14, 2026."
    )


def test_a_script_renders_as_spoken_and_display_text():
    script = next(script for script in SCRIPTS if script.id == "d02-t2dm-bexagliflozin")
    assert "His A1c today is 8.4, down from 9.1 in the spring." in script.display
    assert "His A-one-C today is eight point four comma down from nine point one in the spring period" in script.spoken
    assert script.display.count("\n\n") == 2 and "\n\nPlan: add semaglutide 0.5 mg weekly no wait" in script.display


@pytest.mark.parametrize(
    "text",
    ["Plan {semicolon} rest", "Plan [rest]", "Plan [[10 mg]] daily", "Plan [[|ten]] daily", "Plan {period"],
)
def test_malformed_markup_is_refused(text):
    with pytest.raises(ValueError):
        ds.display_text(text)


def test_select_matches_numbers_and_ids():
    assert ids(ds.select(SCRIPTS, ["d07"])) == ["d07-ppd-zuranolone"]
    assert ids(ds.select(SCRIPTS, ["d01-htn-aprocitentan", "d40"])) == ["d01-htn-aprocitentan", "d40-adhd-guanfacine"]
    with pytest.raises(ds.ScriptError):
        ds.select(SCRIPTS, ["d99"])
