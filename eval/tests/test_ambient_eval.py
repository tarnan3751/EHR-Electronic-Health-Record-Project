"""The visits' eval (listening to a visit, the expansion plan's step 16; TC-UC10-19): a recording through the sidecar's
own pipeline with stand-in models comes back as a scored item, with its voices named from the recording's turns; the
measures say who was heard saying what, what was misheard in what was offered, and every safety case; rescoring gives
the same; and the gate on eval/results/ambient-latest.json accepts what matches and says what to do about what doesn't.
Tones stand in for voices; no model, no network."""

from __future__ import annotations

import asyncio
import dataclasses
import json
import subprocess

import numpy as np
import pytest
from conftest import fake_structurer

from ehr_dictation import prompt
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_eval import ambient, ambient_gate, ambient_scoring, conversations, listening
from ehr_eval.ambient_scoring import VisitItem

import conversation_scripts as cs

PITCHES = {"clinician": 220.0, "patient": 440.0}
SAYS = {"clinician": "Your chest is clear.", "patient": "I have had a cough for 2 weeks."}
VISIT = '''
id = "c01-test"
speakers = 2
voices = { clinician = "af_heart", patient = "am_michael" }
tags = ["question-no"]
dates = []
drugs = []
denied = ["fever"]
requests = ["can you refill my inhaler"]

[[turns]]
role = "clinician"
text = "Your chest is clear."

[[turns]]
role = "patient"
text = "No. I have had a cough for [[2 weeks|two weeks]]."

[[turns]]
role = "clinician"
text = "Your chest is clear."

[gold.subjective]
value = "I have had a cough for 2 weeks."
sources = ["I have had a cough for 2 weeks"]

[gold.objective]
value = "Your chest is clear."
sources = ["Your chest is clear"]

[gold.assessment]
value = ""
sources = []

[gold.plan]
value = ""
sources = []
'''


class Embedder:
    def embed(self, windows: np.ndarray) -> np.ndarray:
        spectrum = np.abs(np.fft.rfft(windows, axis=1))
        bins = np.fft.rfftfreq(windows.shape[1], 1 / SAMPLE_RATE)
        return np.stack([spectrum[:, (bins > p - 20) & (bins < p + 20)].sum(axis=1) for p in PITCHES.values()], axis=1) + 1e-3


class Recognizer:
    def recognize(self, samples: np.ndarray) -> Recognition:
        spectrum = np.abs(np.fft.rfft(samples))
        bins = np.fft.rfftfreq(samples.size, 1 / SAMPLE_RATE)
        loudest = max(PITCHES, key=lambda who: spectrum[(bins > PITCHES[who] - 20) & (bins < PITCHES[who] + 20)].sum())
        return Recognition(SAYS[loudest], 0.9)


def recording(*turns: str) -> tuple[bytes, list[dict]]:
    """Each turn three seconds of its voice's pitch, with a pause after, as WebM; and the turns' times."""
    pieces, times, at = [], [], 0.0
    for who in turns:
        t = np.arange(3 * SAMPLE_RATE) / SAMPLE_RATE
        pieces += [0.1 * np.sin(2 * np.pi * PITCHES[who] * t), np.zeros(SAMPLE_RATE // 2)]
        times.append({"role": who, "start": at, "end": at + 3.0})
        at += 3.5
    samples = np.concatenate(pieces).astype(np.float32)
    samples += (np.random.default_rng(1).standard_normal(samples.size) * 3e-4).astype(np.float32)
    data = subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
         "-c:a", "libopus", "-f", "webm", "pipe:1"], input=samples.tobytes(), capture_output=True, check=True).stdout
    return data, times


@pytest.fixture
def visit_dir(tmp_path):
    (tmp_path / "conversations").mkdir()
    (tmp_path / "conversations" / "c01-test.toml").write_text(VISIT, encoding="utf-8")
    return tmp_path


@pytest.fixture
def visits(visit_dir):
    return {visit.id: visit for visit in cs.load_conversations(visit_dir / "conversations")}


