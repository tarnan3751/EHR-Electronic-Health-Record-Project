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
VOICES = tomllib.loads((EVAL_DIR / "voices.toml").read_text(encoding="utf-8"))

CONTENT_TAGS = {
    "dates", "numbers", "vitals", "drug-new", "drug-uncommon", "negation", "self-correction", "order-attempt",
    "allergy-change", "injection", "missing-field", "spoken-punctuation", "long", "short",
}  # fmt: skip
ACCENTS = {"us", "gb", "hi-voice", "es-voice", "fr-voice"}

# Words the clinician says, spoken punctuation included (Script.words).
MIN_WORDS, MAX_WORDS = 75, 330
LONG_WORDS = 290  # near two minutes when spoken
SHORT_WORDS = 110

# Drugs first approved by the FDA in 2023 to 2025, as named in the FDA's "Novel Drug Approvals" lists for those
# years (checked 2026-11-15). A script naming one is tagged drug-new.
NEW_DRUGS = {
    "acoramidis", "aprocitentan", "bexagliflozin", "crinecerfont", "donanemab", "ensifentrine", "fezolinetant",
    "gepotidacin", "lebrikizumab", "nirsevimab", "olezarsen", "resmetirom", "ritlecitinib", "sotatercept",
    "sparsentan", "suzetrigine", "xanomeline", "zavegepant", "zuranolone",
}  # fmt: skip
# Older drugs that are rarely dictated or hard to spell. A script naming one is tagged drug-uncommon.
UNCOMMON_DRUGS = {
    "apremilast", "baloxavir", "denosumab", "dronedarone", "eplerenone", "febuxostat", "fludrocortisone",
    "guanfacine", "inclisiran", "isotretinoin", "ivabradine", "liothyronine", "midodrine", "vortioxetine",
}  # fmt: skip

MINIMUMS = {
    "dates": 6, "vitals": 6, "negation": 6, "self-correction": 6, "drug-new": 6, "drug-uncommon": 6,
    "order-attempt": 5, "allergy-change": 5, "injection": 3, "missing-field": 4, "long": 6,
}  # fmt: skip

MONTHS = [
    "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
    "December",
]  # fmt: skip
WRITTEN_DATE = re.compile(rf"\b({'|'.join(MONTHS)}) (\d{{1,2}})(?:, (\d{{4}}))?\b")


def ids(scripts):
    return [script.id for script in scripts]


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
    assert MIN_WORDS <= script.words <= MAX_WORDS
    assert ("long" in script.tags) == (script.words >= LONG_WORDS)
    assert ("short" in script.tags) == (script.words <= SHORT_WORDS)


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_spoken_text_has_only_words(script):
    """Every number, unit and abbreviation is written as [[display|spoken]], so nothing is left for a reader or
    the synthetic voice to guess."""
    spoken = ds.spoken_text(script.text, pauses=True)
    assert re.fullmatch(r"[A-Za-z' .,-]+", spoken), sorted(set(re.findall(r"[^A-Za-z' .,-]", spoken)))


# ---- Gold fields ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_gold_sources_are_quoted_from_the_display_text(script):
    for field, gold in script.gold.items():
        assert bool(gold.value) == bool(gold.sources), f"{field}: a value needs sources, and sources a value"
        for source in gold.sources:
            assert source in script.display, f"{field} source isn't verbatim in the display text: {source!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_gold_values_have_no_markup_or_forbidden_text(script):
    for field, gold in script.gold.items():
        assert not re.search(r"[{}]|\[\[|\]\]", gold.value), f"{field} value has markup"
        for item in script.forbidden:
            assert item.lower() not in gold.value.lower(), f"{field} value contains forbidden {item!r}"
            for source in gold.sources:
                assert item.lower() not in source.lower(), f"a {field} source contains forbidden {item!r}"


@pytest.mark.parametrize("script", SCRIPTS, ids=ids(SCRIPTS))
def test_numbers_in_gold_values_are_written_as_in_the_display_text(script):
    for field, gold in script.gold.items():
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
    assert ("drug-new" in tags) == bool(drugs & NEW_DRUGS)
    assert ("drug-uncommon" in tags) == bool(drugs & UNCOMMON_DRUGS)
    if "self-correction" in tags:
        assert re.search(r"\b(sorry|wait no|no correction|no wait)\b", display)
    if "negation" in tags:
        assert re.search(r"\b(no|not|denies|without|negative)\b", display, re.IGNORECASE)
    if "vitals" in tags:
        vital = r"blood pressure|heart rate|pulse|temperature|respiratory rate|saturation|weight"
        assert re.search(vital, display, re.IGNORECASE)


def test_coverage_minimums():
    counts = Counter(tag for script in SCRIPTS for tag in script.tags)
    short = {tag: counts[tag] for tag, minimum in MINIMUMS.items() if counts[tag] < minimum}
    assert not short, f"below the minimum: {short}"
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


def test_variants_add_up_to_about_eighty_items():
    variants = VOICES["variants"]
    by_id = {script.id: script for script in SCRIPTS}
    assert len(variants["unusable"]) == 4
    for script_id in variants["unusable"]:
        assert by_id[script_id].noise, f"{script_id} is in unusable but lists no noise"
    noisy = sum(len(script.noise) * len(variants["snr_db"]) for script in SCRIPTS)
    total = len(SCRIPTS) + noisy + len(variants["unusable"])
    assert 75 <= total <= 90, total


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
