"""The eval's synthetic visits (eval/conversations/*.toml) and conversation-voices.toml: every visit follows the format,
says what its tags say, and covers what the plan asks for (eval/README.md, Visits); its gold quotes only who each field
may quote; the rule-following gold passes the sidecar's own checks for a conversation against the visit as MedASR
would write it; and every order, allergy change, patient's request, denied symptom and retracted value is where the
scoring looks for it. No audio, no models."""

from __future__ import annotations

import re
import tomllib
from collections import Counter

import pytest

from ehr_dictation import conversation, validation
from ehr_eval import ambient_scoring
from ehr_eval.conversations import CONVERSATIONS, heard_conversation

import conversation_scripts as cs

VISITS = CONVERSATIONS
VOICES = tomllib.loads((cs.CONVERSATIONS_DIR.parent / "conversation-voices.toml").read_text(encoding="utf-8"))
SPEAKERS = validation.SPEAKERS

TAGS = {
    "small-talk", "thinking-aloud", "home-readings", "relative-health", "refill-request", "order-spoken",
    "allergy-change", "self-correction", "question-no", "negation", "numbers", "dates", "same-gender",
    "three-speakers", "long", "drug-new", "hedging", "backchannel",
}  # fmt: skip
MINIMUMS = {
    "small-talk": 4, "thinking-aloud": 3, "home-readings": 3, "relative-health": 3, "refill-request": 3,
    "order-spoken": 4, "allergy-change": 2, "self-correction": 3, "question-no": 6, "same-gender": 4, "hedging": 3,
    "backchannel": 4,
}  # fmt: skip
WORDS = {"two": (550, 950), "three": (850, 1100), "long": (2300, 2650)}
CORRECTION = re.compile(r"\b(sorry|no wait|wait no|I mean|actually|make that|scratch that)\b", re.IGNORECASE)
NEGATION = re.compile(r"\b(no|not|never|none|denies|without|haven't|hasn't|don't|didn't|isn't|nope)\b", re.IGNORECASE)


def kind(visit: cs.Conversation) -> str:
    return "long" if "long" in visit.tags else "three" if visit.speakers == 3 else "two"


def turn_with(visit: cs.Conversation, text: str, roles) -> cs.Turn | None:  # noqa: ANN001
    return next((turn for turn in visit.turns if turn.role in roles and text in turn.display), None)


@pytest.mark.parametrize("path", sorted(cs.CONVERSATIONS_DIR.glob("c*.toml")), ids=lambda path: path.stem)
def test_every_visit_parses_and_its_id_is_its_file_name(path):
    assert cs.load_conversation(path).id == path.stem


def test_there_are_twelve_visits_as_the_plan_lists():
    kinds = Counter(kind(visit) for visit in VISITS)
    assert [visit.id[:3] for visit in VISITS] == [f"c{n:02}" for n in range(1, 13)]
    assert kinds == {"two": 8, "three": 2, "long": 2}
    assert Counter(variant for visit in VISITS for variant in visit.noise) == {"room": 6, "babble": 4}


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_voices_tags_turns_and_length_are_known_and_in_range(visit):
    assert set(visit.voices.values()) <= set(VOICES["voices"]) and len(set(visit.voices.values())) == visit.speakers
    assert set(visit.tags) <= TAGS and set(visit.noise) <= {"room", "babble"}
    low, high = WORDS[kind(visit)]
    assert low <= visit.words <= high, visit.words
    for turn in visit.turns:
        assert "{" not in turn.text, "nobody says punctuation aloud in a visit"
        assert turn.overlap is None or 0 < turn.overlap <= 0.5
        assert turn.gap is None or 0 <= turn.gap <= 3
    assert visit.turns[0].overlap is None


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_tags_say_what_the_visit_does(visit):
    kinds = {role: VOICES["voices"][voice]["kind"] for role, voice in visit.voices.items()}
    assert ("three-speakers" in visit.tags) == (visit.speakers == 3)
    assert ("same-gender" in visit.tags) == (kinds["clinician"] == kinds["patient"])
    assert ("refill-request" in visit.tags) == bool(visit.requests)
    assert ("question-no" in visit.tags) == bool(visit.denied)
    assert ("self-correction" in visit.tags) == bool(visit.retracted)
    assert ("order-spoken" in visit.tags or "allergy-change" in visit.tags) == bool(visit.forbidden)
    assert ("dates" in visit.tags) == bool(visit.dates)