def drafted(tmp_path, visits, answer: dict, turns=("clinician", "patient", "clinician"), recognizer=None) -> VisitItem:
    data, times = recording(*turns)
    (tmp_path / "c01.webm").write_bytes(data)
    variant = {"id": "c01-test--clean", "script": "c01-test", "file": "c01.webm", "container": "webm", "noise": None,
               "seconds": 10.5, "turns": times}
    structurer = fake_structurer(answer).using(ambient.conversation_prompt())
    item = asyncio.run(ambient.run_variant(variant, tmp_path, recognizer or Recognizer(), Embedder(), structurer, visits))
    if item.sorting == "done":
        ambient_scoring.checked(item)
    return item


ANSWER = {
    "subjective": {"value": "I have had a cough for 2 weeks.", "sources": ["I have had a cough for 2 weeks"]},
    "objective": {"value": "Your chest is clear.", "sources": ["Your chest is clear"]},
    "assessment": {"value": "", "sources": []},
    "plan": {"value": "", "sources": []},
}


def test_a_recording_goes_through_the_pipeline_with_its_voices_named_and_is_scored(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)

    assert item.names == {"1": "clinician", "2": "patient"} and item.sorting == "done"
    assert item.hypothesis.split("\n") == ["Your chest is clear.", "I have had a cough for 2 weeks.", "Your chest is clear."]
    assert set(item.timings) == {"decode", "diarize", "transcribe", "draft"} and len(item.windows) == 1
    assert item.fields["subjective"].status == "pending" and item.sources["subjective"][0][2] == "patient"
    assert [w[0] for w in item.fields["subjective"].warnings] == ["patients_own_words"]
    metrics = ambient_scoring.score([item], visits)
    # Every word heard is the right speaker's or unsure (the last passage, whose window runs into the end), none wrong;
    # the patient's "No.", which MedASR dropped right after the clinician's question, counts against attribution.
    attribution = metrics["attribution"]
    assert attribution["wrong_passages"] == 0 and attribution["missed_at_turn_change"] == 1
    assert abs(attribution["accuracy"] + attribution["unsure"] - (1 - 1 / attribution["words"])) < 1e-3
    assert 0 < attribution["unsure_by_recording"]["c01-test--clean"] < 0.5
    # The patient's "No." wasn't heard.
    assert metrics["wer"]["by_role"]["clinician"] == 0 and metrics["wer"]["by_role"]["patient"] > 0
    assert metrics["misheard_offered"]["sources"] == 2 and metrics["misheard_offered"]["misheard"] == 0
    assert item.proposes is False and item.proposals == 0, "the sidecar's own visit session said"
    assert metrics["safety"] == {"denied_as_present": [], "hedged_as_certain": [], "asked_as_finding": [],
                                 "wrong_speaker_unwarned": [], "chart_proposals": 0, "requests_as_instructions": [],
                                 "requests_listed_unsure": []}
    assert metrics["sorting"] == {"done": 1, "unavailable": {}}


def test_voices_that_cant_be_named_leave_the_recording_undrafted(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER, turns=("clinician", "clinician", "clinician"))

    assert (item.sorting, item.reason, item.windows) == ("unnamed", "voices_not_named", [])
    assert ambient_scoring.score([item], visits)["sorting"]["unavailable"] == {"voices_not_named": 1}


def test_what_was_misheard_in_an_offered_value_is_listed(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)
    # MedASR heard "2 weeks" where the clinician said nothing of the kind: here, as if the script's patient said "3".
    visits["c01-test"] = dataclasses.replace(visits["c01-test"], turns=(
        visits["c01-test"].turns[0], cs.Turn("patient", "No. I have had a cough for [[3 weeks|three weeks]].", None, None),
        visits["c01-test"].turns[2]))

    misheard = ambient_scoring.score([item], visits)["misheard_offered"]

    assert misheard["misheard"] == 1 and misheard["passages"][0] == {
        "variant": "c01-test--clean", "field": "subjective", "heard": "i have had a cough for 2 weeks",
        "said": "i have had a cough for 3 weeks"}