def test_the_set_covers_what_the_plan_asks_for():
    counts = Counter(tag for visit in VISITS for tag in visit.tags)
    short = {tag: counts[tag] for tag, minimum in MINIMUMS.items() if counts[tag] < minimum}
    assert not short, f"below the minimum: {short}"


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_gold_quotes_only_who_each_field_may_quote(visit):
    for table, golds in (("gold", visit.gold), ("gold_rules", visit.gold_rules)):
        for name, gold in golds.items():
            assert bool(gold.value) == bool(gold.sources), f"{table}.{name}"
            allowed = {role for role in cs.ROLES if role in SPEAKERS[name]}
            for source in gold.sources:
                assert turn_with(visit, source, allowed), f"{table}.{name}: {source!r} isn't in a turn it may quote"


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_every_rule_following_gold_value_passes_the_checks_for_a_conversation(visit):
    heard = heard_conversation(visit)
    fields = {name: {"value": gold.value, "sources": list(gold.sources)} for name, gold in visit.gold_rules.items()}

    checks = validation.check(fields, [], heard, scope=validation.Scope((0, len(heard.text)), heard.passages()))

    for name, checked in checks.fields.items():
        assert checked.reasons == ([] if fields[name]["value"] else ["not_dictated"]), f"gold_rules.{name}: {checked.reasons}"


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_a_rule_following_gold_is_written_only_where_it_differs(visit):
    for name in visit.rules_fields:
        assert visit.gold_rules[name] != visit.gold[name], name
        assert bool(visit.gold_rules[name].value) == bool(visit.gold[name].value), name


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_gold_values_hold_nothing_forbidden_retracted_or_denied(visit):
    for _, golds in (("gold", visit.gold), ("gold_rules", visit.gold_rules)):
        for name, gold in golds.items():
            value = gold.value.lower()
            assert not any(text.lower() in value for text in visit.forbidden + visit.requests), name
            assert not any(re.search(rf"\b{re.escape(text.lower())}\b", value) for text in visit.retracted), name
            for symptom in visit.denied:
                for found in re.finditer(rf"\b{re.escape(symptom.lower())}\b", value):
                    assert NEGATION.search(value[max(0, found.start() - 40) : found.start()]), f"{name}: {symptom} as present"


@pytest.mark.parametrize("visit", [v for v in VISITS if v.forbidden], ids=lambda v: v.id)
def test_every_order_or_allergy_change_is_the_clinicians_and_an_instruction_the_checks_recognize(visit):
    heard = heard_conversation(visit)
    listed = validation.conversation_not_carried_over(heard, heard.passages())
    said = [" ".join(heard.text[item["start"] : item["end"]].lower().split()) for item in listed
            if item["speaker"] == "clinician"]
    for forbidden in visit.forbidden:
        assert turn_with(visit, forbidden, {"clinician"}), f"{forbidden!r} isn't in a clinician's turn"
        assert any(" ".join(forbidden.lower().split()) in item for item in said), forbidden
    assert len(listed) >= len(visit.forbidden)


@pytest.mark.parametrize("visit", [v for v in VISITS if v.requests], ids=lambda v: v.id)
def test_a_patients_request_is_never_listed_as_the_clinicians_instruction(visit):
    heard = heard_conversation(visit)
    listed = [heard.text[item["start"] : item["end"]].lower() for item in
              validation.conversation_not_carried_over(heard, heard.passages())]
    for request in visit.requests:
        assert turn_with(visit, request, {"patient", "other"}), f"{request!r} isn't the patient's or someone else's"
        assert not any(request.lower() in item or item in request.lower() for item in listed), request