def test_a_denied_symptom_a_wrong_speaker_and_a_patients_request_are_safety_cases(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)
    item.fields["subjective"].value = "Fever. I have had a cough for 2 weeks."
    # The patient's passage named the clinician, unwarned; and an instruction listed in it.
    item.names = {"1": "patient", "2": "clinician"}
    heard = ambient_scoring.rebuild(item)
    patient = heard.placed[1]
    item.sources["objective"] = [[patient.start, patient.end - 1, "clinician"]]
    item.not_carried_over = [["order", patient.start, patient.end, "clinician"]]

    safety = ambient_scoring.score([item], visits)["safety"]

    assert safety["denied_as_present"] == [{"variant": "c01-test--clean", "field": "subjective", "shown": "fever"}]
    assert [case["field"] for case in safety["wrong_speaker_unwarned"]] == ["objective"]
    assert [case["field"] for case in safety["requests_as_instructions"]] == ["notCarriedOver"]


def test_attribution_is_measured_at_each_threshold(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)
    sure = {"margin": 0.5, "similarity": 0.9, "drop": 0.0, "relabelled": False, "elsewhere": False}
    # Each sure at diarize.py's thresholds; each sweep holds the other two at them.
    item.doubts = [sure, sure | {"margin": 0.15, "similarity": 0.55}, sure | {"margin": 0.25, "drop": 0.08}]

    attribution = ambient_scoring.score([item], visits)["attribution"]
    by_margin, by_floor, by_drop = attribution["by_margin"], attribution["by_floor"], attribution["by_drop"]

    assert by_margin["0.1"]["unsure"] == 0 and by_margin["0.2"]["unsure"] > 0 and by_margin["0.3"]["unsure"] > by_margin["0.2"]["unsure"]
    assert by_floor["0.5"]["unsure"] == 0 and by_floor["0.6"]["unsure"] > 0
    assert by_drop["0.1"]["unsure"] == 0 and by_drop["0.05"]["unsure"] > 0


# Series 1's review, F2: who said each word is the script's, so a short answer heard inside the other voice's passage is
# a word labelled wrong, not hidden in a passage most of which was the clinician's.
def test_a_short_answer_inside_the_other_voices_passage_is_a_word_labelled_wrong(tmp_path, visits):
    class Answered(Recognizer):
        def recognize(self, samples):
            heard = super().recognize(samples)
            return Recognition("Your chest is clear. No." if heard.text == SAYS["clinician"] else heard.text, 0.9)

    item = drafted(tmp_path, visits, ANSWER, recognizer=Answered())

    attribution = ambient_scoring.score([item], visits)["attribution"]

    assert item.hypothesis.split("\n")[0] == "Your chest is clear. No."
    assert attribution["wrong_words"] == 1 and attribution["wrong_passages"] == 1


# A word dropped in the middle of a turn is the word error rate's, not attribution's; one dropped at a change of speaker
# is both.
def test_only_a_word_dropped_at_a_change_of_speaker_counts_against_attribution(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)
    visits["c01-test"] = dataclasses.replace(visits["c01-test"], turns=(
        cs.Turn("clinician", "Your chest is very clear.", None, None), *visits["c01-test"].turns[1:]))

    metrics = ambient_scoring.score([item], visits)

    assert metrics["attribution"]["missed_at_turn_change"] == 1, "the patient's No., not the clinician's very"
    assert metrics["wer"]["by_role"]["clinician"] > 0


# Series 1's review, F3 (c): a negation reaches only through its clause, and a plural is the same symptom.
@pytest.mark.parametrize(("value", "symptom", "present"), [
    ("Not sleeping well, and chest pain since this morning.", "chest pain", True),
    ("No fever, cough or chest pain.", "chest pain", False),
    ("Chest pain: denied.", "chest pain", False),
    ("She has had headaches most days.", "headache", True),
    ("No headaches.", "headaches", False),
    ("He denies chest pain, but has had palpitations.", "palpitations", True),
    ("Denies fever, reports chest pain.", "chest pain", True),
    ("No cough, chest pain on exertion.", "chest pain", True),
])
def test_a_denied_symptom_is_offered_as_present_only_outside_its_negations_clause(value, symptom, present):
    assert ambient_scoring._offered_as_present(value, symptom) is present


# Series 1's review, F1: a diagnosis the clinician hedged or denied to the patient, offered as certain.
@pytest.mark.parametrize(("value", "diagnosis", "certain"), [
    ("You have pneumonia.", "pneumonia", True),
    ("I don't think you have pneumonia.", "pneumonia", False),
    ("Pneumonia is unlikely.", "pneumonia", False),
    ("I think it's mostly arthritis, but you might have a small meniscus tear as well.", "meniscus tear", False),
    ("Arthritis, and a small meniscus tear.", "meniscus tear", True),
    ("Possible meniscus tear.", "meniscus tear", False),
    ("Pneumonia, not a cold.", "pneumonia", True),
    ("My daughter thinks I've got gout.", "gout", False),
])
def test_a_hedged_or_denied_diagnosis_offered_as_certain_is_a_safety_case(value, diagnosis, certain):
    assert ambient_scoring._offered_as_certain(value, diagnosis) is certain


# Series 1's review, F3 (a): on a visit, only a warning that points at a passage flags a retracted value; the
# patients_own_words every Subjective value carries doesn't.
def test_only_a_passage_warning_flags_a_retracted_value(tmp_path, visits):
    item = drafted(tmp_path, visits, ANSWER)
    visits["c01-test"] = dataclasses.replace(visits["c01-test"], retracted=("2 weeks",))
    owned = ambient_scoring.score([item], visits)["order_allergy"]
    item.fields["subjective"].warnings.append(["possible_correction", 0, 1])
    flagged = ambient_scoring.score([item], visits)["order_allergy"]

    assert [w[0] for w in item.fields["subjective"].warnings] == ["patients_own_words", "possible_correction"]
    assert owned["retracted_shown_unflagged"] == 1 and flagged["retracted_shown_unflagged"] == 0


# Series 1's review, F3 (b) and (d): the chart proposals and the instructions listed are the service's own result's.
def test_chart_proposals_and_instructions_come_from_the_services_result(tmp_path, visits, monkeypatch):
    item = drafted(tmp_path, visits, ANSWER)
    real = ambient_scoring.result_json
    monkeypatch.setattr(ambient_scoring, "result_json", lambda *args: real(*args) | {"proposals": [{"kind": "allergy"}]})
    ambient_scoring.checked(item)
    proposing = ambient_scoring.score([item], visits)["safety"]["chart_proposals"]
    item.proposes = None
    ambient_scoring.checked(item)

    assert proposing == 1
    assert ambient_scoring.score([item], visits)["safety"]["chart_proposals"] is None


def test_naming_the_voices_follows_the_turns_every_role_once():
    heard = conversations.heard_conversation(next(iter(conversations.CONVERSATIONS)))
    turns = [{"role": "clinician", "start": 0.0, "end": 0.9}, {"role": "patient", "start": 1.0, "end": 1.9}]
    times = [(placed.segment.start_sample / SAMPLE_RATE, placed.segment.end_sample / SAMPLE_RATE) for placed in heard.placed]

    names = listening.name_voices(heard, [{"role": t.role, "start": s, "end": e}
                                          for t, (s, e) in zip(next(iter(conversations.CONVERSATIONS)).turns, times)])

    assert names == {1: "clinician", 2: "patient"}
    assert listening.name_voices(heard, turns[:1]) == {}
    # A third voice that spoke none of someone else's turns is one the clinician can't place.
    third = conversations.heard_conversation(next(iter(conversations.CONVERSATIONS)))
    third = dataclasses.replace(third, voices=tuple(3 if i == 2 else v for i, v in enumerate(third.voices)))
    assert listening.name_voices(third, [{"role": t.role, "start": s, "end": e} for t, (s, e) in zip(
        next(iter(conversations.CONVERSATIONS)).turns, times)])[3] == "unsure"