# A diagnosis or a finding hedged, denied or reported, by the clinician to the patient's face ("I don't think you have
# pneumonia") or by the patient ("I don't reckon I've had any hypos", "my daughter thinks I've got gout"), is kept with
# its hedge in the gold, and the checks refuse it offered as certain.
@pytest.mark.parametrize("visit", [v for v in VISITS if v.hedged], ids=lambda v: v.id)
def test_each_hedged_diagnosis_is_said_with_its_hedge_kept_so_and_refused_as_certain(visit):
    heard = heard_conversation(visit)
    scope = validation.Scope((0, len(heard.text)), heard.passages())
    for diagnosis in visit.hedged:
        said = [turn for turn in visit.turns if diagnosis in turn.display.lower()]
        assert said and not any(ambient_scoring._offered_as_certain(turn.display, diagnosis) for turn in said), diagnosis
        for golds in (visit.gold, visit.gold_rules):
            assert not any(ambient_scoring._offered_as_certain(gold.value, diagnosis) for gold in golds.values()), diagnosis
        (field, source), *_ = [(name, s) for name in cs.FIELDS for s in visit.gold[name].sources if diagnosis in s.lower()]
        certain = {name: {"value": "", "sources": []} for name in cs.FIELDS} | {
            field: {"value": f"Has {diagnosis}.", "sources": [source]}}
        reasons = validation.check(certain, [], heard, scope=scope).fields[field].reasons
        assert {"negation_dropped", "certainty_dropped", "reported_dropped"} & set(reasons), (diagnosis, reasons)


# A question, in the shapes people ask them, is never a finding's source: each listed is asked in a turn, and the
# checks find it a question.
@pytest.mark.parametrize("visit", [v for v in VISITS if v.asked], ids=lambda v: v.id)
def test_each_listed_question_is_asked_and_found_a_question(visit):
    heard = heard_conversation(visit)
    questions = validation.conversation_questions(heard.text, heard.passages())
    for asked in visit.asked:
        assert any(f"{asked}?" in turn.display for turn in visit.turns), asked
        phrase = " ".join(cs.CLAUSE_PUNCTUATION.sub(" ", asked).split()).lower()
        at = heard.text.lower().find(phrase)
        assert any(start < at + len(phrase) and at < end for start, end in questions), asked


@pytest.mark.parametrize("visit", [v for v in VISITS if v.denied], ids=lambda v: v.id)
def test_each_denied_symptom_is_asked_about_and_answered_no(visit):
    turns = visit.turns
    for symptom in visit.denied:
        asked = [i for i, turn in enumerate(turns) if turn.role == "clinician" and symptom in turn.display.lower()
                 and turn.display.rstrip().endswith("?")]
        assert asked, f"{symptom!r} is never asked about"
        assert any(i + 1 < len(turns) and turns[i + 1].role != "clinician" and NEGATION.search(turns[i + 1].display)
                   for i in asked), f"{symptom!r} is never answered no"


@pytest.mark.parametrize("visit", [v for v in VISITS if v.retracted], ids=lambda v: v.id)
def test_each_retracted_value_comes_just_before_a_correction(visit):
    for retracted in visit.retracted:
        assert any(retracted in turn.display and CORRECTION.search(turn.display[turn.display.find(retracted):][:80])
                   for turn in visit.turns), retracted


@pytest.mark.parametrize("visit", VISITS, ids=lambda v: v.id)
def test_dates_and_drugs_are_the_ones_the_visit_says(visit):
    text = visit.display
    for drug in visit.drugs:
        assert drug.lower() in text.lower(), drug
    named = {word.lower() for word in re.findall(r"[A-Za-z]+", text) if validation._is_drug(word.lower())}
    assert named <= {drug.lower() for drug in visit.drugs}, named
    for date in visit.dates:
        month, day = int(date[-5:-3]), int(date[-2:])
        written = f"{['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'][month - 1]} {day}"
        assert written in text, date


def test_the_gold_is_checked_against_the_visit_as_medasr_writes_it():
    visit = VISITS[0]
    heard = heard_conversation(visit)

    assert heard.text.count("\n") >= 1 and "?" not in heard.text
    assert all(speaker in cs.ROLES for _, _, speaker, _ in heard.passages())
    assert conversation.windows(heard)[0].text.split("\n")[0].startswith(("Clinician: ", "Patient: ", "Other: "))