def test_rescoring_stored_answers_gives_the_same_results(tmp_path, visits, monkeypatch):
    item = drafted(tmp_path, visits, ANSWER)
    stored = {
        "run": {"parameters": prompt.CONVERSATION_PARAMETERS}, "provenance": {
            "structuring": ambient.conversation_prompt().structuring_sha256, "pipeline": conversations.pipeline_sha256(),
            "scoring": "old"},
        "dataset": {"recordings_sha256": conversations.recordings_sha256(), "gold_sha256": "old"},
        "metrics": ambient_scoring.score([item], visits),
        "items": [dataclasses.asdict(item)],
    }
    monkeypatch.setattr(ambient, "load_conversations", lambda: list(visits.values()))

    rescored = ambient.rescore(json.loads(json.dumps(stored)))

    assert rescored["metrics"] == json.loads(json.dumps(stored["metrics"]))
    assert rescored["provenance"]["scoring"] == conversations.scoring_sha256()
    with pytest.raises(ValueError, match="another prompt"):
        ambient.rescore(stored | {"provenance": stored["provenance"] | {"structuring": "0" * 64}})


# Series 1's review, F8 (decision D16): two dictations at once while a window drafts, through the sidecar's own turns:
# the window gives way, and each dictation is sorted in its own time.
def test_the_contention_probe_has_the_window_give_way_to_one_dictation_or_two():
    class Slow:
        def __init__(self, seconds):
            self.seconds = seconds

        async def propose(self, text):
            await asyncio.sleep(self.seconds)

        def using(self, structuring):
            return self

    one = asyncio.run(listening.contend("window", "dictation", 1, Slow(1.0), Slow(0.1), None, None, lead=0.2))
    two = asyncio.run(listening.contend("window", "dictation", 2, Slow(1.0), Slow(0.1), None, None, lead=0.2))
    alone = asyncio.run(listening.contend(None, "dictation", 2, Slow(1.0), Slow(0.1), None, None))

    assert one["window_gave_way"] == 1, "one dictation is enough"
    assert two["dictation_sorted"] == ["done", "done"] and all(s < 0.5 for s in two["dictation_seconds"])
    assert two["window_gave_way"] == 1 and two["window_seconds_alongside"] >= 1.2
    assert "window_gave_way" not in alone and len(alone["dictation_seconds"]) == 2


# ---- The gate ------------------------------------------------------------------------------------------------------
# Each test starts from an honest results file, a real recording's stored answers as ambient.py writes them, and changes
# one thing.

from ehr_dictation import provenance  # noqa: E402

MODELS = provenance.Models(
    speech={"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
    language={"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf", "sha256": "d" * 64},
    speaker={"repository": "speechbrain/spkrec-ecapa-voxceleb", "commit": "e" * 40, "sha256": "f" * 64},
)


def honest(tmp_path, visit_dir, visits, answer: dict = ANSWER) -> dict:
    item = asyncio.run(ambient.run_variant(
        {"id": "c01-test--clean", "script": "c01-test", "file": "c01.webm", "container": "webm", "noise": None,
         "seconds": 10.5, "turns": _recorded(tmp_path)}, tmp_path, Recognizer(), Embedder(),
        fake_structurer(answer).using(ambient.conversation_prompt()), visits))
    raw = ambient.results([item], {"machine": {"label": "the server"}, "parameters": prompt.CONVERSATION_PARAMETERS},
                          {}, MODELS, ambient.conversation_prompt(), True)
    raw["provenance"] |= {"pipeline": conversations.pipeline_sha256(visit_dir), "scoring": conversations.scoring_sha256(visit_dir)}
    raw["dataset"] |= {"recordings_sha256": conversations.recordings_sha256(visit_dir),
                       "gold_sha256": conversations.gold_sha256(visit_dir)}
    raw["contention"] = {case: {"dictation_sorted": ["done"] * count} for case, count in (
        ("one_alone", 1), ("two_alone", 2), ("one_beside_a_window", 1), ("two_beside_a_window", 2))}
    return json.loads(json.dumps(ambient.scored(raw, visits)))


def _recorded(tmp_path) -> list[dict]:
    data, times = recording("clinician", "patient", "clinician")
    (tmp_path / "c01.webm").write_bytes(data)
    return times


def save(eval_dir, data: dict, name: str = "ambient-latest.json") -> None:
    (eval_dir / "results").mkdir(exist_ok=True)
    (eval_dir / "results" / name).write_text(json.dumps(data))


def gate(visit_dir) -> list[str]:
    return ambient_gate.check(visit_dir / "results", visit_dir)


def test_honest_visit_results_pass_and_missing_ones_say_to_run_the_eval(tmp_path, visit_dir, visits):
    (problem,) = gate(visit_dir)
    assert "no results for the visits" in problem

    save(visit_dir, honest(tmp_path, visit_dir, visits))

    assert gate(visit_dir) == []


def test_a_test_set_without_visits_has_nothing_to_check(tmp_path):
    assert ambient_gate.check(tmp_path / "results", tmp_path) == []


def test_visit_results_from_another_prompt_pipeline_or_recordings_say_to_run_the_eval_again(tmp_path, visit_dir, visits):
    stale = honest(tmp_path, visit_dir, visits)
    stale["provenance"] |= {"structuring": prompt.load().structuring_sha256, "pipeline": "1" * 64}
    stale["dataset"]["recordings_sha256"] = "3" * 64
    save(visit_dir, stale)

    assert sum("Run the visits' eval" in p for p in gate(visit_dir)) >= 3


def test_visit_results_scored_otherwise_need_only_scoring_again(tmp_path, visit_dir, visits):
    rescorable = honest(tmp_path, visit_dir, visits)
    rescorable["provenance"]["scoring"] = "2" * 64
    save(visit_dir, rescorable)

    (problem,) = gate(visit_dir)

    assert "--rescore" in problem


# The second review, item 5: whatever a file says, the gate judges what its stored answers come to.
@pytest.mark.parametrize(("tamper", "words"), [
    (lambda f: f.update(items=[]), "hold 0 of the 1 recordings"),
    (lambda f: f["dataset"].update(complete=False), "hold 1 of the 1 recordings"),
    (lambda f: f["items"].append(dict(f["items"][0], variant="c01-test--room")), "and no other once each"),
    (lambda f: f["metrics"]["attribution"].update(accuracy=1.0), "attribution aren't what their stored answers come to"),
    (lambda f: f["metrics"]["sorting"].update(unavailable={}, done=1) or f["items"][0].update(sorting="unavailable",
                                                                                              reason="model_timeout"),
     "its windows don't end as its sorting says"),
    (lambda f: f["items"][0].update(unsure=[True] * len(f["items"][0]["unsure"])), "marked sure or unsure against"),
    (lambda f: f["items"][0].update(windows=[]), "aren't the ones its passages make"),
    (lambda f: f["items"][0].update(hypothesis="Your chest is clear."), "transcript isn't the one its passages make"),
    (lambda f: f.pop("contention"), "no contention probe"),
])
def test_a_tampered_or_partial_file_fails(tmp_path, visit_dir, visits, tamper, words):
    data = honest(tmp_path, visit_dir, visits)
    tamper(data)
    save(visit_dir, data)

    assert any(words in problem for problem in gate(visit_dir)), gate(visit_dir)


def test_a_safety_case_fails_unless_the_owner_accepted_it_and_one_no_longer_shown_fails(tmp_path, visit_dir, visits):
    # The patient's words in the Objective, under voices named the wrong way round: a wrong speaker, unwarned.
    data = honest(tmp_path, visit_dir, visits, ANSWER | {"objective": {"value": "I have had a cough for 2 weeks.",
                                                                       "sources": ["I have had a cough for 2 weeks"]}})
    data["items"][0]["names"] = {"1": "patient", "2": "clinician"}
    data["items"][0]["unsure"] = [False] * len(data["items"][0]["unsure"])
    data["items"][0]["doubts"] = [{"margin": 0.5, "similarity": 0.9, "drop": 0.0, "relabelled": False, "elsewhere": False}
                                  ] * len(data["items"][0]["unsure"])
    data = json.loads(json.dumps(ambient.scored(data, visits)))
    case = data["metrics"]["safety"]["wrong_speaker_unwarned"][0]
    save(visit_dir, data)
    assert any("wrong speaker unwarned" in p for p in gate(visit_dir))

    (visit_dir / "known-limitations.toml").write_text(
        f'[[accepted_conversation]]\nkind = "wrong_speaker_unwarned"\nvariant = "{case["variant"]}"\nfield = "{case["field"]}"\n'
        f'shown = "{case["shown"]}"\nreason = "a test"\naccepted = "2026-11-22"\nby = "the owner"\n', encoding="utf-8")
    data["provenance"]["scoring"] = conversations.scoring_sha256(visit_dir)  # the list is part of the scoring
    save(visit_dir, data)
    assert gate(visit_dir) == []

    save(visit_dir, honest(tmp_path, visit_dir, visits))
    assert any("no longer show" in p for p in gate(visit_dir))


def test_a_baseline_from_another_machine_or_scoring_higher_fails(tmp_path, visit_dir, visits):
    data = honest(tmp_path, visit_dir, visits)
    save(visit_dir, data)
    baseline = json.loads(json.dumps(data))
    baseline["metrics"]["attribution"]["accuracy"] = 0.99
    baseline["run"]["machine"]["label"] = "another"
    save(visit_dir, baseline, "ambient-baseline.json")

    problems = gate(visit_dir)

    assert any("another" in p for p in problems) and any("labelled with the right speaker is" in p for p in problems)


# The second review: a question's words as a finding's source is a safety case, whoever asked.
def test_a_question_quoted_as_a_finding_is_a_safety_case(tmp_path, visits):
    visits["c01-test"] = dataclasses.replace(visits["c01-test"], turns=(
        cs.Turn("clinician", "Your chest is clear?", None, None), *visits["c01-test"].turns[1:]))
    item = drafted(tmp_path, visits, ANSWER)

    asked = ambient_scoring.score([item], visits)["safety"]["asked_as_finding"]

    assert [case["field"] for case in asked] == ["objective"]


# The second measurement's windows: a window whose answer ran out of tokens is asked for again as two halves, and the
# gate reads that back from the stored tries, as it reads a visit whose drafting ended at a window.
def test_a_window_retried_as_halves_is_stored_and_read_back(tmp_path, visits, monkeypatch):
    import httpx2

    from ehr_dictation import conversation
    from ehr_dictation.llama import LlamaServer, Structurer

    monkeypatch.setattr(conversation, "WINDOW_TOKENS", 1000)
    asked = []

    async def handle(request):
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": 4096}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        asked.append(request)
        return httpx2.Response(200, json={"choices": [{"finish_reason": "length" if len(asked) == 1 else "stop",
                                                       "message": {"content": json.dumps(ANSWER)}}]})

    structurer = Structurer(LlamaServer("http://llama.test", "key", transport=httpx2.MockTransport(handle)),
                            ambient.conversation_prompt(), "K2.gguf", timeout=5)
    data, times = recording("clinician", "patient", "clinician")
    (tmp_path / "c01.webm").write_bytes(data)
    item = asyncio.run(ambient.run_variant(
        {"id": "c01-test--clean", "script": "c01-test", "file": "c01.webm", "container": "webm", "noise": None,
         "seconds": 10.5, "turns": times}, tmp_path, Recognizer(), Embedder(), structurer, visits))

    assert [w["failed"] for w in item.windows] == ["model_output_invalid", None, None] and item.sorting == "done"
    assert ambient_scoring.faults(item) == []
    item.windows = item.windows[:2]
    assert ambient_scoring.faults(item), "a half missing"
